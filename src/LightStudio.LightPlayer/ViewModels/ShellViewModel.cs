using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Animation;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Animations;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// Root view model for the application shell. Owns navigation, search, the
/// playback bar, the now-playing queue, the more-menu commands, and scan status.
/// </summary>
public partial class ShellViewModel : ViewModelBase
{
    private readonly IReadOnlyDictionary<AppRouteId, AppRoute> routesById;
    private readonly IShellStateStore stateStore;
    private readonly IAppSettingsStore appSettingsStore;
    private readonly IThemeVariantService themeVariantService;
    private readonly ILibraryLocationService libraryLocationService;
    private readonly ILibraryScanService? libraryScanService;
    private readonly PlaybackController playbackController;
    private readonly ISystemSampleRateProvider systemSampleRateProvider;
    private readonly PlaybackHistoryService? playbackHistoryService;
    private readonly IPlaylistService? playlistService;
    private readonly IPlaylistDialogService? playlistDialogService;
    private readonly IDialogService? dialogService;
    private readonly ILyricSourceService? lyricSourceService;
    private readonly LyricsService lyricsService;

    private TrackActionCallbacks trackActions = new();
    private TileActionCallbacks albumTileActions = new();
    private TileActionCallbacks artistTileActions = new();
    private LibraryCommands libraryCommands = new();
    private ILibraryBrowsePageViewModel? activeBrowsePage;
    private LibraryItemKind? activeBrowseKind;
    private bool isSyncingViewSettings;
    private readonly AlbumArtService albumArtService = new();
    private readonly Stack<(AppRouteId Id, object? Parameter, PageViewModelBase? Page)> backStack = new();
    private AppRouteId currentRouteId;
    private object? currentParameter;
    private DispatcherTimer? scanStatusClearTimer;
    private readonly DrillInPageTransition hierarchicalPageTransition = new();
    private readonly EntrancePageTransition rootPageTransition = new();

    [ObservableProperty]
    private PageViewModelBase currentPage = null!;

    /// <summary>
    /// True while the active navigation is a back navigation, so the page transition plays in
    /// reverse. Bound to <c>TransitioningContentControl.IsTransitionReversed</c>.
    /// </summary>
    [ObservableProperty]
    private bool isBackNavigation;

    /// <summary>
    /// Transition the content host plays for the active navigation. Hierarchical (drill) navigations
    /// use the scale + fade; root-to-root navigations use the lighter entrance slide-up + fade.
    /// Bound to <c>TransitioningContentControl.PageTransition</c>.
    /// </summary>
    [ObservableProperty]
    private IPageTransition currentPageTransition = null!;

    [ObservableProperty]
    private bool isScanActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanStatusText))]
    private string scanStatusText = string.Empty;

    [ObservableProperty]
    private bool hasWarnings;

    [ObservableProperty]
    private string warningText = string.Empty;

    [ObservableProperty]
    private string warningDetailsText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortByTitle))]
    [NotifyPropertyChangedFor(nameof(IsSortByAlbumName))]
    [NotifyPropertyChangedFor(nameof(IsSortByArtistName))]
    [NotifyPropertyChangedFor(nameof(IsSortByGenre))]
    [NotifyPropertyChangedFor(nameof(IsSortByYear))]
    [NotifyPropertyChangedFor(nameof(IsSortByDateAdded))]
    private LibrarySortOption selectedSort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortDescending))]
    private bool sortAscending = true;

    [ObservableProperty]
    private bool groupingEnabled;

    public ShellViewModel(
        IReadOnlyList<AppRoute> routes,
        IShellStateStore stateStore,
        IAppSettingsStore appSettingsStore,
        IThemeVariantService themeVariantService,
        ILibraryLocationService? libraryLocationService = null,
        ILibraryScanService? libraryScanService = null,
        IPlaylistService? playlistService = null,
        IPlaylistDialogService? playlistDialogService = null,
        IDialogService? dialogService = null,
        ILyricSourceService? lyricSourceService = null)
    {
        this.stateStore = stateStore;
        this.appSettingsStore = appSettingsStore;
        this.themeVariantService = themeVariantService;
        this.libraryLocationService = libraryLocationService ?? new AppSettingsLibraryLocationService(appSettingsStore);
        this.libraryScanService = libraryScanService;
        this.playlistService = playlistService;
        this.playlistDialogService = playlistDialogService;
        this.dialogService = dialogService;
        this.lyricSourceService = lyricSourceService;
        lyricsService = new LyricsService(lyricSourceService);

        Routes = routes;
        routesById = routes.ToDictionary(route => route.Id);
        SortOptions = LibrarySortOptions.All;
        selectedSort = SortOptions[0];

        Navigation = new NavigationViewModel(routes) { IsExpanded = stateStore.IsNavigationExpanded };
        Navigation.NavigationRequested += OnNavigationRequested;
        Navigation.PropertyChanged += OnNavigationPropertyChanged;

        Search = new SearchBoxViewModel(SearchLibrarySuggestionsAsync);
        Search.QuerySubmitted += OnQuerySubmitted;
        Search.SuggestionChosen += OnSuggestionChosen;

        // Publishes "now playing" / transport to the OS (MPRIS on Linux, no-op placeholders elsewhere).
        playbackController = new PlaybackController(SystemMediaControlsFactory.Create());
        systemSampleRateProvider = new OpenAlSystemSampleRateProvider();
        playbackController.PreferredSampleRate = appSettingsStore.PreferredSampleRate;
        playbackController.AlwaysResample = appSettingsStore.AlwaysResample;
        playbackController.SystemSampleRateProvider = systemSampleRateProvider;
        playbackController.AlbumArtPathResolver = albumArtService.GetTrackArtFilePathAsync;
        playbackController.PlaybackError += OnPlaybackError;
        playbackController.PersistRequested += PersistPlaybackQueue;
        playbackController.PropertyChanged += OnPlaybackControllerPropertyChanged;

        // Records played library tracks to the playback history shown on the home page.
        playbackHistoryService = new PlaybackHistoryService(appSettingsStore, () => IsScanActive);

        PlaybackBar = new PlaybackBarViewModel(playbackController, albumArtService, playlistService, playlistDialogService);
        PlaybackBar.OpenNowPlaying = ToggleNowPlaying;

        Queue = new QueuePaneViewModel(playbackController) { IsOpen = stateStore.IsQueuePaneOpen };
        Queue.PropertyChanged += OnQueuePropertyChanged;
        Queue.SaveAsPlaylist = SaveQueueAsPlaylistAsync;
        Queue.ResolveInsertItems = ResolveInsertPayloadAsync;
        Queue.BuildFileItems = BuildFileItemsWithMetadataAsync;

        ShuffleAllCommand = new AsyncRelayCommand(ShuffleAllAsync);
        RecentlyAddedCommand = new RelayCommand(RecentlyAdded);
        RefreshLibraryCommand = new AsyncRelayCommand(RefreshLibraryAsync, CanRefreshLibrary);
        AboutCommand = new RelayCommand(() => _ = dialogService?.ShowAboutAsync());
        ViewWarningsCommand = new RelayCommand(() => { });
        SetSortCommand = new RelayCommand<LibrarySortField>(SetSort);
        SortAscendingCommand = new RelayCommand(() => SortAscending = true);
        SortDescendingCommand = new RelayCommand(() => SortAscending = false);

        trackActions = new TrackActionCallbacks
        {
            Play = PlayTrack,
            PlayNext = InsertTrackNext,
            AddToQueue = AddTrackToQueue,
            AddToPlaylist = AddTrackToPlaylist,
            ShowProperties = track => _ = dialogService?.ShowPropertiesAsync(track.FilePath, track.Title),
            OpenContainingFolder = OpenTrackFolder,
        };

        albumTileActions = new TileActionCallbacks
        {
            Open = OpenAlbumTile,
            Play = PlayAlbumTile,
            AddToQueue = AddAlbumTileToQueue,
        };

        artistTileActions = new TileActionCallbacks
        {
            Open = OpenArtistTile,
            Play = PlayArtistTile,
            AddToQueue = AddArtistTileToQueue,
        };

        libraryCommands = new LibraryCommands
        {
            TrackActions = trackActions,
            AlbumTileActions = albumTileActions,
            ArtistTileActions = artistTileActions,
            PlayTracks = PlayTracks,
            ShuffleTracks = ShuffleTracks,
            AddTracksToQueue = AddTracksToQueue,
            AddTracksToPlaylist = AddTracksToPlaylist,
            OpenAlbum = id => Navigate(AppRouteId.AlbumDetail, id),
            OpenArtist = id => Navigate(AppRouteId.ArtistDetail, id),
            GoBack = GoBack,
        };

        RestorePlaybackQueue();

        themeVariantService.Apply(appSettingsStore.ThemePreference);
        SubscribeToScanEvents();
        Navigate(GetInitialRouteId());
    }

    public IReadOnlyList<AppRoute> Routes { get; }

    public NavigationViewModel Navigation { get; }

    public SearchBoxViewModel Search { get; }

    public PlaybackBarViewModel PlaybackBar { get; }

    public QueuePaneViewModel Queue { get; }

    public IReadOnlyList<LibrarySortOption> SortOptions { get; }

    public IRelayCommand ShuffleAllCommand { get; }

    public IRelayCommand RecentlyAddedCommand { get; }

    public IAsyncRelayCommand RefreshLibraryCommand { get; }

    public IRelayCommand AboutCommand { get; }

    public IRelayCommand ViewWarningsCommand { get; }

    public IRelayCommand<LibrarySortField> SetSortCommand { get; }

    public IRelayCommand SortAscendingCommand { get; }

    public IRelayCommand SortDescendingCommand { get; }

    public bool IsSortDescending => !SortAscending;

    public bool HasScanStatusText => !string.IsNullOrWhiteSpace(ScanStatusText);

    public bool IsSortByTitle => SelectedSort.Field == LibrarySortField.Title;

    public bool IsSortByAlbumName => SelectedSort.Field == LibrarySortField.AlbumName;

    public bool IsSortByArtistName => SelectedSort.Field == LibrarySortField.ArtistName;

    public bool IsSortByGenre => SelectedSort.Field == LibrarySortField.Genre;

    public bool IsSortByYear => SelectedSort.Field == LibrarySortField.Year;

    public bool IsSortByDateAdded => SelectedSort.Field == LibrarySortField.DateAdded;

    public bool CanSortByTitle => IsSortFieldVisible(LibrarySortField.Title);

    public bool CanSortByAlbumName => IsSortFieldVisible(LibrarySortField.AlbumName);

    public bool CanSortByArtistName => IsSortFieldVisible(LibrarySortField.ArtistName);

    public bool CanSortByGenre => IsSortFieldVisible(LibrarySortField.Genre);

    public bool CanSortByYear => IsSortFieldVisible(LibrarySortField.Year);

    public bool CanSortByDateAdded => IsSortFieldVisible(LibrarySortField.DateAdded);

    private bool IsSortFieldVisible(LibrarySortField field) =>
        activeBrowseKind is null || LibraryViewSettings.SupportedSorts(activeBrowseKind.Value).Contains(field);

    public void Navigate(AppRouteId routeId, object? parameter = null) =>
        NavigateInternal(routeId, parameter, recordHistory: true);

    private void NavigateInternal(AppRouteId routeId, object? parameter, bool recordHistory)
    {
        if (!routesById.TryGetValue(routeId, out var route))
        {
            throw new ArgumentOutOfRangeException(nameof(routeId), routeId, "Route is not registered.");
        }

        if (route.NavigationSlot == AppNavigationSlot.Primary)
        {
            appSettingsStore.LastUsedRouteId = routeId;
        }

        // Keep browse pages (music/albums/artists) alive on the back stack so returning to them
        // restores their already-loaded items and scroll position instead of rebuilding and
        // re-querying the library. Other pages are cheap to recreate, so only their route is kept.
        PageViewModelBase? retained = null;
        if (recordHistory && CurrentPage is not null)
        {
            retained = CurrentPage is ILibraryBrowsePageViewModel ? CurrentPage : null;
            backStack.Push((currentRouteId, currentParameter, retained));
        }

        ShowPage(routeId, parameter, CreatePage(route, parameter), isBack: false, retained);
    }

    private void GoBack()
    {
        if (backStack.Count == 0)
        {
            return;
        }

        var entry = backStack.Pop();
        // Reuse the cached browse page when one was kept; otherwise rebuild it from the stored route.
        var page = entry.Page ?? CreatePage(routesById[entry.Id], entry.Parameter);
        ShowPage(entry.Id, entry.Parameter, page, isBack: true, retained: null);
    }

    private void ShowPage(AppRouteId routeId, object? parameter, PageViewModelBase page, bool isBack, PageViewModelBase? retained)
    {
        // Choose the transition from the navigation's hierarchy relationship before currentRouteId is
        // overwritten: drill in/out whenever either endpoint is a hierarchical (detail / now-playing)
        // page, otherwise the lighter entrance slide-up used between root pages. Mirrors the original
        // app's DrillInNavigationTransitionInfo vs. the default NavigationView entrance transition.
        var isHierarchical = (CurrentPage is not null && IsHierarchicalRoute(currentRouteId))
                             || IsHierarchicalRoute(routeId);

        currentRouteId = routeId;
        currentParameter = parameter;

        var previous = CurrentPage;
        // Back navigation plays the transition in reverse. Both transition flags must be set before
        // CurrentPage so the content host picks them up for this swap.
        IsBackNavigation = isBack;
        CurrentPageTransition = isHierarchical ? hierarchicalPageTransition : rootPageTransition;
        CurrentPage = page;
        // Dispose the page we navigated away from unless it was retained on the back stack for a
        // later return (retained pages are reused, so disposing them would tear down live state).
        if (!ReferenceEquals(previous, retained))
        {
            (previous as IDisposable)?.Dispose();
        }

        SetActiveBrowsePage(page as ILibraryBrowsePageViewModel);
        Navigation.SetActive(routeId);
    }

    /// <summary>
    /// Pages that sit below a root page in the navigation hierarchy. Navigating to or from one of
    /// these plays the drill-in/out transition; every other navigation is treated as a root switch.
    /// </summary>
    private bool IsHierarchicalRoute(AppRouteId routeId) =>
        routeId == AppRouteId.NowPlaying
        || (routesById.TryGetValue(routeId, out var route) && route.Kind == AppRouteKind.Detail);

    private PageViewModelBase CreatePage(AppRoute route, object? parameter)
    {
        if (route.Id == AppRouteId.Settings)
        {
            return new SettingsPageViewModel(
                appSettingsStore,
                themeVariantService,
                RefreshLibraryCommand,
                lyricSourceService,
                rate => playbackController.PreferredSampleRate = rate,
                alwaysResample => playbackController.AlwaysResample = alwaysResample,
                systemSampleRateProvider,
                playbackHistoryService is null ? null : playbackHistoryService.ClearHistoryAsync);
        }

        return route.Id switch
        {
            AppRouteId.Songs => CreateBrowsePage(new SongsPageViewModel(trackActions)),
            AppRouteId.Albums => CreateBrowsePage(new AlbumsPageViewModel(albumTileActions, albumArtService)),
            AppRouteId.Artists => CreateBrowsePage(new ArtistsPageViewModel(artistTileActions, albumArtService)),
            AppRouteId.AlbumDetail => CreateAlbumDetailPage(parameter, route),
            AppRouteId.ArtistDetail => CreateArtistDetailPage(parameter, route),
            AppRouteId.Search => CreateSearchPage(parameter),
            AppRouteId.Playlists => CreatePlaylistsPage(route),
            AppRouteId.PlaylistDetail => CreatePlaylistDetailPage(parameter, route),
            AppRouteId.Home => CreateHomePage(),
            AppRouteId.NowPlaying => CreateNowPlayingPage(),
            _ => PlaceholderPageViewModel.Placeholder(route),
        };
    }

    private PageViewModelBase CreateAlbumDetailPage(object? parameter, AppRoute route)
    {
        if (parameter is MediaLibraryItemIdentifier id)
        {
            var page = new AlbumDetailPageViewModel(id, libraryCommands, albumArtService);
            _ = LoadDetailPageAsync(page.LoadAsync());
            return page;
        }

        return PlaceholderPageViewModel.Placeholder(route);
    }

    private PageViewModelBase CreateArtistDetailPage(object? parameter, AppRoute route)
    {
        if (parameter is MediaLibraryItemIdentifier id)
        {
            var page = new ArtistDetailPageViewModel(id, libraryCommands, albumArtService);
            _ = LoadDetailPageAsync(page.LoadAsync());
            return page;
        }

        return PlaceholderPageViewModel.Placeholder(route);
    }

    private PageViewModelBase CreateSearchPage(object? parameter)
    {
        var keyword = parameter as string ?? Search.Text ?? string.Empty;
        var page = new SearchPageViewModel(keyword, libraryCommands, albumArtService);
        _ = LoadDetailPageAsync(page.LoadAsync());
        return page;
    }

    private PageViewModelBase CreateHomePage()
    {
        var page = new HomePageViewModel(playbackController, albumArtService, OpenAlbumTile, playbackHistoryService);
        _ = LoadDetailPageAsync(page.LoadAsync());
        return page;
    }

    private PageViewModelBase CreateNowPlayingPage() =>
        new NowPlayingPageViewModel(
            playbackController,
            albumArtService,
            lyricsService,
            dialogService,
            NavigateToArtistByName,
            NavigateToAlbumByTrackPath);

    private async void NavigateToArtistByName(string artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return;
        }

        try
        {
            var artist = await LibraryHelper.GetArtistByNameAsync(artistName);
            Navigate(AppRouteId.ArtistDetail, (MediaLibraryItemIdentifier)artist);
        }
        catch (ArgumentException)
        {
            // The now-playing track is not in the library; nothing to navigate to.
        }
    }

    private async void NavigateToAlbumByTrackPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            var album = await LibraryHelper.GetAlbumByTrackPathAsync(filePath);
            Navigate(AppRouteId.AlbumDetail, (MediaLibraryItemIdentifier)album);
        }
        catch (ArgumentException)
        {
            // The now-playing track is not in the library; nothing to navigate to.
        }
    }

    private void ToggleNowPlaying()
    {
        if (currentRouteId == AppRouteId.NowPlaying)
        {
            GoBack();
        }
        else
        {
            Navigate(AppRouteId.NowPlaying);
        }
    }

    private PageViewModelBase CreatePlaylistsPage(AppRoute route)
    {
        if (playlistService is null || playlistDialogService is null)
        {
            return PlaceholderPageViewModel.ForEmpty(route, "No playlists yet", "Save the now-playing queue to create one.");
        }

        return new PlaylistsPageViewModel(
            playlistService,
            playlistDialogService,
            (playlistId, back) =>
            {
                var detail = new PlaylistDetailPageViewModel(
                    playlistId,
                    playlistService,
                    playlistDialogService,
                    trackActions,
                    PlayPlaylistItems,
                    EnqueuePlaylistItems,
                    back);

                // The inline detail's header back button is shown only in the adaptive
                // Playlists view's narrow single-pane layout; PlaylistsPageViewModel drives
                // its visibility through its IsCompact state.
                return detail;
            });
    }

    private PageViewModelBase CreatePlaylistDetailPage(object? parameter, AppRoute route)
    {
        if (playlistService is not null && playlistDialogService is not null && parameter is string playlistId)
        {
            return new PlaylistDetailPageViewModel(
                playlistId,
                playlistService,
                playlistDialogService,
                trackActions,
                PlayPlaylistItems,
                EnqueuePlaylistItems,
                GoBack);
        }

        return PlaceholderPageViewModel.Placeholder(route);
    }

    private AppRouteId GetInitialRouteId()
    {
        var routeId = appSettingsStore.RememberLastPage
            ? appSettingsStore.LastUsedRouteId
            : appSettingsStore.DefaultRouteId;
        return routesById.TryGetValue(routeId, out var route) && route.Kind == AppRouteKind.TopLevel
            ? routeId
            : AppRouteId.Home;
    }

    private void OnNavigationRequested(AppRouteId routeId) => Navigate(routeId);

    private void OnNavigationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavigationViewModel.IsExpanded))
        {
            stateStore.IsNavigationExpanded = Navigation.IsExpanded;
        }
    }

    private void OnQueuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueuePaneViewModel.IsOpen))
        {
            stateStore.IsQueuePaneOpen = Queue.IsOpen;
        }
    }

    private void OnQuerySubmitted(string query)
    {
        // Collapse the overlay navigation pane so it stops covering the search results.
        Navigation.IsExpanded = false;

        // Re-search in place when already on the search page: the shell mediates,
        // so the search box never couples directly to the page.
        if (CurrentPage is SearchPageViewModel searchPage)
        {
            currentParameter = query;
            searchPage.UpdateKeyword(query);
            return;
        }

        Navigate(AppRouteId.Search, query);
    }

    private void OnSuggestionChosen(SearchResultModel result)
    {
        // Collapse the overlay navigation pane so it stops covering the destination page.
        Navigation.IsExpanded = false;

        switch (result.Kind)
        {
            case SearchResultKind.Album when result.Identifier is MediaLibraryItemIdentifier albumId:
                Navigate(AppRouteId.AlbumDetail, albumId);
                break;
            case SearchResultKind.Artist when result.Identifier is MediaLibraryItemIdentifier artistId:
                Navigate(AppRouteId.ArtistDetail, artistId);
                break;
            default:
                // Music (and any suggestion without an identifier) open the full search results.
                OnQuerySubmitted(result.Title);
                break;
        }
    }

    /// <summary>
    /// Builds drop-down suggestions for the shell search box from the real library. Albums and
    /// artists carry their identifier so choosing one opens its detail page.
    /// </summary>
    private async Task<IReadOnlyList<SearchResultModel>> SearchLibrarySuggestionsAsync(string? query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchResultModel>();
        }

        var results = await Task.Run(() => LibraryHelper.SearchAsync(query), cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<SearchResultModel>();
        }

        const int maxSuggestions = 10;
        var suggestions = new List<SearchResultModel>(maxSuggestions);

        foreach (var album in results.Albums)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                break;
            }

            var albumTitle = LibrarySortKeys.Fallback(album.Title, "Unknown Album");
            var albumArtist = LibrarySortKeys.Fallback(album.Artist, "Unknown Artist");
            var suggestion = new SearchResultModel(
                albumTitle,
                "Album",
                SearchResultKind.Album,
                ThumbnailKind.Album,
                hasThumbnail: true)
            {
                Identifier = (MediaLibraryItemIdentifier)album,
            };

            // Swap the disc placeholder for the real cover once it is extracted. Keys match the album
            // pages so the throttled, on-disk artwork cache is shared.
            _ = LoadSuggestionArtAsync(
                suggestion,
                albumArtService.GetAlbumArtAsync(album.ArtistForQuery ?? albumArtist, album.Title ?? albumTitle, album.FirstFileInAlbum));

            suggestions.Add(suggestion);
        }

        foreach (var artist in results.Artists)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                break;
            }

            suggestions.Add(new SearchResultModel(
                LibrarySortKeys.Fallback(artist.Name, "Unknown Artist"),
                "Artist",
                SearchResultKind.Artist,
                ThumbnailKind.Artist,
                hasThumbnail: true)
            {
                Identifier = (MediaLibraryItemIdentifier)artist,
            });
        }

        foreach (var file in results.Files)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                break;
            }

            var pathTitle = Path.GetFileNameWithoutExtension(file.Path);
            var songTitle = LibrarySortKeys.Fallback(
                file.Title,
                string.IsNullOrWhiteSpace(pathTitle) ? "Unknown Title" : pathTitle);
            suggestions.Add(new SearchResultModel(songTitle, "Song", SearchResultKind.Song)
            {
                Identifier = (MediaLibraryItemIdentifier)file,
            });
        }

        return suggestions;
    }

    /// <summary>
    /// Assigns a suggestion's artwork on the UI thread once <paramref name="artTask"/> completes, so
    /// the search drop-down shows the real cover instead of the placeholder. Fire-and-forget: a missing
    /// cover or a failure simply leaves the placeholder in place.
    /// </summary>
    private static async Task LoadSuggestionArtAsync(SearchResultModel suggestion, Task<Bitmap?> artTask)
    {
        Bitmap? bitmap;
        try
        {
            bitmap = await artTask.ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (bitmap is null)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            suggestion.Image = bitmap;
        }
        else
        {
            Dispatcher.UIThread.Post(() => suggestion.Image = bitmap);
        }
    }

    private void OnPlaybackError(string message)
    {
        HasWarnings = true;
        WarningText = "Playback error";
        WarningDetailsText = message;
    }

    /// <summary>
    /// Surfaces cue sheets that could not be resolved to a playable audio file in the same warning
    /// banner used for playback/decode errors. When a sheet lives outside the sandbox-readable music
    /// folder, its audio file is reachable only if opened alongside the sheet, so the message tells
    /// the user to open both together.
    /// </summary>
    private void ReportCueFailures(IReadOnlyList<CueResolutionFailure> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var messages = failures.Select(failure =>
        {
            var cueName = Path.GetFileName(failure.CueFilePath);
            var referenced = string.IsNullOrWhiteSpace(failure.ReferencedFileName)
                ? "its audio file"
                : $"\"{failure.ReferencedFileName}\"";
            return $"{cueName}: couldn't find or open {referenced}. " +
                "If the CUE sheet is outside your music folder, open the CUE sheet and its audio file together so the app can access both.";
        });

        HasWarnings = true;
        WarningText = "Couldn't open CUE sheet";
        WarningDetailsText = string.Join(Environment.NewLine, messages);
    }

    /// <summary>Clears the warning/error banner once the user has reviewed it.</summary>
    public void ClearWarnings()
    {
        HasWarnings = false;
        WarningText = string.Empty;
        WarningDetailsText = string.Empty;
    }

    private void PersistPlaybackQueue()
    {
        stateStore.PlaybackQueue = playbackController.CreatePersistedState();
    }

    private void RestorePlaybackQueue()
    {
        var saved = stateStore.PlaybackQueue;
        if (saved is not null)
        {
            playbackController.RestorePersistedState(saved);
        }
    }

    /// <summary>
    /// Shuffles every track in the library into a random order and replaces the
    /// playback queue with it, then plays from the start. The playback mode is
    /// left untouched (this only reorders the queue).
    /// </summary>
    private async Task ShuffleAllAsync()
    {
        var items = await LoadShuffledLibraryItemsAsync();

        playbackController.ShuffleAll(items);
    }

    private Task<IReadOnlyList<MusicPlaybackItemModel>> LoadShuffledLibraryItemsAsync() =>
        Task.Run(async () =>
        {
            if (GlobalLibraryCache.CachedDbMediaFile is null)
            {
                await GlobalLibraryCache.LoadMediaAsync();
            }

            var items = (GlobalLibraryCache.CachedDbMediaFile ?? [])
                .Select(file => TrackRowFactory.CreateRow(file, trackActions))
                .Select(ToQueueItem);
            return ShuffleQueueItems(items);
        });

    private static IReadOnlyList<MusicPlaybackItemModel> ShuffleQueueItems(IEnumerable<MusicPlaybackItemModel> source)
    {
        var list = source.ToList();
        var rng = Random.Shared;
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    /// <summary>
    /// Adds external media files (file picker, command-line activation) to the queue, expanding
    /// CUE sheets — standalone <c>.cue</c> files and audio files with an embedded sheet — into
    /// their individual tracks.
    /// </summary>
    public void AddFilesToQueue(IReadOnlyList<string> paths, bool play)
    {
        _ = AddFilesToQueueAsync(paths, play);
    }

    private async Task AddFilesToQueueAsync(IReadOnlyList<string> paths, bool play)
    {
        // Parse cue sheets / probe tags off the UI thread, then apply the queue mutation on the
        // UI thread where the playback controller's queue projection is owned.
        var expansion = await CueFileExpander.ExpandAsync(paths).ConfigureAwait(false);
        if (expansion.Items.Count == 0 && expansion.Failures.Count == 0)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ReportCueFailures(expansion.Failures);

            if (expansion.Items.Count == 0)
            {
                return;
            }

            if (play)
            {
                playbackController.PlayAll(expansion.Items, shuffle: false);
            }
            else
            {
                playbackController.EnqueueRange(expansion.Items);
            }
        });
    }

    /// <summary>
    /// Resolves dropped external files into the queue items to insert, expanding CUE sheets into
    /// their tracks. Called on the drag-drop path. Cue sheets that cannot be resolved to a playable
    /// audio file surface in the warning banner rather than failing silently.
    /// </summary>
    private async Task<IReadOnlyList<MusicPlaybackItemModel>> BuildFileItemsWithMetadataAsync(IReadOnlyList<string> paths)
    {
        var expansion = await CueFileExpander.ExpandAsync(paths).ConfigureAwait(false);
        if (expansion.Failures.Count > 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ReportCueFailures(expansion.Failures));
        }

        return expansion.Items;
    }

    /// <summary>Flushes persisted playback state and tears down the audio engine.</summary>
    public void Shutdown()
    {
        PersistPlaybackQueue();
        playbackController.Dispose();
    }

    private void SetSort(LibrarySortField field)
    {
        var option = SortOptions.FirstOrDefault(o => o.Field == field);
        if (option is not null)
        {
            SelectedSort = option;
        }
    }

    private PageViewModelBase CreateBrowsePage<TPage>(TPage page)
        where TPage : PageViewModelBase, ILibraryBrowsePageViewModel
    {
        page.ApplyViewSettings(appSettingsStore.GetLibraryViewSettings(page.Kind));
        _ = LoadBrowsePageAsync(page);
        return page;
    }

    private void SetActiveBrowsePage(ILibraryBrowsePageViewModel? browse)
    {
        activeBrowsePage = browse;
        activeBrowseKind = browse?.Kind;

        if (browse is not null)
        {
            var settings = appSettingsStore.GetLibraryViewSettings(browse.Kind);
            isSyncingViewSettings = true;
            try
            {
                SelectedSort = SortOptions.FirstOrDefault(o => o.Field == settings.Sort) ?? SortOptions[0];
                SortAscending = settings.Ascending;
                GroupingEnabled = settings.GroupingEnabled;
            }
            finally
            {
                isSyncingViewSettings = false;
            }
        }

        UpdateNowPlayingHighlight();
        UpdateSortVisibility();
    }

    private void OnPlaybackControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackController.CurrentItem))
        {
            UpdateNowPlayingHighlight();
            RecordPlaybackHistory();
        }
    }

    private void RecordPlaybackHistory()
    {
        var filePath = playbackController.CurrentItem?.FilePath;
        if (playbackHistoryService is not null && !string.IsNullOrWhiteSpace(filePath))
        {
            _ = playbackHistoryService.AddHistoryAsync(filePath);
        }
    }

    private void UpdateNowPlayingHighlight() =>
        (activeBrowsePage as SongsPageViewModel)?.SetPlayingPath(playbackController.CurrentItem?.FilePath);

    private void UpdateSortVisibility()
    {
        OnPropertyChanged(nameof(CanSortByTitle));
        OnPropertyChanged(nameof(CanSortByAlbumName));
        OnPropertyChanged(nameof(CanSortByArtistName));
        OnPropertyChanged(nameof(CanSortByGenre));
        OnPropertyChanged(nameof(CanSortByYear));
        OnPropertyChanged(nameof(CanSortByDateAdded));
    }

    private void PersistViewSettings()
    {
        if (isSyncingViewSettings || activeBrowsePage is null || activeBrowseKind is null)
        {
            return;
        }

        var settings = new LibraryViewSettings(SelectedSort.Field, SortAscending, GroupingEnabled);
        appSettingsStore.SetLibraryViewSettings(activeBrowseKind.Value, settings);
        activeBrowsePage.ApplyViewSettings(settings);
    }

    partial void OnSelectedSortChanged(LibrarySortOption value) => PersistViewSettings();

    partial void OnSortAscendingChanged(bool value) => PersistViewSettings();

    partial void OnGroupingEnabledChanged(bool value) => PersistViewSettings();

    private void RecentlyAdded()
    {
        appSettingsStore.SetLibraryViewSettings(
            LibraryItemKind.Song,
            new LibraryViewSettings(LibrarySortField.DateAdded, Ascending: false, GroupingEnabled: false));
        Navigate(AppRouteId.Songs);
    }

    private void OpenAlbumTile(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            Navigate(AppRouteId.AlbumDetail, id);
        }
    }

    private void OpenArtistTile(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            Navigate(AppRouteId.ArtistDetail, id);
        }
    }

    private async void PlayAlbumTile(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            PlayTracks(await ResolveAlbumTracksAsync(id));
        }
    }

    private async void AddAlbumTileToQueue(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            AddTracksToQueue(await ResolveAlbumTracksAsync(id));
        }
    }

    private async void PlayArtistTile(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            PlayTracks(await ResolveArtistTracksAsync(id));
        }
    }

    private async void AddArtistTileToQueue(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            AddTracksToQueue(await ResolveArtistTracksAsync(id));
        }
    }

    private Task<IReadOnlyList<TrackRowModel>> ResolveAlbumTracksAsync(MediaLibraryItemIdentifier id) =>
        Task.Run(async () =>
        {
            var album = await LibraryHelper.GetAlbumByNameAsync(id.ArtistName, id.AlbumName);
            var files = await LibraryHelper.GetMediaFileByAlbumAsync(album.Title, album.ArtistForQuery);
            return (IReadOnlyList<TrackRowModel>)files
                .Select(f => TrackRowFactory.CreateRow(f, trackActions))
                .OrderBy(row => row.DiscNumber)
                .ThenBy(row => row.TrackNumber)
                .ToList();
        });

    private Task<IReadOnlyList<TrackRowModel>> ResolveArtistTracksAsync(MediaLibraryItemIdentifier id) =>
        Task.Run(async () =>
        {
            var artist = await LibraryHelper.GetArtistByNameAsync(id.ArtistName);
            var files = await LibraryHelper.GetMediaFileByArtistAsync(artist);
            return (IReadOnlyList<TrackRowModel>)TrackRowFactory.OrderByAlbumThenTrack(
                files.Select(f => TrackRowFactory.CreateRow(f, trackActions)));
        });

    private void PlayTrack(TrackRowModel track)
    {
        // Activating a row replaces the now-playing queue with the whole list it is
        // shown in and starts at the tapped track. Rows without a list context fall
        // back to single-track play.
        var context = track.PlaybackContext?.Invoke();
        if (context is { Count: > 0 })
        {
            var ordered = context.ToList();
            var index = ordered.IndexOf(track);
            if (index >= 0)
            {
                playbackController.PlayAllFrom(ordered.Select(ToQueueItem).ToList(), index);
                return;
            }
        }

        playbackController.PlayNow(ToQueueItem(track));
    }

    private void AddTrackToQueue(TrackRowModel track) =>
        playbackController.Enqueue(ToQueueItem(track));

    private void InsertTrackNext(TrackRowModel track) =>
        playbackController.InsertNext(ToQueueItem(track));

    private static void OpenTrackFolder(TrackRowModel track)
    {
        var folder = Path.GetDirectoryName(track.FilePath);
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        try
        {
            // Platform-agnostic: ShellExecute opens the containing folder in the OS
            // file manager (Explorer / Finder / xdg-open) without per-platform branching.
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch
        {
            // Best effort: opening the folder must not crash the app.
        }
    }

    private static MusicPlaybackItemModel ToQueueItem(TrackRowModel track) =>
        new(track.Title, track.Artist, track.Album, track.DurationTime, track.FilePath, track.StartTimeMs);

    private void PlayTracks(IReadOnlyList<TrackRowModel> tracks)
    {
        if (tracks.Count > 0)
        {
            playbackController.PlayAll(tracks.Select(ToQueueItem).ToList(), shuffle: false);
        }
    }

    private void ShuffleTracks(IReadOnlyList<TrackRowModel> tracks)
    {
        if (tracks.Count > 0)
        {
            playbackController.PlayAll(tracks.Select(ToQueueItem).ToList(), shuffle: true);
        }
    }

    private void AddTracksToQueue(IReadOnlyList<TrackRowModel> tracks)
    {
        if (tracks.Count > 0)
        {
            playbackController.EnqueueRange(tracks.Select(ToQueueItem).ToList());
        }
    }

    /// <summary>
    /// Resolves a library item dragged onto the now-playing queue into the ordered
    /// playback items to insert, mirroring the order they would have if added through
    /// their normal play/queue actions (albums and artists in disc/track order, a
    /// playlist in its stored order, a single track as itself).
    /// </summary>
    private async Task<IReadOnlyList<MusicPlaybackItemModel>> ResolveInsertPayloadAsync(LibraryInsertPayload payload)
    {
        switch (payload.Kind)
        {
            case LibraryInsertKind.Album when payload.Identifier is { } albumId:
                return (await ResolveAlbumTracksAsync(albumId)).Select(ToQueueItem).ToList();
            case LibraryInsertKind.Artist when payload.Identifier is { } artistId:
                return (await ResolveArtistTracksAsync(artistId)).Select(ToQueueItem).ToList();
            case LibraryInsertKind.Track when payload.Track is { } track:
                return new[] { ToQueueItem(track) };
            case LibraryInsertKind.Playlist when payload.Playlist is { } playlist:
                return playlist.Items.Select(item => item.ToPlaybackItem()).ToList();
            default:
                return Array.Empty<MusicPlaybackItemModel>();
        }
    }

    private async void AddTrackToPlaylist(TrackRowModel track)
    {
        if (playlistService is null || playlistDialogService is null)
        {
            return;
        }

        var target = await playlistDialogService.PickPlaylistAsync();
        if (target is not null)
        {
            await playlistService.AddItemsAsync(target.Id, new[] { ToPlaylistItem(track) });
        }
    }

    private async void AddTracksToPlaylist(IReadOnlyList<TrackRowModel> tracks)
    {
        if (playlistService is null || playlistDialogService is null || tracks.Count == 0)
        {
            return;
        }

        var target = await playlistDialogService.PickPlaylistAsync();
        if (target is not null)
        {
            await playlistService.AddItemsAsync(target.Id, tracks.Select(ToPlaylistItem).ToList());
        }
    }

    private void PlayPlaylistItems(IReadOnlyList<PlaylistItemModel> items, bool shuffle)
    {
        if (items.Count > 0)
        {
            playbackController.PlayAll(items.Select(item => item.ToPlaybackItem()).ToList(), shuffle);
        }
    }

    private void EnqueuePlaylistItems(IReadOnlyList<PlaylistItemModel> items)
    {
        if (items.Count > 0)
        {
            playbackController.EnqueueRange(items.Select(item => item.ToPlaybackItem()).ToList());
        }
    }

    private async Task SaveQueueAsPlaylistAsync(string name)
    {
        if (playlistService is null)
        {
            return;
        }

        var items = playbackController.QueueItems.Select(PlaylistItemModel.FromPlaybackItem).ToList();
        var playlist = await playlistService.CreateAsync(name);
        if (items.Count > 0)
        {
            await playlistService.AddItemsAsync(playlist.Id, items);
        }
    }

    private static PlaylistItemModel ToPlaylistItem(TrackRowModel track) =>
        new(track.Title, track.Artist, track.Album, track.FilePath, track.DurationTime);

    private async Task LoadDetailPageAsync(Task load)
    {
        try
        {
            await load;
        }
        catch (Exception ex)
        {
            var message = ex.GetBaseException().Message;
            Dispatcher.UIThread.Post(() =>
            {
                HasWarnings = true;
                WarningText = "Could not load page";
                WarningDetailsText = message;
            });
        }
    }

    private async Task LoadBrowsePageAsync(ILibraryBrowsePageViewModel page)
    {
        try
        {
            await page.LoadAsync();
        }
        catch (Exception ex)
        {
            var message = ex.GetBaseException().Message;
            Dispatcher.UIThread.Post(() =>
            {
                HasWarnings = true;
                WarningText = "Could not load library";
                WarningDetailsText = message;
            });
        }
    }

    private void ReloadActiveBrowsePage()
    {
        if (activeBrowsePage is not null)
        {
            _ = LoadBrowsePageAsync(activeBrowsePage);
        }

        // Home is not a browse page, but its all-music grid must also refresh
        // when a scan finishes.
        if (CurrentPage is HomePageViewModel home)
        {
            _ = home.LoadAsync();
        }
    }

    private async Task RefreshLibraryAsync()
    {
        HasWarnings = false;
        WarningText = string.Empty;
        WarningDetailsText = string.Empty;
        CancelScanStatusClear();
        IsScanActive = true;
        ScanStatusText = "Refreshing library...";

        if (libraryScanService is null)
        {
            IsScanActive = false;
            ScanStatusText = string.Empty;
            return;
        }

        try
        {
            var result = await Task.Run(libraryScanService.ScanAsync);
            ScanStatusText = FormatScanCompletedText(result.IndexedCount);
            ApplyScanResult(result);
            ReloadActiveBrowsePage();
        }
        catch (Exception ex)
        {
            HasWarnings = true;
            WarningText = "Library scan failed";
            WarningDetailsText = ex.GetBaseException().Message;
            ScanStatusText = "Library scan failed.";
        }
        finally
        {
            IsScanActive = false;
            ScheduleScanStatusClear();
        }
    }

    /// <summary>Auto-clears the completion status banner after a few seconds.</summary>
    private void ScheduleScanStatusClear()
    {
        CancelScanStatusClear();
        scanStatusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        scanStatusClearTimer.Tick += (_, _) =>
        {
            CancelScanStatusClear();
            if (!IsScanActive)
            {
                ScanStatusText = string.Empty;
            }
        };
        scanStatusClearTimer.Start();
    }

    private void CancelScanStatusClear()
    {
        scanStatusClearTimer?.Stop();
        scanStatusClearTimer = null;
    }

    private bool CanRefreshLibrary()
    {
        if (IsScanActive)
        {
            return false;
        }

        return libraryScanService is not null && libraryLocationService.GetLibraryFolderPaths().Count > 0;
    }

    private void SubscribeToScanEvents()
    {
        if (libraryScanService is null)
        {
            return;
        }

        libraryScanService.ScanStarted += OnScanStarted;
        libraryScanService.ScanProgressChanged += OnScanProgressChanged;
    }

    private void OnScanStarted(object? sender, LibraryScanStartedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CancelScanStatusClear();
            ScanStatusText = e.Folders.Count == 1
                ? "Scanning 1 folder..."
                : $"Scanning {e.Folders.Count} folders...";
        });
    }

    private void OnScanProgressChanged(object? sender, LibraryScanProgressEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ScanStatusText = e.IndexedCount == 1
                ? "Indexed 1 item..."
                : $"Indexed {e.IndexedCount} items...";
        });
    }

    private void ApplyScanResult(LibraryScanResult result)
    {
        if (result.Warnings.Count == 0)
        {
            return;
        }

        HasWarnings = true;
        WarningText = result.Warnings.Count == 1
            ? "1 scan warning"
            : $"{result.Warnings.Count} scan warnings";
        WarningDetailsText = string.Join(
            Environment.NewLine + Environment.NewLine,
            result.Warnings.Select(warning => $"{warning.Path}{Environment.NewLine}{warning.Message}"));
    }

    private static string FormatScanCompletedText(int indexedCount)
    {
        if (indexedCount < 0)
        {
            return "Library scan completed with database warnings.";
        }

        return indexedCount == 1
            ? "Library scan complete: 1 item indexed."
            : $"Library scan complete: {indexedCount} items indexed.";
    }

    partial void OnIsScanActiveChanged(bool value)
    {
        RefreshLibraryCommand.NotifyCanExecuteChanged();
    }
}
