using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database.Entities;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Video playlist detail page: the playlist header and its entries as video rows.
/// </summary>
/// <remarks>
/// Deliberately narrower than the music detail page. Videos never enter the music queue, so there
/// is no shuffle and no add-to-queue here; what a row offers instead is the entry-level edit the
/// video library grid has nowhere to apply.
/// </remarks>
public sealed partial class VideoPlaylistDetailPageViewModel : SelectionPageViewModelBase, IDisposable, IInlineDetailPage
{
    private readonly string playlistId;
    private readonly IPlaylistService service;
    private readonly IPlaylistDialogService dialogs;
    private readonly IUnifiedVideoLibraryQueryService videoLibraryQuery;
    private readonly VideoPreviewService previews;
    private readonly IVideoPlaybackStateStore playbackStates;
    private readonly VideoPlaylistCallbacks callbacks;
    private readonly Action goBack;
    private readonly Dictionary<VideoTileModel, PlaylistItemModel> rowToItem = [];
    private readonly List<Sourced<DbVideoFile>> resolvedVideos = [];
    private PlaylistModel? playlist;
    private CancellationTokenSource? loadCancellation;
    private bool suppressItemsSync;
    private bool disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    private bool hasItems;

    [ObservableProperty]
    private string headerSubtitle = string.Empty;

    [ObservableProperty]
    private bool showBackButton = !PlatformServices.HostProvidesBackNavigation;

    public VideoPlaylistDetailPageViewModel(
        string playlistId,
        IPlaylistService service,
        IPlaylistDialogService dialogs,
        IUnifiedVideoLibraryQueryService videoLibraryQuery,
        VideoPreviewService previews,
        IVideoPlaybackStateStore playbackStates,
        VideoPlaylistCallbacks callbacks,
        Action goBack)
    {
        this.playlistId = playlistId;
        this.service = service;
        this.dialogs = dialogs;
        this.videoLibraryQuery = videoLibraryQuery;
        this.previews = previews;
        this.playbackStates = playbackStates;
        this.callbacks = callbacks;
        this.goBack = goBack;
        TitleKey = nameof(Strings.Resources.Playlists_VideoPlaylist);

        BackCommand = new RelayCommand(() =>
        {
            if (!TryHandleBack())
            {
                this.goBack();
            }
        });
        PlayCommand = new RelayCommand(PlayFromStart, () => HasItems);
        RenameCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(RenameAsync));
        DeleteCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(DeleteAsync));
        ExportCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(ExportAsync), () => HasItems);
        MultiSelectionActions.AddVideoActions(Selection, callbacks, playbackStates);
        Selection.AddAction(nameof(Strings.Resources.Common_RemoveFromPlaylist), RemoveRowsAsync);
        PageSearch.SetSource(new PageSearchSource(
            () => PageSearchSource.Entries(Rows).Prepend(
                new PageSearchEntry(this, string.Join('\n', Title, HeaderSubtitle))), [Rows], this,
            itemState: () => Rows));

        playlist = service.GetById(playlistId);
        if (playlist is not null)
        {
            Title = playlist.Title;
            playlist.PropertyChanged += OnPlaylistPropertyChanged;
            playlist.Items.CollectionChanged += OnPlaylistItemsChanged;
        }

        _ = ReloadRowsAsync();
    }

    protected override void OnHasShellBackButtonChanged(bool value) =>
        ShowBackButton = !PlatformServices.HostProvidesBackNavigation && !value;

    public ObservableCollection<VideoTileModel> Rows { get; } = [];

    public IRelayCommand BackCommand { get; }

    public IRelayCommand PlayCommand { get; }

    public IAsyncRelayCommand RenameCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }

    public IAsyncRelayCommand ExportCommand { get; }

    public bool ShowEmpty => !HasItems;

    public bool ShowContent => HasItems;

    public bool CanDelete => playlist is not null && !playlist.IsBuiltIn;

    public string EmptyHeader => Strings.Resources.PlaylistDetail_EmptyHeader;

    public string EmptyDescription => Strings.Resources.PlaylistDetail_VideoEmptyDescription;

    /// <summary>
    /// Rebuilds every row from the playlist. Each entry is looked up in the video library so the
    /// row carries real metadata; entries whose video has left the library keep a row built from
    /// the stored entry, which is the only way the user can still remove them.
    /// </summary>
    private async Task ReloadRowsAsync()
    {
        Selection.Exit();
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = new CancellationTokenSource();
        var cancellationToken = loadCancellation.Token;

        var entries = playlist?.Items.ToList() ?? [];
        var resolved = new List<(PlaylistItemModel Entry, Sourced<DbVideoFile> Video)>(entries.Count);
        foreach (var entry in entries)
        {
            var media = new MediaReference(entry.SourceId, entry.FilePath);
            Sourced<DbVideoFile>? found = null;
            try
            {
                found = await videoLibraryQuery.GetVideoByPathAsync(media, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // An unreachable source leaves the stored entry to stand in for its video.
            }

            resolved.Add((entry, found ?? new Sourced<DbVideoFile>(entry.SourceId, Placeholder(entry))));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        ReleaseRows();
        Rows.Clear();
        rowToItem.Clear();
        resolvedVideos.Clear();
        foreach (var (entry, video) in resolved)
        {
            resolvedVideos.Add(video);
            var row = CreateRow(video);
            rowToItem[row] = entry;
            Rows.Add(row);
        }

        UpdateState();
    }

    /// <summary>A stand-in for an entry the video library no longer knows about.</summary>
    private static DbVideoFile Placeholder(PlaylistItemModel entry) => new()
    {
        Path = entry.FilePath,
        FolderPath = VideoFolderPaths.ParentOf(entry.FilePath) ?? string.Empty,
        FileName = Path.GetFileName(entry.FilePath),
        Title = string.IsNullOrWhiteSpace(entry.Title) ? null : entry.Title,
        Duration = entry.Duration,
    };

    private VideoTileModel CreateRow(Sourced<DbVideoFile> video)
    {
        var media = new MediaReference(video.SourceId, video.Value.Path);
        var row = VideoTileModel.ForVideo(video, FormatSubtitle(video.Value));
        row.ContextFlyoutKey = "VideoPlaylistRowContextFlyout";
        row.OpenCommand = new RelayCommand(() =>
            callbacks.OpenVideo(video, resolvedVideos.ToList(), false));
        row.ResumeCommand = new RelayCommand(() =>
            callbacks.OpenVideo(video, resolvedVideos.ToList(), true));
        row.ApplyPlaybackState(playbackStates.Get(media));
        row.GoToLocationCommand = new RelayCommand(() => callbacks.GoToLocation(video));
        if (callbacks.OpenContainingFolder is { } openContainingFolder)
        {
            row.OpenFolderCommand = new RelayCommand(() => openContainingFolder(video));
        }

        row.ClearPlaybackProgressCommand = new RelayCommand(() =>
        {
            callbacks.ClearPlaybackProgress([video]);
            row.ApplyPlaybackState(null);
        });
        row.MarkAsWatchedCommand = new RelayCommand(() =>
        {
            callbacks.MarkWatched([video]);
            row.ApplyPlaybackState(playbackStates.Get(media));
        });
        row.PreviewLoader = async (model, token) =>
        {
            var bitmap = await previews.GetPreviewAsync(media, token).ConfigureAwait(false);
            if (bitmap is not null)
            {
                AlbumArtService.ApplyOnUiThread(bitmap, loaded => model.Preview = loaded, token);
            }
        };
        row.RemoveFromPlaylistCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(() => RemoveRowsAsync([row])));
        if (callbacks.AddToPlaylist is { } addToPlaylist)
        {
            row.AddToPlaylistCommand = new RelayCommand(() => addToPlaylist(video));
        }

        return row;
    }

    /// <summary>Reloads resume points after this retained detail returns from playback.</summary>
    public void RefreshPlaybackProgress()
    {
        foreach (var row in Rows)
        {
            if (row.SourcedVideo is { } video)
            {
                row.ApplyPlaybackState(playbackStates.Get(
                    new MediaReference(video.SourceId, video.Value.Path)));
            }
        }
    }

    private void PlayFromStart()
    {
        if (resolvedVideos.Count > 0)
        {
            callbacks.OpenVideo(resolvedVideos[0], resolvedVideos.ToList(), false);
        }
    }

    private async Task RemoveRowsAsync(IReadOnlyList<SelectionItemModel> selected)
    {
        if (disposed || suppressItemsSync)
        {
            return;
        }

        var removed = selected.Cast<VideoTileModel>().ToHashSet();
        var remaining = Rows.Where(row => !removed.Contains(row)).Select(row => rowToItem[row]).ToArray();

        suppressItemsSync = true;
        try
        {
            await service.ReplaceItemsAsync(playlistId, remaining);
            foreach (var row in removed)
            {
                rowToItem.Remove(row);
                var index = Rows.IndexOf(row);
                if (index >= 0)
                {
                    Rows.RemoveAt(index);
                    resolvedVideos.RemoveAt(index);
                }

                row.ReleasePreview();
            }

            UpdateState();
        }
        finally
        {
            suppressItemsSync = false;
        }
    }

    private async Task RenameAsync()
    {
        if (playlist is null)
        {
            return;
        }

        var name = await dialogs.PromptForTitleAsync(
            Strings.Resources.PlaylistDetail_RenameDialogHeader,
            Strings.Resources.Common_Rename,
            playlist.Title);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await service.RenameAsync(playlistId, name);
        }
    }

    private async Task DeleteAsync()
    {
        if (playlist is null)
        {
            return;
        }

        var confirmed = await dialogs.ConfirmAsync(
            Strings.Resources.PlaylistDetail_DeletePlaylist,
            string.Format(Strings.Resources.PlaylistDetail_DeleteConfirmFormat, playlist.Title),
            Strings.Resources.Common_Delete);
        if (confirmed)
        {
            await service.DeleteAsync(playlistId);
            goBack();
        }
    }

    private async Task ExportAsync()
    {
        if (playlist is null)
        {
            return;
        }

        await dialogs.SaveExportFileAsync(
            playlist.Title,
            async fileName => (await service.ExportAsync(playlistId, fileName))?.Content);
    }

    private void OnPlaylistPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistModel.Title) && playlist is not null)
        {
            Title = playlist.Title;
        }
    }

    private void OnPlaylistItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reflect edits made elsewhere (the tile star, another page) in real time. Self-initiated
        // edits set the guard to avoid a redundant rebuild.
        if (!suppressItemsSync)
        {
            _ = ReloadRowsAsync();
        }
    }

    private void UpdateState()
    {
        Selection.SetItems(Rows);
        HasItems = Rows.Count > 0;
        HeaderSubtitle = BuildSubtitle();
        PlayCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }

    private string BuildSubtitle()
    {
        var count = Rows.Count;
        var label = count == 1
            ? Strings.Resources.Common_OneItem
            : string.Format(Strings.Resources.Common_ItemsFormat, count);
        var total = rowToItem.Values.Aggregate(TimeSpan.Zero, (sum, item) => sum + item.Duration);
        return total > TimeSpan.Zero
            ? $"{label}  ·  {LibrarySortKeys.FormatDuration(total)}"
            : label;
    }

    private static string FormatSubtitle(DbVideoFile video)
    {
        var duration = video.Duration > TimeSpan.Zero
            ? video.Duration.TotalHours >= 1
                ? video.Duration.ToString(@"h\:mm\:ss")
                : video.Duration.ToString(@"m\:ss")
            : string.Empty;
        var resolution = video.Height > 0 ? $"{video.Width}×{video.Height}" : string.Empty;
        return string.Join("  ·  ", new[] { duration, resolution }.Where(part => part.Length > 0));
    }

    private void ReleaseRows()
    {
        foreach (var row in Rows)
        {
            row.ReleasePreview();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        PageSearch.Close();
        Selection.Dispose();
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        if (playlist is not null)
        {
            playlist.PropertyChanged -= OnPlaylistPropertyChanged;
            playlist.Items.CollectionChanged -= OnPlaylistItemsChanged;
        }

        ReleaseRows();
    }
}

/// <summary>What the video playlist detail page asks the shell to do on its behalf.</summary>
public sealed record VideoPlaylistCallbacks : IVideoSelectionCallbacks
{
    /// <summary>Opens a video in the player, with the playlist and saved-position choice.</summary>
    public required Action<Sourced<DbVideoFile>, IReadOnlyList<Sourced<DbVideoFile>>, bool> OpenVideo { get; init; }

    /// <summary>Navigates to the video's folder in the video library.</summary>
    public required Action<Sourced<DbVideoFile>> GoToLocation { get; init; }

    /// <summary>Reveals the video in the platform file manager, or null where unsupported.</summary>
    public Action<Sourced<DbVideoFile>>? OpenContainingFolder { get; init; }

    /// <summary>Picks another video playlist and appends the video to it.</summary>
    public Action<Sourced<DbVideoFile>>? AddToPlaylist { get; init; }

    public Func<IReadOnlyList<Sourced<DbVideoFile>>, Task>? AddVideosToPlaylistAsync { get; init; }

    /// <summary>Clears saved display progress while retaining track preferences.</summary>
    public required Action<IReadOnlyList<Sourced<DbVideoFile>>> ClearPlaybackProgress { get; init; }

    /// <summary>Records a fully watched marker for each video.</summary>
    public required Action<IReadOnlyList<Sourced<DbVideoFile>>> MarkWatched { get; init; }
}
