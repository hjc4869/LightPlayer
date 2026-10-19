using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database.Entities;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Presentation model for one cell of the video library grid: either a video, drawn over its
/// preview frame, or a folder to descend into.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="LibraryTileModel"/>: video tiles carry no drag payload and
/// no queue actions, which is what keeps videos out of the music queue and the drop targets that
/// feed it.
/// </remarks>
public sealed partial class VideoTileModel : SelectionItemModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private IImage? preview;

    /// <summary>
    /// Previews of the videos and pictures beneath a folder tile, composited into a grid in place of
    /// the folder glyph. Empty for video tiles and for folders nothing could be previewed from.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCollage))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private IReadOnlyList<IImage> collageImages = [];

    /// <summary>Column count of the folder collage: 1, 2, or 3.</summary>
    [ObservableProperty]
    private int collageColumns = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaybackProgress))]
    [NotifyPropertyChangedFor(nameof(CanClearPlaybackProgress))]
    [NotifyPropertyChangedFor(nameof(HasPlaybackStateActions))]
    [NotifyPropertyChangedFor(nameof(ShowSubtitle))]
    [NotifyPropertyChangedFor(nameof(DisplaySubtitle))]
    private double playbackProgressRatio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySubtitle))]
    private string playbackProgressText = string.Empty;

    /// <summary>
    /// Whether this video has been watched through to the end. A watched video keeps the plain tile
    /// of an unwatched one and marks itself on the type badge instead, because a full bar under a
    /// duration repeated against itself says the same thing twice and says it badly.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClearPlaybackProgress))]
    [NotifyPropertyChangedFor(nameof(CanMarkAsWatched))]
    [NotifyPropertyChangedFor(nameof(HasPlaybackStateActions))]
    private bool isWatched;

    private bool hasResumePosition;

    /// <summary>Whether this video sits in the built-in favorite video playlist.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteIconKey))]
    [NotifyPropertyChangedFor(nameof(FavoriteToolTip))]
    private bool isFavorite;

    /// <summary>
    /// What the tile says under its title. A folder's is filled in once it has been listed, which
    /// waits until the tile is scrolled into view along with the previews it composites.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySubtitle))]
    [NotifyPropertyChangedFor(nameof(ShowSubtitle))]
    private string subtitle;

    private readonly object previewGate = new();
    private CancellationTokenSource? previewRequest;
    private bool previewLoaded;
    private int previewHolders;

    private VideoTileModel(string title, string subtitle, bool isFolder)
    {
        Title = title;
        Subtitle = subtitle;
        IsFolder = isFolder;
        GlyphKey = isFolder ? "IconFolder" : "IconVideo";
    }

    /// <summary>Creates a tile for a video file.</summary>
    public static VideoTileModel ForVideo(Sourced<DbVideoFile> video, string subtitle) =>
        new(video.Value.DisplayName ?? string.Empty, subtitle, isFolder: false) { SourcedVideo = video };

    /// <summary>Creates a tile for a browsable folder.</summary>
    public static VideoTileModel ForFolder(string name, VideoFolderReference folder, string subtitle) =>
        new(name, subtitle, isFolder: true)
        {
            Folder = folder,
            ContextFlyoutKey = "VideoFolderTileContextFlyout",
        };

    /// <summary>
    /// Creates a tile for a picture sitting in the folder. Pictures are not indexed, so a tile is
    /// all there is of one until it is opened.
    /// </summary>
    public static VideoTileModel ForPhoto(PhotoFileReference photo) =>
        new(photo.FileName, photo.DisplaySize, isFolder: false)
        {
            Photo = photo,
            GlyphKey = "IconImage",

            // Only the file-level actions apply; the library ones need an index this never entered.
            ContextFlyoutKey = "PhotoTileContextFlyout",
        };

    public string Title { get; }

    public bool IsFolder { get; }

    /// <summary>True when the tile has one or more child previews to composite.</summary>
    public bool HasCollage => CollageImages.Count > 0;

    /// <summary>True while neither a preview frame nor a collage has anything to draw.</summary>
    public bool ShowPlaceholder => Preview is null && !HasCollage;

    /// <summary>True when this video has saved playback progress to display.</summary>
    public bool HasPlaybackProgress => PlaybackProgressRatio > 0;

    public string DisplaySubtitle => HasPlaybackProgress ? PlaybackProgressText : Subtitle;

    public bool ShowSubtitle => !string.IsNullOrEmpty(DisplaySubtitle);

    /// <summary>The indexed video this tile shows, or null for a folder tile.</summary>
    public Sourced<DbVideoFile>? SourcedVideo { get; private init; }

    public DbVideoFile? Video => SourcedVideo?.Value;

    /// <summary>The folder this tile descends into, or null for a video tile.</summary>
    public VideoFolderReference? Folder { get; private init; }

    /// <summary>The picture this tile opens, or null when the tile is a video or a folder.</summary>
    public PhotoFileReference? Photo { get; private init; }

    /// <summary>True when the tile stands for a still picture rather than something to play.</summary>
    public bool IsPhoto => Photo is not null;

    private string? NewWindowPath => Photo is { IsLocal: true } photo ? photo.Path
        : SourcedVideo is { SourceId: var sourceId } video && sourceId == LibrarySourceId.Local
            ? video.Value.Path : null;

    public bool CanOpenInNewWindow => NewWindowPath is not null && PlatformServices.TryOpenInNewWindow is not null;

    [RelayCommand(CanExecute = nameof(CanOpenInNewWindow))]
    private void OpenInNewWindow()
    {
        if (NewWindowPath is { } path)
            PlatformServices.TryOpenInNewWindow?.Invoke(path);
    }

    public string? FolderPath => Folder?.Path;

    // A remote picture is read through its server, which leaves nothing on this device to reveal.
    public bool CanOpenContainingFolder =>
        OpenFolderCommand is not null &&
        (IsPhoto ? Photo!.IsLocal : (IsFolder ? Folder?.SourceId : SourcedVideo?.SourceId)?.IsLocal == true) &&
        PlatformServices.SupportsLocalFileAccess;
    /// <summary>
    /// Offered where the tile is shown away from the folder it lives in - the recently viewed list -
    /// so the library can be re-entered at that folder rather than browsed back down to it.
    /// </summary>
    public bool CanGoToLocation => GoToLocationCommand is not null;

    /// <summary>
    /// Whether a subtitle has been imported for this video, and so is there to drop. Read as the
    /// context menu opens rather than as the tile is built, because an import made since then
    /// happened on the playback page and never reached this list.
    /// </summary>
    public bool CanClearSubtitleCache =>
        ClearSubtitleCacheCommand is not null &&
        SourcedVideo is { } video &&
        SubtitleCache.HasImports(new MediaReference(video.SourceId, video.Value.Path));

    public bool CanClearPlaybackProgress =>
        ClearPlaybackProgressCommand is not null && (IsFolder || HasPlaybackProgress || IsWatched);

    /// <summary>
    /// Folder tiles always offer it, since the action covers every video beneath them and some of
    /// those are bound to be unwatched.
    /// </summary>
    public bool CanMarkAsWatched => MarkAsWatchedCommand is not null && (IsFolder || !IsWatched);

    /// <summary>The star is only offered where a video playlist store was wired up.</summary>
    public bool CanFavorite => ToggleFavoriteCommand is not null;

    /// <summary>Whether this video has a useful saved position and an action that can open it there.</summary>
    public bool CanResume => ResumeCommand is not null && hasResumePosition;

    public string FavoriteIconKey => IsFavorite ? "IconStarFilled" : "IconStarOutline";

    public string FavoriteToolTip => IsFavorite
        ? Strings.Resources.Video_RemoveFromFavorites
        : Strings.Resources.Video_AddToFavorites;

    public bool CanAddToPlaylist => AddToPlaylistCommand is not null;

    public bool CanRemoveFromPlaylist => RemoveFromPlaylistCommand is not null;

    public bool CanShowProperties => PropertiesCommand is not null;

    // Group flags for the context menu separators: a separator drawn for a group whose every item is
    // hidden leaves a doubled rule with nothing between it.
    public bool HasLocationActions => CanGoToLocation || CanOpenContainingFolder;

    public bool HasPlaybackStateActions =>
        CanMarkAsWatched || CanClearPlaybackProgress || CanClearSubtitleCache;

    /// <summary>
    /// Which shared flyout this tile opens. Set by the page: a video in a playlist offers the
    /// playlist actions the library grid has no use for, and the other way round.
    /// </summary>
    public string ContextFlyoutKey { get; set; } = "VideoTileContextFlyout";

    /// <summary>Icon drawn in place of a missing preview, and on the tile's type badge.</summary>
    public string GlyphKey { get; set; }

    /// <summary>Opens the video, or descends into the folder.</summary>
    public IRelayCommand? OpenCommand { get; set; }

    /// <summary>Opens the video at its saved position. Null for folders.</summary>
    public IRelayCommand? ResumeCommand { get; set; }

    /// <summary>Shows the video's file properties. Null for folder tiles.</summary>
    public IRelayCommand? PropertiesCommand { get; set; }

    /// <summary>Reveals the video, or the folder itself, in the platform file manager.</summary>
    public IRelayCommand? OpenFolderCommand { get; set; }

    /// <summary>Navigates to the library folder this tile's item lives in. Null while already there.</summary>
    public IRelayCommand? GoToLocationCommand { get; set; }

    /// <summary>Drops the subtitles imported for this video. Null for folder tiles.</summary>
    public IRelayCommand? ClearSubtitleCacheCommand { get; set; }

    /// <summary>Clears this video's saved resume point, or that of every video beneath this folder.</summary>
    public IRelayCommand? ClearPlaybackProgressCommand { get; set; }

    /// <summary>Drops the whole recently viewed list. Only the tile standing for that list has one.</summary>
    public IRelayCommand? ClearRecentCommand { get; set; }

    /// <summary>
    /// Records a watched marker for this video, or for every video beneath this folder.
    /// </summary>
    public IRelayCommand? MarkAsWatchedCommand { get; set; }

    /// <summary>Adds or removes this video from the built-in favorite video playlist.</summary>
    public IRelayCommand? ToggleFavoriteCommand { get; set; }

    /// <summary>Picks a video playlist and appends this video to it. Null for folder tiles.</summary>
    public IRelayCommand? AddToPlaylistCommand { get; set; }

    /// <summary>Drops this entry from the playlist showing it. Null outside a playlist.</summary>
    public IRelayCommand? RemoveFromPlaylistCommand { get; set; }

    /// <summary>Loads the preview frame. Invoked lazily when the tile is realized.</summary>
    public Func<VideoTileModel, CancellationToken, Task>? PreviewLoader { get; set; }

    /// <summary>Updates the elapsed/total label and normalized progress from persisted state.</summary>
    public void ApplyPlaybackState(VideoPlaybackState? state)
    {
        var position = state?.PositionSeconds ?? 0;
        var duration = state?.DurationSeconds ?? 0;
        if (!double.IsFinite(duration) || duration <= 0)
        {
            duration = Video?.Duration.TotalSeconds ?? 0;
        }

        hasResumePosition = VideoPlaybackProgressPolicy.ShouldOfferResume(state, duration);
        OnPropertyChanged(nameof(CanResume));

        IsWatched = VideoPlaybackProgressPolicy.IsComplete(state);
        if (IsWatched || !double.IsFinite(position) || position <= 0 ||
            !double.IsFinite(duration) || duration <= 0)
        {
            PlaybackProgressText = string.Empty;
            PlaybackProgressRatio = 0;
            return;
        }

        position = Math.Min(position, duration);
        var resolution = Video is { Height: > 0 } video
            ? $"  ·  {video.Width}×{video.Height}"
            : string.Empty;
        PlaybackProgressText = $"{FormatTime(position)} / {FormatTime(duration)}{resolution}";
        PlaybackProgressRatio = position / duration;
    }

    /// <summary>
    /// Registers one displayed view of this tile and triggers a one-time preview load. Pair every
    /// call with <see cref="ReleasePreview"/>.
    /// </summary>
    public void RequestPreview()
    {
        CancellationTokenSource request;
        lock (previewGate)
        {
            previewHolders++;
            if (previewLoaded || previewRequest is not null || PreviewLoader is null)
            {
                return;
            }

            previewRequest = request = new CancellationTokenSource();
        }

        _ = Task.Run(() => RunPreviewLoaderAsync(request));
    }

    /// <summary>Drops one displayed view, freeing the preview bitmap once the last one goes.</summary>
    public void ReleasePreview()
    {
        var hadPreview = Preview is not null || CollageImages.Count > 0;
        lock (previewGate)
        {
            if (previewHolders > 0 && --previewHolders > 0)
            {
                return;
            }

            previewHolders = 0;
            previewRequest?.Cancel();
            previewRequest = null;

            // A video no preview could be produced for has nothing to reload, so scrolling back over
            // it does not re-run the loader against the same empty source.
            if (hadPreview)
            {
                previewLoaded = false;
            }
        }

        Preview = null;
        CollageImages = [];
    }

    partial void OnPreviewChanged(IImage? oldValue, IImage? newValue) =>
        (oldValue as IDisposable)?.Dispose();

    partial void OnCollageImagesChanged(IReadOnlyList<IImage>? oldValue, IReadOnlyList<IImage> newValue) =>
        AlbumArtService.ReleaseImages(oldValue ?? []);

    private static string FormatTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds - 1));
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private async Task RunPreviewLoaderAsync(CancellationTokenSource request)
    {
        try
        {
            if (PreviewLoader is { } loader)
            {
                await loader(this, request.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (previewGate)
            {
                if (ReferenceEquals(previewRequest, request))
                {
                    previewLoaded = true;
                    previewRequest = null;
                }

                request.Dispose();
            }
        }
    }
}
