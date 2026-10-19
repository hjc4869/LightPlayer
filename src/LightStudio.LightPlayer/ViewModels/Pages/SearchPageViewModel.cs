using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Search results page: photo, song, album, artist, and video sections for a keyword. The
/// keyword arrives as a navigation parameter, and the shell re-runs the query in
/// place via <see cref="UpdateKeyword"/> when the user searches again, so the
/// search box never couples directly to this page.
/// </summary>
public sealed partial class SearchPageViewModel : PageViewModelBase, INowPlayingHighlightTarget,
    IScrollPositionPageViewModel, IDisposable
{
    private readonly LibraryCommands commands;
    private readonly AlbumArtService albumArt;
    private readonly IUnifiedLibraryQueryService libraryQuery;
    private readonly VideoSearchServices? videoSearch;
    private readonly PhotoSearchServices? photoSearch;
    private readonly Action<LibraryItemKind, string>? showMoreMusic;
    private CancellationTokenSource? queryCancellation;
    private Task? loadTask;
    private int queryVersion;
    private MediaItemKey? nowPlaying;
    private double previewWidth = 1180;
    private bool photosHaveMore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SongPreview))]
    [NotifyPropertyChangedFor(nameof(PhotoPreview))]
    [NotifyPropertyChangedFor(nameof(HasMoreSongs))]
    [NotifyPropertyChangedFor(nameof(HasMorePhotos))]
    private int previewLimit = 8;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlbumPreview))]
    [NotifyPropertyChangedFor(nameof(ArtistPreview))]
    [NotifyPropertyChangedFor(nameof(HasMoreAlbums))]
    [NotifyPropertyChangedFor(nameof(HasMoreArtists))]
    private int tilePreviewLimit = 16;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private string keyword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResults))]
    [NotifyPropertyChangedFor(nameof(ShowNoResults))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoResults))]
    private bool noResults;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbums))]
    [NotifyPropertyChangedFor(nameof(AlbumsHeader))]
    [NotifyPropertyChangedFor(nameof(AlbumPreview))]
    [NotifyPropertyChangedFor(nameof(HasMoreAlbums))]
    private IReadOnlyList<LibraryTileModel> albumResults = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArtists))]
    [NotifyPropertyChangedFor(nameof(ArtistsHeader))]
    [NotifyPropertyChangedFor(nameof(ArtistPreview))]
    [NotifyPropertyChangedFor(nameof(HasMoreArtists))]
    private IReadOnlyList<LibraryTileModel> artistResults = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSongs))]
    [NotifyPropertyChangedFor(nameof(SongsHeader))]
    [NotifyPropertyChangedFor(nameof(SongPreview))]
    [NotifyPropertyChangedFor(nameof(HasMoreSongs))]
    private IReadOnlyList<TrackRowModel> songResults = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVideos))]
    [NotifyPropertyChangedFor(nameof(VideosHeader))]
    private IReadOnlyList<VideoTileModel> videoResults = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhotos))]
    [NotifyPropertyChangedFor(nameof(PhotoPreview))]
    [NotifyPropertyChangedFor(nameof(HasMorePhotos))]
    private IReadOnlyList<VideoTileModel> photoResults = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartialFailures))]
    private string partialFailureText = string.Empty;

    public SearchPageViewModel(
        string keyword,
        LibraryCommands commands,
        AlbumArtService albumArt,
        IUnifiedLibraryQueryService libraryQuery,
        VideoSearchServices? videoSearch = null,
        PhotoSearchServices? photoSearch = null,
        Action? onBack = null,
        Action<LibraryItemKind, string>? showMoreMusic = null)
    {
        this.commands = commands;
        this.albumArt = albumArt;
        this.libraryQuery = libraryQuery;
        this.videoSearch = videoSearch;
        this.photoSearch = photoSearch;
        this.showMoreMusic = showMoreMusic;
        TitleKey = nameof(Strings.Resources.Common_Search);
        this.keyword = keyword ?? string.Empty;
        BackCommand = new RelayCommand(() => onBack?.Invoke(), () => onBack is not null);
    }

    public IRelayCommand BackCommand { get; }

    public double ScrollOffset { get; set; }

    public bool ShowBackButton => BackCommand.CanExecute(null) && !PlatformServices.HostProvidesBackNavigation && !HasShellBackButton;

    protected override void OnIsNarrowChanged(bool value) => SetPreviewWidth(previewWidth);

    protected override void OnHasShellBackButtonChanged(bool value) => OnPropertyChanged(nameof(ShowBackButton));

    public IEnumerable<LibraryTileModel> AlbumPreview => AlbumResults.Take(TilePreviewLimit);
    public IEnumerable<LibraryTileModel> ArtistPreview => ArtistResults.Take(TilePreviewLimit);
    public IEnumerable<TrackRowModel> SongPreview => SongResults.Take(PreviewLimit);
    public IEnumerable<VideoTileModel> PhotoPreview => PhotoResults.Take(PreviewLimit);
    internal UnifiedLibrarySearchResults? MusicResults { get; private set; }
    public bool HasMoreAlbums => MusicResults?.Albums.Count > TilePreviewLimit;
    public bool HasMoreArtists => MusicResults?.Artists.Count > TilePreviewLimit;
    public bool HasMoreSongs => MusicResults?.Files.Count > PreviewLimit;
    public bool HasMorePhotos => photosHaveMore || PhotoResults.Count > PreviewLimit;

    public void SetPreviewWidth(double width)
    {
        if (width <= 0)
            return;
        previewWidth = width;
        TilePreviewLimit = 2 * Math.Max(1, (int)(width / 136));
        PreviewLimit = IsNarrow ? 6 : 2 * Math.Max(1, (int)(width / 274));
    }

    public string HeaderText =>
        string.IsNullOrWhiteSpace(Keyword) ? Strings.Resources.Common_Search : string.Format(Strings.Resources.Search_ResultsForFormat, Keyword);

    public bool HasAlbums => AlbumResults.Count > 0;

    public bool HasArtists => ArtistResults.Count > 0;

    public bool HasSongs => SongResults.Count > 0;

    public bool HasVideos => VideoResults.Count > 0;

    public bool HasPhotos => PhotoResults.Count > 0;

    [RelayCommand]
    private void ShowMorePhotos() => photoSearch?.ShowAll(Keyword);

    [RelayCommand]
    private void ShowMoreAlbums() => showMoreMusic?.Invoke(LibraryItemKind.Album, Keyword);

    [RelayCommand]
    private void ShowMoreArtists() => showMoreMusic?.Invoke(LibraryItemKind.Artist, Keyword);

    [RelayCommand]
    private void ShowMoreSongs() => showMoreMusic?.Invoke(LibraryItemKind.Song, Keyword);

    public string AlbumsHeader => string.Format(Strings.Resources.Search_AlbumsHeaderFormat, MusicResults?.Albums.Count ?? 0);

    public string ArtistsHeader => string.Format(Strings.Resources.Search_ArtistsHeaderFormat, MusicResults?.Artists.Count ?? 0);

    public string SongsHeader => string.Format(Strings.Resources.Search_SongsHeaderFormat, MusicResults?.Files.Count ?? 0);

    public string VideosHeader => string.Format(
        Localized(nameof(Strings.Resources.Search_VideosHeaderFormat), "Videos ({0})"),
        VideoResults.Count);

    public bool ShowResults => !IsBusy && !NoResults;

    public bool ShowNoResults => !IsBusy && NoResults;

    public bool HasPartialFailures => PartialFailureText.Length > 0;

    /// <summary>Runs the initial query for the keyword supplied at navigation.</summary>
    public Task LoadAsync() => loadTask ??= RunQueryAsync();

    /// <inheritdoc />
    public void SetNowPlaying(MediaItemKey? mediaItem)
    {
        nowPlaying = mediaItem;
        NowPlayingHighlight.Apply(SongResults, nowPlaying);
    }

    /// <summary>Re-runs the search for a new keyword without recreating the page.</summary>
    public void UpdateKeyword(string newKeyword)
    {
        var nextKeyword = newKeyword ?? string.Empty;
        if (Keyword == nextKeyword)
            return;
        ScrollOffset = 0;
        Keyword = nextKeyword;
        loadTask = RunQueryAsync();
    }

    private async Task RunQueryAsync()
    {
        var version = ++queryVersion;
        var term = Keyword;
        queryCancellation?.Cancel();
        queryCancellation?.Dispose();
        queryCancellation = new CancellationTokenSource();
        var cancellationToken = queryCancellation.Token;

        IsBusy = true;
        NoResults = false;
        MusicResults = null;

        try
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                if (version != queryVersion)
                {
                    return;
                }

                AlbumResults = [];
                ArtistResults = [];
                SongResults = [];
                VideoResults = [];
                PhotoResults = [];
                PartialFailureText = string.Empty;
                NoResults = true;
                return;
            }

            // The two libraries are indexed separately; query them together so a slow video source
            // does not hold the music results back.
            var musicQuery = libraryQuery.SearchAsync(term, cancellationToken);
            var videoQuery = videoSearch is null
                ? Task.FromResult(new UnifiedCollection<Sourced<DbVideoFile>>([], []))
                : videoSearch.Query.SearchVideosAsync(term, cancellationToken);
            var photoQuery = photoSearch is null
                ? Task.FromResult(new PhotoSearchBatch([], false, ""))
                : new PhotoSearchSession(photoSearch.Sources, term).ReadAsync(8, cancellationToken);
            var results = await musicQuery.WaitAsync(cancellationToken);
            var videos = await videoQuery.WaitAsync(cancellationToken);
            var photos = await photoQuery;
            if (version != queryVersion)
                return;
            PartialFailureText = string.Join(
                Environment.NewLine,
                results.SourceFailures.Concat(videos.SourceFailures).Select(failure =>
                    $"{failure.SourceName}: {failure.Message}").Append(photos.ErrorText).Where(message => message.Length > 0));

            MusicResults = results;
            AlbumResults = results.Albums.Take(16).Select(a =>
                AlbumTileFactory.Create(a, commands.AlbumTileActions, albumArt)).ToList();
            ArtistResults = results.Artists.Take(16).Select(artist =>
                SearchArtistTileFactory.Create(artist, commands.ArtistTileActions)).ToList();
            SongResults = results.Files.Take(8).Select(file =>
            {
                var row = TrackRowFactory.CreateRow(file, commands.TrackActions, () => SongResults);
                row.AsyncPlaybackContext = () => Task.FromResult<IReadOnlyList<TrackRowModel>>(
                    results.Files.Select(match => TrackRowFactory.CreateRow(match, commands.TrackActions)).ToArray());
                return row;
            }).ToList();
            VideoResults = videos.Items.Select(CreateVideoTile).ToList();
            photosHaveMore = photos.HasMore;
            PhotoResults = photos.Items.Select(photo => PhotoSearchTileFactory.Create(photo, photoSearch!, () => PhotoResults)).ToList();
            NowPlayingHighlight.Apply(SongResults, nowPlaying);
            NoResults = AlbumResults.Count == 0 && ArtistResults.Count == 0 &&
                SongResults.Count == 0 && VideoResults.Count == 0 && PhotoResults.Count == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Warning("Search", $"Search for {term} failed.", exception);
            if (version != queryVersion)
            {
                return;
            }

            AlbumResults = [];
            ArtistResults = [];
            SongResults = [];
            VideoResults = [];
            PhotoResults = [];
            NoResults = true;
            PartialFailureText = string.Empty;
        }
        finally
        {
            if (version == queryVersion)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Builds a result tile carrying the same actions the video library grid offers, so a video
    /// found here behaves like the one browsed to.
    /// </summary>
    private VideoTileModel CreateVideoTile(Sourced<DbVideoFile> video)
    {
        var services = videoSearch!;
        var media = new MediaReference(video.SourceId, video.Value.Path);
        var tile = VideoTileModel.ForVideo(video, VideoSubtitle(video.Value));
        tile.OpenCommand = new RelayCommand(() => _ = OpenVideoAsync(video, resume: false));
        tile.ResumeCommand = new RelayCommand(() => _ = OpenVideoAsync(video, resume: true));
        tile.ApplyPlaybackState(services.PlaybackStates.Get(media));
        tile.PreviewLoader = async (model, token) =>
        {
            var bitmap = await services.Previews.GetPreviewAsync(media, token).ConfigureAwait(false);
            if (bitmap is not null)
            {
                AlbumArtService.ApplyOnUiThread(bitmap, loaded => model.Preview = loaded, token);
            }
        };
        if (services.Callbacks.ShowProperties is { } showProperties)
        {
            tile.PropertiesCommand = new RelayCommand(() => showProperties(video));
        }

        if (services.Callbacks.OpenContainingFolder is { } openContainingFolder)
        {
            tile.OpenFolderCommand = new RelayCommand(() => openContainingFolder(video));
        }

        if (services.Callbacks.ClearSubtitleCache is { } clearSubtitleCache)
        {
            tile.ClearSubtitleCacheCommand = new RelayCommand(() => clearSubtitleCache(video));
        }

        if (services.Callbacks.AddToPlaylist is { } addToPlaylist)
        {
            tile.AddToPlaylistCommand = new RelayCommand(() => addToPlaylist(video));
        }

        tile.ClearPlaybackProgressCommand = new RelayCommand(() =>
        {
            services.Callbacks.ClearPlaybackProgress([video]);
            tile.ApplyPlaybackState(null);
        });

        tile.MarkAsWatchedCommand = new RelayCommand(() =>
        {
            services.Callbacks.MarkWatched([video]);
            tile.ApplyPlaybackState(services.PlaybackStates.Get(media));
        });

        return tile;
    }

    /// <summary>
    /// Plays a result with its own folder as the playlist rather than the rest of the results: what
    /// follows a video is what follows it in the library, not what else happened to match.
    /// </summary>
    private async Task OpenVideoAsync(Sourced<DbVideoFile> video, bool resume)
    {
        if (videoSearch is null)
        {
            return;
        }

        IReadOnlyList<Sourced<DbVideoFile>> siblings;
        try
        {
            siblings = await videoSearch.Query
                .GetVideosInFolderAsync(video.SourceId, video.Value.FolderPath, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch
        {
            siblings = [video];
        }

        videoSearch.Callbacks.OpenVideo(video, siblings, resume);
    }

    private static string VideoSubtitle(DbVideoFile video)
    {
        var duration = video.Duration > TimeSpan.Zero
            ? video.Duration.TotalHours >= 1
                ? video.Duration.ToString(@"h\:mm\:ss")
                : video.Duration.ToString(@"m\:ss")
            : string.Empty;
        var resolution = video.Height > 0 ? $"{video.Width}×{video.Height}" : string.Empty;
        return string.Join("  ·  ", new[] { duration, resolution }.Where(part => part.Length > 0));
    }

    partial void OnAlbumResultsChanged(IReadOnlyList<LibraryTileModel>? oldValue, IReadOnlyList<LibraryTileModel> newValue) =>
        ReleaseTiles(oldValue);

    partial void OnArtistResultsChanged(IReadOnlyList<LibraryTileModel>? oldValue, IReadOnlyList<LibraryTileModel> newValue) =>
        ReleaseTiles(oldValue);

    partial void OnVideoResultsChanged(IReadOnlyList<VideoTileModel>? oldValue, IReadOnlyList<VideoTileModel> newValue) =>
        ReleaseVideoTiles(oldValue);

    partial void OnPhotoResultsChanged(IReadOnlyList<VideoTileModel>? oldValue, IReadOnlyList<VideoTileModel> newValue) =>
        ReleaseVideoTiles(oldValue);

    public void Dispose()
    {
        queryVersion++;
        queryCancellation?.Cancel();
        queryCancellation?.Dispose();
        ReleaseTiles(AlbumResults);
        ReleaseTiles(ArtistResults);
        ReleaseVideoTiles(VideoResults);
        ReleaseVideoTiles(PhotoResults);
    }

    private static void ReleaseTiles(IReadOnlyList<LibraryTileModel>? tiles)
    {
        foreach (var tile in tiles ?? [])
        {
            tile.ReleaseArt();
        }
    }

    private static void ReleaseVideoTiles(IReadOnlyList<VideoTileModel>? tiles)
    {
        foreach (var tile in tiles ?? [])
        {
            tile.ReleasePreview();
        }
    }
}
