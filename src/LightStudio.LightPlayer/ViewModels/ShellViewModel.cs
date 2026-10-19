using LightMediaRenderer.Avalonia.Controls;
using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Animations;
using LightStudio.LightPlayer.Controls;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// Root view model for the application shell. Owns navigation, search, the
/// playback bar, the now-playing queue, the more-menu commands, and library refresh status.
/// </summary>
public partial class ShellViewModel : ViewModelBase, IShellOverlayHost
{
    private readonly IReadOnlyDictionary<AppRouteId, AppRoute> routesById;
    private readonly IShellStateStore stateStore;
    private readonly IAppSettingsStore appSettingsStore;
    private readonly IThemeService themeService;
    private readonly ILibraryLocationService libraryLocationService;
    private readonly ILibraryScanService? libraryScanService;
    private readonly IPhotoIndexService? photoIndexService = PlatformServices.PhotoIndex;
    private readonly ILibraryQueryService libraryQueryService;
    private readonly IVideoLibraryQueryService? videoLibraryQueryService;
    private readonly IUnifiedLibraryQueryService unifiedLibraryQueryService;
    private readonly IUnifiedVideoLibraryQueryService unifiedVideoLibraryQueryService;
    private readonly IClientMediaResolver mediaResolver;
    private readonly ILibrarySourceRegistry sourceRegistry;
    private readonly IRemoteLibraryManager? remoteLibraryManager;
    private readonly ISubtitleFileBrowser? subtitleFileBrowser;
    private readonly VideoPreviewService? videoPreviewService;
    private readonly PhotoFolderService photoFolderService = new();
    private readonly PlaybackController playbackController;
    private readonly ISystemSampleRateProvider systemSampleRateProvider;
    private readonly IAudioOutputSettings audioOutputSettings;
    private readonly IPlaybackHistoryService? playbackHistoryService;
    private readonly IPlaylistService? playlistService;
    private readonly IPlaylistService? videoPlaylistService;
    private readonly IPlaylistDialogService? playlistDialogService;
    private readonly IDialogService? dialogService;
    private readonly ILyricSourceService lyricSourceService;
    private readonly IUpdateCheckService? updateCheckService;
    private readonly ILyricsService lyricsService;
    private readonly IFileQueueExpander fileQueueExpander;
    private readonly IFileRevealService fileRevealService;

    private TrackActionCallbacks trackActions = new();
    private TileActionCallbacks albumTileActions = new();
    private TileActionCallbacks artistTileActions = new();
    private LibraryCommands libraryCommands = new();
    private ILibraryBrowsePageViewModel? activeBrowsePage;
    private LibraryItemKind? activeBrowseKind;
    private bool isSyncingViewSettings;
    private readonly AlbumArtService albumArtService;
    private readonly IVideoPlaybackStateStore videoPlaybackStates;
    private readonly IVideoHistoryStore videoHistory;

    private readonly Stack<(AppRouteId Id, object? Parameter, PageViewModelBase? Page)> backStack = new();

    // Entries a Back navigation stepped away from, enabling a matching Forward. Cleared by any fresh
    // navigation (standard back/forward semantics). Forward targets are rebuilt from their route, so
    // these entries never carry a live page.
    private readonly Stack<(AppRouteId Id, object? Parameter, PageViewModelBase? Page)> forwardStack = new();
    private AppRouteId currentRouteId;
    private object? currentParameter;
    private DispatcherTimer? scanStatusClearTimer;
    private bool includeLocalScanStatus;
    private bool isLocalScanActive;
    private int localScanFolderCount;
    private int localScanIndexedCount;
    private int localScanWarningCount;
    private bool localScanFailed;
    private bool includeRemoteRefreshStatus;
    private RemoteLibraryRefreshStatus remoteRefreshStatus = new(0, false, false, 0, 0, false);
    private bool includePhotoIndexStatus;
    private PhotoIndexStatus photoIndexStatus = new();

    // Host-owned navigation history (the browser head's URL and back/forward buttons). Null on heads
    // that own their own lifetime, which keep using backStack/forwardStack directly.
    private readonly INavigationHistory? navigationHistory = PlatformServices.NavigationHistory;

    // Position in the host history and the furthest entry reached from it, so the shell's own
    // back/forward affordances follow the host rather than its internal stacks.
    private int historyIndex;
    private int historyMaxIndex;

    // Number of layered surfaces (in-page detail, expanded now-playing sheet, maximized video sheet)
    // the last history sync recorded. Comparing it with the current count turns an opened surface
    // into a pushed entry and a self-closed one into a host back step.
    private int lastOverlayDepth;
    private bool isApplyingHistory;
    private bool isHistorySuspended;

    // Set while a video sheet is being put back by a history entry that already addresses it, so its
    // arrival rewrites that entry instead of pushing one on top of it.
    private bool isRestoringVideoSheet;

    // The session currently on the video sheet, followed so the address tracks the video it plays.
    private VideoPlayerPageViewModel? videoSession;

    // A detail restored from a reloaded location lands once the page's items arrive, after the
    // restore itself has finished; the entry it belongs to already exists, so the first sync it
    // triggers updates that entry instead of adding one.
    private bool isRestoringInPageState;

    // Below this shell width the shell switches to the narrow (mobile) adaptive layout: the
    // navigation rail is replaced by a top bar plus bottom navigation, and the now-playing queue is
    // presented as a full navigation page instead of a docked side panel.
    private const double NarrowWidthThreshold = 530d;
    private const double QueueOverlayWidthThreshold = 1100d;
    private const double SplitViewCompactInlineThreshold = 1750d;

    // Suggestion thumbnails are 34 DIPs; this covers displays scaled up to 3x.
    private const int SuggestionArtPixelSize = 102;

    // Library tabs surfaced by the narrow layout's top tab strip and bottom "Library" section.
    private static readonly AppRouteId[] LibrarySectionRoutes =
    {
        AppRouteId.Songs,
        AppRouteId.Albums,
        AppRouteId.Artists,
    };

    // Last library tab the user viewed, so the narrow layout's bottom "Library" button returns to it.
    private AppRouteId lastLibraryRouteId = AppRouteId.Albums;

    // Whether the docked queue side panel was open when the shell last narrowed. The narrow layout
    // has no room for it, so it is closed on the way in and re-docked on the way back out.
    private bool queueWasOpenWhenWide;

    private bool isAppleChromeEnabled;
    private double viewportWidth;

    // The page shown by the settings window while one is open.
    private SettingsPageViewModel? settingsWindowPage;

    // Whether the shell is currently narrower than NarrowWidthThreshold. Drives the narrow/wide
    // adaptive layout switch and whether opening the queue docks a side panel or navigates to a page.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLibraryTabs))]
    [NotifyPropertyChangedFor(nameof(ShowLibraryPager))]
    [NotifyPropertyChangedFor(nameof(RoutedPage))]
    [NotifyPropertyChangedFor(nameof(NavRailCompactWidth))]
    [NotifyPropertyChangedFor(nameof(IsNavPaneVisible))]
    [NotifyPropertyChangedFor(nameof(IsNarrowChromeVisible))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowBackButton))]
    [NotifyPropertyChangedFor(nameof(ShowPlaybackBar))]
    private bool isNarrow;

    // True while the narrow layout shows its top search field in place of the page title.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNarrowBackButton))]
    private bool isSearchActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SafeAreaPadding))]
    [NotifyPropertyChangedFor(nameof(SafeAreaBackgroundMargin))]
    [NotifyPropertyChangedFor(nameof(SafeAreaContentMargin))]
    [NotifyPropertyChangedFor(nameof(SafeAreaOutsetMargin))]
    [NotifyPropertyChangedFor(nameof(QueueSafeAreaContentMargin))]
    [NotifyPropertyChangedFor(nameof(BottomNavSafeAreaPadding))]
    [NotifyPropertyChangedFor(nameof(BottomNavSafeAreaMargin))]
    [NotifyPropertyChangedFor(nameof(BottomNavItemPadding))]
    [NotifyPropertyChangedFor(nameof(BottomNavItemMargin))]
    private Thickness deviceSafeAreaPadding;

    // The system window controls over the top-left corner where the platform measures them (iPadOS).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowControlsInset))]
    [NotifyPropertyChangedFor(nameof(NavWindowControlsHeight))]
    [NotifyPropertyChangedFor(nameof(NarrowTopBarMargin))]
    private Thickness deviceWindowControls;

    public Thickness SafeAreaPadding => VideoSheet.IsFullScreen ? default : DeviceSafeAreaPadding;

    public Thickness SafeAreaBackgroundMargin => new(
        -SafeAreaPadding.Left, -SafeAreaPadding.Top, 0, -SafeAreaPadding.Bottom);

    public Thickness SafeAreaContentMargin => new(SafeAreaPadding.Left, 0, 0, 0);

    public Thickness SafeAreaOutsetMargin => new(
        -SafeAreaPadding.Left, -SafeAreaPadding.Top, -SafeAreaPadding.Right, -SafeAreaPadding.Bottom);

    public Thickness QueueSafeAreaContentMargin => new(0, 0, SafeAreaPadding.Right, 0);

    public Thickness BottomNavSafeAreaPadding => new(
        DeviceSafeAreaPadding.Left, 0, DeviceSafeAreaPadding.Right, DeviceSafeAreaPadding.Bottom);

    public Thickness BottomNavSafeAreaMargin => new(
        -DeviceSafeAreaPadding.Left, 0, -DeviceSafeAreaPadding.Right, -DeviceSafeAreaPadding.Bottom);

    private double BottomNavItemBottomSpacing => OperatingSystem.IsIOS()
        ? Math.Max(0, 6 - DeviceSafeAreaPadding.Bottom)
        : 6;

    public Thickness BottomNavItemPadding => new(0, 6, 0, BottomNavItemBottomSpacing);

    public Thickness BottomNavItemMargin => new(2, 6, 2, BottomNavItemBottomSpacing);

    private DispatcherTimer? updateStatusClearTimer;
    private string updateDownloadUrl = string.Empty;
    private readonly DrillInPageTransition hierarchicalPageTransition = new();
    private readonly EntrancePageTransition rootPageTransition = new();

    // Narrow (mobile) layout transitions: hierarchical navigations slide horizontally like a
    // left/right swipe, and switching between top-level views is instant (no animation).
    private readonly SlidePageTransition narrowHierarchicalTransition = new();
    private readonly NoPageTransition narrowRootTransition = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoutedPage))]
    [NotifyPropertyChangedFor(nameof(WindowPageTitle))]
    [NotifyPropertyChangedFor(nameof(SettingsSidebar))]
    private PageViewModelBase currentPage = null!;

    /// <summary>The page label shown in the main window title.</summary>
    public string WindowPageTitle => IsVideoSheetAddressed
        ? Strings.Resources.Nav_VideoPlayer
        : (backPresentation?.To ?? CurrentPage)?.Title ?? string.Empty;

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

    internal Func<PageViewModelBase?, PageViewModelBase, IPageTransition, IPageTransition>? PreparePageTransition { get; set; }

    public PageAddressBarService AddressBar { get; } = new();

    [ObservableProperty]
    private bool isScanActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanStatusText))]
    private string scanStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateStatus))]
    private bool isUpdateCheckActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateStatus))]
    private string updateStatusText = string.Empty;

    [ObservableProperty]
    private string updateIconKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateStatus))]
    private bool isUpdateAvailable;

    [ObservableProperty]
    private bool hasWarnings;

    /// <summary>Whether this head is currently serving its library over HTTP.</summary>
    [ObservableProperty]
    private bool isServerRunning;

    [ObservableProperty]
    private string serverStatusText = string.Empty;

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
    [NotifyPropertyChangedFor(nameof(IsSortByDuration))]
    [NotifyPropertyChangedFor(nameof(IsSortByFileType))]
    [NotifyPropertyChangedFor(nameof(IsSortByFileSize))]
    private LibrarySortOption selectedSort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortDescending))]
    private bool sortAscending = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLayoutGrid))]
    [NotifyPropertyChangedFor(nameof(IsLayoutList))]
    [NotifyPropertyChangedFor(nameof(IsLayoutFolders))]
    private LibraryLayoutMode selectedLayout;

    /// <summary>
    /// Content of the in-shell modal overlay (single-view/mobile dialogs). Null when no overlay is
    /// showing. Bound by <c>ShellView</c> to a scrim + centered card layered above the shell.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverlayOpen))]
    private Control? overlayContent;

    private TaskCompletionSource? overlayClosed;

    public ShellViewModel(
        IReadOnlyList<AppRoute> routes,
        IShellStateStore stateStore,
        IAppSettingsStore appSettingsStore,
        IThemeService themeService,
        ILibraryLocationService? libraryLocationService = null,
        ILibraryScanService? libraryScanService = null,
        IPlaylistService? playlistService = null,
        IPlaylistDialogService? playlistDialogService = null,
        IDialogService? dialogService = null,
        ILyricSourceService? lyricSourceService = null,
        IUpdateCheckService? updateCheckService = null,
        IPlaybackHistoryServiceFactory? playbackHistoryServiceFactory = null,
        ILibraryQueryService? libraryQueryService = null,
        ILyricsService? lyricsService = null,
        IFileQueueExpander? fileQueueExpander = null,
        IFileRevealService? fileRevealService = null,
        IVideoLibraryQueryService? videoLibraryQueryService = null,
        ISubtitleFileBrowser? subtitleFileBrowser = null,
        IClientMediaResolver? mediaResolver = null,
        IUnifiedLibraryQueryService? unifiedLibraryQueryService = null,
        IUnifiedVideoLibraryQueryService? unifiedVideoLibraryQueryService = null,
        IPlaylistService? videoPlaylistService = null)
    {
        this.stateStore = stateStore;
        this.appSettingsStore = appSettingsStore;
        this.themeService = themeService;
        isAppleChromeEnabled = themeService.IsAppleChromeEnabled;
        this.libraryLocationService = libraryLocationService
            ?? throw new ArgumentNullException(nameof(libraryLocationService));
        this.libraryScanService = libraryScanService;
        this.libraryQueryService = libraryQueryService
            ?? throw new ArgumentNullException(nameof(libraryQueryService));
        this.playlistService = playlistService;
        this.videoPlaylistService = videoPlaylistService;
        this.playlistDialogService = playlistDialogService;
        this.dialogService = dialogService;
        this.lyricSourceService = lyricSourceService
            ?? throw new ArgumentNullException(nameof(lyricSourceService));
        this.updateCheckService = updateCheckService;
        this.lyricsService = lyricsService
            ?? throw new ArgumentNullException(nameof(lyricsService));
        this.fileQueueExpander = fileQueueExpander
            ?? throw new ArgumentNullException(nameof(fileQueueExpander));
        this.fileRevealService = fileRevealService ?? new DesktopFileRevealService();
        this.videoLibraryQueryService = videoLibraryQueryService;
        this.subtitleFileBrowser = subtitleFileBrowser;
        videoPlaybackStates = PlatformServices.VideoPlaybackStateStore ?? new JsonVideoPlaybackStateStore();
        videoHistory = PlatformServices.VideoHistoryStore ?? new JsonVideoHistoryStore(appSettingsStore);
        mediaResolver ??= new CapturedPlatformMediaResolver(IMultiMediaPlatformIO.Instance);
        this.mediaResolver = mediaResolver;
        albumArtService = new AlbumArtService(mediaResolver, PlatformServices.MaxConcurrentArtworkLoads);
        sourceRegistry = PlatformServices.LibrarySources
            ?? throw new InvalidOperationException("A library source registry must be configured before the shell is created.");
        remoteLibraryManager = PlatformServices.RemoteLibraryManager;
        this.unifiedLibraryQueryService = unifiedLibraryQueryService
            ?? new UnifiedLibraryQueryService(sourceRegistry);
        this.unifiedVideoLibraryQueryService = unifiedVideoLibraryQueryService
            ?? new UnifiedVideoLibraryQueryService(sourceRegistry);
        videoPreviewService = new VideoPreviewService(this.unifiedVideoLibraryQueryService);

        // Command handlers and background work across the app report failures here; the banner is
        // the only surface that can show them. Assigning replaces an earlier shell (server switch).
        AppErrorReporter.Handler = ShowFailureBanner;

        Routes = routes;
        routesById = routes.ToDictionary(route => route.Id);
        SortOptions = LibrarySortOptions.All;
        selectedSort = SortOptions[0];

        Navigation = new NavigationViewModel(routes) { IsExpanded = false };
        Navigation.NavigationRequested += OnNavigationRequested;

        // The narrow layout presents the library sections as one swipeable pager instead of four
        // separate destinations; it owns their pages for as long as that layout lasts.
        LibraryPager = new LibraryPagerViewModel(LibrarySectionRoutes, CreateLibraryPage);
        LibraryPager.PageSelected += OnLibraryPageSelected;

        Search = new SearchBoxViewModel(SearchLibrarySuggestionsAsync);
        Search.QuerySubmitted += OnQuerySubmitted;
        Search.SuggestionChosen += OnSuggestionChosen;

        // Publishes "now playing" / transport to the OS (MPRIS on Linux, no-op placeholders elsewhere).
        audioOutputSettings = AudioOutputDeviceFactory.Settings;
        foreach (var option in appSettingsStore.AudioOutputOptions)
        {
            audioOutputSettings.SetOption(option.Key, option.Value);
        }
        audioOutputSettings.SetDevice(appSettingsStore.AudioOutputDeviceId);

        playbackController = new PlaybackController(SystemMediaControlsFactory.Create(), mediaResolver);
        systemSampleRateProvider = AudioOutputDeviceFactory.SystemSampleRateProvider;
        playbackController.PreferredSampleRate = appSettingsStore.PreferredSampleRate;
        playbackController.AlwaysResample = appSettingsStore.AlwaysResample;
        playbackController.SystemSampleRateProvider = systemSampleRateProvider;
        playbackController.AlbumArtContentResolver = albumArtService.GetTrackArtContentAsync;
        playbackController.PlaybackError += OnPlaybackError;
        playbackController.PersistRequested += PersistPlaybackQueue;
        playbackController.PropertyChanged += OnPlaybackControllerPropertyChanged;
        playbackController.QueueItems.CollectionChanged += OnPlaybackQueueChanged;

        // Records played library tracks to the playback history shown on the home page.
        playbackHistoryService = playbackHistoryServiceFactory?.Create(() => isLocalScanActive);

        PlaybackBar = new PlaybackBarViewModel(playbackController, albumArtService, playlistService, playlistDialogService);
        PlaybackBar.OpenNowPlaying = ToggleNowPlaying;
        PlaybackBar.PropertyChanged += OnPlaybackBarPropertyChanged;

        Queue = new QueuePaneViewModel(playbackController)
        {
            IsOpen = stateStore.IsQueuePaneOpen,
            PinnedWidth = QueuePinnedWidth,
        };
        Queue.PropertyChanged += OnQueuePropertyChanged;
        Queue.EnsureOpen = EnsureQueueOpen;
        Queue.SaveAsPlaylist = SaveQueueAsPlaylistAsync;
        Queue.ResolveInsertItems = ResolveInsertPayloadAsync;
        Queue.BuildFileItems = BuildFileItemsWithMetadataAsync;
        Queue.ShowProperties = item => ShowProperties(item.Media, item.Title);
        Queue.OpenContainingFolder = item => RevealInFileManager(item.FilePath);
        Queue.OpenAlbum = item => OpenAlbumForMedia(item.Media);
        Queue.OpenArtist = item => OpenArtistForMedia(item.Media);

        // The narrow layout replaces the docked playback bar and the Now Playing page with a
        // draggable bottom sheet layered over the current page; the two presentations hand off to
        // each other in SetViewportWidth.
        NowPlayingSheet = new NowPlayingSheetViewModel(PlaybackBar, Queue, CreateNowPlayingContent);
        NowPlayingSheet.PropertyChanged += OnNowPlayingSheetPropertyChanged;

        // Video is a sheet of its own, layered over every layout rather than navigated to: it
        // covers the shell while maximized and shrinks to a corner card while the library is
        // browsed behind it. Music is stood down for as long as it exists.
        VideoSheet = new VideoSheetViewModel(CloseVideoSheet, stateStore);
        VideoSheet.PropertyChanged += OnVideoSheetPropertyChanged;

        // Starting music while a video is on the sheet ends the video: the two cannot share the
        // audio device, and the user has plainly asked for the queue back.
        playbackController.PlaybackRequestedWhileSuspended += CloseVideoSheet;

        ShuffleAllCommand = new AsyncRelayCommand(ShuffleAllAsync);
        SaveQueueAsPlaylistCommand = new AsyncRelayCommand(SaveQueueAsPlaylistFromMenuAsync);
        RecentlyAddedCommand = new RelayCommand(RecentlyAdded);
        RefreshLibraryCommand = new AsyncRelayCommand(RefreshLibraryAsync, CanRefreshLibrary);
        ToggleQueueCommand = new RelayCommand(ToggleQueue);
        OpenStreamLinkCommand = new AsyncRelayCommand(OpenStreamLinkAsync, () => dialogService is not null);
        AboutCommand = new RelayCommand(() => _ = dialogService?.ShowAboutAsync());
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, CanCheckForUpdates);
        DownloadUpdateCommand = new RelayCommand(DownloadUpdate);
        ViewWarningsCommand = new RelayCommand(() => { });
        SetSortCommand = new RelayCommand<LibrarySortField>(SetSort);
        SortByColumnCommand = new RelayCommand<LibrarySortField>(SortByColumn);
        SortAscendingCommand = new RelayCommand(() => SortAscending = true);
        SortDescendingCommand = new RelayCommand(() => SortAscending = false);
        SetLayoutCommand = new RelayCommand<LibraryLayoutMode>(SetLayout);
        SetVideoNarrowPresentationCommand = new RelayCommand<LibraryItemPresentation>(SetVideoNarrowPresentation);
        GoHomeCommand = new RelayCommand(() => NavigateTopLevel(AppRouteId.Home));
        OpenLibraryCommand = new RelayCommand(OpenLibrary);
        OpenVideoLibraryCommand = new RelayCommand(() => NavigateTopLevel(AppRouteId.Videos));
        OpenPlaylistsCommand = new RelayCommand(() => NavigateTopLevel(AppRouteId.Playlists));
        OpenSettingsCommand = new RelayCommand(() => NavigateTopLevel(AppRouteId.Settings));
        NarrowBackCommand = new RelayCommand(() => TryGoBack());
        GoBackCommand = new RelayCommand(() => TryGoBack(), () => CanNavigateBack);
        GoForwardCommand = new RelayCommand(GoForward, () => CanGoForward);
        GoToRouteCommand = new RelayCommand<AppRouteId>(NavigateTopLevel);

        PlatformServices.FullScreenChanged = value =>
        {
            if (CurrentPage is PhotoViewerPageViewModel photo && !VideoSheet.IsFullScreen)
            {
                photo.IsFullScreen = value;
            }
            else if (VideoSheet.Content is { } video)
            {
                video.IsFullScreen = value;
            }
        };

        trackActions = new TrackActionCallbacks
        {
            Play = PlayTrack,
            PlayNext = InsertTrackNext,
            AddToQueue = AddTrackToQueue,
            AddToPlaylist = AddTrackToPlaylist,
            OpenAlbum = OpenAlbumForTrack,
            OpenArtist = OpenArtistForTrack,
            ShowProperties = track => ShowProperties(track.Media, track.Title),
            OpenContainingFolder = OpenTrackFolder,
            PlayFolder = PlayMusicFolderAsync,
            PlayFolderNext = PlayMusicFolderNextAsync,
            AddFolderToQueue = AddMusicFolderToQueueAsync,
            AddFolderToPlaylist = AddMusicFolderToPlaylistAsync,
        };

        albumTileActions = new TileActionCallbacks
        {
            Open = OpenAlbumTile,
            Play = PlayAlbumTile,
            PlayNext = PlayAlbumTileNext,
            AddToQueue = AddAlbumTileToQueue,
            AddToPlaylist = AddAlbumTileToPlaylist,
            OpenArtist = OpenArtistForAlbumTile,
            OpenContainingFolder = OpenAlbumTileFolder,
        };

        artistTileActions = new TileActionCallbacks
        {
            Open = OpenArtistTile,
            Play = PlayArtistTile,
            PlayNext = PlayArtistTileNext,
            AddToQueue = AddArtistTileToQueue,
            AddToPlaylist = AddArtistTileToPlaylist,
            OpenContainingFolder = OpenArtistTileFolder,
        };

        libraryCommands = new LibraryCommands
        {
            TrackActions = trackActions,
            AlbumTileActions = albumTileActions,
            ArtistTileActions = artistTileActions,
            PlayTracks = PlayTracks,
            ShuffleTracks = ShuffleTracks,
            PlayTracksNext = tracks => playbackController.InsertNextRange(tracks.Select(ToQueueItem).ToList()),
            AddTracksToQueue = AddTracksToQueue,
            AddTracksToPlaylist = AddTracksToPlaylist,
            AddTracksToPlaylistAsync = AddTracksToPlaylistAsync,
            OpenAlbum = id => Navigate(AppRouteId.AlbumDetail, id),
            OpenArtist = id => Navigate(AppRouteId.ArtistDetail, id),
            GoBack = NavigateBack,
        };

        RestorePlaybackQueue();

        themeService.ApplyFromSettings();
        themeService.BackdropStyleChanged += OnBackdropStyleChanged;
        SubscribeToScanEvents();
        SubscribeToRemoteRefreshStatus();
        sourceRegistry.CatalogChanged += OnCatalogChanged;
        SubscribeToRemoteServerState();
        RestoreNavigation();
    }

    /// <summary>
    /// Mirrors the head's HTTP server state into the pinned shell indicator, so the user always
    /// sees when this machine is sharing its library over the network.
    /// </summary>
    private void SubscribeToRemoteServerState()
    {
        if (PlatformServices.RemoteApiServerController is not { } serverController)
        {
            return;
        }

        UpdateRemoteServerState(serverController.State);
        serverController.StateChanged += (_, _) =>
        {
            var state = serverController.State;
            if (Dispatcher.UIThread.CheckAccess())
            {
                UpdateRemoteServerState(state);
            }
            else
            {
                Dispatcher.UIThread.Post(() => UpdateRemoteServerState(state));
            }
        };
    }

    private void UpdateRemoteServerState(RemoteApiServerState state)
    {
        IsServerRunning = state.IsRunning;
        ServerStatusText = state.IsRunning
            ? string.Format(Strings.Resources.Shell_ServerRunning, state.Address)
            : string.Empty;
    }

    public IReadOnlyList<AppRoute> Routes { get; }

    public NavigationViewModel Navigation { get; }

    /// <summary>The narrow (mobile) layout's swipeable library-section pager.</summary>
    public LibraryPagerViewModel LibraryPager { get; }

    public SearchBoxViewModel Search { get; }

    public PlaybackBarViewModel PlaybackBar { get; }

    public QueuePaneViewModel Queue { get; }

    /// <summary>The narrow (mobile) now-playing bottom sheet that stands in for the playback bar.</summary>
    public NowPlayingSheetViewModel NowPlayingSheet { get; }

    /// <summary>The video player sheet layered over the shell, on every layout.</summary>
    public VideoSheetViewModel VideoSheet { get; }

    public IReadOnlyList<LibrarySortOption> SortOptions { get; }

    public IRelayCommand ShuffleAllCommand { get; }

    public IAsyncRelayCommand SaveQueueAsPlaylistCommand { get; }

    public IRelayCommand RecentlyAddedCommand { get; }

    public IAsyncRelayCommand RefreshLibraryCommand { get; }

    public IRelayCommand ToggleQueueCommand { get; }

    public IAsyncRelayCommand OpenStreamLinkCommand { get; }

    public IRelayCommand AboutCommand { get; }

    public IAsyncRelayCommand CheckForUpdatesCommand { get; }

    public IRelayCommand DownloadUpdateCommand { get; }

    public IRelayCommand ViewWarningsCommand { get; }

    public IRelayCommand<LibrarySortField> SetSortCommand { get; }

    public IRelayCommand<LibrarySortField> SortByColumnCommand { get; }

    public IRelayCommand SortAscendingCommand { get; }

    public IRelayCommand SortDescendingCommand { get; }

    public IRelayCommand<LibraryLayoutMode> SetLayoutCommand { get; }

    public IRelayCommand<LibraryItemPresentation> SetVideoNarrowPresentationCommand { get; }

    /// <summary>Narrow-layout bottom navigation: opens the Home page.</summary>
    public IRelayCommand GoHomeCommand { get; }

    /// <summary>Narrow-layout bottom navigation: opens the library section (last-viewed tab).</summary>
    public IRelayCommand OpenLibraryCommand { get; }

    /// <summary>Narrow-layout bottom navigation: opens the video library.</summary>
    public IRelayCommand OpenVideoLibraryCommand { get; }

    /// <summary>Narrow-layout bottom navigation: opens the Playlists page.</summary>
    public IRelayCommand OpenPlaylistsCommand { get; }

    /// <summary>Narrow-layout bottom navigation: opens the Settings page.</summary>
    public IRelayCommand OpenSettingsCommand { get; }

    /// <summary>Native menu "Go &gt; Back": steps back one navigation entry when possible.</summary>
    public IRelayCommand GoBackCommand { get; }

    /// <summary>Native menu "Go &gt; Forward": returns to the entry a prior Back stepped away from.</summary>
    public IRelayCommand GoForwardCommand { get; }

    /// <summary>Native menu "Go": navigates to a top-level route (a no-op when already active).</summary>
    public IRelayCommand<AppRouteId> GoToRouteCommand { get; }

    public bool IsSortDescending => !SortAscending;

    public bool HasScanStatusText => !string.IsNullOrWhiteSpace(ScanStatusText);

    public bool IsUpdateCheckSupported => IsBrowserUpdateCheckSupported || updateCheckService?.IsSupported == true;

    public bool IsBrowserUpdateCheckSupported => BrowserUpdates is not null;

    public IBrowserUpdateService? BrowserUpdates => PlatformServices.BrowserUpdates;

    /// <summary>
    /// Visibility of the non-interactive update status row (checking / up-to-date / failed). The
    /// "update available" state is presented by a separate clickable row, so it is excluded here.
    /// </summary>
    public bool ShowUpdateStatus => !IsUpdateAvailable && (IsUpdateCheckActive || !string.IsNullOrWhiteSpace(UpdateStatusText));

    // Keep these widths linked to PhotoViewerPageView.GetBackPreviewMargin.
    /// <summary>Compact navigation-rail width for the wide layout; collapses to 0 in the narrow layout.</summary>
    public double NavRailCompactWidth => IsNarrow || !IsChromeVisible ? 0d : 56d;

    /// <summary>Width of the expanded navigation pane, collapsed while another surface owns it.</summary>
    public double NavPaneOpenWidth => IsChromeVisible ? (IsAppleLayout ? AppleSidebarWidth : 320d) : 0d;

    public const double AppleSidebarWidth = 220d;

    // The Apple layout's docked queue matches its sidebar.
    private double QueuePinnedWidth => IsAppleLayout ? AppleSidebarWidth : QueuePaneViewModel.DefaultWidth;

    // Clears the native macOS window controls over the shell's top-left corner.
    private const double WindowControlsWidth = 80d;

    // Keep linked to the AppleTopBar height in ShellView.axaml.
    private const double AppleToolbarHeight = 52d;

    /// <summary>
    /// Whether the wide layout takes its Apple form: a sidebar with a toolbar over the page, and the
    /// playback bar floating over the page bottom. See <see cref="IThemeService.IsAppleChromeEnabled"/>.
    /// </summary>
    public bool IsAppleLayout => isAppleChromeEnabled && !IsNarrow;

    /// <summary>Whether the Apple layout's sidebar stays expanded, as macOS and iPadOS sidebars do.</summary>
    public bool IsSidebarPinned => IsAppleLayout;

    public bool UseCustomWindowControls => PlatformServices.SupportsClientSideWindowControls && isAppleChromeEnabled;

    /// <summary>
    /// Whether the window's title bar is merged into the shell, leaving the window controls over the
    /// sidebar. Video and photo views clear <see cref="WindowControlsInset"/> while they cover it.
    /// </summary>
    public bool ExtendsIntoTitleBar => (IsSidebarPinned && OperatingSystem.IsMacOS()) || UseCustomWindowControls;

    /// <summary>
    /// Space the window controls take at the shell's top-left corner. Desktop hides them in full
    /// screen; iPadOS keeps them over the window whatever the shell shows.
    /// </summary>
    public Thickness WindowControlsInset => ExtendsIntoTitleBar
        ? (VideoSheet.IsFullScreen ? default : new Thickness(UseCustomWindowControls ? 104d : WindowControlsWidth, 0, 0, 0))
        : new Thickness(DeviceWindowControls.Left, 0, 0, 0);

    /// <summary>The narrow top bar's margin, clear of the window controls.</summary>
    public Thickness NarrowTopBarMargin => new(8 + WindowControlsInset.Left, 0, 8, 0);

    /// <summary>Height the navigation pane leaves clear above its entries for the window controls.</summary>
    public double NavWindowControlsHeight => ExtendsIntoTitleBar || DeviceWindowControls.Top > 0 ? AppleToolbarHeight : 0;

    /// <summary>
    /// Whether the shell offers the back affordance itself (the narrow top bar or the Apple toolbar),
    /// so pages leave out their own back buttons.
    /// </summary>
    public bool HasShellBackButton => IsNarrow || IsAppleLayout;

    /// <summary>Whether the Apple layout's toolbar is shown over the page.</summary>
    public bool ShowAppleTopBar => IsAppleLayout && IsChromeVisible;

    /// <summary>Whether the playback bar is docked below the shell.</summary>
    public bool ShowDockedPlaybackBar => ShowPlaybackBar && !IsAppleLayout;

    /// <summary>Whether the playback bar floats over the bottom of the page.</summary>
    public bool ShowFloatingPlaybackBar => ShowPlaybackBar && IsAppleLayout;

    /// <summary>
    /// Whether the shell's navigation chrome is visible. A photo owns the page surface outright,
    /// while a maximized video owns the whole window; either can remain active behind the other.
    /// </summary>
    public bool IsChromeVisible =>
        (backPresentation?.To ?? CurrentPage) is not PhotoViewerPageViewModel && !VideoSheet.IsShellChromeHidden;

    /// <summary>Whether the wide layout's navigation rail is shown.</summary>
    public bool IsNavPaneVisible => !IsNarrow && IsChromeVisible;

    /// <summary>
    /// The Settings page whose sections the Apple layout's sidebar lists in place of the destinations
    /// while the page is open, or null.
    /// </summary>
    public SettingsPageViewModel? SettingsSidebar => IsAppleLayout ? CurrentPage as SettingsPageViewModel : null;

    /// <summary>Whether the navigation pane shows its destinations and status rows.</summary>
    public bool ShowNavEntries => IsNavPaneVisible && SettingsSidebar is null;

    /// <summary>
    /// Presents Settings in a window of its own over the main window, completing once it closes, or
    /// brings forward the window already showing the page. Assigned by the macOS main window; the
    /// Apple layout opens Settings there instead of navigating to it.
    /// </summary>
    public Func<SettingsPageViewModel, Task>? ShowSettingsWindow { get; set; }

    /// <summary>Whether the narrow layout's top bar and bottom navigation are shown.</summary>
    public bool IsNarrowChromeVisible => IsNarrow && IsChromeVisible;

    /// <summary>
    /// Whether the docked playback bar is shown. The narrow layout presents playback through the
    /// now-playing sheet instead, so the bar is wide-layout only.
    /// </summary>
    public bool ShowPlaybackBar =>
        !IsNarrow && IsChromeVisible && !VideoSheet.IsActive && PlaybackBar.HasItem;

    /// <summary>True when the active route is one of the library tabs (music / albums / artists).</summary>
    public bool IsLibrarySection => currentParameter is not MusicFolderReference &&
        Array.IndexOf(LibrarySectionRoutes, currentRouteId) >= 0;

    /// <summary>Narrow-layout bottom-navigation highlight for "Home".</summary>
    public bool IsHomeSelected => currentRouteId == AppRouteId.Home;

    /// <summary>Narrow-layout bottom-navigation highlight for "Library" (any library tab is active).</summary>
    public bool IsLibrarySelected => Array.IndexOf(LibrarySectionRoutes, currentRouteId) >= 0;

    /// <summary>Narrow-layout bottom-navigation highlight for "Settings".</summary>
    public bool IsSettingsSelected => currentRouteId == AppRouteId.Settings;

    /// <summary>Narrow-layout bottom-navigation highlight for "Video".</summary>
    public bool IsVideoSelected => currentRouteId == AppRouteId.Videos;

    /// <summary>Narrow-layout bottom-navigation highlight for "Playlists".</summary>
    public bool IsPlaylistsSelected => currentRouteId == AppRouteId.Playlists;

    /// <summary>Whether the video library is reachable at all on this head.</summary>
    public bool HasVideoLibrary => videoLibraryQueryService is not null;

    /// <summary>
    /// Shows the narrow layout's library sub-tab strip: only while the shell is narrow, a library
    /// tab is active, and the page is showing its list rather than a drilled-in detail. A
    /// master-detail library page (songs / albums-or-artists-as-list) that has opened
    /// its inline detail in the compact single-pane layout hides the tabs so the detail owns the
    /// full height, matching the shell back button that stands in for the tab strip while drilled in.
    /// </summary>
    public bool ShowLibraryTabs => backPresentation?.ShowsLibraryTabs ?? (IsNarrow && IsLibrarySection && !IsInPageDetailOpen);

    /// <summary>
    /// Whether the narrow layout is presenting the library sections through the swipe pager. While
    /// it is, the shell's routed content host stands down (see <see cref="RoutedPage"/>) and the
    /// pager hosts the current page instead, so the neighboring sections can be swiped into view.
    /// </summary>
    public bool ShowLibraryPager => IsNarrow && IsLibrarySection;

    /// <summary>
    /// Page shown by the shell's routed content host. Null while the library pager is showing, which
    /// hosts <see cref="CurrentPage"/> itself: a page view model can only be presented in one place.
    /// </summary>
    public PageViewModelBase? RoutedPage => ShowLibraryPager ? null : CurrentPage;

    /// <summary>
    /// True when the active page is a master-detail page currently showing its detail in the compact
    /// single-pane layout (its inline detail is open). Mirrors
    /// <see cref="IInPageBackHandler.CanHandleBack"/>.
    /// </summary>
    private bool IsInPageDetailOpen => CurrentPage is IInPageBackHandler handler && handler.CanHandleBack;

    /// <summary>
    /// Opens an external URI in the platform browser. Assigned by the view, which owns the
    /// <c>TopLevel</c> needed to reach the platform launcher (works on desktop and Android).
    /// </summary>
    public Func<string, Task>? OpenExternalUrl { get; set; }

    public bool IsSortByTitle => SelectedSort.Field == LibrarySortField.Title;

    public bool IsSortByAlbumName => SelectedSort.Field == LibrarySortField.AlbumName;

    public bool IsSortByArtistName => SelectedSort.Field == LibrarySortField.ArtistName;

    public bool IsSortByGenre => SelectedSort.Field == LibrarySortField.Genre;

    public bool IsSortByYear => SelectedSort.Field == LibrarySortField.Year;

    public bool IsSortByDateAdded => SelectedSort.Field == LibrarySortField.DateAdded;

    public bool IsSortByDuration => SelectedSort.Field == LibrarySortField.Duration;

    public bool IsSortByFileType => SelectedSort.Field == LibrarySortField.FileType;

    public bool IsSortByFileSize => SelectedSort.Field == LibrarySortField.FileSize;

    public bool CanSortItems => activeBrowsePage is not null;

    public bool CanSortByTitle => IsSortFieldVisible(LibrarySortField.Title);

    public bool CanSortByAlbumName => IsSortFieldVisible(LibrarySortField.AlbumName);

    public bool CanSortByArtistName => IsSortFieldVisible(LibrarySortField.ArtistName);

    public bool CanSortByGenre => IsSortFieldVisible(LibrarySortField.Genre);

    public bool CanSortByYear => IsSortFieldVisible(LibrarySortField.Year);

    public bool CanSortByDateAdded => IsSortFieldVisible(LibrarySortField.DateAdded);

    public bool CanSortByDuration => IsSortFieldVisible(LibrarySortField.Duration);

    public bool CanSortByFileType => IsSortFieldVisible(LibrarySortField.FileType);

    public bool CanSortByFileSize => IsSortFieldVisible(LibrarySortField.FileSize);

    private bool IsSortFieldVisible(LibrarySortField field) =>
        activeBrowseKind is null || LibraryViewSettings.SupportedSorts(activeBrowseKind.Value).Contains(field);

    /// <summary>Whether the active page offers the grid/list layout choice (albums and artists do).</summary>
    public bool CanChooseLayout => activeBrowseKind is LibraryItemKind.Album or LibraryItemKind.Artist;

    /// <summary>Whether the active page offers the flat/folder view choice.</summary>
    public bool CanChooseVideoLayout => activeBrowseKind is LibraryItemKind.Video or LibraryItemKind.Song;

    public bool IsLayoutGrid => SelectedLayout == LibraryLayoutMode.Grid;

    public bool IsLayoutList => SelectedLayout == LibraryLayoutMode.List;

    public bool IsLayoutFolders => SelectedLayout == LibraryLayoutMode.Folders;

    public bool CanChooseVideoPresentation => IsNarrow && activeBrowsePage is MediaPageViewModel;

    public bool IsVideoNarrowGrid => activeBrowsePage is MediaPageViewModel { NarrowPresentation: LibraryItemPresentation.Grid };

    public bool IsVideoNarrowList => activeBrowsePage is MediaPageViewModel { NarrowPresentation: LibraryItemPresentation.List };

    public void Navigate(AppRouteId routeId, object? parameter = null) =>
        NavigateInternal(routeId, parameter, recordHistory: true);

    /// <summary>True when there is a previous page to return to (the back stack is non-empty).</summary>
    public bool CanGoBack => navigationHistory is null ? backStack.Count > 0 : historyIndex > 0;

    /// <summary>True when a prior Back can be reversed with Forward (the forward stack is non-empty).</summary>
    public bool CanGoForward => navigationHistory is null ? forwardStack.Count > 0 : historyIndex < historyMaxIndex;

    /// <summary>
    /// Whether any back action is currently available: either the shell can pop its navigation stack,
    /// or the active page can consume a back internally (a master-detail page collapsing its open
    /// detail on the narrow single-pane layout).
    /// </summary>
    public bool CanNavigateBack =>
        IsVideoSheetAddressed
        || NowPlayingSheet.IsExpanded
        || CanGoBack
        || (CurrentPage is IInPageBackHandler handler && handler.CanHandleBack);

    /// <summary>
    /// Whether the shell shows its own back button in the top-left of the narrow layout. Desktop
    /// narrow layouts have no global back gesture, so the shell supplies one that stands in for the
    /// platform back key; Android keeps its system back and the wide layout uses the per-page back
    /// buttons instead. Hidden while the search field replaces the title bar, or when there is
    /// nothing to go back to.
    /// </summary>
    public bool ShowNarrowBackButton =>
        IsNarrow && !PlatformServices.HostProvidesBackNavigation && !IsSearchActive && CanNavigateBack;

    /// <summary>Invokes the shell's simulated global back key (the narrow-layout back button).</summary>
    public IRelayCommand NarrowBackCommand { get; }

    /// <summary>
    /// Navigates back one entry when possible, returning true if a back navigation occurred. Lets
    /// platform back gestures (Android system back, desktop mouse back button, and the in-page back
    /// buttons on detail / now-playing pages) share one code path and decide whether to consume the
    /// gesture or defer to the platform default (minimize / exit) when there is nothing to pop.
    /// </summary>
    public bool TryGoBack()
    {
        // The video sheet is layered over everything and is not part of the shell's page, so a back
        // gesture drops it into its corner card before any history is consulted. Playback goes on
        // there; only the close button ends it. On a head with a host history the collapse is
        // reported to it, so the address and the app stay in step.
        if (IsVideoSheetAddressed)
        {
            VideoSheet.IsExpanded = false;
            return true;
        }

        // With a host history the shell is not the source of truth: stepping the host back raises
        // LocationChanged, and applying that entry unwinds whatever it recorded (a layered surface
        // or a whole page) in the same order this method would.
        if (navigationHistory is not null)
        {
            if (!CanNavigateBack)
            {
                return false;
            }

            navigationHistory.Go(-1);
            return true;
        }

        // The narrow now-playing sheet is layered over the current page rather than being a
        // navigation entry, so a back gesture minimizes it before anything else is considered.
        if (NowPlayingSheet.IsExpanded)
        {
            NowPlayingSheet.IsExpanded = false;
            return true;
        }

        // Give the active page a chance to consume the gesture first. Master-detail pages (playlists,
        // settings) collapse their open detail back to their own list on narrow layouts, so the first
        // back returns to the page menu and only a subsequent back pops the shell's navigation stack.
        if (CurrentPage is IInPageBackHandler handler && handler.TryHandleBack())
        {
            return true;
        }

        if (backStack.Count == 0)
        {
            return false;
        }

        GoBack();
        return true;
    }

    /// <summary>
    /// Back entry point for page chrome (the detail pages' header buttons). Steps the host history
    /// when there is one so the address bar and the app stay in step; otherwise pops the shell's own
    /// stack directly.
    /// </summary>
    private void NavigateBack()
    {
        if (navigationHistory is not null)
        {
            navigationHistory.Go(-1);
            return;
        }

        GoBack();
    }

    internal PageViewModelBase? PreviewPhotoBack(PhotoViewerPageViewModel photo)
    {
        if (!ReferenceEquals(CurrentPage, photo) || !backStack.TryPeek(out var entry))
            return null;

        var page = ResolvePage(routesById[entry.Id], entry.Parameter, entry.Page);
        ApplyLayout(page);
        backStack.Pop();
        backStack.Push((entry.Id, entry.Parameter, page));
        return page;
    }

    private void NavigateInternal(AppRouteId routeId, object? parameter, bool recordHistory)
    {
        if (!routesById.TryGetValue(routeId, out var route))
        {
            throw new ArgumentOutOfRangeException(nameof(routeId), routeId, "Route is not registered.");
        }

        if (routeId == AppRouteId.Settings && IsAppleLayout && ShowSettingsWindow is { } showSettingsWindow)
        {
            // The window leaves the page in the main window as it is, so the rail keeps its highlight.
            Navigation.SetActive(currentRouteId);
            _ = AppErrorReporter.GuardAsync(() => OpenSettingsWindowAsync(showSettingsWindow, route, parameter));
            return;
        }

        if (route.NavigationSlot == AppNavigationSlot.Primary)
        {
            appSettingsStore.LastUsedRouteId = routeId;
        }

        ReleaseMusicRootForFolder(parameter);

        // Keep browse and search pages alive on Back to preserve loaded results and scroll positions.
        // The narrow layout's library pager owns its pages, so the stack must not retain them.
        PageViewModelBase? retained = null;
        if (recordHistory && CurrentPage is not null)
        {
            retained = (CurrentPage is ILibraryBrowsePageViewModel || IsSearchPage(CurrentPage)) && !LibraryPager.Owns(CurrentPage)
                ? CurrentPage
                : null;
            backStack.Push((currentRouteId, currentParameter, retained));
        }

        // A fresh navigation invalidates any forward history (standard back/forward behavior).
        ClearForwardStack();

        ShowPage(routeId, parameter, ResolvePage(route, parameter), isBack: false, retained);
        SyncHistory(HistorySync.Navigated);
    }

    // Shows Settings in its window, or brings the open one forward at the requested section.
    private async Task OpenSettingsWindowAsync(Func<SettingsPageViewModel, Task> show, AppRoute route, object? parameter)
    {
        if (settingsWindowPage is { } open)
        {
            if (parameter is string sectionKey)
            {
                open.RestoreDetail(sectionKey);
            }

            await show(open);
            return;
        }

        var page = (SettingsPageViewModel)CreatePage(route, parameter);
        settingsWindowPage = page;
        try
        {
            await show(page);
        }
        finally
        {
            settingsWindowPage = null;
            page.Dispose();
        }
    }

    /// <summary>
    /// Picks the page instance a navigation should show: the library pager's live page when it
    /// already holds one for the route (so swiping and navigating land on the same, already loaded
    /// page), otherwise the page cached on the navigation stack, otherwise a fresh one.
    /// </summary>
    private PageViewModelBase ResolvePage(AppRoute route, object? parameter, PageViewModelBase? cached = null)
    {
        if (IsNarrow && parameter is not MusicFolderReference && LibraryPager.GetPage(route.Id) is { } paged)
        {
            if (cached is not null && !ReferenceEquals(cached, paged))
            {
                (cached as IDisposable)?.Dispose();
            }

            return paged;
        }

        return cached ?? CreatePage(route, parameter);
    }

    private void ReleaseMusicRootForFolder(object? parameter)
    {
        if (parameter is MusicFolderReference && currentRouteId == AppRouteId.Songs &&
            currentParameter is null && LibraryPager.Owns(CurrentPage))
        {
            LibraryPager.Remove(AppRouteId.Songs);
        }
    }

    // Builds a page for the narrow layout's library pager, matching what navigating to the same
    // route would produce.
    private PageViewModelBase CreateLibraryPage(AppRouteId routeId)
    {
        var page = CreatePage(routesById[routeId], null);
        ApplyLayout(page);
        return page;
    }

    // Syncs a page to the shell's layout so it renders with the matching insets and chrome.
    internal void ApplyLayout(PageViewModelBase page)
    {
        page.IsNarrow = IsNarrow;
        page.HasShellBackButton = HasShellBackButton;
        page.HasShellPathMenu = isAppleChromeEnabled;
    }

    // Pops <paramref name="steps"/> entries and shows the one landed on. Steps beyond the first are
    // passed over without being shown, so a jump out of a chain (a breadcrumb step) is one
    // transition rather than one per level.
    private void GoBack(int steps = 1)
    {
        if (backStack.Count == 0 || steps <= 0)
        {
            return;
        }

        var retained = IsSearchPage(CurrentPage) ? CurrentPage : null;
        forwardStack.Push((currentRouteId, currentParameter, retained));

        var entry = backStack.Pop();
        for (var i = 1; i < steps && backStack.Count > 0; i++)
        {
            var forwardPage = IsSearchPage(entry.Page) ? entry.Page : null;
            forwardStack.Push((entry.Id, entry.Parameter, forwardPage));
            if (forwardPage is null)
                (entry.Page as IDisposable)?.Dispose();
            entry = backStack.Pop();
        }

        // Reuse the cached browse page when one was kept; otherwise rebuild it from the stored route.
        var page = ResolvePage(routesById[entry.Id], entry.Parameter, entry.Page);
        ShowPage(entry.Id, entry.Parameter, page, isBack: true, retained);
    }

    /// <summary>
    /// Navigates to a location, or steps back to it when the back stack already holds it. Lets a
    /// move up a hierarchy (a video breadcrumb step, Go up) leave the chain the way it was entered:
    /// the drill-out transition plays and the page returns with its items and scroll position,
    /// instead of the history growing a second copy of a folder the user came from.
    /// </summary>
    private void NavigateOrGoBackTo(AppRouteId routeId, object? parameter)
    {
        var steps = FindBackDepth(routeId, parameter);
        if (steps == 0)
        {
            Navigate(routeId, parameter);
            return;
        }

        // Layered surfaces are host entries of their own, and the navigation collapses them, so they
        // are part of the jump as far as the host is concerned.
        var overlayDepth = PageOverlayDepth;

        GoBack(steps);

        // Keep a host history in step: it holds one entry per shell navigation, so the same jump
        // back lands on the entry just shown. Applying it once the host reports the move is a no-op.
        navigationHistory?.Go(-(steps + overlayDepth));
    }

    // How many back steps away the nearest entry addressing the given location is, or 0 when the
    // back stack holds none.
    private int FindBackDepth(AppRouteId routeId, object? parameter)
    {
        var depth = 0;
        foreach (var entry in backStack)
        {
            depth++;
            if (entry.Id == routeId && Equals(entry.Parameter, parameter))
            {
                return depth;
            }
        }

        return 0;
    }

    // Returns to the entry a prior Back stepped away from. Records the page being left on the back
    // stack exactly like a fresh navigation (retaining browse pages so a later Back reuses their
    // loaded state) and resolves the forward target from its cached page or route.
    private void GoForward()
    {
        // The host history owns the forward entries when there is one; stepping it forward raises
        // LocationChanged, which reapplies the entry (page or layered surface) the user left.
        if (navigationHistory is not null)
        {
            if (CanGoForward)
            {
                navigationHistory.Go(1);
            }

            return;
        }

        GoForwardCore();
    }

    private void GoForwardCore()
    {
        if (forwardStack.Count == 0)
        {
            return;
        }

        var entry = forwardStack.Pop();

        ReleaseMusicRootForFolder(entry.Parameter);

        var retained = (CurrentPage is ILibraryBrowsePageViewModel || IsSearchPage(CurrentPage)) && !LibraryPager.Owns(CurrentPage)
            ? CurrentPage
            : null;
        backStack.Push((currentRouteId, currentParameter, retained));

        var page = ResolvePage(routesById[entry.Id], entry.Parameter, entry.Page);
        ShowPage(entry.Id, entry.Parameter, page, isBack: false, retained);
    }

    private static bool IsSearchPage(PageViewModelBase? page) =>
        page is SearchPageViewModel or PhotoSearchPageViewModel or MusicSearchPageViewModel;

    // Discards the forward history. A fresh navigation invalidates any pages queued for Forward;
    // dispose any that carried a live page so none are leaked.
    private void ClearForwardStack()
    {
        if (forwardStack.Count == 0)
        {
            return;
        }

        foreach (var entry in forwardStack)
        {
            (entry.Page as IDisposable)?.Dispose();
        }

        forwardStack.Clear();
    }

    private void ShowPage(AppRouteId routeId, object? parameter, PageViewModelBase page, bool isBack, PageViewModelBase? retained,
        IPageTransition? transitionOverride = null)
    {
        // A navigation collapses layered surfaces and swaps the page in one go; the host history is
        // synced once for the finished state instead of once per intermediate change.
        SuspendHistorySync(() => ShowPageCore(routeId, parameter, page, isBack, retained, transitionOverride));
    }

    private void ShowPageCore(AppRouteId routeId, object? parameter, PageViewModelBase page, bool isBack, PageViewModelBase? retained,
        IPageTransition? transitionOverride)
    {
        // Drill in or out when either endpoint is hierarchical; slide up between root pages.
        var isHierarchical = (CurrentPage is not null && IsHierarchicalRoute(currentRouteId, currentParameter))
                             || IsHierarchicalRoute(routeId, parameter);

        // The narrow now-playing sheet is layered over the routed content, so it would otherwise
        // hide whatever a navigation it started (its own "go to artist / album", or the same actions
        // on a queue item's menu) just brought up. Closing it here covers every entry point,
        // including the asynchronous album/artist lookups that navigate well after the tap.
        NowPlayingSheet.IsExpanded = false;

        currentRouteId = routeId;
        currentParameter = parameter;

        var previous = CurrentPage;

        // Back navigation plays the transition in reverse. Both transition flags must be set before
        // CurrentPage so the content host picks them up for this swap.
        IsBackNavigation = isBack;
        IPageTransition transition = !IsNarrow && previous is SongsPageViewModel && page is SongsPageViewModel && isHierarchical
            ? narrowRootTransition
            : SelectPageTransition(isHierarchical);
        CurrentPageTransition = transitionOverride ?? PreparePageTransition?.Invoke(previous, page, transition) ?? transition;
        bool retainedForTransition = previous is IDisposable && CurrentPageTransition is ConnectedPageTransition &&
            !ReferenceEquals(previous, retained) && !LibraryPager.Owns(previous);
        if (retainedForTransition)
            ((ConnectedPageTransition)CurrentPageTransition).RetainOutgoingPage(previous!);

        // The narrow layout hosts the library sections in the swipe pager rather than in the routed
        // content host, so hand the page over before it is published as the current page.
        if (IsNarrow && IsLibrarySection)
        {
            LibraryPager.Adopt(routeId, page);
        }

        CurrentPage = page;
        // Sync the new page to the shell's current layout so it renders with the correct (wide vs.
        // narrow) content insets and chrome from its first frame; later resizes flow through
        // OnIsNarrowChanged.
        ApplyLayout(page);
        // Dispose the page we navigated away from unless it was retained on the back stack for a
        // later return, or is held by the library pager (both are reused, so disposing them would
        // tear down live state).
        if (!retainedForTransition && !ReferenceEquals(previous, retained) && !LibraryPager.Owns(previous))
        {
            (previous as IDisposable)?.Dispose();
        }

        AfterPageShown(routeId, page);
    }

    // Derived shell state that follows whichever page is on screen, whether it arrived through a
    // navigation or by swiping the narrow layout's library pager.
    private void AfterPageShown(AppRouteId routeId, PageViewModelBase page)
    {
        RefreshVideoPlaybackProgress(page);

        SetActiveBrowsePage(page as ILibraryBrowsePageViewModel);
        Navigation.SetActive(routeId);
        NotifyBackAffordanceChanged();

        // Remember the active library tab so the narrow "Library" button returns to it.
        if (Array.IndexOf(LibrarySectionRoutes, routeId) >= 0)
        {
            lastLibraryRouteId = routeId;
        }

        LibraryPager.CanSwipe = !IsInPageDetailOpen;

        // Refresh the narrow layout's derived selection/highlight state for the new route.
        OnPropertyChanged(nameof(IsLibrarySection));
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsLibrarySelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(IsVideoSelected));
        OnPropertyChanged(nameof(IsPlaylistsSelected));
        OnPropertyChanged(nameof(ShowLibraryTabs));
        OnPropertyChanged(nameof(ShowLibraryPager));
        OnPropertyChanged(nameof(RoutedPage));
    }

    // The user changed library section by swiping the pager (or by tapping a tab while it is
    // showing). The pages are already built and hosted side by side, so this is not a navigation:
    // the new section is adopted as the current route in place, with no transition and no history
    // entry of its own.
    private void OnLibraryPageSelected(AppRouteId routeId)
    {
        if (routeId == currentRouteId || LibraryPager.GetPage(routeId) is not { } page)
        {
            return;
        }

        appSettingsStore.LastUsedRouteId = routeId;
        currentRouteId = routeId;
        currentParameter = null;
        ApplyLayout(page);
        CurrentPage = page;

        AfterPageShown(routeId, page);

        // Swiping the pager is not a navigation (the sections are already live side by side), so the
        // host history records the new section in place instead of gaining a back step.
        SyncHistory(HistorySync.StateChanged);
    }

    /// <summary>
    /// Picks the page transition for the pending navigation. The wide layout keeps the drill-in
    /// (detail) and entrance slide-up (root) transitions. The narrow (mobile) layout instead slides
    /// hierarchical navigations horizontally like a left/right swipe and switches between top-level
    /// views instantly with no animation.
    /// </summary>
    private IPageTransition SelectPageTransition(bool isHierarchical)
    {
        if (IsNarrow)
        {
            return isHierarchical ? narrowHierarchicalTransition : narrowRootTransition;
        }

        return isHierarchical ? hierarchicalPageTransition : rootPageTransition;
    }

    /// <summary>
    /// Pages that sit below a root page in the navigation hierarchy. Navigating to or from one of
    /// these plays the drill-in/out transition; every other navigation is treated as a root switch.
    /// </summary>
    private bool IsHierarchicalRoute(AppRouteId routeId, object? parameter) =>
        routeId == AppRouteId.NowPlaying
        // A video library folder is a level below the library root rather than a root of its own,
        // so descending into one drills in (and Back out of it plays the same transition reversed).
        || (routeId == AppRouteId.Videos && parameter is VideoFolderReference)
        || (routeId == AppRouteId.Songs && parameter is MusicFolderReference)
        || (routesById.TryGetValue(routeId, out var route) && route.Kind == AppRouteKind.Detail);

    private PageViewModelBase CreatePage(AppRoute route, object? parameter)
    {
        if (route.Id == AppRouteId.Settings)
        {
            var settings = new SettingsPageViewModel(
                appSettingsStore,
                themeService,
                dialogService,
                RefreshLibraryCommand,
                lyricSourceService,
                rate => playbackController.PreferredSampleRate = rate,
                alwaysResample => playbackController.AlwaysResample = alwaysResample,
                systemSampleRateProvider,
                audioOutputSettings,
                ClearPlaybackHistoriesAsync,
                lyricsService)
            {
                IsSidebarLayout = IsAppleLayout,
            };

            // A section key parameter deep-links straight to that section (see OpenRemoteAccessSettings).
            if (parameter is string sectionKey)
            {
                settings.RestoreDetail(sectionKey);
            }

            return settings;
        }

        return route.Id switch
        {
            AppRouteId.Songs => CreateSongsPage(parameter),
            AppRouteId.Albums => CreateAlbumsPage(),
            AppRouteId.Artists => CreateArtistsPage(),
            AppRouteId.AlbumDetail => CreateAlbumDetailPage(parameter, route),
            AppRouteId.ArtistDetail => CreateArtistDetailPage(parameter, route),
            AppRouteId.Search => CreateSearchPage(parameter),
            AppRouteId.PhotoSearch => CreatePhotoSearchPage(parameter, route),
            AppRouteId.AlbumSearch => CreateMusicSearchPage(parameter, LibraryItemKind.Album),
            AppRouteId.ArtistSearch => CreateMusicSearchPage(parameter, LibraryItemKind.Artist),
            AppRouteId.SongSearch => CreateMusicSearchPage(parameter, LibraryItemKind.Song),
            AppRouteId.Playlists => CreatePlaylistsPage(route),
            AppRouteId.PlaylistDetail => CreatePlaylistDetailPage(parameter, route),
            AppRouteId.Home => CreateHomePage(),
            AppRouteId.NowPlaying => CreateNowPlayingPage(),
            AppRouteId.Videos => CreateVideoPage(parameter),
            AppRouteId.PhotoViewer => CreatePhotoPage(parameter, route),
            _ => PlaceholderPageViewModel.Placeholder(route),
        };
    }

    /// <summary>
    /// The photo viewer receives the current photo and its ordered neighboring file references.
    /// </summary>
    private PageViewModelBase CreatePhotoPage(object? parameter, AppRoute route) => parameter switch
    {
        PhotoViewerRequest { Photos.Count: > 0 } request => CreatePhotoPage(request),
        MediaReference media when media.Path.Length > 0 =>
            CreatePhotoPage(PhotoViewerRequest.ForOne(media)),
        string path when path.Length > 0 =>
            CreatePhotoPage(PhotoViewerRequest.ForOne(MediaReference.Local(path))),
        _ => PlaceholderPageViewModel.Placeholder(route),
    };

    private PhotoViewerPageViewModel CreatePhotoPage(PhotoViewerRequest request) =>
        new(
            appSettingsStore,
            mediaResolver,
            request,
            new PhotoViewerCallbacks
            {
                LoadPreview = (media, token) => media.SourceId.IsLocal
                    ? photoFolderService.GetThumbnailAsync(media.Path, token)
                    : videoPreviewService?.GetPreviewAsync(media, token) ?? Task.FromResult<Bitmap?>(null),
                Close = NavigateBack,
                SetFullScreen = SetHostFullScreen,
                TryOpenInNewWindow = PlatformServices.TryOpenInNewWindow,
                ShowProperties = dialogService is null ? null : ShowProperties,
                RevealInFileManager = RevealInFileManager,
            });

    // Each folder of the video library is its own page: the parameter is the folder to show, or null
    // for the library root. The flat/folder choice only applies to the root; a folder page is always
    // browsing folders.
    private PageViewModelBase CreateVideoPage(object? parameter)
    {
        if (videoLibraryQueryService is null || videoPreviewService is null)
        {
            return PlaceholderPageViewModel.Placeholder(routesById[AppRouteId.Videos]);
        }

        var folder = parameter as VideoFolderReference;
        var page = new MediaPageViewModel(
            unifiedVideoLibraryQueryService,
            videoPreviewService,
            photoFolderService,
            videoPlaybackStates,
            CreateVideoCallbacks(),
            sourceRegistry,
            folder is null
                ? appSettingsStore.GetLibraryLayout(LibraryItemKind.Video)
                : LibraryLayoutMode.Folders,
            folder,
            videoPlaylistService,
            videoHistory)
        {
            NarrowPresentation = appSettingsStore.VideoNarrowPresentation,
        };
        if (CurrentPage is MediaPageViewModel previous)
        {
            page.SeedBreadcrumb(previous);
        }

        return CreateBrowsePage(page);
    }

    private MediaPageCallbacks CreateVideoCallbacks() =>
        new()
        {
            ChangeViewSettings = ChangeMediaViewSettings,
            OpenVideo = OpenVideoPlayer,
            OpenPhoto = (photo, siblings) => Navigate(
                AppRouteId.PhotoViewer,
                BuildPhotoRequest(photo, siblings)),
            ShowPhotoProperties = dialogService is null
                ? null
                : photo => ShowProperties(photo.Media, photo.FileName),
            OpenPhotoContainingFolder = photo => RevealPhotoInFileManager(photo),
            OpenFolder = value => Navigate(AppRouteId.Videos, value),
            OpenAncestorFolder = value => NavigateOrGoBackTo(AppRouteId.Videos, value),
            ShowProperties = dialogService is null
                ? null
                : video => ShowProperties(
                    new MediaReference(video.SourceId, video.Value.Path),
                    video.Value.DisplayName ?? video.Value.FileName),
            OpenContainingFolder = video => RevealVideoInFileManager(video),
            OpenContainingFolderOfFolder = RevealVideoFolderInFileManager,
            AddToPlaylist = AddVideoToPlaylist,
            AddVideosToPlaylistAsync = AddVideosToPlaylistAsync,
            ClearSubtitleCache = ClearVideoSubtitleCache,
            ClearPlaybackProgress = ClearVideoPlaybackProgress,
            MarkWatched = MarkVideosWatched,
        };

    /// <summary>
    /// Drops everything the playback history setting covers, music and video alike, when the user
    /// turns it off.
    /// </summary>
    private async Task ClearPlaybackHistoriesAsync()
    {
        videoHistory.Clear();
        if (playbackHistoryService is not null)
        {
            await playbackHistoryService.ClearHistoryAsync();
        }
    }

    /// <summary>Appends a video to a video playlist the user picks; music playlists are not offered.</summary>
    private async void AddVideoToPlaylist(Sourced<DbVideoFile> video) =>
        await AppErrorReporter.GuardAsync(() => AddVideosToPlaylistAsync([video]));

    private async Task AddVideosToPlaylistAsync(IReadOnlyList<Sourced<DbVideoFile>> videos)
    {
        if (videoPlaylistService is null || playlistDialogService is null)
        {
            return;
        }

        var target = await playlistDialogService.PickPlaylistAsync(PlaylistKind.Video);
        if (target is not null)
        {
            await videoPlaylistService.AddItemsAsync(
                target.Id,
                videos.Select(video => PlaylistItemModel.FromVideo(video.SourceId, video.Value)));
        }
    }

    /// <summary>Drops a video's imported subtitles, and the saved choice that pointed at them.</summary>
    private void ClearVideoSubtitleCache(Sourced<DbVideoFile> video)
    {
        var media = new MediaReference(video.SourceId, video.Value.Path);
        SubtitleCache.Clear(media);

        if (videoPlaybackStates.Get(media) is { SubtitleSourceKind: SubtitleSourceKind.Cached } state)
        {
            state.SubtitleSource = null;
            state.SubtitleStreamIndex = null;
            state.SubtitleSourceKind = SubtitleSourceKind.Indexed;
            videoPlaybackStates.Save(media, state);
        }
    }

    /// <summary>Drops the videos' display progress without disturbing their audio or subtitle choices.</summary>
    private void ClearVideoPlaybackProgress(IReadOnlyList<Sourced<DbVideoFile>> videos)
    {
        var updates = new List<(MediaReference Video, VideoPlaybackState State)>(videos.Count);
        foreach (var video in videos)
        {
            var media = new MediaReference(video.SourceId, video.Value.Path);
            if (videoPlaybackStates.Get(media) is not { } state)
            {
                continue;
            }

            state.ClearProgress();
            updates.Add((media, state));
        }

        videoPlaybackStates.SaveAll(updates);
    }

    /// <summary>Marks videos watched without playing them, keeping their track choices intact.</summary>
    private void MarkVideosWatched(IReadOnlyList<Sourced<DbVideoFile>> videos)
    {
        var updates = new List<(MediaReference Video, VideoPlaybackState State)>(videos.Count);
        foreach (var video in videos)
        {
            var media = new MediaReference(video.SourceId, video.Value.Path);
            var state = videoPlaybackStates.Get(media) ?? new VideoPlaybackState();

            // The indexed duration is the one the tile measures against; a state carrying its own
            // only wins when the index has none.
            var duration = video.Value.Duration > TimeSpan.Zero
                ? video.Value.Duration.TotalSeconds
                : state.DurationSeconds;
            if (VideoPlaybackProgressPolicy.MarkWatched(state, duration))
            {
                updates.Add((media, state));
            }
        }

        videoPlaybackStates.SaveAll(updates);
    }

    /// <summary>
    /// Puts a video on the sheet, starting a session when there is none. Music playback is stood
    /// down for as long as the sheet lives: the video pipeline opens the same audio device, and the
    /// queue is kept so the user can pick it up again afterwards.
    /// </summary>
    private void OpenVideoPlayer(
        Sourced<DbVideoFile> video,
        IReadOnlyList<Sourced<DbVideoFile>> siblings,
        bool resume)
    {
        var player = OpenVideoSession();
        _ = player.OpenAsync(video, siblings, resume);
    }

    private async Task OpenStreamLinkAsync()
    {
        if (dialogService is null || await dialogService.PromptForStreamUrlAsync() is not { } streamLink)
            return;

        await OpenStreamAsync(streamLink).ConfigureAwait(true);
    }

    private Task OpenStreamAsync(StreamLink streamLink) => OpenVideoSession().OpenStreamAsync(streamLink);

    private Task OpenStreamAsync(Uri streamUri) => OpenVideoSession().OpenStreamAsync(streamUri);

    /// <summary>
    /// The live video session, maximized. Creating one takes the audio device and the OS transport
    /// controls off the music queue: suspending is not enough on its own, because the OS session
    /// would still name the stopped track and a play from the lock screen would start music up
    /// behind the video.
    /// </summary>
    private VideoPlayerPageViewModel OpenVideoSession()
    {
        if (VideoSheet.Content is { } existing)
        {
            VideoSheet.IsExpanded = true;
            return existing;
        }

        playbackController.SuspendForVideo();

        var player = new VideoPlayerPageViewModel(
            appSettingsStore,
            playbackController,
            videoPlaybackStates,
            dialogService,
            subtitleFileBrowser,
            mediaResolver,
            SetHostFullScreen,
            RequestPreferredOrientation,
            RequestKeepScreenOn,
            () => VideoSheet.MinimizeCommand.Execute(null),
            () => VideoSheet.CloseCommand.Execute(null))
        {
            IsNarrow = IsNarrow,
            GoToLocation = videoLibraryQueryService is null ? null : video =>
            {
                VideoSheet.Content?.MinimizeCommand.Execute(null);
                GoToVideoLocation(video);
            },
            OpenContainingFolder = RevealVideoInFileManager,
            AddToPlaylist = videoPlaylistService is not null && playlistDialogService is not null
                ? AddVideoToPlaylist : null,
            ShowProperties = dialogService is null ? null : video => ShowProperties(
                new MediaReference(video.SourceId, video.Value.Path),
                video.Value.DisplayName ?? video.Value.FileName),
        };

        player.TakeSystemMediaControls(playbackController.AcquireSystemMediaSession());
        Queue.IsOpen = false;
        VideoSheet.Attach(player);
        return player;
    }

    /// <summary>
    /// Ends the video session and hands the audio device, the OS transport controls and the shell's
    /// own chrome back to music. Also the handler for a play command arriving while the sheet holds
    /// the device, so starting a track anywhere in the app tears the video down first.
    /// </summary>
    private void CloseVideoSheet()
    {
        if (VideoSheet.Detach() is not { } player)
        {
            return;
        }

        playbackController.DeferPlaybackUntil(player.DisposeAsync().AsTask());
        playbackController.ResumeAfterVideo();
        NotifyBackAffordanceChanged();

        // The visible video rows carry a resume marker that the session just moved.
        RefreshVideoPlaybackProgress(CurrentPage);
    }

    private static void RefreshVideoPlaybackProgress(PageViewModelBase? page)
    {
        switch (page)
        {
            case HomePageViewModel home:
                home.RefreshPlaybackProgress();
                break;
            case MediaPageViewModel videos:
                videos.RefreshPlaybackProgress();
                break;
            case VideoPlaylistDetailPageViewModel playlist:
                playlist.RefreshPlaybackProgress();
                break;
            case PlaylistsPageViewModel { SelectedDetail: VideoPlaylistDetailPageViewModel playlist }:
                playlist.RefreshPlaybackProgress();
                break;
        }
    }

    /// <param name="fromHistory">
    /// Whether the location being restored already addresses this video, in which case the sheet
    /// arriving rewrites the current history entry rather than adding one.
    /// </param>
    private async Task ReopenVideoAsync(MediaReference media, bool fromHistory = false)
    {
        try
        {
            var video = await unifiedVideoLibraryQueryService.GetVideoByPathAsync(media).ConfigureAwait(true);
            if (video is null)
            {
                return;
            }

            var siblings = await unifiedVideoLibraryQueryService
                .GetVideosInFolderAsync(video.SourceId, video.Value.FolderPath)
                .ConfigureAwait(true);

            isRestoringVideoSheet = fromHistory;
            OpenVideoPlayer(video, siblings, resume: false);
        }
        catch
        {
            // A video that is no longer indexed simply leaves the sheet closed.
        }
        finally
        {
            isRestoringVideoSheet = false;
        }
    }

    private static void SetHostFullScreen(bool value)
    {
        if (PlatformServices.FullScreenRequested is { } hook)
        {
            hook(value);
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            if (value)
            {
                stateBeforeFullScreen = window.WindowState;
                window.WindowState = WindowState.FullScreen;
            }
            else if (window.WindowState == WindowState.FullScreen)
            {
                window.WindowState = stateBeforeFullScreen;
            }
        }
    }

    private static WindowState stateBeforeFullScreen = WindowState.Normal;

    private static void RequestPreferredOrientation(double aspectRatio) =>
        PlatformServices.PreferredOrientationRequested?.Invoke(aspectRatio);

    private static void RequestKeepScreenOn(bool value) =>
        PlatformServices.KeepScreenOnRequested?.Invoke(value);

    private PageViewModelBase CreateAlbumsPage()
    {
        var inner = new AlbumsPageViewModel(albumTileActions, albumArtService, unifiedLibraryQueryService);
        MultiSelectionActions.AddMusicActions(inner.Selection, libraryCommands, ResolveSelectedMusicAsync);
        return appSettingsStore.GetLibraryLayout(LibraryItemKind.Album) == LibraryLayoutMode.List
            ? CreateBrowsePage(new ListItemsPageViewModel(
                inner,
                CreateInlineAlbumDetail,
                Strings.Resources.Library_SelectAlbumHeader,
                Strings.Resources.Library_SelectItemDescription))
            : CreateBrowsePage(inner);
    }

    private PageViewModelBase CreateArtistsPage()
    {
        var inner = new ArtistsPageViewModel(artistTileActions, albumArtService, unifiedLibraryQueryService);
        MultiSelectionActions.AddMusicActions(inner.Selection, libraryCommands, ResolveSelectedMusicAsync);
        return appSettingsStore.GetLibraryLayout(LibraryItemKind.Artist) == LibraryLayoutMode.List
            ? CreateBrowsePage(new ListItemsPageViewModel(
                inner,
                CreateInlineArtistDetail,
                Strings.Resources.Library_SelectArtistHeader,
                Strings.Resources.Library_SelectItemDescription))
            : CreateBrowsePage(inner);
    }

    // Builds the album/artist detail hosted inline in the list layout's detail pane. Back returns to
    // the list (clearing the selection) instead of popping the shell's navigation stack.
    private PageViewModelBase? CreateInlineAlbumDetail(LibraryTileModel tile, Action back)
    {
        if (tile.Identifier is not { } id)
        {
            return null;
        }

        var page = new AlbumDetailPageViewModel(id, libraryCommands, albumArtService, unifiedLibraryQueryService, back);
        _ = LoadDetailPageAsync(page.LoadAsync());
        return page;
    }

    private PageViewModelBase? CreateInlineArtistDetail(LibraryTileModel tile, Action back)
    {
        if (tile.Identifier is not { } id)
        {
            return null;
        }

        var page = new ArtistDetailPageViewModel(id, libraryCommands, albumArtService, unifiedLibraryQueryService, back);
        _ = LoadDetailPageAsync(page.LoadAsync());
        return page;
    }

    private PageViewModelBase CreateAlbumDetailPage(object? parameter, AppRoute route)
    {
        if (parameter is MediaLibraryItemIdentifier id)
        {
            var page = new AlbumDetailPageViewModel(id, libraryCommands, albumArtService, unifiedLibraryQueryService);
            _ = LoadDetailPageAsync(page.LoadAsync());
            return page;
        }

        return PlaceholderPageViewModel.Placeholder(route);
    }

    private PageViewModelBase CreateArtistDetailPage(object? parameter, AppRoute route)
    {
        if (parameter is MediaLibraryItemIdentifier id)
        {
            var page = new ArtistDetailPageViewModel(id, libraryCommands, albumArtService, unifiedLibraryQueryService);
            _ = LoadDetailPageAsync(page.LoadAsync());
            return page;
        }

        return PlaceholderPageViewModel.Placeholder(route);
    }

    private PageViewModelBase CreateSearchPage(object? parameter)
    {
        var keyword = parameter as string ?? Search.Text ?? string.Empty;
        var page = new SearchPageViewModel(
            keyword,
            libraryCommands,
            albumArtService,
            unifiedLibraryQueryService,
            CreateVideoSearchServices(),
            CreatePhotoSearchServices(),
            NavigateBack,
            (kind, query) => Navigate(kind switch
            {
                LibraryItemKind.Album => AppRouteId.AlbumSearch,
                LibraryItemKind.Artist => AppRouteId.ArtistSearch,
                _ => AppRouteId.SongSearch,
            }, query));
        _ = LoadDetailPageAsync(page.LoadAsync());
        return page;
    }

    private PageViewModelBase CreatePhotoSearchPage(object? parameter, AppRoute route)
    {
        if (CreatePhotoSearchServices() is not { } services)
            return PlaceholderPageViewModel.Placeholder(route);
        var page = new PhotoSearchPageViewModel(parameter as string ?? Search.Text ?? string.Empty, services, NavigateBack);
        _ = LoadDetailPageAsync(page.LoadAsync());
        return page;
    }

    private PhotoSearchServices? CreatePhotoSearchServices() => videoPreviewService is null
        ? null
        : new PhotoSearchServices(sourceRegistry, photoFolderService, videoPreviewService, CreateVideoCallbacks(),
            query => Navigate(AppRouteId.PhotoSearch, query), videoHistory);

    private PageViewModelBase CreateMusicSearchPage(object? parameter, LibraryItemKind kind)
    {
        var query = parameter as string ?? Search.Text ?? string.Empty;
        var source = CurrentPage is SearchPageViewModel search && search.Keyword == query
            ? search
            : backStack.Select(entry => entry.Page).OfType<SearchPageViewModel>()
                .FirstOrDefault(search => search.Keyword == query);
        var page = new MusicSearchPageViewModel(query, kind, libraryCommands, albumArtService,
            unifiedLibraryQueryService, source?.MusicResults, NavigateBack);
        _ = LoadDetailPageAsync(page.LoadAsync());
        return page;
    }

    // Null on heads with no video library, which keeps the video section off the search page there.
    private VideoSearchServices? CreateVideoSearchServices() =>
        videoLibraryQueryService is null || videoPreviewService is null
            ? null
            : new VideoSearchServices(
                unifiedVideoLibraryQueryService,
                videoPreviewService,
                videoPlaybackStates,
                CreateVideoCallbacks());

    private PageViewModelBase CreateHomePage()
    {
        var page = new HomePageViewModel(
            playbackController,
            albumArtService,
            trackActions,
            unifiedLibraryQueryService,
            new HomePageCallbacks
            {
                OpenAlbum = identifier => Navigate(AppRouteId.AlbumDetail, identifier),
                AlbumTileActions = albumTileActions,
                ShowAllAlbums = () => Navigate(AppRouteId.Albums),
                ShowNowPlaying = ToggleNowPlaying,
            },
            CreateHomeMediaServices(),
            playbackHistoryService);
        _ = LoadDetailPageAsync(page.LoadAsync());
        return page;
    }

    // Null on heads with no video library, which keeps the video and picture rows off the home page
    // there and leaves it showing music alone.
    private HomeMediaServices? CreateHomeMediaServices() =>
        videoLibraryQueryService is null || videoPreviewService is null
            ? null
            : new HomeMediaServices(
                unifiedVideoLibraryQueryService,
                videoPreviewService,
                photoFolderService,
                videoPlaybackStates,
                sourceRegistry,
                CreateVideoCallbacks(),
                videoHistory);

    private PageViewModelBase CreateNowPlayingPage() => CreateNowPlayingContent();

    // The now-playing surface used by both presentations: the wide layout's page and the narrow
    // layout's sheet build the same view model, so lyrics, artwork and up-next behave identically.
    private NowPlayingPageViewModel CreateNowPlayingContent() =>
        new NowPlayingPageViewModel(
            playbackController,
            albumArtService,
            lyricsService,
            dialogService,
            NavigateToArtist,
            NavigateToAlbum);

    private async void NavigateToArtist(MusicPlaybackItemModel item)
    {
        if (string.IsNullOrWhiteSpace(item.Artist))
        {
            return;
        }

        try
        {
            var selected = await unifiedLibraryQueryService.GetArtistByTrackAsync(item.Media);
            var results = await unifiedLibraryQueryService.SearchAsync(selected.DisplayName);
            var artist = results.Artists.FirstOrDefault(candidate => string.Equals(
                    candidate.NormalizedName,
                    selected.NormalizedName,
                    StringComparison.OrdinalIgnoreCase))
                ?? selected;
            Navigate(AppRouteId.ArtistDetail, LibraryItemReference.Artist(artist));
        }
        catch (ArgumentException)
        {
            // The now-playing track is not in the library; nothing to navigate to.
        }
        catch (Exception ex)
        {
            ReportActionFailure(Strings.Resources.Shell_WarnLoadPage, ex);
        }
    }

    private async void NavigateToAlbum(MusicPlaybackItemModel item)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath))
        {
            return;
        }

        try
        {
            var album = await unifiedLibraryQueryService.GetAlbumByTrackAsync(item.Media);
            Navigate(AppRouteId.AlbumDetail, LibraryItemReference.Album(album.SourceId, album.Value));
        }
        catch (ArgumentException)
        {
            // The now-playing track is not in the library; nothing to navigate to.
        }
        catch (Exception ex)
        {
            ReportActionFailure(Strings.Resources.Shell_WarnLoadPage, ex);
        }
    }

    // Navigates to a top-level route unless it is already the active page, mirroring the navigation
    // rail's "re-selecting the active destination is a no-op" behavior for the bottom navigation.
    // A parameterized instance of the route (a video library folder) is not its root, so
    // re-selecting it there returns to the root.
    private void NavigateTopLevel(AppRouteId routeId)
    {
        if (currentRouteId != routeId || currentParameter is not null)
        {
            Navigate(routeId);
        }
    }

    // Opens the library section from the narrow bottom navigation, returning to the last library tab
    // viewed. When a library tab is already active this stays put so the top tab strip owns switching.
    private void OpenLibrary()
    {
        if (!IsLibrarySection)
        {
            Navigate(lastLibraryRouteId);
        }
    }

    /// <summary>Reveals the narrow layout's top search field.</summary>
    public void OpenSearch() => IsSearchActive = true;

    /// <summary>
    /// Hides the narrow layout's top search field, returning to the page title. Returns true when a
    /// field was open, so a back gesture can consume itself closing search before navigating.
    /// </summary>
    public bool CloseSearch()
    {
        if (!IsSearchActive)
        {
            return false;
        }

        IsSearchActive = false;
        return true;
    }

    private void ToggleNowPlaying()
    {
        // Narrow layouts open the bottom sheet over the current page; wide layouts navigate to the
        // full Now Playing page.
        if (IsNarrow && PlaybackBar.HasItem)
        {
            NowPlayingSheet.IsExpanded = !NowPlayingSheet.IsExpanded;
            return;
        }

        if (currentRouteId == AppRouteId.NowPlaying)
        {
            GoBack();
        }
        else
        {
            Navigate(AppRouteId.NowPlaying);
        }
    }

    /// <summary>
    /// Toggles the now-playing queue side panel from the navigation rail. Below the queue overlay
    /// breakpoint it floats over the page; the narrow layout uses the now-playing sheet instead.
    /// </summary>
    private void ToggleQueue() => Queue.IsOpen = !Queue.IsOpen;

    // Makes sure the queue is visible before an action that operates on it (e.g. "Select all"). Only
    // the wide layout can dock the panel; on narrow layouts the queue lives in the now-playing sheet,
    // which the user opens themselves.
    private void EnsureQueueOpen()
    {
        if (!IsNarrow)
        {
            Queue.IsOpen = true;
        }
    }

    // Keep the active page's narrow flag in step with the shell as the viewport crosses the narrow
    // breakpoint in place, so its content insets and wide-only chrome (e.g. the songs column header)
    // follow the resize. Newly navigated pages are synced in ShowPage instead.
    partial void OnIsNarrowChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChooseVideoPresentation));
        if (CurrentPage is not null)
        {
            CurrentPage.IsNarrow = value;
        }

        // The video sheet is layered outside the routed content, so it never passes through
        // ShowPage and has to be told about the layout itself.
        if (VideoSheet.Content is { } player)
        {
            player.IsNarrow = value;
        }

        // Tell the queue which of its two presentations is live: the docked side panel on wide
        // layouts, the now-playing sheet's "Up next" page on narrow ones. Both are instantiated at
        // once, so the shared view model uses this to address the one actually on screen.
        Queue.IsSheetHosted = value;

        // The library sections swap presentation with the layout. Entering narrow hands the current
        // library page to the pager -- the very same instance, so the user stays exactly where they
        // were -- and leaving narrow takes it back as a plain routed page, dropping the neighboring
        // sections the pager had built alongside it.
        if (value)
        {
            if (IsLibrarySection && CurrentPage is not null)
            {
                LibraryPager.Adopt(currentRouteId, CurrentPage);
            }
        }
        else
        {
            LibraryPager.Release(currentRouteId);
        }

        UpdateNowPlayingPresentation();
    }

    // The sheet exists only on the narrow layout, and only while there is something to play; the
    // wide layout uses the docked playback bar and the Now Playing page instead.
    private void UpdateNowPlayingPresentation()
    {
        NowPlayingSheet.IsAvailable =
            IsNarrow && IsChromeVisible && !VideoSheet.IsActive && PlaybackBar.HasItem;
        OnPropertyChanged(nameof(ShowPlaybackBar));
        OnPropertyChanged(nameof(ShowDockedPlaybackBar));
        OnPropertyChanged(nameof(ShowFloatingPlaybackBar));
    }

    private void NotifyChromeVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsChromeVisible));
        OnPropertyChanged(nameof(NavRailCompactWidth));
        OnPropertyChanged(nameof(NavPaneOpenWidth));
        OnPropertyChanged(nameof(IsNavPaneVisible));
        OnPropertyChanged(nameof(IsNarrowChromeVisible));
        OnPropertyChanged(nameof(ShowAppleTopBar));
        OnPropertyChanged(nameof(ShowNavEntries));
        UpdateNowPlayingPresentation();
    }

    private void OnBackdropStyleChanged()
    {
        if (isAppleChromeEnabled != themeService.IsAppleChromeEnabled)
        {
            isAppleChromeEnabled = !isAppleChromeEnabled;
            OnShellLayoutChanged();
        }
    }

    // The Apple layout follows both the Apple UI style preference and the narrow breakpoint.
    private void OnShellLayoutChanged()
    {
        OnPropertyChanged(nameof(IsAppleLayout));
        OnPropertyChanged(nameof(IsSidebarPinned));
        OnPropertyChanged(nameof(UseCustomWindowControls));
        OnPropertyChanged(nameof(ExtendsIntoTitleBar));
        OnPropertyChanged(nameof(WindowControlsInset));
        OnPropertyChanged(nameof(NarrowTopBarMargin));
        OnPropertyChanged(nameof(NavWindowControlsHeight));
        OnPropertyChanged(nameof(HasShellBackButton));
        OnPropertyChanged(nameof(NavPaneOpenWidth));
        OnPropertyChanged(nameof(ShowAppleTopBar));
        OnPropertyChanged(nameof(ShowDockedPlaybackBar));
        OnPropertyChanged(nameof(ShowFloatingPlaybackBar));
        Queue.PinnedWidth = QueuePinnedWidth;
        UpdateNavigationMode();
        if (CurrentPage is not null)
        {
            CurrentPage.HasShellBackButton = HasShellBackButton;
            CurrentPage.HasShellPathMenu = isAppleChromeEnabled;
        }

        if (CurrentPage is SettingsPageViewModel settings)
        {
            settings.IsSidebarLayout = IsAppleLayout;
        }

        OnPropertyChanged(nameof(SettingsSidebar));
        OnPropertyChanged(nameof(ShowNavEntries));
    }

    // The Apple layout keeps its sidebar expanded; every other layout collapses it to the rail,
    // docking it inline only on very wide shells.
    private void UpdateNavigationMode()
    {
        if (IsSidebarPinned)
        {
            Navigation.DisplayMode = SplitViewDisplayMode.Inline;
            Navigation.IsExpanded = true;
            return;
        }

        bool wasPinned = Navigation.DisplayMode == SplitViewDisplayMode.Inline;
        Navigation.DisplayMode = viewportWidth >= SplitViewCompactInlineThreshold
            ? SplitViewDisplayMode.CompactInline
            : SplitViewDisplayMode.CompactOverlay;
        if (wasPinned)
        {
            Navigation.IsExpanded = false;
        }
    }

    private void OnPlaybackBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackBarViewModel.HasItem))
        {
            UpdateNowPlayingPresentation();
        }
    }

    private void OnNowPlayingSheetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingSheetViewModel.IsExpanded))
        {
            // The expanded sheet covers the bottom navigation and stands in for a navigation entry,
            // so the shell's back affordances must reflect that it can be dismissed.
            NotifyBackAffordanceChanged();
            SyncHistory(HistorySync.StateChanged);
        }
    }

    private void OnVideoSheetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VideoSheetViewModel.IsFullScreen):
                OnPropertyChanged(nameof(SafeAreaPadding));
                OnPropertyChanged(nameof(SafeAreaBackgroundMargin));
                OnPropertyChanged(nameof(SafeAreaContentMargin));
                OnPropertyChanged(nameof(SafeAreaOutsetMargin));
                OnPropertyChanged(nameof(QueueSafeAreaContentMargin));
                OnPropertyChanged(nameof(WindowControlsInset));
                OnPropertyChanged(nameof(NarrowTopBarMargin));
                break;

            case nameof(VideoSheetViewModel.IsShellChromeHidden):
                // Driven by the card's travel rather than by its logical state, so the navigation
                // and playback chrome only stands down once the card that replaces it has arrived.
                NotifyChromeVisibilityChanged();
                break;

            case nameof(VideoSheetViewModel.IsPictureDetached):
                // The picture left for a window of its own, or came back: the sheet stops or starts
                // covering the shell, so the host history follows it exactly like a maximize would.
                OnPropertyChanged(nameof(WindowPageTitle));
                NotifyBackAffordanceChanged();
                SyncHistory(HistorySync.StateChanged);
                break;

            case nameof(VideoSheetViewModel.IsExpanded):
                // The maximized card is dismissed by a back gesture, exactly like the now-playing
                // sheet, so the shell's back affordances have to reflect it.
                OnPropertyChanged(nameof(WindowPageTitle));
                NotifyBackAffordanceChanged();

                // It also covers the shell outright, so it is a host history entry of its own: the
                // browser's back drops it into its corner before the page underneath is touched. A
                // card put back by a history entry is already what that entry addresses, so it
                // rewrites the entry instead of adding one.
                SyncHistory(isRestoringVideoSheet ? HistorySync.Rearranged : HistorySync.StateChanged);
                break;

            case nameof(VideoSheetViewModel.Content):
                SubscribeVideoSession(VideoSheet.Content);
                UpdateNowPlayingPresentation();
                break;
        }
    }

    // Follows the session on the sheet so the address names the video actually playing, including
    // the ones the player moves to on its own at the end of a file.
    private void SubscribeVideoSession(VideoPlayerPageViewModel? player)
    {
        if (videoSession is not null)
        {
            videoSession.PropertyChanged -= OnVideoSessionPropertyChanged;
        }

        videoSession = player;

        if (videoSession is not null)
        {
            videoSession.PropertyChanged += OnVideoSessionPropertyChanged;
        }
    }

    private void OnVideoSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The sheet moved to another video by itself. The entry it was opened on is rewritten rather
        // than added to, so the address stays copyable without the history growing a step per file.
        if (e.PropertyName == nameof(INavigationParameterSource.NavigationParameter) && IsVideoSheetAddressed)
        {
            SyncHistory(HistorySync.Replaced);
        }
    }

    // Follow the active page so the shell's narrow back button reflects whether that page can consume
    // a back itself (a master-detail page with an open detail). Re-raise the affordance on every page
    // swap and subscribe to the new page so opening/collapsing its detail toggles the button live.
    partial void OnCurrentPageChanged(PageViewModelBase? oldValue, PageViewModelBase newValue)
    {
        AddressBar.SetPage(newValue);

        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnCurrentPageTitleChanged;
        }

        if (oldValue is IInPageBackHandler or IInPageDetailState or INavigationParameterSource)
        {
            oldValue.PropertyChanged -= OnCurrentPageBackStateChanged;
        }

        newValue.PropertyChanged += OnCurrentPageTitleChanged;

        if (newValue is IInPageBackHandler or IInPageDetailState or INavigationParameterSource)
        {
            newValue.PropertyChanged += OnCurrentPageBackStateChanged;
        }

        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(ShowNarrowBackButton));
        GoBackCommand.NotifyCanExecuteChanged();
        NotifyChromeVisibilityChanged();
    }

    private void OnCurrentPageTitleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageViewModelBase.Title))
        {
            OnPropertyChanged(nameof(WindowPageTitle));
        }
    }

    private void OnCurrentPageBackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(INavigationParameterSource.NavigationParameter)
            && sender is INavigationParameterSource source)
        {
            // The page moved to something else on its own (the video player picking up the next
            // video). The location follows it in place so the address stays copyable at any moment,
            // while the entry the page was opened on is rewritten rather than added to.
            currentParameter = source.NavigationParameter;
            SyncHistory(HistorySync.Replaced);
            return;
        }

        if (e.PropertyName == nameof(IInPageDetailState.DetailKey))
        {
            // The open detail is part of the shell's location even where it is not a back step (the
            // wide two-pane layout), so the address follows the selection either way.
            SyncHistory(HistorySync.StateChanged);
            return;
        }

        if (e.PropertyName == nameof(IInPageBackHandler.CanHandleBack))
        {
            OnPropertyChanged(nameof(CanNavigateBack));
            OnPropertyChanged(nameof(ShowNarrowBackButton));
            GoBackCommand.NotifyCanExecuteChanged();
            // A library master-detail page opening/collapsing its inline detail also toggles the
            // narrow tab strip and the pager swipe: both step aside while the detail owns the
            // single pane.
            OnPropertyChanged(nameof(ShowLibraryTabs));
            LibraryPager.CanSwipe = !IsInPageDetailOpen;
        }
    }

    // Raises every property derived from the back stack after it is pushed/popped/trimmed, so the
    // shell's back affordances (CanGoBack and the narrow back button) refresh together.
    private void NotifyBackAffordanceChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(ShowNarrowBackButton));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Reports the current shell width so the shell can adapt as the window resizes. Crossing
    /// <see cref="NarrowWidthThreshold"/> swaps the now-playing presentation between the wide
    /// layout's docked playback bar plus queue side panel and the narrow layout's bottom sheet,
    /// carrying the user's current surface across the switch.
    /// </summary>
    public void SetViewportWidth(double width)
    {
        if (width <= 0)
        {
            return;
        }

        viewportWidth = width;
        UpdateNavigationMode();
        Queue.IsOverlayMode = width < QueueOverlayWidthThreshold;

        var narrow = width < NarrowWidthThreshold;
        if (narrow == IsNarrow)
        {
            return;
        }

        // Resizing is not navigation: the switch rearranges surfaces (the now-playing page becomes
        // the sheet and back) rather than moving the user, so the host history records the result in
        // place instead of gaining or consuming entries.
        SuspendHistorySync(() => ApplyViewportWidth(narrow));
        SyncHistory(HistorySync.Rearranged);
    }

    private void ApplyViewportWidth(bool narrow)
    {
        // Sample the sheet before the layout flag flips: assigning IsNarrow immediately clears the
        // sheet's availability (it is narrow-only), which in turn collapses it, so the wide branch
        // below could otherwise never tell that the user was sitting on the now-playing surface.
        var sheetWasOpen = NowPlayingSheet.IsExpanded;

        IsNarrow = narrow;
        OnShellLayoutChanged();

        if (narrow)
        {
            // Entering narrow: the navigation rail is replaced by the bottom navigation, so collapse
            // the expandable drawer that exists only on the wide layout.
            Navigation.IsExpanded = false;

            // The Now Playing page becomes the bottom sheet: pop the page and open the sheet in its
            // place, then strip the route from history so a back gesture cannot resurrect the wide
            // three-column layout inside the narrow shell.
            if (currentRouteId == AppRouteId.NowPlaying && CanGoBack)
            {
                GoBack();

                // With nothing playing there is no sheet to open; the user simply lands back on the
                // page they came from.
                NowPlayingSheet.IsExpanded = PlaybackBar.HasItem;
            }

            RemoveRouteHistory(AppRouteId.NowPlaying);

            // A docked side panel would crowd the content, so close it and remember that it was
            // open; growing back past the breakpoint restores it. The queue itself stays reachable
            // meanwhile through the now-playing sheet's "Up next" page.
            queueWasOpenWhenWide = Queue.IsOpen;
            Queue.IsOpen = false;
        }
        else
        {
            // Leaving narrow: re-dock the side panel if the narrowing above is what closed it.
            if (queueWasOpenWhenWide)
            {
                queueWasOpenWhenWide = false;
                Queue.IsOpen = true;
            }

            // The sheet has no wide-layout counterpart, so an open one unfolds into the Now Playing
            // page it stands in for, keeping the user on the same surface across the layout switch.
            // Skipped when that page is already showing.
            if (sheetWasOpen && currentRouteId != AppRouteId.NowPlaying)
            {
                Navigate(AppRouteId.NowPlaying);
            }
        }
    }

    // Remove routes whose presentation is replaced during a layout switch.
    private void RemoveRouteHistory(AppRouteId routeId)
    {
        var trimmed = TrimRouteEntries(backStack, routeId);
        trimmed |= TrimRouteEntries(forwardStack, routeId);

        if (trimmed)
        {
            NotifyBackAffordanceChanged();
        }
    }

    private static bool TrimRouteEntries(Stack<(AppRouteId Id, object? Parameter, PageViewModelBase? Page)> stack, AppRouteId routeId)
    {
        if (!stack.Any(entry => entry.Id == routeId))
        {
            return false;
        }

        // A Stack enumerates top-first, so reverse the kept entries before rebuilding it.
        var kept = stack.Where(entry => entry.Id != routeId).Reverse().ToList();
        stack.Clear();
        foreach (var entry in kept)
        {
            stack.Push(entry);
        }

        return true;
    }

    private PageViewModelBase CreatePlaylistsPage(AppRoute route)
    {
        if (playlistService is null || videoPlaylistService is null || playlistDialogService is null)
        {
            return PlaceholderPageViewModel.ForEmpty(route, nameof(Strings.Resources.Playlists_NoPlaylists), nameof(Strings.Resources.Playlists_SaveQueueToCreate));
        }

        return new PlaylistsPageViewModel(
            playlistService,
            videoPlaylistService,
            playlistDialogService,
            // The inline detail's header back button is shown only in the adaptive Playlists view's
            // narrow single-pane layout; PlaylistsPageViewModel drives its visibility.
            (playlist, back) => playlist.IsVideo
                ? CreateVideoPlaylistDetail(playlist.Id, back)
                : new PlaylistDetailPageViewModel(
                    playlist.Id,
                    playlistService,
                    playlistDialogService,
                    trackActions,
                    PlayPlaylistItems,
                    EnqueuePlaylistItems,
                    back,
                    libraryCommands),
            PlayPlaylistItems,
            PlayPlaylistItemsNext,
            EnqueuePlaylistItems);
    }

    /// <summary>
    /// The video playlist detail page, or a placeholder on heads without a video library behind it.
    /// </summary>
    private PageViewModelBase CreateVideoPlaylistDetail(string playlistId, Action back)
    {
        if (videoPlaylistService is null || playlistDialogService is null || videoPreviewService is null)
        {
            return PlaceholderPageViewModel.Placeholder(routesById[AppRouteId.PlaylistDetail]);
        }

        return new VideoPlaylistDetailPageViewModel(
            playlistId,
            videoPlaylistService,
            playlistDialogService,
            unifiedVideoLibraryQueryService,
            videoPreviewService,
            videoPlaybackStates,
            new VideoPlaylistCallbacks
            {
                OpenVideo = OpenVideoPlayer,
                GoToLocation = GoToVideoLocation,
                OpenContainingFolder = RevealVideoInFileManager,
                AddToPlaylist = AddVideoToPlaylist,
                AddVideosToPlaylistAsync = AddVideosToPlaylistAsync,
                ClearPlaybackProgress = ClearVideoPlaybackProgress,
                MarkWatched = MarkVideosWatched,
            },
            back);
    }

    private PageViewModelBase CreatePlaylistDetailPage(object? parameter, AppRoute route)
    {
        if (playlistDialogService is not null && parameter is string playlistId)
        {
            if (videoPlaylistService?.GetById(playlistId) is not null)
            {
                return CreateVideoPlaylistDetail(playlistId, NavigateBack);
            }

            if (playlistService is not null)
            {
                return new PlaylistDetailPageViewModel(
                    playlistId,
                    playlistService,
                    playlistDialogService,
                    trackActions,
                    PlayPlaylistItems,
                    EnqueuePlaylistItems,
                    NavigateBack,
                    libraryCommands);
            }
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

    /// <summary>
    /// Opens the first page of the session. A location recorded by the host (the browser head's URL
    /// hash, restored across a page refresh) wins over the startup-page setting; anything else falls
    /// back to <see cref="GetInitialRouteId"/>.
    /// </summary>
    private void RestoreNavigation()
    {
        // The restore navigations rebuild a location the host already holds, so they must not add
        // entries of their own.
        isApplyingHistory = true;
        try
        {
            RestoreNavigationCore();
        }
        finally
        {
            isApplyingHistory = false;
        }

        if (navigationHistory is null)
        {
            return;
        }

        // Keep the index the host already carries (a refresh lands in the middle of a session's
        // history) so its earlier entries stay reachable, and record what was actually restored:
        // state that could not be reopened, such as the sheet, must not linger in the address.
        historyIndex = Math.Max(navigationHistory.CurrentIndex, 0);
        historyMaxIndex = historyIndex;
        lastOverlayDepth = OverlayDepth;
        navigationHistory.Replace(FormatCurrentLocation(), historyIndex);
        navigationHistory.LocationChanged += OnHistoryLocationChanged;
        NotifyBackAffordanceChanged();
    }

    private void RestoreNavigationCore()
    {
        if (!TryGetRestoredLocation(out var restored))
        {
            Navigate(GetInitialRouteId());
            return;
        }

        // Restored detail / search / now-playing locations are opened on top of the page they are
        // normally reached from, so Back leads somewhere instead of dead-ending.
        if (GetRestoreParentRouteId(restored) is { } parentRouteId)
        {
            Navigate(parentRouteId);
        }

        // A video is a sheet rather than a page now, so a location left by an older session is
        // reopened over the library it was started from instead of being navigated to.
        if (restored.RouteId == AppRouteId.VideoPlayer)
        {
            if (restored.Parameter is string videoPath)
            {
                _ = ReopenVideoAsync(
                    MediaReferenceUri.TryParse(videoPath, out var parsed)
                        ? parsed.Media
                        : MediaReference.Local(videoPath),
                    fromHistory: true);
            }

            return;
        }

        if (restored.RouteId == AppRouteId.Search && restored.Parameter is string keyword)
        {
            Search.Text = keyword;
        }

        Navigate(restored.RouteId, restored.Parameter);
        ApplyInPageState(restored);
        isRestoringInPageState = restored.DetailKey is not null
            && (CurrentPage as IInPageDetailState)?.DetailKey is null;
    }

    private bool TryGetRestoredLocation(out NavigationLocation location)
    {
        location = new NavigationLocation(AppRouteId.Home);

        if (navigationHistory?.CurrentLocation is not { } text
            || !NavigationLocation.TryParse(text, out location)
            || !routesById.TryGetValue(location.RouteId, out var route))
        {
            return false;
        }

        // A detail route whose parameter did not survive would only produce a placeholder page.
        return route.Kind != AppRouteKind.Detail || location.Parameter is not null;
    }

    // The page a restored location should sit on top of, or null when it is a navigation-rail
    // destination and therefore already a valid root.
    private AppRouteId? GetRestoreParentRouteId(NavigationLocation location)
    {
        var routeId = location.RouteId;

        // A restored video folder is a rail destination, but not a root: it sits on the library root
        // so Back leads out of the folder chain rather than dead-ending inside it.
        if (routeId == AppRouteId.Videos)
        {
            return location.Parameter is string { Length: > 0 } ? AppRouteId.Videos : null;
        }

        if (routesById[routeId].NavigationSlot != AppNavigationSlot.None)
        {
            return null;
        }

        var parentRouteId = routeId switch
        {
            AppRouteId.AlbumDetail => AppRouteId.Albums,
            AppRouteId.ArtistDetail => AppRouteId.Artists,
            AppRouteId.PlaylistDetail => AppRouteId.Playlists,
            AppRouteId.VideoPlayer => AppRouteId.Videos,
            _ => GetInitialRouteId(),
        };

        return parentRouteId == routeId ? null : parentRouteId;
    }

    /// <summary>The shell location as it stands, including the surfaces layered over the page.</summary>
    private string FormatCurrentLocation() => new NavigationLocation(
        currentRouteId,
        currentParameter,
        (CurrentPage as IInPageDetailState)?.DetailKey,
        NowPlayingSheet.IsExpanded,
        IsVideoSheetAddressed ? CurrentVideoKey : null).Format();

    // The video on the sheet, addressed the way a video navigation parameter is, or null when there
    // is no session.
    private string? CurrentVideoKey =>
        VideoSheet.Content?.NavigationParameter is VideoPlaybackRequest { Video: { Value.Path: { Length: > 0 } path } video }
            ? MediaReferenceUri.Format(new MediaReference(video.SourceId, path))
            : null;

    // Whether the video sheet is part of the shell's location. Only the maximized card is: the
    // corner card plays beside the page rather than over it, and a picture handed to a window of its
    // own leaves the page in charge of the shell, so neither belongs in the address or answers a back.
    private bool IsVideoSheetAddressed => VideoSheet is { IsExpanded: true, IsPictureDetached: false };

    // Surfaces layered over the current page, each of which is one back step (and so one host
    // history entry): a master-detail page's open detail, the expanded now-playing sheet, and the
    // maximized video sheet.
    private int OverlayDepth => PageOverlayDepth + (IsVideoSheetAddressed ? 1 : 0);

    // The subset of those surfaces a page navigation collapses on its way to the new page; the video
    // sheet keeps playing across one, so it is not counted here.
    private int PageOverlayDepth =>
        (IsInPageDetailOpen ? 1 : 0) + (NowPlayingSheet.IsExpanded ? 1 : 0);

    private enum HistorySync
    {
        /// <summary>A page navigation: always a new entry.</summary>
        Navigated,

        /// <summary>A layered surface opened or closed, or the page changed in place.</summary>
        StateChanged,

        /// <summary>
        /// The page now addresses something else without the user navigating: the current entry is
        /// rewritten, so the address is shareable without the history growing an entry.
        /// </summary>
        Replaced,

        /// <summary>
        /// The shell rearranged itself without the user navigating (an adaptive layout switch): the
        /// current entry is rewritten, however many surfaces the rearrangement opened or closed.
        /// </summary>
        Rearranged,
    }

    // Runs a compound change (a page swap, a layout switch) as a single history unit: the states it
    // passes through on the way are not entries of their own.
    private void SuspendHistorySync(Action change)
    {
        var previous = isHistorySuspended;
        isHistorySuspended = true;
        try
        {
            change();
        }
        finally
        {
            isHistorySuspended = previous;
        }
    }

    /// <summary>
    /// Mirrors the shell's state into the host history. Opening a layered surface adds an entry so
    /// the host's back closes it; closing one from inside the app steps the host back instead of
    /// rewriting the entry, so its history never keeps a state the user can no longer return to.
    /// </summary>
    private void SyncHistory(HistorySync kind)
    {
        if (navigationHistory is null || isApplyingHistory || isHistorySuspended)
        {
            return;
        }

        var depth = OverlayDepth;
        var steps = lastOverlayDepth - depth;
        lastOverlayDepth = depth;

        if (isRestoringInPageState)
        {
            isRestoringInPageState = false;
            kind = kind == HistorySync.Navigated ? kind : HistorySync.Rearranged;
        }

        if (kind == HistorySync.Navigated || (kind == HistorySync.StateChanged && steps < 0))
        {
            historyIndex++;
            historyMaxIndex = historyIndex;
            navigationHistory.Push(FormatCurrentLocation(), historyIndex);
        }
        else if (kind == HistorySync.StateChanged && steps > 0 && historyIndex >= steps)
        {
            // The entries being left behind already describe the state the app is now in, so
            // applying them when the host reports the move is a no-op.
            navigationHistory.Go(-steps);
            return;
        }
        else
        {
            navigationHistory.Replace(FormatCurrentLocation(), historyIndex);
        }

        NotifyBackAffordanceChanged();
    }

    /// <summary>
    /// Applies the entry the host moved to. The location is the whole shell state, so applying it is
    /// idempotent: a move the app itself caused (closing a surface, which steps the host back)
    /// resolves to no visible change.
    /// </summary>
    private void OnHistoryLocationChanged(string? text, int index)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnHistoryLocationChanged(text, index));
            return;
        }

        var delta = index - historyIndex;
        if (delta == 0 || navigationHistory is null)
        {
            return;
        }

        // Surfaces that are not part of the location (a modal dialog, the narrow search field)
        // consume a back the way the platform back gesture does, and the move is undone so the
        // history entry they were opened over stays current.
        if (delta < 0 && (CloseOverlay() || CloseSearch()))
        {
            navigationHistory.Go(-delta);
            return;
        }

        historyIndex = index;
        historyMaxIndex = Math.Max(historyMaxIndex, index);

        if (NavigationLocation.TryParse(text, out var target))
        {
            isApplyingHistory = true;
            try
            {
                ApplyLocation(target, isBack: delta < 0);
            }
            finally
            {
                isApplyingHistory = false;
                lastOverlayDepth = OverlayDepth;
            }
        }

        NotifyBackAffordanceChanged();
    }

    private void ApplyLocation(NavigationLocation target, bool isBack)
    {
        // Collapse first: the sheets are layered over the page, so they would otherwise cover
        // whatever the entry navigates to. The video sheet drops into its corner card and plays on;
        // only its close button ends the session.
        if (IsVideoSheetAddressed && target.VideoKey != CurrentVideoKey)
        {
            VideoSheet.IsExpanded = false;
        }

        if (NowPlayingSheet.IsExpanded && !target.IsSheetExpanded)
        {
            NowPlayingSheet.IsExpanded = false;
        }

        if (!target.SamePage(new NavigationLocation(currentRouteId, currentParameter)))
        {
            // Prefer the shell's own stacks so a returning browse page keeps its loaded items and
            // scroll position; rebuild from the route when the entry is not the adjacent one (a
            // multi-entry jump, or a session restored across a reload).
            if (isBack && backStack.Count > 0 && Addresses(backStack.Peek(), target))
            {
                GoBack();
            }
            else if (!isBack && forwardStack.Count > 0 && Addresses(forwardStack.Peek(), target))
            {
                GoForwardCore();
            }
            else
            {
                NavigateInternal(target.RouteId, target.Parameter, recordHistory: true);
            }
        }

        ApplyInPageState(target);
    }

    private void ApplyInPageState(NavigationLocation target)
    {
        (CurrentPage as IInPageDetailState)?.RestoreDetail(target.DetailKey);

        // The sheet only exists on the narrow layout while something is playing; a location that
        // outlived either is restored without it.
        if (target.IsSheetExpanded && NowPlayingSheet.IsAvailable)
        {
            NowPlayingSheet.IsExpanded = true;
        }

        if (target.VideoKey is { Length: > 0 } videoKey)
        {
            RestoreVideoSheet(videoKey);
        }
    }

    // Puts the video a location names back on the sheet, maximized: the session still on the sheet
    // when it is the same video, otherwise the file looked up again (a reload, or a step onto an
    // entry whose session was closed since). A video no longer in the library leaves the sheet shut.
    private void RestoreVideoSheet(string videoKey)
    {
        if (videoKey == CurrentVideoKey)
        {
            VideoSheet.ExpandCommand.Execute(null);
            return;
        }

        if (MediaReferenceUri.TryParse(videoKey, out var addressed))
        {
            _ = ReopenVideoAsync(addressed.Media, fromHistory: true);
        }
    }

    private bool Addresses((AppRouteId Id, object? Parameter, PageViewModelBase? Page) entry, NavigationLocation target) =>
        target.SamePage(new NavigationLocation(entry.Id, entry.Parameter));

    private void OnNavigationRequested(AppRouteId routeId)
    {
        // While the narrow layout's pager is showing, the library tabs move the pager instead of
        // navigating: the sections are already live side by side, so switching is a slide rather
        // than a rebuilt page with its own history entry.
        if (ShowLibraryPager && LibraryPager.Contains(routeId))
        {
            LibraryPager.SelectRoute(routeId);
            return;
        }

        Navigate(routeId);
    }

    private void OnQueuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueuePaneViewModel.IsOpen))
        {
            // Only the wide layout can dock the panel, so only it decides the persisted state: the
            // narrow layout force-closes the panel, which must not be remembered as a user choice.
            if (!IsNarrow)
            {
                stateStore.IsQueuePaneOpen = Queue.IsOpen;
            }
        }
    }

    private void OnQuerySubmitted(string query)
    {
        // Collapse the overlay navigation pane so it stops covering the search results.
        Navigation.CollapseOverlay();

        // Collapse the narrow layout's search field once a query is committed.
        IsSearchActive = false;

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
        Navigation.CollapseOverlay();

        // Collapse the narrow layout's search field once a suggestion is chosen.
        IsSearchActive = false;

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
    /// artists carry their identifier so choosing one opens its detail page. Videos are listed
    /// plainly by name: they have no detail page of their own to point at.
    /// </summary>
    private async Task<IReadOnlyList<SearchResultModel>> SearchLibrarySuggestionsAsync(string? query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchResultModel>();
        }

        UnifiedLibrarySearchResults results;
        IReadOnlyList<Sourced<DbVideoFile>> videos;
        try
        {
            var videoQuery = videoLibraryQueryService is null
                ? Task.FromResult(new UnifiedCollection<Sourced<DbVideoFile>>([], []))
                : unifiedVideoLibraryQueryService.SearchVideosAsync(query, cancellationToken);
            results = await unifiedLibraryQueryService
                .SearchAsync(query, cancellationToken)
                .ConfigureAwait(false);
            videos = (await videoQuery.ConfigureAwait(false)).Items;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<SearchResultModel>();
        }
        catch (Exception ex)
        {
            // AutoCompleteBox awaits the populator from its own void path, so a failed lookup here
            // would otherwise crash the app while the user is typing.
            ReportActionFailure(Strings.Resources.Shell_WarnLoadLibrary, ex);
            return Array.Empty<SearchResultModel>();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<SearchResultModel>();
        }

        const int maxSuggestions = 10;
        var suggestions = new List<SearchResultModel>(maxSuggestions);

        foreach (var sourcedAlbum in results.Albums)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                break;
            }

            var album = sourcedAlbum.Value;
            var albumTitle = LibrarySortKeys.Fallback(album.Title, Strings.Resources.Common_UnknownAlbum);
            var suggestion = new SearchResultModel(
                albumTitle,
                Strings.Resources.Common_ColumnAlbum,
                SearchResultKind.Album,
                ThumbnailKind.Album,
                hasThumbnail: true)
            {
                Identifier = LibraryItemReference.Album(sourcedAlbum.SourceId, album),
            };

            // Swap the disc placeholder for the real cover once it is extracted. Keys match the album
            // pages so the throttled, on-disk artwork cache is shared.
            _ = LoadSuggestionArtAsync(
                suggestion,
                albumArtService.GetLowQualityTrackArtAsync(
                    new MediaReference(sourcedAlbum.SourceId, album.FirstFileInAlbum ?? string.Empty),
                    1,
                    SuggestionArtPixelSize,
                    cancellationToken),
                cancellationToken);

            suggestions.Add(suggestion);
        }

        foreach (var artist in results.Artists)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                break;
            }

            suggestions.Add(new SearchResultModel(
                LibrarySortKeys.Fallback(artist.DisplayName, Strings.Resources.Common_UnknownArtist),
                Strings.Resources.Common_ColumnArtist,
                SearchResultKind.Artist,
                ThumbnailKind.Artist,
                hasThumbnail: true)
            {
                Identifier = LibraryItemReference.Artist(artist),
            });
        }

        foreach (var sourcedFile in results.Files)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                break;
            }

            var file = sourcedFile.Value;
            var pathTitle = Path.GetFileNameWithoutExtension(file.Path);
            var songTitle = LibrarySortKeys.Fallback(
                file.Title,
                string.IsNullOrWhiteSpace(pathTitle) ? Strings.Resources.Common_UnknownTitle : pathTitle);
            suggestions.Add(new SearchResultModel(songTitle, Strings.Resources.Common_Song, SearchResultKind.Song)
            {
                Identifier = (MediaLibraryItemIdentifier)file,
            });
        }

        foreach (var sourcedVideo in videos)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                break;
            }

            var video = sourcedVideo.Value;
            suggestions.Add(new SearchResultModel(
                LibrarySortKeys.Fallback(video.DisplayName, Strings.Resources.Common_UnknownTitle),
                Strings.Resources.Nav_Video,
                SearchResultKind.Video));
        }

        return suggestions;
    }

    /// <summary>
    /// Assigns a suggestion's artwork on the UI thread once <paramref name="artTask"/> completes, so
    /// the search drop-down shows the real cover instead of the placeholder. Fire-and-forget: a missing
    /// cover or a failure simply leaves the placeholder in place.
    /// </summary>
    private static async Task LoadSuggestionArtAsync(SearchResultModel suggestion, Task<FastImageSource?> artTask, CancellationToken cancellationToken)
    {
        FastImageSource? image;
        try
        {
            image = await artTask.ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (image is null)
        {
            return;
        }

        AlbumArtService.ApplyOnUiThread(image, art => suggestion.Image = art, cancellationToken);
    }

    private void OnPlaybackError(string message)
    {
        HasWarnings = true;
        WarningText = Strings.Resources.Shell_WarnPlaybackError;
        WarningDetailsText = message;
    }

    // Menu, tile and navigation handlers are `async void`, so an exception escaping one is
    // unhandled and takes the process down. In client mode every library or playlist lookup can
    // fail on the wire (e.g. the server went away), so those failures land in the warning banner.
    private void ReportActionFailure(string warning, Exception ex) => AppErrorReporter.Report(warning, ex);

    private void ShowFailureBanner(string warning, string details)
    {
        Dispatcher.UIThread.Post(() =>
        {
            HasWarnings = true;
            WarningText = warning;
            WarningDetailsText = details;
        });
    }

    // Reading a track's properties goes to the server in client mode, so the dialog can fail.
    private void ShowProperties(MediaReference media, string fallbackTitle)
    {
        if (dialogService is { } dialogs)
        {
            _ = AppErrorReporter.GuardAsync(() =>
                dialogs.ShowPropertiesAsync(media, fallbackTitle, mediaResolver));
        }
    }

    private void RevealVideoInFileManager(Sourced<DbVideoFile> video)
    {
        if (mediaResolver.GetCapabilities(video.SourceId).SupportsFileReveal)
        {
            RevealInFileManager(video.Value.Path);
        }
    }

    private async void GoToVideoLocation(Sourced<DbVideoFile> video) =>
        await AppErrorReporter.GuardAsync(async () =>
        {
            var folder = await VideoFolderPaths.GetLocationAsync(
                new MediaReference(video.SourceId, video.Value.Path), sourceRegistry,
                video.Value.FolderPath, video.Value.MediaKind != VideoMediaKind.File);
            if (folder is not null)
            {
                Navigate(AppRouteId.Videos, folder);
            }
        });

    private void RevealVideoFolderInFileManager(VideoFolderReference folder)
    {
        if (folder.Path is { Length: > 0 } path &&
            mediaResolver.GetCapabilities(folder.SourceId).SupportsFileReveal)
        {
            RevealInFileManager(path);
        }
    }

    private void RevealPhotoInFileManager(PhotoFileReference photo)
    {
        if (mediaResolver.GetCapabilities(LibrarySourceId.Local).SupportsFileReveal)
        {
            RevealInFileManager(photo.Path);
        }
    }

    /// <summary>
    /// The picture that was opened, together with the rest of the page it came from so the viewer
    /// can be paged sideways through them.
    /// </summary>
    private static PhotoViewerRequest BuildPhotoRequest(
        PhotoFileReference photo,
        IReadOnlyList<PhotoFileReference> siblings)
    {
        var references = new List<MediaReference>(siblings.Count);
        var index = -1;
        foreach (var sibling in siblings)
        {
            if (sibling.Source == photo.Source && sibling.Path == photo.Path)
            {
                index = references.Count;
            }

            references.Add(sibling.Media);
        }

        // A page that has moved on since the tile was built may no longer list the picture at all.
        return index < 0 ? PhotoViewerRequest.ForOne(photo.Media) : new PhotoViewerRequest(references, index);
    }

    /// <summary>Pictures opened from outside the library: the first is shown, the rest page from it.</summary>
    private static PhotoViewerRequest LocalPhotoRequest(IReadOnlyList<string> paths) =>
        new(paths.Select(MediaReference.Local).ToList(), 0);

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
                ? Strings.Resources.Shell_CueItsAudioFile
                : $"\"{failure.ReferencedFileName}\"";
            return string.Format(Strings.Resources.Shell_CueFailureFormat, cueName, referenced);
        });

        HasWarnings = true;
        WarningText = Strings.Resources.Shell_WarnCueOpen;
        WarningDetailsText = string.Join(Environment.NewLine, messages);
    }

    /// <summary>Clears the warning/error banner once the user has reviewed it.</summary>
    public void ClearWarnings()
    {
        HasWarnings = false;
        WarningText = string.Empty;
        WarningDetailsText = string.Empty;
    }

    /// <summary>
    /// Shows the full warning or error text in the platform dialog surface, then clears the banner.
    /// </summary>
    public async Task ShowWarningDetailsAsync()
    {
        if (dialogService is null || string.IsNullOrWhiteSpace(WarningDetailsText))
        {
            return;
        }

        await dialogService.ShowWarningDetailsAsync(WarningText, WarningDetailsText);
        ClearWarnings();
    }

    /// <inheritdoc />
    public bool IsOverlayOpen => OverlayContent is not null;

    /// <inheritdoc />
    public Task ShowOverlayAsync(Control content)
    {
        // Replace any overlay already showing so a new dialog never stacks behind an old one.
        CloseOverlay();
        OverlayContent = content;
        overlayClosed = new TaskCompletionSource();
        return overlayClosed.Task;
    }

    /// <inheritdoc />
    public bool CloseOverlay()
    {
        if (OverlayContent is null)
        {
            return false;
        }

        OverlayContent = null;
        var pending = overlayClosed;
        overlayClosed = null;
        pending?.TrySetResult();
        return true;
    }

    private void PersistPlaybackQueue()
    {
        var snapshot = playbackController.CreatePersistedState();
        stateStore.PlaybackQueue = snapshot;
        if (PlatformServices.PlaybackStateObserver is { } observer)
        {
            _ = observer.UpdateSnapshotAsync(snapshot, playbackController.State == PlaybackState.Playing);
        }
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
        try
        {
            var items = await LoadShuffledLibraryItemsAsync();

            playbackController.ShuffleAll(items);
        }
        catch (Exception ex)
        {
            ReportActionFailure(Strings.Resources.Shell_WarnLoadLibrary, ex);
        }
    }

    private async Task<IReadOnlyList<MusicPlaybackItemModel>> LoadShuffledLibraryItemsAsync()
    {
        var seed = Random.Shared.NextInt64();
        for (var attempt = 0; ; attempt++)
        {
            var items = new List<MusicPlaybackItemModel>();
            string? cursor = null;
            try
            {
                do
                {
                    var page = await unifiedLibraryQueryService.GetShuffledMediaFilesPageAsync(
                        new ShuffledMediaFilePageRequest(
                            seed,
                            MediaFilePageRequest.MaxPageSize,
                            cursor));
                    items.AddRange(page.Items
                        .Select(file => TrackRowFactory.CreateRow(file, trackActions))
                        .Select(ToQueueItem));
                    cursor = page.NextCursor;
                }
                while (cursor is not null);

                return items;
            }
            catch (UnifiedQueryInvalidatedException) when (attempt == 0)
            {
            }
        }
    }

    /// <summary>
    /// Opens launch activation targets. HTTP(S) targets are opened together as one streaming media
    /// session; otherwise the local files are handled as a regular file activation.
    /// </summary>
    public void OpenActivationTargets(IReadOnlyList<string> targets)
    {
        var streamUris = new List<Uri>();
        foreach (var target in targets)
        {
            if (StreamUrl.TryParse(target, out var streamUri))
            {
                streamUris.Add(streamUri);
            }
        }

        if (streamUris.Count > 0)
        {
            _ = OpenStreamAsync(new StreamLink(streamUris));
            return;
        }

        OpenActivationFiles(targets);
    }

    /// <summary>
    /// Opens externally activated files (file picker, file-manager "Open with", command line, or a
    /// request forwarded from a secondary instance). Files carrying moving footage open on the video
    /// player page, lone pictures on the photo viewer, and the rest go to the music queue.
    /// </summary>
    public void OpenActivationFiles(IReadOnlyList<string> paths)
    {
        _ = OpenActivationFilesAsync(paths);
    }

    private async Task OpenActivationFilesAsync(IReadOnlyList<string> paths)
    {
        var (videos, photos, rest) = await ClassifyFilesAsync(paths).ConfigureAwait(false);

        if (videos.Count == 0)
        {
            await AddFilesToQueueAsync(rest, play: true).ConfigureAwait(false);

            // Only where nothing is playing: a picture opening over a video the same activation
            // asked for would take the page away from what the user is far more likely to have
            // meant by opening the two together.
            if (photos.Count > 0)
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () => Navigate(AppRouteId.PhotoViewer, LocalPhotoRequest(photos)));
            }

            return;
        }

        // The video engine takes the audio device over, so audio opened alongside a video only
        // lines up in the queue for when the user leaves the player.
        if (rest.Count > 0)
        {
            await AddFilesToQueueAsync(rest, play: false).ConfigureAwait(false);
        }

        await OpenVideoFilesAsync(videos).ConfigureAwait(false);
    }

    /// <summary>
    /// Sorts externally supplied paths (file picker, activation, drag and drop) into the videos to
    /// play, the pictures to show, and everything else, keeping the caller's order within each
    /// group.
    /// </summary>
    private async Task<(List<DbVideoFile> Videos, List<string> Photos, List<string> Others)> ClassifyFilesAsync(
        IReadOnlyList<string> paths)
    {
        var videos = new List<DbVideoFile>();
        var photos = new List<string>();
        var rest = new List<string>();
        foreach (var path in paths)
        {
            var (video, isPhoto) = await ClassifyFileAsync(path).ConfigureAwait(false);
            if (video is not null)
            {
                videos.Add(video);
            }
            else if (isPhoto)
            {
                photos.Add(path);
            }
            else
            {
                rest.Add(path);
            }
        }

        return (videos, photos, rest);
    }

    /// <summary>Starts a video session on the first of <paramref name="videos"/>, the rest as its playlist.</summary>
    private async Task OpenVideoFilesAsync(IReadOnlyList<DbVideoFile> videos)
    {
        var sourcedVideos = videos
            .Select(video => new Sourced<DbVideoFile>(LibrarySourceId.Local, video))
            .ToList();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _ = OpenVideoSession().OpenAsync(
                sourcedVideos[0], sourcedVideos, resume: false, persistPlaybackState: false);
        });
    }

    /// <summary>
    /// Opens supported image file names as photos and classifies other files by their probed media tracks.
    /// Indexed videos retain their sidecars; audio and unreadable files remain queue items.
    /// </summary>
    private async Task<(DbVideoFile? Video, bool IsPhoto)> ClassifyFileAsync(string path)
    {
        try
        {
            if (MediaContainerFormats.IsImage(path))
                return (null, true);
            if (videoLibraryQueryService is not null)
            {
                var indexed = await videoLibraryQueryService.GetVideoByPathAsync(path).ConfigureAwait(false);
                if (indexed is not null)
                {
                    return (indexed, false);
                }
            }

            var platform = await mediaResolver
                .ResolvePlatformAsync(MediaReference.Local(path))
                .ConfigureAwait(false);
            var probe = await platform
                .ProbeVideoAsync(path, withPreview: false)
                .ConfigureAwait(false);
            if (probe is null)
            {
                return (null, false);
            }

            if (probe.IsStillImage)
            {
                // A picture alongside audio is artwork for it, so the sound is what to play.
                return (null, probe.AudioStreams.Count == 0);
            }

            return (new DbVideoFile
            {
                Path = path,
                MediaKind = probe.MediaKind,
                DiscTitle = probe.DiscTitle,
                FolderPath = Path.GetDirectoryName(path) ?? string.Empty,
                FileName = MediaIdentityNames.FileName(path),
                Title = string.IsNullOrWhiteSpace(probe.Title) ? null : probe.Title,
                Duration = probe.Duration,
                Width = probe.Width,
                Height = probe.Height,
                FrameRate = probe.FrameRate,
                VideoCodec = probe.VideoCodec,
                VideoStreamIndex = probe.VideoStreamIndex,
            }, false);
        }
        catch
        {
            // A file that cannot be probed is left to the queue, which reports its own failures.
            return (null, false);
        }
    }

    private async Task AddFilesToQueueAsync(IReadOnlyList<string> paths, bool play)
    {
        // Parse cue sheets / probe tags off the UI thread, then apply the queue mutation on the
        // UI thread where the playback controller's queue projection is owned.
        var expansion = await fileQueueExpander.ExpandAsync(paths).ConfigureAwait(false);
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
    /// their tracks. Called on the drag-drop path. Files carrying moving footage leave the drop here
    /// and open on the video player instead -- the music queue cannot play them -- exactly as if they
    /// had been opened from the file manager, and dropped pictures open on the photo viewer. Cue
    /// sheets that cannot be resolved to a playable audio file surface in the warning banner rather
    /// than failing silently.
    /// </summary>
    private async Task<IReadOnlyList<MusicPlaybackItemModel>> BuildFileItemsWithMetadataAsync(IReadOnlyList<string> paths)
    {
        var (videos, photos, rest) = await ClassifyFilesAsync(paths).ConfigureAwait(false);
        if (videos.Count > 0)
        {
            await OpenVideoFilesAsync(videos).ConfigureAwait(false);
        }
        else if (photos.Count > 0)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => Navigate(AppRouteId.PhotoViewer, LocalPhotoRequest(photos)));
        }

        if (rest.Count == 0)
        {
            return [];
        }

        var expansion = await fileQueueExpander.ExpandAsync(rest).ConfigureAwait(false);
        if (expansion.Failures.Count > 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ReportCueFailures(expansion.Failures));
        }

        return expansion.Items;
    }

    /// <summary>Flushes persisted playback state and tears down the audio engine.</summary>
    public void Shutdown()
    {
        themeService.BackdropStyleChanged -= OnBackdropStyleChanged;
        sourceRegistry.CatalogChanged -= OnCatalogChanged;
        if (remoteLibraryManager is not null)
        {
            remoteLibraryManager.RefreshStatusChanged -= OnRemoteRefreshStatusChanged;
        }

        if (photoIndexService is not null)
        {
            photoIndexService.Changed -= OnPhotoIndexStatusChanged;
        }

        // Ending the session is what writes the video's resume point.
        VideoSheet.Detach()?.Dispose();
        PersistPlaybackQueue();
        playbackController.Dispose();
    }

    private void OnCatalogChanged(object? sender, LibrarySourceChangedEventArgs eventArgs)
    {
        // Local scan completion already drives the existing scan status/reload path. Remote scans
        // have no shell-wide progress state, so their catalog event reloads live aggregate pages.
        if (eventArgs.SourceId == LibrarySourceId.Local)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            RefreshLibraryCommand.NotifyCanExecuteChanged();
            if (eventArgs.SourceId is null)
            {
                InvalidateCachedLibraryPages(backStack);
                InvalidateCachedLibraryPages(forwardStack);
            }

            ReloadActiveBrowsePage();
        });
    }

    private static void InvalidateCachedLibraryPages(
        Stack<(AppRouteId Id, object? Parameter, PageViewModelBase? Page)> history)
    {
        var entries = history.ToArray();
        history.Clear();
        foreach (var entry in entries.Reverse())
        {
            (entry.Page as IDisposable)?.Dispose();
            history.Push((entry.Id, entry.Parameter, null));
        }
    }

    private void SortByColumn(LibrarySortField field)
    {
        if (activeBrowseKind is null || !LibraryViewSettings.SupportedSorts(activeBrowseKind.Value).Contains(field))
        {
            return;
        }

        if (SelectedSort.Field == field)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SetSort(field);
        }
    }

    private void SetSort(LibrarySortField field)
    {
        var option = SortOptions.FirstOrDefault(o => o.Field == field);
        if (option is not null)
        {
            SelectedSort = option;
        }
    }

    private void ChangeMediaViewSettings(LibraryViewSettings settings)
    {
        if (activeBrowsePage is not MediaPageViewModel)
            return;

        isSyncingViewSettings = true;
        try
        {
            SelectedSort = SortOptions.First(option => option.Field == settings.Sort);
            SortAscending = settings.Ascending;
            GroupingEnabled = settings.GroupingEnabled;
            GroupBy = settings.GroupBy;
        }
        finally
        {
            isSyncingViewSettings = false;
        }

        PersistViewSettings();
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
                if (browse is SongsPageViewModel songs)
                {
                    if (currentParameter is null)
                    {
                        songs.SetLayout(appSettingsStore.GetLibraryLayout(LibraryItemKind.Song));
                    }
                    songs.ApplyViewSettings(settings);
                }
                if (browse is MediaPageViewModel video)
                {
                    if (currentParameter is null)
                    {
                        video.SetLayout(appSettingsStore.GetLibraryLayout(LibraryItemKind.Video));
                    }
                    video.NarrowPresentation = appSettingsStore.VideoNarrowPresentation;
                    video.ApplyViewSettings(settings);
                }
                SelectedSort = SortOptions.FirstOrDefault(o => o.Field == settings.Sort) ?? SortOptions[0];
                SortAscending = settings.Ascending;
                GroupingEnabled = settings.GroupingEnabled;
                GroupBy = settings.GroupBy;
                SelectedLayout = browse switch
                {
                    SongsPageViewModel music => music.Layout,
                    MediaPageViewModel media => media.Layout,
                    _ => appSettingsStore.GetLibraryLayout(browse.Kind),
                };
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
            SynchronizePlaybackSnapshot();
            return;
        }

        if (e.PropertyName is nameof(PlaybackController.Mode) or nameof(PlaybackController.Volume))        {
            if (playbackController.QueueItems.Count > 0)
            {
                SynchronizePlaybackSnapshot();
            }

            return;
        }

        if (e.PropertyName is nameof(PlaybackController.State)
            or nameof(PlaybackController.PositionMs)
            or nameof(PlaybackController.DurationMs))
        {
            SynchronizePlaybackProgress();
        }
    }

    private void OnPlaybackQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        SynchronizePlaybackSnapshot();

    private void SynchronizePlaybackSnapshot()
    {
        if (PlatformServices.PlaybackStateObserver is { } observer)
        {
            _ = observer.UpdateSnapshotAsync(
                playbackController.CreatePersistedState(),
                playbackController.State == PlaybackState.Playing);
        }
    }

    private void SynchronizePlaybackProgress()
    {
        if (PlatformServices.PlaybackStateObserver is { } observer)
        {
            _ = AppErrorReporter.GuardAsync(() => observer.UpdateProgressAsync(
                playbackController.State == PlaybackState.Playing,
                playbackController.PositionMs,
                playbackController.DurationMs));
        }
    }

    private void RecordPlaybackHistory()
    {
        var item = playbackController.CurrentItem;
        if (playbackHistoryService is not null && item is not null && !string.IsNullOrWhiteSpace(item.FilePath))
        {
            _ = AppErrorReporter.GuardAsync(() => playbackHistoryService.AddHistoryAsync(item));
        }
    }

    private void UpdateNowPlayingHighlight()
    {
        var current = playbackController.CurrentItem;
        (CurrentPage as INowPlayingHighlightTarget)?.SetNowPlaying(
            current is null ? null : new MediaItemKey(current.Media, current.StartTimeMs));
    }

    private void UpdateSortVisibility()
    {
        UpdateGroupingVisibility();
        OnPropertyChanged(nameof(CanSortItems));
        OnPropertyChanged(nameof(CanSortByTitle));
        OnPropertyChanged(nameof(CanSortByAlbumName));
        OnPropertyChanged(nameof(CanSortByArtistName));
        OnPropertyChanged(nameof(CanSortByGenre));
        OnPropertyChanged(nameof(CanSortByYear));
        OnPropertyChanged(nameof(CanSortByDateAdded));
        OnPropertyChanged(nameof(CanSortByDuration));
        OnPropertyChanged(nameof(CanSortByFileType));
        OnPropertyChanged(nameof(CanSortByFileSize));
        OnPropertyChanged(nameof(CanChooseLayout));
        OnPropertyChanged(nameof(CanChooseVideoLayout));
        OnPropertyChanged(nameof(IsLayoutGrid));
        OnPropertyChanged(nameof(IsLayoutList));
        OnPropertyChanged(nameof(IsLayoutFolders));
        OnPropertyChanged(nameof(CanChooseVideoPresentation));
        OnPropertyChanged(nameof(IsVideoNarrowGrid));
        OnPropertyChanged(nameof(IsVideoNarrowList));
    }

    private void PersistViewSettings()
    {
        if (isSyncingViewSettings || activeBrowsePage is null || activeBrowseKind is null)
        {
            return;
        }

        var settings = new LibraryViewSettings(SelectedSort.Field, SortAscending)
        {
            GroupingEnabled = GroupingEnabled,
            GroupBy = GroupBy,
        }.Normalized(activeBrowseKind.Value);
        appSettingsStore.SetLibraryViewSettings(activeBrowseKind.Value, settings);
        activeBrowsePage.ApplyViewSettings(settings);
    }

    partial void OnSelectedSortChanged(LibrarySortOption value) => PersistViewSettings();

    partial void OnSortAscendingChanged(bool value) => PersistViewSettings();

    private void SetVideoNarrowPresentation(LibraryItemPresentation presentation)
    {
        if (!CanChooseVideoPresentation || !Enum.IsDefined(presentation))
        {
            return;
        }

        appSettingsStore.VideoNarrowPresentation = presentation;
        ((MediaPageViewModel)activeBrowsePage!).NarrowPresentation = presentation;
        OnPropertyChanged(nameof(IsVideoNarrowGrid));
        OnPropertyChanged(nameof(IsVideoNarrowList));
    }

    private void SetLayout(LibraryLayoutMode layout) => SelectedLayout = layout;

    partial void OnSelectedLayoutChanged(LibraryLayoutMode value)
    {
        // Ignore the programmatic sync that runs when a browse page becomes active.
        if (isSyncingViewSettings)
        {
            return;
        }

        if (activeBrowseKind == LibraryItemKind.Song)
        {
            appSettingsStore.SetLibraryLayout(LibraryItemKind.Song, value);
            (activeBrowsePage as SongsPageViewModel)?.SetLayout(value);
            return;
        }

        // The video page swaps between its flat and folder views in place, keeping the loaded
        // library and its preview bitmaps rather than re-querying everything.
        if (activeBrowseKind == LibraryItemKind.Video)
        {
            appSettingsStore.SetLibraryLayout(LibraryItemKind.Video, value);
            (activeBrowsePage as MediaPageViewModel)?.SetLayout(value);
            return;
        }

        if (activeBrowseKind is not (LibraryItemKind.Album or LibraryItemKind.Artist))
        {
            return;
        }

        appSettingsStore.SetLibraryLayout(activeBrowseKind.Value, value);

        // The grid and list layouts are distinct views, so rebuild the current page to swap between
        // them. The library cache makes the re-query cheap and the entrance transition plays.
        RecreateCurrentBrowsePage();
    }

    private void RecreateCurrentBrowsePage()
    {
        if (activeBrowseKind is null)
        {
            return;
        }

        // A paged library section is owned by the pager, so drop its slot first; otherwise the
        // navigation below would resolve straight back to the page it is meant to replace.
        InvalidateLibraryPage(currentRouteId);

        NavigateInternal(currentRouteId, currentParameter, recordHistory: false);
    }

    // Keep the current page alive until navigation replaces it.
    private void InvalidateLibraryPage(AppRouteId routeId)
    {
        var removed = LibraryPager.Remove(routeId);
        if (removed is not null && !ReferenceEquals(removed, CurrentPage))
        {
            (removed as IDisposable)?.Dispose();
        }
    }

    private void RecentlyAdded()
    {
        appSettingsStore.SetLibraryLayout(LibraryItemKind.Song, LibraryLayoutMode.Grid);
        appSettingsStore.SetLibraryViewSettings(
            LibraryItemKind.Song,
            appSettingsStore.GetLibraryViewSettings(LibraryItemKind.Song) with
            {
                Sort = LibrarySortField.DateAdded,
                Ascending = false,
            });

        // The sort is applied when the page is built, so a page the pager is already holding has to
        // be rebuilt for the new order to take effect.
        InvalidateLibraryPage(AppRouteId.Songs);

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

    private PageViewModelBase CreateSongsPage(object? parameter)
    {
        var folder = parameter as MusicFolderReference;
        var page = new SongsPageViewModel(trackActions, unifiedLibraryQueryService, sourceRegistry,
            folder is null ? appSettingsStore.GetLibraryLayout(LibraryItemKind.Song) : LibraryLayoutMode.Folders,
            folder,
            value => Navigate(AppRouteId.Songs, value),
            value => NavigateOrGoBackTo(AppRouteId.Songs, value));
        if (CurrentPage is SongsPageViewModel previous)
        {
            page.SeedBreadcrumb(previous);
        }
        MultiSelectionActions.AddMusicActions(page.Selection, libraryCommands);
        return CreateBrowsePage(page);
    }

    private async Task<IReadOnlyList<TrackRowModel>> ResolveSelectedMusicAsync(IReadOnlyList<SelectionItemModel> items)
    {
        var tracks = new List<TrackRowModel>();
        foreach (var tile in items.Cast<LibraryTileModel>())
        {
            if (tile.Track is { } track)
            {
                tracks.Add(track);
            }
            else if (tile.Identifier is { } identifier)
            {
                tracks.AddRange(tile.ThumbnailKind == ThumbnailKind.Artist
                    ? await ResolveArtistTracksAsync(identifier)
                    : await ResolveAlbumTracksAsync(identifier));
            }
        }

        return tracks.DistinctBy(track => track.MediaKey).ToArray();
    }

    private async void PlayAlbumTile(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            try
            {
                PlayTracks(await ResolveAlbumTracksAsync(id));
            }
            catch (Exception ex)
            {
                ReportActionFailure(Strings.Resources.Shell_WarnActionFailed, ex);
            }
        }
    }

    private async void PlayAlbumTileNext(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            try
            {
                var tracks = await ResolveAlbumTracksAsync(id);
                playbackController.InsertNextRange(tracks.Select(ToQueueItem).ToList());
            }
            catch (Exception ex)
            {
                ReportActionFailure(Strings.Resources.Shell_WarnActionFailed, ex);
            }
        }
    }

    private async void AddAlbumTileToQueue(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            try
            {
                AddTracksToQueue(await ResolveAlbumTracksAsync(id));
            }
            catch (Exception ex)
            {
                ReportActionFailure(Strings.Resources.Shell_WarnActionFailed, ex);
            }
        }
    }

    private async void AddAlbumTileToPlaylist(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            try
            {
                AddTracksToPlaylist(await ResolveAlbumTracksAsync(id));
            }
            catch (Exception ex)
            {
                ReportActionFailure(Strings.Resources.Shell_WarnActionFailed, ex);
            }
        }
    }

    private async void OpenArtistForAlbumTile(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            try
            {
                var reference = LibraryItemReference.Normalize(id);
                var album = await unifiedLibraryQueryService.GetAlbumAsync(
                    reference.SourceId,
                    reference.ArtistName,
                    reference.AlbumName,
                    albumGroupingKey: reference.AlbumGroupingKey);
                if (!string.IsNullOrWhiteSpace(album.Value.Artist))
                {
                    var artistName = album.Value.Artist;
                    var artist = new UnifiedArtistReference(
                        FederatedLibraryOrdering.NormalizeArtistName(artistName),
                        artistName,
                        [reference.SourceId],
                        new Dictionary<LibrarySourceId, string> { [reference.SourceId] = artistName });
                    Navigate(AppRouteId.ArtistDetail, LibraryItemReference.Artist(artist));
                }
            }
            catch (Exception ex)
            {
                ReportActionFailure(Strings.Resources.Shell_WarnLoadPage, ex);
            }
        }
    }

    private void OpenAlbumTileFolder(LibraryTileModel tile)
    {
        if (tile.CanOpenContainingFolder && tile.Media is { } media)
        {
            RevealInFileManager(media.Path);
        }
    }

    private async void PlayArtistTile(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            try
            {
                PlayTracks(await ResolveArtistTracksAsync(id));
            }
            catch (Exception ex)
            {
                ReportActionFailure(Strings.Resources.Shell_WarnActionFailed, ex);
            }
        }
    }

    private async void PlayArtistTileNext(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            try
            {
                var tracks = await ResolveArtistTracksAsync(id);
                playbackController.InsertNextRange(tracks.Select(ToQueueItem).ToList());
            }
            catch (Exception ex)
            {
                ReportActionFailure(Strings.Resources.Shell_WarnActionFailed, ex);
            }
        }
    }

    private async void AddArtistTileToQueue(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            try
            {
                AddTracksToQueue(await ResolveArtistTracksAsync(id));
            }
            catch (Exception ex)
            {
                ReportActionFailure(Strings.Resources.Shell_WarnActionFailed, ex);
            }
        }
    }

    private async void AddArtistTileToPlaylist(LibraryTileModel tile)
    {
        if (tile.Identifier is { } id)
        {
            try
            {
                AddTracksToPlaylist(await ResolveArtistTracksAsync(id));
            }
            catch (Exception ex)
            {
                ReportActionFailure(Strings.Resources.Shell_WarnActionFailed, ex);
            }
        }
    }

    private async void OpenArtistTileFolder(LibraryTileModel tile)
    {
        if (tile.CanOpenContainingFolder && tile.Identifier is { } id)
        {
            try
            {
                var tracks = await ResolveArtistTracksAsync(id);
                if (tracks.FirstOrDefault(track => track.CanOpenContainingFolder) is { } track)
                {
                    OpenTrackFolder(track);
                }
            }
            catch (Exception ex)
            {
                ReportActionFailure(Strings.Resources.Shell_WarnActionFailed, ex);
            }
        }
    }

    private async Task<IReadOnlyList<TrackRowModel>> ResolveAlbumTracksAsync(MediaLibraryItemIdentifier id)
    {
        var reference = LibraryItemReference.Normalize(id);
        var album = await unifiedLibraryQueryService.GetAlbumAsync(
            reference.SourceId,
            reference.ArtistName,
            reference.AlbumName,
            albumGroupingKey: reference.AlbumGroupingKey);
        var files = await unifiedLibraryQueryService.GetAlbumTracksAsync(
            reference.SourceId,
            album.Value.Title,
            album.Value.ArtistForQuery,
            albumGroupingKey: album.Value.AlbumGroupingKey);
        return files
            .Select(file => TrackRowFactory.CreateRow(file, trackActions))
            .OrderBy(row => row.DiscNumber)
            .ThenBy(row => row.TrackNumber)
            .ToList();
    }

    private async Task<IReadOnlyList<TrackRowModel>> ResolveArtistTracksAsync(MediaLibraryItemIdentifier id)
    {
        var reference = LibraryItemReference.Normalize(id);
        var artist = reference.UnifiedArtist ?? new UnifiedArtistReference(
            reference.ArtistName ?? string.Empty,
            reference.ArtistName ?? string.Empty,
            [reference.SourceId],
            new Dictionary<LibrarySourceId, string>
            {
                [reference.SourceId] = reference.ArtistName ?? string.Empty,
            });
        var files = await unifiedLibraryQueryService.GetArtistTracksAsync(artist);
        return TrackRowFactory.OrderByAlbumThenTrack(
            files.Items.Select(file => TrackRowFactory.CreateRow(file, trackActions)));
    }

    private void PlayTrack(TrackRowModel track)
    {
        if (track.AsyncPlaybackContext is { } asyncContext)
        {
            _ = PlayTrackAsync(track, asyncContext);
            return;
        }

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

    private async Task PlayTrackAsync(
        TrackRowModel track,
        Func<Task<IReadOnlyList<TrackRowModel>>> contextProvider)
    {
        try
        {
            var ordered = await contextProvider();
            var index = -1;
            for (var itemIndex = 0; itemIndex < ordered.Count; itemIndex++)
            {
                var item = ordered[itemIndex];
                if (item.SourceId == track.SourceId &&
                    item.MediaFileId == track.MediaFileId &&
                    item.StartTimeMs == track.StartTimeMs)
                {
                    index = itemIndex;
                    break;
                }
            }

            if (index >= 0)
            {
                playbackController.PlayAllFrom(ordered.Select(ToQueueItem).ToList(), index);
            }
            else
            {
                playbackController.PlayNow(ToQueueItem(track));
            }
        }
        catch (Exception exception)
        {
            ReportActionFailure(Strings.Resources.Shell_WarnLoadLibrary, exception);
        }
    }

    private void AddTrackToQueue(TrackRowModel track) =>
        playbackController.Enqueue(ToQueueItem(track));

    private void InsertTrackNext(TrackRowModel track) =>
        playbackController.InsertNext(ToQueueItem(track));

    private void OpenTrackFolder(TrackRowModel track) => RevealInFileManager(track.FilePath);

    // Resolve the album/artist for a track from its backing file, then navigate. The lookup
    // runs off the UI thread; a track that isn't in the library (e.g. an ad-hoc dropped file, or
    // one whose artist has no page) throws, which we swallow so the menu action is a safe no-op.
    private void OpenAlbumForTrack(TrackRowModel track) => OpenAlbumForMedia(track.Media);

    private async void OpenAlbumForMedia(MediaReference media)
    {
        if (string.IsNullOrEmpty(media.Path))
        {
            return;
        }

        try
        {
            var album = await unifiedLibraryQueryService.GetAlbumByTrackAsync(media);
            Navigate(AppRouteId.AlbumDetail, LibraryItemReference.Album(album.SourceId, album.Value));
        }
        catch (ArgumentException)
        {
        }
        catch (Exception ex)
        {
            ReportActionFailure(Strings.Resources.Shell_WarnLoadPage, ex);
        }
    }

    private void OpenArtistForTrack(TrackRowModel track) => OpenArtistForMedia(track.Media);

    private async void OpenArtistForMedia(MediaReference media)
    {
        if (string.IsNullOrEmpty(media.Path))
        {
            return;
        }

        try
        {
            var artist = await unifiedLibraryQueryService.GetArtistByTrackAsync(media);
            Navigate(AppRouteId.ArtistDetail, LibraryItemReference.Artist(artist));
        }
        catch (ArgumentException)
        {
        }
        catch (Exception ex)
        {
            ReportActionFailure(Strings.Resources.Shell_WarnLoadPage, ex);
        }
    }

    // How a file's folder is shown differs per platform (a filesystem path handed to the OS file
    // manager on desktop, a document URI handed to a provider on Android), so the head supplies the
    // implementation. Failures are reported in a dialog rather than swallowed: the action is
    // otherwise silent, leaving the user with no way to tell that nothing could open the folder.
    private async void RevealInFileManager(string filePath)
    {
        try
        {
            await fileRevealService.RevealAsync(filePath);
        }
        catch (Exception ex)
        {
            string loggedMessage = AppErrorReporter.LogAndGetMessage(
                "Application",
                $"Could not reveal {filePath} in the file manager.",
                ex);
            var message = ex is FileRevealException ? ex.Message : loggedMessage;
            if (dialogService is not null)
            {
                await dialogService.ShowWarningDetailsAsync(Strings.Resources.Shell_OpenFolderFailed, message);
            }
        }
    }

    private static MusicPlaybackItemModel ToQueueItem(TrackRowModel track) =>
        new(
            track.Title,
            track.Artist,
            track.Album,
            track.DurationTime,
            track.FilePath,
            track.StartTimeMs,
            track.SourceId);

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

    private Task PlayMusicFolderAsync(MusicFolderReference folder) =>
        ApplyToMusicFolderAsync(folder, tracks =>
        {
            PlayTracks(tracks);
            return Task.CompletedTask;
        });

    private Task PlayMusicFolderNextAsync(MusicFolderReference folder) =>
        ApplyToMusicFolderAsync(folder, tracks =>
        {
            playbackController.InsertNextRange(tracks.Select(ToQueueItem).ToList());
            return Task.CompletedTask;
        });

    private Task AddMusicFolderToQueueAsync(MusicFolderReference folder) =>
        ApplyToMusicFolderAsync(folder, tracks =>
        {
            AddTracksToQueue(tracks);
            return Task.CompletedTask;
        });

    private Task AddMusicFolderToPlaylistAsync(MusicFolderReference folder) =>
        ApplyToMusicFolderAsync(folder, AddTracksToPlaylistAsync);

    private async Task ApplyToMusicFolderAsync(
        MusicFolderReference folder,
        Func<IReadOnlyList<TrackRowModel>, Task> action)
    {
        try
        {
            var tracks = await ResolveMusicFolderTracksAsync(folder);
            if (tracks.Count > 0)
            {
                await action(tracks);
            }
        }
        catch (Exception exception)
        {
            ReportActionFailure(Strings.Resources.Shell_WarnActionFailed, exception);
        }
    }

    private async Task<IReadOnlyList<TrackRowModel>> ResolveMusicFolderTracksAsync(MusicFolderReference folder)
    {
        if (!sourceRegistry.TryGetSource(folder.SourceId, out var source) || !source.LoadMusic)
        {
            return [];
        }

        var settings = appSettingsStore.GetLibraryViewSettings(LibraryItemKind.Song)
            .Normalized(LibraryItemKind.Song);
        var files = await MusicFolderTrackResolver.ResolveAsync(
            source, folder, settings.Sort, settings.Ascending);
        return files.Select(file => TrackRowFactory.CreateRow(file, trackActions)).ToArray();
    }

    /// <summary>
    /// Resolves a library item dragged onto the now-playing queue into the ordered
    /// playback items to insert, mirroring the order they would have if added through
    /// their normal play/queue actions (albums and artists in disc/track order, a
    /// playlist in its stored order, a single track as itself).
    /// </summary>
    private async Task<IReadOnlyList<MusicPlaybackItemModel>> ResolveInsertPayloadAsync(LibraryInsertPayload payload)
    {
        try
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
                case LibraryInsertKind.MusicFolder when payload.MusicFolder is { } folder:
                    return (await ResolveMusicFolderTracksAsync(folder)).Select(ToQueueItem).ToList();
                default:
                    return Array.Empty<MusicPlaybackItemModel>();
            }
        }
        catch (Exception ex)
        {
            // The drop handler is `async void`; dropping nothing beats tearing the app down.
            ReportActionFailure(Strings.Resources.Shell_WarnActionFailed, ex);
            return Array.Empty<MusicPlaybackItemModel>();
        }
    }

    private async void AddTrackToPlaylist(TrackRowModel track)
    {
        if (playlistService is null || playlistDialogService is null)
        {
            return;
        }

        try
        {
            var target = await playlistDialogService.PickPlaylistAsync();
            if (target is not null)
            {
                await playlistService.AddItemsAsync(target.Id, new[] { ToPlaylistItem(track) });
            }
        }
        catch (Exception ex)
        {
            ReportActionFailure(Strings.Resources.Shell_WarnActionFailed, ex);
        }
    }

    private async void AddTracksToPlaylist(IReadOnlyList<TrackRowModel> tracks) =>
        await AppErrorReporter.GuardAsync(() => AddTracksToPlaylistAsync(tracks));

    private async Task AddTracksToPlaylistAsync(IReadOnlyList<TrackRowModel> tracks)
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

    private void PlayPlaylistItemsNext(IReadOnlyList<PlaylistItemModel> items)
    {
        for (var index = items.Count - 1; index >= 0; index--)
        {
            playbackController.InsertNext(items[index].ToPlaybackItem());
        }
    }

    private async Task SaveQueueAsPlaylistAsync(string name)
    {
        if (playlistService is null)
        {
            return;
        }

        var items = playbackController.QueueItems.Select(PlaylistItemModel.FromPlaybackItem).ToList();
        try
        {
            var playlist = await playlistService.CreateAsync(name);
            if (items.Count > 0)
            {
                await playlistService.AddItemsAsync(playlist.Id, items);
            }
        }
        catch (Exception ex)
        {
            ReportActionFailure(Strings.Resources.Shell_WarnActionFailed, ex);
        }
    }

    private async Task SaveQueueAsPlaylistFromMenuAsync()
    {
        if (playlistService is null || playlistDialogService is null)
        {
            return;
        }

        var name = await playlistDialogService.PromptForTitleAsync(
            Strings.Resources.QueuePane_SaveAsPlaylist,
            Strings.Resources.Common_Save);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await SaveQueueAsPlaylistAsync(name);
        }
    }

    private static PlaylistItemModel ToPlaylistItem(TrackRowModel track) =>
        new(
            track.Title,
            track.Artist,
            track.Album,
            track.FilePath,
            track.DurationTime,
            track.StartTimeMs,
            track.SourceId);

    private async Task LoadDetailPageAsync(Task load)
    {
        try
        {
            await load;
        }
        catch (Exception ex)
        {
            string message = AppErrorReporter.LogAndGetMessage(
                "Library",
                "Could not load a library detail page.",
                ex);
            Dispatcher.UIThread.Post(() =>
            {
                HasWarnings = true;
                WarningText = Strings.Resources.Shell_WarnLoadPage;
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
            string message = AppErrorReporter.LogAndGetMessage(
                "Library",
                "Could not load a library browse page.",
                ex);
            Dispatcher.UIThread.Post(() =>
            {
                HasWarnings = true;
                WarningText = Strings.Resources.Shell_WarnLoadLibrary;
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

        // The narrow layout's pager keeps the neighboring library sections alive off screen, so
        // refresh them too; otherwise a swipe lands on results from before the scan.
        foreach (var page in LibraryPager.LivePages)
        {
            if (page is ILibraryBrowsePageViewModel browse && !ReferenceEquals(browse, activeBrowsePage))
            {
                _ = LoadBrowsePageAsync(browse);
            }
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
        if (libraryScanService is null)
        {
            return;
        }

        HasWarnings = false;
        WarningText = string.Empty;
        WarningDetailsText = string.Empty;
        BeginLocalScan(0);

        try
        {
            var result = await Task.Run(libraryScanService.ScanAsync);
            CompleteLocalScan(result.IndexedCount, result.Warnings.Count, failed: false);
            ApplyScanResult(result);
            if (result.HasChanges)
            {
                ReloadActiveBrowsePage();
            }
        }
        catch (Exception ex)
        {
            HasWarnings = true;
            WarningText = Strings.Resources.Shell_WarnScanFailed;
            WarningDetailsText = AppErrorReporter.LogAndGetMessage(
                "Library.Scan",
                "The library scan failed.",
                ex);
            CompleteLocalScan(localScanIndexedCount, localScanWarningCount, failed: true);
        }
    }

    /// <summary>Auto-clears the completion status banner after a few seconds.</summary>
    private async void ScheduleScanStatusClear()
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        CancelScanStatusClear();
        scanStatusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        scanStatusClearTimer.Tick += (_, _) =>
        {
            CancelScanStatusClear();
            if (!IsScanActive)
            {
                includeLocalScanStatus = false;
                includeRemoteRefreshStatus = false;
                includePhotoIndexStatus = false;
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
        if (isLocalScanActive)
        {
            return false;
        }

        return libraryScanService is not null && HasLocalLibraryFolders(libraryLocationService);
    }

    internal static bool HasLocalLibraryFolders(ILibraryLocationService locationService) =>
        locationService.GetLibraryFolderPaths().Count > 0 ||
        locationService.GetVideoFolderPaths().Count > 0;

    private bool CanCheckForUpdates() => !IsUpdateCheckActive && IsUpdateCheckSupported;

    partial void OnIsUpdateCheckActiveChanged(bool value) => CheckForUpdatesCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Uses the browser host's update UI when available; other heads show checking, download,
    /// and result status in the split-view rows, independently of the library scan status.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        if (BrowserUpdates is { } browserUpdates)
        {
            await browserUpdates.CheckForUpdatesAsync();
            return;
        }

        if (updateCheckService is null || !updateCheckService.IsSupported)
        {
            return;
        }

        CancelUpdateStatusClear();
        IsUpdateAvailable = false;
        updateDownloadUrl = string.Empty;
        UpdateIconKey = string.Empty;
        IsUpdateCheckActive = true;
        UpdateStatusText = Strings.Resources.Shell_UpdateChecking;

        UpdateCheckResult result;
        try
        {
            result = await updateCheckService.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            string message = AppErrorReporter.LogAndGetMessage(
                "Update",
                "The update check failed.",
                ex);
            result = new UpdateCheckResult(UpdateCheckStatus.Failed, ErrorMessage: message);
        }
        finally
        {
            IsUpdateCheckActive = false;
        }

        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable when !string.IsNullOrEmpty(result.DownloadUrl):
                updateDownloadUrl = result.DownloadUrl!;
                UpdateStatusText = string.Format(Strings.Resources.Shell_UpdateDownloadFormat, result.LatestVersionText);
                IsUpdateAvailable = true;
                break;

            case UpdateCheckStatus.Failed:
                UpdateIconKey = "IconWarning";
                UpdateStatusText = string.Format(Strings.Resources.Shell_UpdateFailedFormat, result.ErrorMessage);
                ScheduleUpdateStatusClear();
                break;

            default:
                UpdateIconKey = "IconCheck";
                UpdateStatusText = Strings.Resources.Shell_UpdateUpToDate;
                ScheduleUpdateStatusClear();
                break;
        }
    }

    /// <summary>Opens the release download page in the browser, then hides the download row.</summary>
    private void DownloadUpdate()
    {
        var url = updateDownloadUrl;
        if (!string.IsNullOrEmpty(url))
        {
            _ = OpenExternalUrl?.Invoke(url);
        }

        // After one click the affordance is dismissed, per the update flow.
        IsUpdateAvailable = false;
        updateDownloadUrl = string.Empty;
        UpdateStatusText = string.Empty;
        UpdateIconKey = string.Empty;
    }

    /// <summary>Clears the update result banner after ten seconds (up-to-date / failed states).</summary>
    private void ScheduleUpdateStatusClear()
    {
        CancelUpdateStatusClear();
        updateStatusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        updateStatusClearTimer.Tick += (_, _) =>
        {
            CancelUpdateStatusClear();
            if (!IsUpdateCheckActive && !IsUpdateAvailable)
            {
                UpdateStatusText = string.Empty;
                UpdateIconKey = string.Empty;
            }
        };
        updateStatusClearTimer.Start();
    }

    private void CancelUpdateStatusClear()
    {
        updateStatusClearTimer?.Stop();
        updateStatusClearTimer = null;
    }

    private void SubscribeToScanEvents()
    {
        if (photoIndexService is not null)
        {
            photoIndexService.Changed += OnPhotoIndexStatusChanged;
            ApplyPhotoIndexStatus(photoIndexService.Status);
        }

        if (libraryScanService is null)
        {
            return;
        }

        libraryScanService.ScanStarted += OnScanStarted;
        libraryScanService.ScanProgressChanged += OnScanProgressChanged;
        libraryScanService.ScanWarning += OnScanWarning;
        libraryScanService.ScanCompleted += OnScanCompleted;
    }

    private void OnPhotoIndexStatusChanged(object? sender, EventArgs e)
    {
        var status = photoIndexService!.Status;
        Dispatcher.UIThread.Post(() => ApplyPhotoIndexStatus(status));
    }

    private void ApplyPhotoIndexStatus(PhotoIndexStatus status)
    {
        if (!status.IsIndexing && !photoIndexStatus.IsIndexing)
        {
            return;
        }

        photoIndexStatus = status;
        includePhotoIndexStatus = true;
        UpdateScanStatus(scheduleClear: !status.IsIndexing);
    }

    private void SubscribeToRemoteRefreshStatus()
    {
        if (remoteLibraryManager is null)
        {
            return;
        }

        remoteLibraryManager.RefreshStatusChanged += OnRemoteRefreshStatusChanged;
        if (remoteLibraryManager.RefreshStatus.HasActivity)
        {
            ApplyRemoteRefreshStatus(remoteLibraryManager.RefreshStatus);
        }
    }

    private void OnScanStarted(object? sender, LibraryScanStartedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            BeginLocalScan(e.Folders.Count);
        });
    }

    private void OnScanProgressChanged(object? sender, LibraryScanProgressEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            localScanIndexedCount = Math.Max(0, e.IndexedCount);
            UpdateScanStatus();
        });
    }

    private void OnScanWarning(object? sender, LibraryScanWarningEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            localScanWarningCount++;
            HasWarnings = true;
            WarningText = Strings.Resources.Shell_OneScanWarning;
            var details = $"{e.Warning.Path}{Environment.NewLine}{e.Warning.Message}";
            WarningDetailsText = string.IsNullOrEmpty(WarningDetailsText)
                ? details
                : $"{WarningDetailsText}{Environment.NewLine}{Environment.NewLine}{details}";
        });
    }

    private void OnScanCompleted(object? sender, LibraryScanCompletedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CompleteLocalScan(e.IndexedCount, e.WarningCount, failed: e.Failed);
            if (e.HasChanges)
            {
                ReloadActiveBrowsePage();
            }
        });
    }

    private void OnRemoteRefreshStatusChanged(object? sender, RemoteLibraryRefreshStatus status) =>
        Dispatcher.UIThread.Post(() => ApplyRemoteRefreshStatus(status));

    private void ApplyRemoteRefreshStatus(RemoteLibraryRefreshStatus status)
    {
        if (status.Revision <= remoteRefreshStatus.Revision)
        {
            return;
        }

        var isStarting = status.IsActive && !remoteRefreshStatus.IsActive;
        if (isStarting && !IsScanActive)
        {
            includeLocalScanStatus = false;
            includePhotoIndexStatus = false;
        }

        remoteRefreshStatus = status;
        includeRemoteRefreshStatus = status.HasActivity;
        UpdateScanStatus(scheduleClear: status.HasActivity && !status.IsActive);
    }

    private void BeginLocalScan(int folderCount)
    {
        var wasActive = isLocalScanActive;
        if (!wasActive)
        {
            if (!IsScanActive)
            {
                includeRemoteRefreshStatus = remoteRefreshStatus.IsActive;
                includePhotoIndexStatus = false;
            }

            includeLocalScanStatus = true;
            localScanIndexedCount = 0;
            localScanWarningCount = 0;
            localScanFailed = false;
        }

        isLocalScanActive = true;
        localScanFolderCount = Math.Max(0, folderCount);
        if (!wasActive)
        {
            RefreshLibraryCommand.NotifyCanExecuteChanged();
        }

        UpdateScanStatus();
    }

    private void CompleteLocalScan(int indexedCount, int warningCount, bool failed)
    {
        var wasActive = isLocalScanActive;
        includeLocalScanStatus = true;
        isLocalScanActive = false;
        localScanIndexedCount = Math.Max(0, indexedCount);
        localScanWarningCount = Math.Max(localScanWarningCount, Math.Max(0, warningCount));
        localScanFailed |= failed;
        if (wasActive)
        {
            RefreshLibraryCommand.NotifyCanExecuteChanged();
        }

        UpdateScanStatus(scheduleClear: true);
    }

    private void UpdateScanStatus(bool scheduleClear = false)
    {
        var includeRemote = includeRemoteRefreshStatus && remoteRefreshStatus.HasActivity;
        var remoteIsActive = includeRemote && remoteRefreshStatus.IsActive;
        var libraryIsActive = isLocalScanActive || remoteIsActive;
        IsScanActive = libraryIsActive || photoIndexStatus.IsIndexing;

        if (!libraryIsActive && includePhotoIndexStatus)
        {
            var progressText = string.Format(Strings.Resources.PhotoIndex_ProgressFormat,
                photoIndexStatus.IndexedCount, photoIndexStatus.ProcessedCount,
                photoIndexStatus.DiscoveredCount, photoIndexStatus.FailedCount);
            ScanStatusText = $"{Strings.Resources.PhotoIndex_Enable}: {progressText}";
            if (!string.IsNullOrEmpty(photoIndexStatus.Error))
            {
                ScanStatusText = $"{Strings.Resources.PhotoIndex_Enable}: {Strings.Resources.Shell_ScanFailed}";
            }

            if (IsScanActive)
            {
                CancelScanStatusClear();
            }
            else if (scheduleClear)
            {
                ScheduleScanStatusClear();
            }

            return;
        }

        if (!includeLocalScanStatus && !includeRemote)
        {
            CancelScanStatusClear();
            ScanStatusText = string.Empty;
            return;
        }

        var indexedCount = (int)Math.Min(
            int.MaxValue,
            (includeLocalScanStatus ? (long)localScanIndexedCount : 0L) +
            (includeRemote ? remoteRefreshStatus.IndexedCount : 0L));

        if (IsScanActive)
        {
            CancelScanStatusClear();
            if (indexedCount > 0)
            {
                ScanStatusText = indexedCount == 1
                    ? Strings.Resources.Shell_IndexedOneItem
                    : string.Format(Strings.Resources.Shell_IndexedItemsFormat, indexedCount);
            }
            else if (isLocalScanActive && !remoteIsActive && localScanFolderCount > 0)
            {
                ScanStatusText = localScanFolderCount == 1
                    ? Strings.Resources.Shell_ScanningOneFolder
                    : string.Format(Strings.Resources.Shell_ScanningFoldersFormat, localScanFolderCount);
            }
            else
            {
                ScanStatusText = Strings.Resources.Shell_ScanRefreshing;
            }

            return;
        }

        var hasWarnings =
            (includeLocalScanStatus && localScanWarningCount > 0) ||
            (includeRemote && remoteRefreshStatus.WarningCount > 0);
        ScanStatusText = localScanFailed || (includeRemote && remoteRefreshStatus.Failed)
            ? Strings.Resources.Shell_ScanFailed
            : hasWarnings
                ? FormatScanCompletedText(-1)
                : FormatScanCompletedText(indexedCount);

        if (scheduleClear)
        {
            ScheduleScanStatusClear();
        }
    }

    private void ApplyScanResult(LibraryScanResult result)
    {
        if (result.Warnings.Count == 0)
        {
            return;
        }

        HasWarnings = true;
        WarningText = result.Warnings.Count == 1
            ? Strings.Resources.Shell_OneScanWarning
            : string.Format(Strings.Resources.Shell_ScanWarningsFormat, result.Warnings.Count);
        WarningDetailsText = string.Join(
            Environment.NewLine + Environment.NewLine,
            result.Warnings.Select(warning => $"{warning.Path}{Environment.NewLine}{warning.Message}"));
    }

    private static string FormatScanCompletedText(int indexedCount)
    {
        if (indexedCount < 0)
        {
            return Strings.Resources.Shell_ScanCompletedWithWarnings;
        }

        return indexedCount == 1
            ? Strings.Resources.Shell_ScanCompleteOneItem
            : string.Format(Strings.Resources.Shell_ScanCompleteItemsFormat, indexedCount);
    }

}
