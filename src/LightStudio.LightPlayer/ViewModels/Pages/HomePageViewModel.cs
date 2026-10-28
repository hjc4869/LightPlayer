using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;

using Strings = LightStudio.LightPlayer.Strings;
using UiPlaybackState = LightStudio.LightPlayer.Models.PlaybackState;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Home page: a spotlight card above the music queue, recently played tracks, videos left part-way through,
/// newly indexed videos, and random album picks. Empty rows hide themselves.
/// </summary>
public sealed partial class HomePageViewModel : PageViewModelBase, IDisposable
{
    /// <summary>Queued tracks shown after the current one.</summary>
    private const int UpcomingLimit = 12;

    /// <summary>
    /// Items each media row holds. The rows are bounded rather than paged: this is a shortcut
    /// surface, and every tile on screen costs a decoded preview frame.
    /// </summary>
    private const int MediaRowLimit = 12;

    /// <summary>Albums the picks row shows, which fills three to five rows as the page resizes.</summary>
    private const int AlbumPickLimit = 18;

    /// <summary>Albums, videos, and pictures available alongside the now-playing spotlight.</summary>
    private const int SpotlightLimit = 8;

    /// <summary>How long one spotlight item stays up before the next takes its place.</summary>
    private static readonly TimeSpan SpotlightInterval = TimeSpan.FromSeconds(9);

    /// <summary>
    /// Decode width of the spotlight's backdrop, blurred once when the artwork loads.
    /// </summary>
    private const int BackdropDecodeWidth = 512;

    private const float BackdropBlurSigma = 32f;

    private readonly PlaybackController playback;
    private readonly AlbumArtService albumArt;
    private readonly TrackActionCallbacks trackActions;
    private readonly IUnifiedLibraryQueryService libraryQuery;
    private readonly HomePageCallbacks callbacks;
    private readonly HomeMediaServices? media;
    private readonly IPlaybackHistoryService? playbackHistory;
    private readonly CancellationTokenSource disposeCancellation = new();
    private readonly MusicTileRow upcomingRow;
    private readonly MusicTileRow recentlyPlayedRow;

    private readonly List<AlbumPick> albumPicks = [];
    private readonly List<SpotlightSlot> spotlightSlots = [];

    private CancellationTokenSource? pageCancellation;
    private CancellationTokenSource? albumPicksCancellation;
    private CancellationTokenSource? queueFillCancellation;
    private DispatcherTimer? spotlightTimer;
    private SpotlightSlideModel? nowPlayingSlide;
    private long albumShuffleSeed = Random.Shared.NextInt64();
    private int pageVersion;
    private int recentHistoryVersion;
    private int nowPlayingArtVersion;
    private int spotlightIndex;
    private bool spotlightActive;
    private bool isSpotlightNarrow;
    private bool disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool hasContent;

    [ObservableProperty]
    private bool showSpotlight;

    /// <summary>The card in the middle of the viewport.</summary>
    [ObservableProperty]
    private SpotlightSlideModel? spotlightCurrent;

    /// <summary>The card a swipe to the right pulls in. Null while the card has nothing to rotate.</summary>
    [ObservableProperty]
    private SpotlightSlideModel? spotlightPrevious;

    /// <summary>The card a swipe to the left pulls in.</summary>
    [ObservableProperty]
    private SpotlightSlideModel? spotlightNext;

    /// <summary>True once the card holds more than one item, which is what the dots and arrows are for.</summary>
    [ObservableProperty]
    private bool hasSpotlightSteps;

    [ObservableProperty]
    private bool hasUpcoming;

    [ObservableProperty]
    private bool hasRecentlyPlayed;

    [ObservableProperty]
    private bool hasContinueWatching;

    [ObservableProperty]
    private bool hasRecentlyAdded;

    [ObservableProperty]
    private bool hasAlbumPicks;

    [ObservableProperty]
    private bool isRefreshingAlbumPicks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartialFailures))]
    private string partialFailureText = string.Empty;

    public HomePageViewModel(
        PlaybackController playback,
        AlbumArtService albumArt,
        TrackActionCallbacks trackActions,
        IUnifiedLibraryQueryService libraryQuery,
        HomePageCallbacks callbacks,
        HomeMediaServices? media = null,
        IPlaybackHistoryService? playbackHistory = null)
    {
        this.playback = playback;
        this.albumArt = albumArt;
        this.trackActions = trackActions;
        this.libraryQuery = libraryQuery;
        this.callbacks = callbacks;
        this.media = media;
        this.playbackHistory = playbackHistory;
        upcomingRow = new MusicTileRow(CreateUpcomingTile);
        recentlyPlayedRow = new MusicTileRow(CreateRecentlyPlayedTile, matchByReference: false);
        TitleKey = nameof(Strings.Resources.Nav_Home);

        RefreshAlbumPicksCommand = new AsyncRelayCommand(RefreshAlbumPicksAsync);
        ShowAllAlbumsCommand = new RelayCommand(callbacks.ShowAllAlbums);
        PageSearch.SetSource(new PageSearchSource(GetSearchEntries,
            [SpotlightSlides, Upcoming, RecentlyPlayed, ContinueWatching, RecentlyAdded, AlbumPicks], this,
            isLoading: () => IsLoading || IsRefreshingAlbumPicks,
            hasPendingItems: () => HasPartialFailures,
            itemState: () => SpotlightSlides.Cast<INotifyPropertyChanged>().Concat(ContinueWatching).Concat(RecentlyAdded)));
        PageSearch.PropertyChanged += OnPageSearchChanged;

        playback.PropertyChanged += OnPlaybackChanged;
        playback.QueueItems.CollectionChanged += OnQueueChanged;
        playback.QueueItemsMetadataChanged += OnQueueMetadataChanged;
        if (playbackHistory is not null)
        {
            playbackHistory.NewEntryAdded += OnHistoryEntryAdded;
        }

        RefreshNowPlaying();
    }

    private IEnumerable<PageSearchEntry> GetSearchEntries() => !HasContent ? [] :
        SpotlightSlides.Select(slide => new PageSearchEntry(slide, PageSearchSource.Text(slide),
                Prepare: () => RevealSpotlight(slide)))
            .Concat(PageSearchSource.Entries(Upcoming))
            .Concat(PageSearchSource.Entries(RecentlyPlayed))
            .Concat(PageSearchSource.Entries(ContinueWatching))
            .Concat(PageSearchSource.Entries(RecentlyAdded))
            .Concat(PageSearchSource.Entries(AlbumPicks));

    private void RevealSpotlight(SpotlightSlideModel slide)
    {
        var index = SpotlightSlides.IndexOf(slide);
        if (index >= 0)
        {
            SpotlightResetRequested?.Invoke();
            StepSpotlight(index - spotlightIndex);
        }
    }

    private void OnPageSearchChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PageSearchModel.IsOpen))
        {
            if (PageSearch.IsOpen)
            {
                SpotlightResetRequested?.Invoke();
            }

            UpdateSpotlightTimer();
        }
    }

    public BulkObservableCollection<LibraryTileModel> Upcoming => upcomingRow.Tiles;

    public BulkObservableCollection<LibraryTileModel> RecentlyPlayed => recentlyPlayedRow.Tiles;

    public BulkObservableCollection<VideoTileModel> ContinueWatching { get; } = [];

    public BulkObservableCollection<VideoTileModel> RecentlyAdded { get; } = [];

    public BulkObservableCollection<LibraryTileModel> AlbumPicks { get; } = [];

    /// <summary>Every card the spotlight holds, which is what its position dots are drawn from.</summary>
    public BulkObservableCollection<SpotlightSlideModel> SpotlightSlides { get; } = [];

    /// <summary>
    /// Raised when the rotation is due to move on. The page answers it by sliding the card across
    /// and calling <see cref="StepSpotlight"/> once the slide lands.
    /// </summary>
    public event Action<int>? SpotlightAdvanceRequested;

    public event Action? SpotlightResetRequested;

    /// <summary>Draws a different handful of albums without reloading the rest of the page.</summary>
    public IAsyncRelayCommand RefreshAlbumPicksCommand { get; }

    public IRelayCommand ShowAllAlbumsCommand { get; }

    public string EmptyHeader => Localized(nameof(Strings.Resources.Home_EmptyHeader), "It's lonely here.");

    public string EmptyDescription =>
        PlatformServices.NativeMediaLibrary is not null ? Strings.Resources.Library_EmptySourcesDescription :
            Localized(nameof(Strings.Resources.Home_EmptyDescription), "Add a library folder and scan to get started.");

    public bool ShowEmpty => !IsLoading && !HasContent;

    public bool HasPartialFailures => PartialFailureText.Length > 0;

    /// <summary>
    /// Ties the spotlight's rotation to the page being on screen, so a home page left behind on the
    /// navigation stack is not cycling artwork nobody is looking at.
    /// </summary>
    public void SetSpotlightActive(bool value)
    {
        if (disposed || spotlightActive == value)
        {
            return;
        }

        spotlightActive = value;
        UpdateSpotlightTimer();
    }

    /// <summary>
    /// Moves the card on by <paramref name="step"/> items, wrapping at either end. Called once the
    /// page's slide has landed, so the card the user pulled in is the one that stays.
    /// </summary>
    public void StepSpotlight(int step)
    {
        if (disposed || SpotlightSlides.Count < 2)
        {
            return;
        }

        spotlightIndex += step;
        UpdateSpotlightWindow();

        // A step taken by hand earns a full interval before the rotation takes the next one.
        if (spotlightTimer is { IsEnabled: true } timer)
        {
            timer.Stop();
            timer.Start();
        }
    }

    public async Task LoadAsync()
    {
        var version = ++pageVersion;
        pageCancellation?.Cancel();
        pageCancellation?.Dispose();
        pageCancellation = CancellationTokenSource.CreateLinkedTokenSource(disposeCancellation.Token);
        var cancellationToken = pageCancellation.Token;

        IsLoading = true;
        PartialFailureText = string.Empty;
        var failures = new List<UnifiedSourceFailure>();

        try
        {
            await Task.WhenAll(
                AppErrorReporter.GuardAsync(LoadRecentlyPlayedAsync),
                LoadAlbumPicksAsync(albumShuffleSeed, failures, cancellationToken),
                LoadMediaRowsAsync(failures, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            await LoadSpotlightAsync(failures, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (version != pageVersion)
        {
            return;
        }

        ApplyFailures(failures);
        IsLoading = false;
        RefreshUpcoming();
    }

    /// <summary>Refreshes music history and saved resume points when returning to a cached page.</summary>
    public void RefreshPlaybackProgress()
    {
        if (!IsLoading)
        {
            _ = AppErrorReporter.GuardAsync(LoadRecentlyPlayedAsync);
        }

        if (media is null || IsLoading)
        {
            return;
        }

        foreach (var tile in ContinueWatching.Concat(RecentlyAdded)
                     .Concat(SpotlightSlides.Select(slide => slide.ContextItem).OfType<VideoTileModel>()))
        {
            if (tile.SourcedVideo is { } video)
            {
                tile.ApplyPlaybackState(media.PlaybackStates.Get(new MediaReference(video.SourceId, video.Value.Path)));
            }
        }

        // A video started since the page loaded joins the row, and one watched through leaves it.
        var sources = VideoSourceNames();
        var picks = ContinueWatchingPicks(sources);
        var shown = ContinueWatching
            .Select(tile => tile.SourcedVideo is { } video
                ? new MediaReference(video.SourceId, video.Value.Path)
                : default)
            .ToList();
        if (!shown.SequenceEqual(picks.Select(pick => pick.Video)))
        {
            ApplyContinueWatching(picks, sources);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        PageSearch.PropertyChanged -= OnPageSearchChanged;
        PageSearch.Close();
        playback.PropertyChanged -= OnPlaybackChanged;
        playback.QueueItems.CollectionChanged -= OnQueueChanged;
        playback.QueueItemsMetadataChanged -= OnQueueMetadataChanged;
        if (playbackHistory is not null)
        {
            playbackHistory.NewEntryAdded -= OnHistoryEntryAdded;
        }

        spotlightTimer?.Stop();
        spotlightTimer = null;
        SpotlightResetRequested?.Invoke();
        CancelAndDispose(ref queueFillCancellation);
        CancelAndDispose(ref albumPicksCancellation);
        CancelAndDispose(ref pageCancellation);
        disposeCancellation.Cancel();
        disposeCancellation.Dispose();

        foreach (var tile in Upcoming.Concat(RecentlyPlayed).Concat(AlbumPicks))
        {
            tile.ReleaseArt();
        }

        foreach (var tile in ContinueWatching.Concat(RecentlyAdded))
        {
            tile.ReleasePreview();
        }

        foreach (var slot in spotlightSlots)
        {
            slot.Release();
        }

        ReleaseNowPlayingSlide();
    }

    /// <summary>Keeps spotlight covers with the layout that has room for them.</summary>
    public void SetSpotlightNarrow(bool value)
    {
        if (isSpotlightNarrow == value)
        {
            return;
        }

        isSpotlightNarrow = value;
        if (nowPlayingSlide is not null)
        {
            nowPlayingSlide.ShowCover = !value;
        }

        foreach (var slot in spotlightSlots)
        {
            slot.Slide.ShowCover = slot.Item.HasCover && !value;
        }
    }

    private void RefreshContent() =>
        HasContent = ShowSpotlight || HasUpcoming || HasRecentlyPlayed || HasContinueWatching ||
            HasRecentlyAdded || HasAlbumPicks;

    private void ApplyFailures(IReadOnlyList<UnifiedSourceFailure> failures) =>
        PartialFailureText = failures.Count > 0
            ? string.Join(
                Environment.NewLine,
                failures.Select(failure => $"{failure.SourceName}: {failure.Message}").Distinct())
            : string.Empty;

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
    }

    private static void Shuffle<T>(IList<T> values)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var swap = Random.Shared.Next(i + 1);
            (values[i], values[swap]) = (values[swap], values[i]);
        }
    }

    // ---- Now playing and the music queue ------------------------------------

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackController.CurrentItem))
        {
            RefreshNowPlaying();
        }
        else if (e.PropertyName == nameof(PlaybackController.State))
        {
            if (playback.State == UiPlaybackState.Playing && nowPlayingSlide is not null)
            {
                ResetSpotlight();
            }

            UpdateSpotlightTimer();
        }
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshUpcoming();

    private void OnQueueMetadataChanged() => RefreshUpcoming();

    private void RefreshNowPlaying()
    {
        var item = playback.CurrentItem;
        var version = ++nowPlayingArtVersion;

        if (item is not null)
        {
            nowPlayingSlide ??= new SpotlightSlideModel
            {
                OpenCommand = new RelayCommand(callbacks.ShowNowPlaying),
            };

            nowPlayingSlide.Header = Localized(nameof(Strings.Resources.QueuePane_NowPlayingHeader), "NOW PLAYING");
            nowPlayingSlide.Title = item.Title;
            nowPlayingSlide.Subtitle = item.Artist;
            nowPlayingSlide.Meta = item.Album;
            nowPlayingSlide.ShowCover = !isSpotlightNarrow;
            nowPlayingSlide.ContextItem = CreateUpcomingTile(item);
            var slide = nowPlayingSlide;
            _ = AppErrorReporter.GuardAsync(() => LoadNowPlayingArtAsync(item, slide, version));
        }
        else
        {
            ReleaseNowPlayingSlide();
        }

        RefreshSpotlightSlides();
        RefreshUpcoming();
    }

    private void ReleaseNowPlayingSlide()
    {
        nowPlayingSlide?.ReleaseImages();
        nowPlayingSlide = null;
    }

    private async Task LoadNowPlayingArtAsync(MusicPlaybackItemModel item, SpotlightSlideModel slide, int version)
    {
        var artwork = await albumArt.GetHighQualityTrackArtWithBackdropAsync(item.Media, BackdropDecodeWidth, BackdropBlurSigma)
            .ConfigureAwait(false);
        var cover = artwork?.Cover;
        var backdrop = artwork?.Backdrop;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => ApplyNowPlayingArt(item, slide, cover, backdrop, version));
            return;
        }

        ApplyNowPlayingArt(item, slide, cover, backdrop, version);
    }

    private void ApplyNowPlayingArt(
        MusicPlaybackItemModel item,
        SpotlightSlideModel slide,
        Bitmap? cover,
        Bitmap? backdrop,
        int version)
    {
        if (disposed || version != nowPlayingArtVersion ||
            !ReferenceEquals(nowPlayingSlide, slide) || !ReferenceEquals(playback.CurrentItem, item))
        {
            cover?.Dispose();
            backdrop?.Dispose();
            return;
        }

        slide.ArtworkKey = cover is not null ? item.Media : null;
        slide.Cover = cover;
        slide.Backdrop = backdrop;
        if (!spotlightActive || !ReferenceEquals(SpotlightCurrent, slide))
        {
            slide.ReleasePreviousBackdrop();
        }
    }

    private void RefreshUpcoming()
    {
        var current = playback.CurrentItem;
        var start = current is null ? 0 : playback.QueueItems.IndexOf(current) + 1;
        upcomingRow.Update(playback.QueueItems.Skip(start).Take(UpcomingLimit));
        HasUpcoming = Upcoming.Count > 0;
        RefreshContent();
    }

    private LibraryTileModel CreateUpcomingTile(MusicPlaybackItemModel item)
    {
        var tile = new LibraryTileModel(item.Title, item.Artist, ThumbnailKind.Album)
        {
            EnableTileActions = false,
        };
        tile.OpenCommand = new RelayCommand(() => playback.PlayQueueItem(item));
        tile.PlayCommand = new RelayCommand(() => playback.PlayQueueItem(item));
        tile.AddToQueueCommand = new RelayCommand(() => playback.Enqueue(item));
        tile.Track = CreateTrackRow(item);
        tile.ArtLoader = (t, token) => LoadTileArtAsync(t, item.Media, token);
        return tile;
    }

    private void OnHistoryEntryAdded(object? sender, MusicPlaybackItemModel item) =>
        Dispatcher.UIThread.Post(() => _ = AppErrorReporter.GuardAsync(LoadRecentlyPlayedAsync));

    private async Task LoadRecentlyPlayedAsync()
    {
        if (disposed)
        {
            return;
        }

        var version = ++recentHistoryVersion;
        var items = playbackHistory is null
            ? []
            : await playbackHistory.GetHistoryAsync(IPlaybackHistoryService.HistoryEntryLimit);
        if (disposed || version != recentHistoryVersion)
        {
            return;
        }

        if (PlatformServices.NativeMediaLibrary?.SupportsMusic == true && PlatformServices.LibrarySources is { } sources)
        {
            var enabled = sources.CreateSnapshot(LibraryMediaCategory.Music).Sources.Select(source => source.Source.Id).ToHashSet();
            items = items.Where(item => enabled.Contains(item.SourceId)).ToArray();
        }

        recentlyPlayedRow.Update(items);
        HasRecentlyPlayed = RecentlyPlayed.Count > 0;
        RefreshContent();
    }

    private LibraryTileModel CreateRecentlyPlayedTile(MusicPlaybackItemModel item)
    {
        var tile = new LibraryTileModel(item.Title, item.Artist, ThumbnailKind.Album)
        {
            EnableTileActions = false,
        };
        tile.OpenCommand = new RelayCommand(() => playback.PlayRecentlyPlayed(item));
        tile.PlayCommand = new RelayCommand(() => playback.PlayRecentlyPlayed(item));
        tile.AddToQueueCommand = new RelayCommand(() => playback.Enqueue(item));
        tile.Track = CreateTrackRow(item);
        tile.ArtLoader = (t, token) => LoadTileArtAsync(t, item.Media, token);
        return tile;
    }

    // Builds the per-track model behind a tile's context menu and drag payload, reusing the shell's
    // track actions exactly as the track rows do.
    private TrackRowModel CreateTrackRow(MusicPlaybackItemModel item) =>
        new(
            mediaFileId: 0,
            filePath: item.FilePath,
            title: item.Title,
            artist: item.Artist,
            album: item.Album,
            year: string.Empty,
            genre: string.Empty,
            duration: LibrarySortKeys.FormatDuration(item.Duration),
            durationTime: item.Duration,
            discNumber: 0,
            trackNumber: 0,
            dateAdded: default,
            callbacks: trackActions,
            startTimeMs: item.StartTimeMs,
            sourceId: item.SourceId);

    private async Task LoadTileArtAsync(
        LibraryTileModel tile,
        MediaReference media,
        CancellationToken cancellationToken)
    {
        var image = await albumArt
            .GetHighQualityTrackArtAsync(media, cancellationToken)
            .ConfigureAwait(false);
        if (image is not null)
        {
            AlbumArtService.ApplyOnUiThread(image, art => tile.Image = art, cancellationToken);
        }
    }

    // ---- Album picks --------------------------------------------------------

    /// <summary>
    /// Draws albums out of a shuffled slice of the track library rather than out of a page of the
    /// album list: one bounded query samples the whole library, where the album list is alphabetical
    /// and would hand back the same first letters every time.
    /// </summary>
    private async Task LoadAlbumPicksAsync(
        long seed,
        List<UnifiedSourceFailure> failures,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await libraryQuery.GetShuffledMediaFilesPageAsync(
                new ShuffledMediaFilePageRequest(seed),
                cancellationToken);
            failures.AddRange(page.SourceFailures ?? []);

            var seen = new HashSet<(LibrarySourceId SourceId, string Album, string AlbumArtist, string Group)>();
            var picks = new List<AlbumPick>();
            foreach (var file in page.Items)
            {
                var album = file.Value.Album ?? string.Empty;
                var albumArtist = file.Value.AlbumArtist ?? string.Empty;
                if (!seen.Add((file.SourceId, album, albumArtist, file.Value.AlbumGroupingKey ?? string.Empty)))
                {
                    continue;
                }

                picks.Add(new AlbumPick(
                    file.SourceId,
                    album,
                    albumArtist,
                    file.Value.AlbumGroupingKey,
                    albumArtist.Length > 0 ? albumArtist : file.Value.Artist ?? string.Empty,
                    new MediaReference(file.SourceId, file.Value.Path ?? string.Empty)));
                if (picks.Count >= AlbumPickLimit)
                {
                    break;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            albumPicks.Clear();
            albumPicks.AddRange(picks);
            ReplaceTiles(AlbumPicks, picks.Select(CreateAlbumPickTile).ToList());
            HasAlbumPicks = AlbumPicks.Count > 0;
            RefreshContent();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failures.Add(new UnifiedSourceFailure(
                LibrarySourceId.Local,
                Localized(nameof(Strings.Resources.Nav_Albums), "Albums"),
                exception.Message,
                Retryable: true));
        }
    }

    private async Task RefreshAlbumPicksAsync()
    {
        if (IsRefreshingAlbumPicks || disposed)
        {
            return;
        }

        CancelAndDispose(ref albumPicksCancellation);
        albumPicksCancellation = CancellationTokenSource.CreateLinkedTokenSource(disposeCancellation.Token);
        albumShuffleSeed = Random.Shared.NextInt64();

        var failures = new List<UnifiedSourceFailure>();
        IsRefreshingAlbumPicks = true;
        try
        {
            await LoadAlbumPicksAsync(albumShuffleSeed, failures, albumPicksCancellation.Token);
        }
        finally
        {
            IsRefreshingAlbumPicks = false;
        }

        ApplyFailures(failures);
    }

    private LibraryTileModel CreateAlbumPickTile(AlbumPick pick)
    {
        var title = LibrarySortKeys.Fallback(pick.Album, Strings.Resources.Common_UnknownAlbum);
        var artist = LibrarySortKeys.Fallback(pick.DisplayArtist, Strings.Resources.Common_UnknownArtist);
        var identifier = new LibraryItemReference
        {
            Type = MediaLibraryItemIdentifier.ItemType.Album,
            SourceId = pick.SourceId,
            ArtistName = pick.AlbumArtist,
            AlbumName = pick.Album,
            AlbumGroupingKey = pick.GroupingKey,
        };
        var tile = new LibraryTileModel(title, artist, ThumbnailKind.Album)
        {
            ArtistName = artist,
            Media = pick.Art,
            Identifier = identifier,
            OpenAlbumCommand = new RelayCommand(() => callbacks.OpenAlbum(identifier)),
        };
        tile.OpenCommand = new RelayCommand(() => _ = PlayAlbumPicksFromAsync(pick));
        tile.PlayCommand = new RelayCommand(() => _ = PlayAlbumPicksFromAsync(pick));
        tile.PlayNextCommand = new RelayCommand(() => callbacks.AlbumTileActions?.PlayNext?.Invoke(tile));
        tile.AddToQueueCommand = new RelayCommand(() => _ = EnqueueAlbumAsync(pick));
        tile.AddToPlaylistCommand = new RelayCommand(() => callbacks.AlbumTileActions?.AddToPlaylist?.Invoke(tile));
        tile.OpenArtistCommand = new RelayCommand(() => callbacks.AlbumTileActions?.OpenArtist?.Invoke(tile));
        tile.OpenFolderCommand = new RelayCommand(() => callbacks.AlbumTileActions?.OpenContainingFolder?.Invoke(tile));
        tile.ArtLoader = (t, token) => LoadTileArtAsync(t, pick.Art, token);
        return tile;
    }

    /// <summary>
    /// Queues every album the row is showing, in the order it shows them, and starts at the one that
    /// was picked. The row then plays as one listening session where the albums either side of the
    /// pick are still a track step away.
    /// </summary>
    private async Task PlayAlbumPicksFromAsync(AlbumPick pick)
    {
        CancelAndDispose(ref queueFillCancellation);
        queueFillCancellation = CancellationTokenSource.CreateLinkedTokenSource(disposeCancellation.Token);
        var cancellationToken = queueFillCancellation.Token;

        // Sampled up front: a refresh part-way through would otherwise draw the rest of the session
        // from a row the user is no longer looking at.
        var picks = albumPicks.ToList();
        var tracks = new List<MusicPlaybackItemModel>();
        var startIndex = 0;

        for (var i = 0; i < picks.Count; i++)
        {
            if (ReferenceEquals(picks[i], pick))
            {
                startIndex = tracks.Count;
            }

            try
            {
                tracks.AddRange(await ResolveAlbumAsync(picks[i], cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // An album whose tracks cannot be read drops out of the session; the rest still plays.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        if (tracks.Count > 0)
        {
            playback.PlayAllFrom(tracks, startIndex);
        }
    }

    private async Task EnqueueAlbumAsync(AlbumPick pick)
    {
        try
        {
            var tracks = await ResolveAlbumAsync(pick, disposeCancellation.Token);
            if (tracks.Count > 0)
            {
                playback.EnqueueRange(tracks);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Same as playing a pick: leave the queue untouched when the album cannot be read.
        }
    }

    private async Task<List<MusicPlaybackItemModel>> ResolveAlbumAsync(
        AlbumPick pick,
        CancellationToken cancellationToken)
    {
        var tracks = await libraryQuery.GetAlbumTracksAsync(
            pick.SourceId,
            pick.Album,
            pick.AlbumArtist,
            cancellationToken,
            albumGroupingKey: pick.GroupingKey);

        // Disc and track numbers are stored as text, so the library hands them back with 10 ahead
        // of 2. Ordered here the way the album page and the library's own album actions order them.
        return tracks
            .OrderBy(track => LibrarySortKeys.ParseLeadingInt(track.Value.DiscNumber))
            .ThenBy(track => LibrarySortKeys.ParseLeadingInt(track.Value.TrackNumber))
            .Select(ToPlaybackItem)
            .ToList();
    }

    private static MusicPlaybackItemModel ToPlaybackItem(Sourced<DbMediaFile> file) => new(
        LibrarySortKeys.Fallback(file.Value.Title, DeriveTitle(file.Value.Path)),
        file.Value.Artist ?? string.Empty,
        file.Value.Album ?? string.Empty,
        file.Value.Duration,
        file.Value.Path ?? string.Empty,
        file.Value.StartTime,
        file.SourceId);

    private static string DeriveTitle(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? Strings.Resources.Common_UnknownTitle
            : Path.GetFileNameWithoutExtension(path);

    private sealed record AlbumPick(
        LibrarySourceId SourceId,
        string Album,
        string AlbumArtist,
        string? GroupingKey,
        string DisplayArtist,
        MediaReference Art);

    // ---- Video and picture rows ---------------------------------------------

    private async Task LoadMediaRowsAsync(
        List<UnifiedSourceFailure> failures,
        CancellationToken cancellationToken)
    {
        if (media is null)
        {
            return;
        }

        var sources = VideoSourceNames();

        ApplyContinueWatching(ContinueWatchingPicks(sources), sources);
        await LoadRecentlyAddedAsync(sources, failures, cancellationToken);
    }

    private Dictionary<LibrarySourceId, string> VideoSourceNames() =>
        media!.Sources.CreateSnapshot(LibraryMediaCategory.Video).Sources
            .ToDictionary(item => item.Source.Id, item => item.Source.DisplayName);

    /// <summary>
    /// The videos left part-way through, newest first. Read straight out of the playback state
    /// store: it records enough to draw the tile and to resume, which keeps the row free of a
    /// library lookup per item over what may be a remote library.
    /// </summary>
    private IReadOnlyList<VideoPlaybackProgress> ContinueWatchingPicks(
        IReadOnlyDictionary<LibrarySourceId, string> sources) =>
        media!.PlaybackStates.GetInProgress(MediaRowLimit * 2)
            // A source that is no longer configured has nothing left to open.
            .Where(progress => sources.ContainsKey(progress.Video.SourceId))
            .Take(MediaRowLimit)
            .ToList();

    private void ApplyContinueWatching(
        IReadOnlyList<VideoPlaybackProgress> picks,
        IReadOnlyDictionary<LibrarySourceId, string> sources)
    {
        ReplaceTiles(
            ContinueWatching,
            picks
                .Select(progress => CreateVideoTile(
                    StandInVideo(progress),
                    SourceName(sources, progress.Video.SourceId),
                    resumeOnOpen: true))
                .ToList());
        HasContinueWatching = ContinueWatching.Count > 0;
        RefreshContent();
    }

    private async Task LoadRecentlyAddedAsync(
        IReadOnlyDictionary<LibrarySourceId, string> sources,
        List<UnifiedSourceFailure> failures,
        CancellationToken cancellationToken)
    {
        var picks = await media!.Query.GetVideoPicksAsync(
            VideoPickOrder.RecentlyAdded,
            MediaRowLimit,
            cancellationToken);
        failures.AddRange(picks.SourceFailures);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        ReplaceTiles(
            RecentlyAdded,
            picks.Items
                .Select(video => CreateVideoTile(video, SourceName(sources, video.SourceId), resumeOnOpen: false))
                .ToList());
        HasRecentlyAdded = RecentlyAdded.Count > 0;
        RefreshContent();
    }

    private static string SourceName(
        IReadOnlyDictionary<LibrarySourceId, string> sources,
        LibrarySourceId sourceId) =>
        sources.TryGetValue(sourceId, out var name) ? name : sourceId.Value;

    private VideoTileModel CreateVideoTile(Sourced<DbVideoFile> video, string sourceName, bool resumeOnOpen)
    {
        var services = media!;
        var reference = new MediaReference(video.SourceId, video.Value.Path);
        var tile = VideoTileModel.ForVideo(video, FormatVideoSubtitle(video.Value));
        tile.OpenCommand = new RelayCommand(() => _ = OpenVideoAsync(video, resumeOnOpen));
        tile.ResumeCommand = new RelayCommand(() => _ = OpenVideoAsync(video, resume: true));
        tile.ApplyPlaybackState(services.PlaybackStates.Get(reference));
        tile.PreviewLoader = async (model, token) =>
        {
            var bitmap = await services.Previews.GetPreviewAsync(reference, token).ConfigureAwait(false);
            if (bitmap is not null)
            {
                AlbumArtService.ApplyOnUiThread(bitmap, loaded => model.Preview = loaded, token);
            }
        };

        // These rows span folders, so the way back into the library has to be offered explicitly;
        // inside a folder page the tile is already at its location.
        tile.GoToLocationCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(async () =>
        {
            var folder = await VideoFolderPaths.GetLocationAsync(
                reference, services.Sources, video.Value.FolderPath, video.Value.MediaKind != VideoMediaKind.File);
            if (folder is not null)
            {
                services.Callbacks.OpenFolder(folder);
            }
        }));

        if (services.Callbacks.ShowProperties is { } showProperties)
        {
            tile.PropertiesCommand = new RelayCommand(() => showProperties(video));
        }

        if (services.Callbacks.OpenContainingFolder is { } openContainingFolder)
        {
            tile.OpenFolderCommand = new RelayCommand(() => openContainingFolder(video));
        }

        if (services.Callbacks.AddToPlaylist is { } addToPlaylist)
        {
            tile.AddToPlaylistCommand = new RelayCommand(() => addToPlaylist(video));
        }

        if (services.Callbacks.ClearSubtitleCache is { } clearSubtitleCache)
        {
            tile.ClearSubtitleCacheCommand = new RelayCommand(() => clearSubtitleCache(video));
        }

        tile.ClearPlaybackProgressCommand = new RelayCommand(() =>
        {
            services.Callbacks.ClearPlaybackProgress([video]);
            tile.ApplyPlaybackState(null);
        });

        tile.MarkAsWatchedCommand = new RelayCommand(() =>
        {
            services.Callbacks.MarkWatched([video]);
            tile.ApplyPlaybackState(services.PlaybackStates.Get(reference));
        });

        return tile;
    }

    /// <summary>
    /// A picture's thumbnail: decoded here when the picture is on this device, and asked of the
    /// server otherwise, since pulling a whole remote picture over just to shrink it would cost far
    /// more than the tile is worth.
    /// </summary>
    private Task<IImage?> PhotoThumbnailAsync(
        LibrarySourceId sourceId,
        string path,
        CancellationToken cancellationToken) =>
        sourceId.IsLocal
            ? media!.Photos.GetThumbnailAsync(path, cancellationToken)
            : media!.Previews.GetPreviewAsync(new MediaReference(sourceId, path), cancellationToken);

    /// <summary>
    /// Opens a video with the folder it lives in as its playlist. The listing is also what supplies
    /// the indexed record for a continue-watching tile, which is built from a path alone.
    /// </summary>
    private async Task OpenVideoAsync(Sourced<DbVideoFile> video, bool resume)
    {
        var services = media!;
        IReadOnlyList<Sourced<DbVideoFile>> siblings = [video];
        try
        {
            siblings = await services.Query.GetVideosInFolderAsync(
                video.SourceId,
                video.Value.FolderPath,
                disposeCancellation.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The folder can no longer be listed; the video is still there to open on its own.
        }

        var indexed = siblings.FirstOrDefault(candidate => string.Equals(
            candidate.Value.Path,
            video.Value.Path,
            StringComparison.OrdinalIgnoreCase)) ?? video;
        services.History?.RecordVideo(video.SourceId, video.Value.Path);
        services.Callbacks.OpenVideo(indexed, siblings, resume);
    }

    /// <summary>
    /// The record behind a continue-watching tile. Progress is stored by path, so the path is all
    /// there is to build one from until the video is opened and its folder listed.
    /// </summary>
    private static Sourced<DbVideoFile> StandInVideo(VideoPlaybackProgress progress) => new(
        progress.Video.SourceId,
        new DbVideoFile
        {
            Path = progress.Video.Path,
            FolderPath = VideoFolderPaths.ParentOf(progress.Video.Path) ?? string.Empty,
            FileName = VideoFolderTree.LeafName(progress.Video.Path),
            Duration = ToDuration(progress.State.DurationSeconds),
        });

    private static TimeSpan ToDuration(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;

    private static string FormatVideoSubtitle(DbVideoFile video)
    {
        var duration = video.Duration > TimeSpan.Zero
            ? video.Duration.TotalHours >= 1
                ? video.Duration.ToString(@"h\:mm\:ss")
                : video.Duration.ToString(@"m\:ss")
            : string.Empty;
        var resolution = video.Height > 0 ? $"{video.Width}×{video.Height}" : string.Empty;
        return string.Join("  ·  ", new[] { duration, resolution }.Where(part => part.Length > 0));
    }

    // ---- Spotlight -----------------------------------------------------------

    /// <summary>
    /// Fills the library spotlight pool with random albums and videos, plus the pictures the user
    /// has opened. Pictures are never indexed, so the ones already
    /// visited are the only ones the page can name without walking the library for them.
    /// </summary>
    private async Task LoadSpotlightAsync(
        List<UnifiedSourceFailure> failures,
        CancellationToken cancellationToken)
    {
        var items = new List<SpotlightItem>();
        foreach (var pick in albumPicks.Take(SpotlightLimit))
        {
            var identifier = new LibraryItemReference
            {
                Type = MediaLibraryItemIdentifier.ItemType.Album,
                SourceId = pick.SourceId,
                ArtistName = pick.AlbumArtist,
                AlbumName = pick.Album,
                AlbumGroupingKey = pick.GroupingKey,
            };
            var tile = new LibraryTileModel(
                LibrarySortKeys.Fallback(pick.Album, Strings.Resources.Common_UnknownAlbum),
                LibrarySortKeys.Fallback(pick.DisplayArtist, Strings.Resources.Common_UnknownArtist),
                ThumbnailKind.Album)
            {
                Media = pick.Art,
                Identifier = identifier,
                OpenAlbumCommand = new RelayCommand(() => callbacks.OpenAlbum(identifier)),
            };
            tile.PlayCommand = new AsyncRelayCommand(async () =>
            {
                var tracks = await ResolveAlbumAsync(pick, disposeCancellation.Token);
                if (tracks.Count > 0)
                {
                    playback.PlayAllFrom(tracks, 0);
                }
            });
            tile.PlayNextCommand = new RelayCommand(() => callbacks.AlbumTileActions?.PlayNext?.Invoke(tile));
            tile.AddToQueueCommand = new RelayCommand(() => _ = EnqueueAlbumAsync(pick));
            tile.AddToPlaylistCommand = new RelayCommand(() => callbacks.AlbumTileActions?.AddToPlaylist?.Invoke(tile));
            tile.OpenArtistCommand = new RelayCommand(() => callbacks.AlbumTileActions?.OpenArtist?.Invoke(tile));
            tile.OpenFolderCommand = new RelayCommand(() => callbacks.AlbumTileActions?.OpenContainingFolder?.Invoke(tile));
            items.Add(new SpotlightItem(
                tile.Title,
                tile.Subtitle,
                token => LoadAlbumSpotlightImagesAsync(pick.Art, token),
                tile.PlayCommand,
                tile,
                HasCover: true));
        }

        if (media is { } services)
        {
            var picks = await services.Query.GetVideoPicksAsync(VideoPickOrder.Random, SpotlightLimit, cancellationToken);
            failures.AddRange(picks.SourceFailures);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            foreach (var video in picks.Items)
            {
                var reference = new MediaReference(video.SourceId, video.Value.Path);
                var tile = CreateVideoTile(video, string.Empty, resumeOnOpen: false);
                items.Add(new SpotlightItem(
                    video.Value.DisplayName ?? string.Empty,
                    FormatVideoSubtitle(video.Value),
                    async token => (null, await services.Previews.GetPreviewAsync(reference, token).ConfigureAwait(false)),
                    tile.OpenCommand!,
                    tile));
            }

            var photos = SpotlightPhotos(VideoSourceNames());
            foreach (var photo in photos)
            {
                var tile = VideoTileModel.ForPhoto(photo);
                tile.OpenCommand = new RelayCommand(() =>
                {
                    services.History?.RecordPhoto(photo.Source, photo.Path);
                    services.Callbacks.OpenPhoto?.Invoke(photo, photos);
                });
                if (services.Callbacks.ShowPhotoProperties is { } showProperties)
                {
                    tile.PropertiesCommand = new RelayCommand(() => showProperties(photo));
                }

                if (photo.IsLocal && services.Callbacks.OpenPhotoContainingFolder is { } openContainingFolder)
                {
                    tile.OpenFolderCommand = new RelayCommand(() => openContainingFolder(photo));
                }

                tile.GoToLocationCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(async () =>
                {
                    var folder = await VideoFolderPaths.GetLocationAsync(
                        new MediaReference(photo.Source, photo.Path), services.Sources);
                    if (folder is not null)
                    {
                        services.Callbacks.OpenFolder(folder);
                    }
                }));
                items.Add(new SpotlightItem(
                    photo.FileName,
                    photo.DisplaySize,
                    async token => (null, await PhotoThumbnailAsync(photo.Source, photo.Path, token).ConfigureAwait(false)),
                    tile.OpenCommand,
                    tile));
            }
        }

        Shuffle(items);
        ReplaceSpotlightSlots(items.Take(SpotlightLimit).ToList());

        RefreshSpotlightSlides();
        RefreshContent();
    }

    private async Task<(IImage? Cover, IImage? Backdrop)> LoadAlbumSpotlightImagesAsync(
        MediaReference art,
        CancellationToken cancellationToken)
    {
        var artwork = await albumArt.GetHighQualityTrackArtWithBackdropAsync(
            art, BackdropDecodeWidth, BackdropBlurSigma, cancellationToken).ConfigureAwait(false);
        if (artwork is not null && cancellationToken.IsCancellationRequested)
        {
            artwork.Dispose();
            return (null, null);
        }

        return (artwork?.Cover, artwork?.Backdrop);
    }

    private IReadOnlyList<PhotoFileReference> SpotlightPhotos(
        IReadOnlyDictionary<LibrarySourceId, string> sources)
    {
        var services = media!;
        if (services.Callbacks.OpenPhoto is null || services.History is null)
        {
            return [];
        }

        var photos = new List<PhotoFileReference>();
        foreach (var entry in services.History.GetEntries())
        {
            if (photos.Count >= SpotlightLimit)
            {
                break;
            }

            // A picture that has left the disk since it was opened has nothing left to show.
            if (entry.Kind == VideoHistoryEntryKind.Photo &&
                sources.ContainsKey(entry.Source) &&
                services.Photos.Describe(entry.Source, entry.Path) is { } photo)
            {
                photos.Add(photo);
            }
        }

        return photos;
    }

    private void ReplaceSpotlightSlots(IReadOnlyList<SpotlightItem> items)
    {
        foreach (var slot in spotlightSlots)
        {
            slot.Release();
        }

        spotlightSlots.Clear();
        var header = Localized(nameof(Strings.Resources.Home_Spotlight), "From your library");
        foreach (var item in items)
        {
            spotlightSlots.Add(new SpotlightSlot(
                item,
                new SpotlightSlideModel
                {
                    Header = header,
                    Title = item.Title,
                    Subtitle = item.Subtitle,
                    ShowCover = item.HasCover && !isSpotlightNarrow,
                    OpenCommand = item.Open,
                    ContextItem = item.ContextItem,
                }));
        }
    }

    private void RefreshSpotlightSlides()
    {
        var slides = spotlightSlots.Select(slot => slot.Slide);
        var values = (nowPlayingSlide is { } slide ? slides.Prepend(slide) : slides).ToList();
        if (!SpotlightSlides.SequenceEqual(values))
        {
            SpotlightSlides.ReplaceAll(values);
        }

        ResetSpotlight();
        ShowSpotlight = SpotlightSlides.Count > 0;
        UpdateSpotlightTimer();
    }

    private void ResetSpotlight()
    {
        SpotlightResetRequested?.Invoke();
        spotlightIndex = 0;
        UpdateSpotlightWindow();
    }

    /// <summary>
    /// Points the three drawn cards at the item in the middle and the two either side, and keeps
    /// artwork only for those: the card can show one of them at a time, and the rest of the pool
    /// would be that many decoded frames held for nothing.
    /// </summary>
    private void UpdateSpotlightWindow()
    {
        var count = SpotlightSlides.Count;
        if (count == 0)
        {
            spotlightIndex = 0;
            SpotlightCurrent = null;
            SpotlightPrevious = null;
            SpotlightNext = null;
            HasSpotlightSteps = false;
        }
        else
        {
            spotlightIndex = ((spotlightIndex % count) + count) % count;
            SpotlightCurrent = SpotlightSlides[spotlightIndex];
            SpotlightPrevious = count > 1 ? SpotlightSlides[(spotlightIndex + count - 1) % count] : null;
            SpotlightNext = count > 1 ? SpotlightSlides[(spotlightIndex + 1) % count] : null;
            HasSpotlightSteps = count > 1;

            for (var i = 0; i < count; i++)
            {
                SpotlightSlides[i].IsCurrent = i == spotlightIndex;
            }
        }

        foreach (var slot in spotlightSlots)
        {
            if (ReferenceEquals(slot.Slide, SpotlightCurrent) ||
                ReferenceEquals(slot.Slide, SpotlightPrevious) ||
                ReferenceEquals(slot.Slide, SpotlightNext))
            {
                RequestSpotlightImage(slot);
            }
            else
            {
                slot.Release();
            }
        }
    }

    private void RequestSpotlightImage(SpotlightSlot slot)
    {
        if (slot.Request is not null)
        {
            return;
        }

        var request = CancellationTokenSource.CreateLinkedTokenSource(disposeCancellation.Token);
        slot.Request = request;

        // The token is taken here rather than inside the load: the request behind it is canceled and
        // disposed the moment the card leaves the drawn window, and reading it again would throw.
        _ = LoadSpotlightImageAsync(slot, request.Token);
    }

    private async Task LoadSpotlightImageAsync(SpotlightSlot slot, CancellationToken cancellationToken)
    {
        try
        {
            var (cover, backdrop) = await slot.Item.LoadImages(cancellationToken).ConfigureAwait(false);
            if (cover is not null)
            {
                AlbumArtService.ApplyOnUiThread(
                    cover,
                    loaded => slot.Slide.Cover = loaded,
                    cancellationToken);
            }

            if (backdrop is not null)
            {
                AlbumArtService.ApplyOnUiThread(
                    backdrop,
                    loaded => slot.Slide.Backdrop = loaded,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Warning("Artwork.Load", "Spotlight images could not be loaded.", exception);
        }
    }

    private void UpdateSpotlightTimer()
    {
        if (!PageSearch.IsOpen && spotlightActive && playback.State != UiPlaybackState.Playing && SpotlightSlides.Count > 1)
        {
            spotlightTimer ??= new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = SpotlightInterval,
            };
            spotlightTimer.Tick -= OnSpotlightTick;
            spotlightTimer.Tick += OnSpotlightTick;
            spotlightTimer.Start();
            return;
        }

        spotlightTimer?.Stop();
    }

    private void OnSpotlightTick(object? sender, EventArgs e)
    {
        if (SpotlightSlides.Count < 2)
        {
            return;
        }

        // The page slides the next card in and takes the step once it lands; with no page listening
        // the step is taken straight away.
        if (SpotlightAdvanceRequested is { } advance)
        {
            advance(1);
        }
        else
        {
            StepSpotlight(1);
        }
    }

    private sealed class SpotlightSlot(SpotlightItem item, SpotlightSlideModel slide)
    {
        public SpotlightItem Item { get; } = item;

        public SpotlightSlideModel Slide { get; } = slide;

        /// <summary>The in-flight artwork load, kept so a second pass does not start another.</summary>
        public CancellationTokenSource? Request { get; set; }

        public void Release()
        {
            var request = Request;
            Request = null;
            request?.Cancel();
            request?.Dispose();
            Slide.ReleaseImages();
        }
    }

    private sealed record SpotlightItem(
        string Title,
        string Subtitle,
        Func<CancellationToken, Task<(IImage? Cover, IImage? Backdrop)>> LoadImages,
        IRelayCommand Open,
        object ContextItem,
        bool HasCover = false);

    private static void ReplaceTiles(
        BulkObservableCollection<LibraryTileModel> target,
        IReadOnlyList<LibraryTileModel> values)
    {
        var discarded = target.ToList();
        target.ReplaceAll(values);
        foreach (var tile in discarded)
        {
            tile.ReleaseArt();
        }
    }

    private static void ReplaceTiles(
        BulkObservableCollection<VideoTileModel> target,
        IReadOnlyList<VideoTileModel> values)
    {
        var discarded = target.ToList();
        target.ReplaceAll(values);
        foreach (var tile in discarded)
        {
            tile.ReleasePreview();
        }
    }
}
