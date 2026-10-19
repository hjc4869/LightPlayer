using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Album detail page: album header (art, title, artist, year, genre, track
/// count, total duration) plus the ordered track list and album-level playback
/// actions.
/// </summary>
public sealed partial class AlbumDetailPageViewModel : SelectionPageViewModelBase, INowPlayingHighlightTarget, IInlineDetailPage, IDisposable
{
    private readonly LibraryItemReference identifier;
    private readonly LibraryCommands commands;
    private readonly AlbumArtService albumArt;
    private readonly IUnifiedLibraryQueryService libraryQuery;
    private MediaItemKey? nowPlaying;

    // Raw artist name used to open the artist detail page; empty for albums with
    // no attributable artist, which disables the artist link.
    private string artistQueryName = string.Empty;

    // Whether artistQueryName resolves to a real artist in the library. Synthetic
    // display names such as "Various" have no artist page, so the link stays off.
    private bool artistExists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    private bool isLoading = true;

    // A failed load leaves the page with nothing to show, which is indistinguishable from an album
    // that has no tracks, so failures get their own state carrying the reason and a way to retry.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    private bool hasLoadError;

    [ObservableProperty]
    private string loadErrorText = string.Empty;

    // Its own header back button. Shown only when the page is on its own route and the shell has no
    // back button of its own. The narrow layout, the Apple layout, and hosts with their own back
    // navigation (Android, the browser) hide it in favor of a single global back affordance. When
    // hosted inline in the album list page, the host drives this instead (see ListItemsPageViewModel).
    [ObservableProperty]
    private bool showBackButton = !PlatformServices.HostProvidesBackNavigation;

    [ObservableProperty]
    private IImage? coverImage;

    [ObservableProperty]
    private string albumTitle = string.Empty;

    [ObservableProperty]
    private string artistName = string.Empty;

    [ObservableProperty]
    private string metaLine = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<TrackRowModel> tracks = [];

    public AlbumDetailPageViewModel(
        MediaLibraryItemIdentifier identifier,
        LibraryCommands commands,
        AlbumArtService albumArt,
        IUnifiedLibraryQueryService libraryQuery)
        : this(identifier, commands, albumArt, libraryQuery, onBack: null)
    {
    }

    public AlbumDetailPageViewModel(
        MediaLibraryItemIdentifier identifier,
        LibraryCommands commands,
        AlbumArtService albumArt,
        IUnifiedLibraryQueryService libraryQuery,
        Action? onBack)
    {
        this.identifier = LibraryItemReference.Normalize(identifier);
        this.commands = commands;
        this.albumArt = albumArt;
        this.libraryQuery = libraryQuery;
        TitleKey = nameof(Strings.Resources.Common_ColumnAlbum);

        // When hosted inline (album list page) back returns to the list; on its own route it
        // falls back to the shell's navigation stack.
        BackCommand = new RelayCommand(() =>
        {
            if (!TryHandleBack())
            {
                (onBack ?? (() => this.commands.GoBack?.Invoke())).Invoke();
            }
        });
        PlayCommand = new RelayCommand(() => this.commands.PlayTracks?.Invoke(Tracks), () => Tracks.Count > 0);
        ShuffleCommand = new RelayCommand(() => this.commands.ShuffleTracks?.Invoke(Tracks), () => Tracks.Count > 0);
        AddToQueueCommand = new RelayCommand(() => this.commands.AddTracksToQueue?.Invoke(Tracks), () => Tracks.Count > 0);
        AddToPlaylistCommand = new RelayCommand(() => this.commands.AddTracksToPlaylist?.Invoke(Tracks), () => Tracks.Count > 0);
        OpenArtistCommand = new RelayCommand(OpenArtist, CanOpenArtist);
        RetryCommand = new AsyncRelayCommand(LoadAsync);
        MultiSelectionActions.AddMusicActions(Selection, commands);
        PageSearch.SetSource(new PageSearchSource(GetSearchEntries, [], this,
            isLoading: () => IsLoading, hasPendingItems: () => HasLoadError));
    }

    private IEnumerable<PageSearchEntry> GetSearchEntries()
    {
        if (!ShowContent)
        {
            yield break;
        }

        yield return new PageSearchEntry(this, string.Join('\n', AlbumTitle, ArtistName, MetaLine));
        foreach (var entry in PageSearchSource.Entries(Tracks))
        {
            yield return entry;
        }
    }

    // A standalone (route-hosted) detail page hides its own back button while the shell shows one.
    // Inline-hosted copies never receive shell layout changes (their host owns ShowBackButton), so
    // this stays inert there.
    protected override void OnHasShellBackButtonChanged(bool value) =>
        ShowBackButton = !PlatformServices.HostProvidesBackNavigation && !value;

    public bool ShowContent => !IsLoading && !HasLoadError;

    public object ArtworkKey => (identifier.SourceId, identifier.ArtistName, identifier.AlbumName, identifier.AlbumGroupingKey);

    /// <inheritdoc />
    public void SetNowPlaying(MediaItemKey? mediaItem)
    {
        nowPlaying = mediaItem;
        NowPlayingHighlight.Apply(Tracks, mediaItem);
    }

    public IRelayCommand BackCommand { get; }

    public IRelayCommand PlayCommand { get; }

    public IRelayCommand ShuffleCommand { get; }

    public IRelayCommand AddToQueueCommand { get; }

    public IRelayCommand AddToPlaylistCommand { get; }

    public IRelayCommand OpenArtistCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }

    public async Task LoadAsync()
    {
        Selection.Exit();
        IsLoading = true;
        HasLoadError = false;
        try
        {
            Sourced<MediaLibraryAlbum> sourcedAlbum;
            try
            {
                sourcedAlbum = await libraryQuery.GetAlbumAsync(
                    identifier.SourceId,
                    identifier.ArtistName,
                    identifier.AlbumName,
                    albumGroupingKey: identifier.AlbumGroupingKey);
            }
            catch (ArgumentException) when (identifier.AlbumGroupingKey is not null)
            {
                sourcedAlbum = await libraryQuery.GetAlbumAsync(
                    identifier.SourceId,
                    identifier.ArtistName,
                    identifier.AlbumName);
            }

            var album = sourcedAlbum.Value;
            AlbumTitle = LibrarySortKeys.Fallback(album.Title, Strings.Resources.Common_UnknownAlbum);
            ArtistName = LibrarySortKeys.Fallback(album.Artist, Strings.Resources.Common_UnknownArtist);
            artistQueryName = album.Artist ?? string.Empty;
            artistExists = !string.IsNullOrWhiteSpace(artistQueryName);

            var files = await libraryQuery.GetAlbumTracksAsync(
                identifier.SourceId,
                album.Title,
                album.ArtistForQuery,
                albumGroupingKey: album.AlbumGroupingKey);
            var rows = files
                .Select(file => TrackRowFactory.CreateRow(file, commands.TrackActions, () => Tracks))
                .OrderBy(row => row.DiscNumber)
                .ThenBy(row => row.TrackNumber)
                .ToList();
            foreach (var row in rows) row.HideAlbum = true;
            var total = files.Aggregate(TimeSpan.Zero, (sum, file) => sum + file.Value.Duration);

            Tracks = rows;
            NowPlayingHighlight.Apply(Tracks, nowPlaying);
            MetaLine = BuildMetaLine(album, rows.Count, total);
            NotifyCommands();

            _ = LoadCoverAsync(album);
        }
        catch (Exception ex)
        {
            // The album may have been renamed or removed, or the library may be a server this
            // session can no longer reach; either way the page says so rather than looking empty.
            Tracks = [];
            NotifyCommands();
            LoadErrorText = AppErrorReporter.LogAndGetMessage("Library", "Could not load the album page.", ex);
            HasLoadError = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadCoverAsync(MediaLibraryAlbum album)
    {
        Bitmap? bitmap = await albumArt.GetHighQualityTrackArtAsync(
            new MediaReference(identifier.SourceId, album.FirstFileInAlbum ?? string.Empty)).ConfigureAwait(false);
        if (bitmap is null)
        {
            return;
        }

        AlbumArtService.ApplyOnUiThread(bitmap, art => CoverImage = art, CancellationToken.None);
    }

    partial void OnCoverImageChanged(IImage? oldValue, IImage? newValue) =>
        (oldValue as IDisposable)?.Dispose();

    partial void OnTracksChanged(IReadOnlyList<TrackRowModel> value) => Selection.SetItems(value);

    public void Dispose()
    {
        PageSearch.Close();
        Selection.Dispose();
        CoverImage = null;
    }

    private void NotifyCommands()
    {
        PlayCommand.NotifyCanExecuteChanged();
        ShuffleCommand.NotifyCanExecuteChanged();
        AddToQueueCommand.NotifyCanExecuteChanged();
        AddToPlaylistCommand.NotifyCanExecuteChanged();
        OpenArtistCommand.NotifyCanExecuteChanged();
    }

    private bool CanOpenArtist() =>
        commands.OpenArtist is not null && !string.IsNullOrWhiteSpace(artistQueryName) && artistExists;

    private void OpenArtist()
    {
        if (CanOpenArtist())
        {
            var artist = new UnifiedArtistReference(
                FederatedLibraryOrdering.NormalizeArtistName(artistQueryName),
                artistQueryName,
                [identifier.SourceId],
                new Dictionary<LibrarySourceId, string> { [identifier.SourceId] = artistQueryName });
            commands.OpenArtist!(LibraryItemReference.Artist(artist));
        }
    }

    private static string BuildMetaLine(MediaLibraryAlbum album, int trackCount, TimeSpan total)
    {
        var parts = new List<string>();
        var year = AlbumTileFactory.YearLabel(album.Date);
        if (!string.IsNullOrEmpty(year))
        {
            parts.Add(year);
        }

        if (!string.IsNullOrWhiteSpace(album.Genre))
        {
            parts.Add(album.Genre);
        }

        parts.Add(trackCount == 1 ? Strings.Resources.Common_OneItem : string.Format(Strings.Resources.Common_ItemsFormat, trackCount));

        if (total > TimeSpan.Zero)
        {
            parts.Add(LibrarySortKeys.FormatDuration(total));
        }

        return string.Join("  ·  ", parts);
    }
}
