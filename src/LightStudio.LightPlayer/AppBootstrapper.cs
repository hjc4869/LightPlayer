using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels;
using LightMediaRenderer.Avalonia.Shaders;
using LightStudio.LightPlayer.Views;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer;

/// <summary>
/// Composition root for the desktop shell. Builds the main window and wires the
/// shell view model with its services.
/// </summary>
public sealed class AppBootstrapper
{
    private readonly IAppSettingsStore settingsStore;
    private readonly ILibraryLocationService libraryLocationService;
    private readonly ILibraryScanService? libraryScanService;
    private readonly ILibraryQueryService libraryQueryService;
    private readonly IVideoLibraryQueryService videoLibraryQueryService;
    private readonly ISubtitleFileBrowser? subtitleFileBrowser;
    private readonly IShellStateStore stateStore;
    private readonly IThemeService themeService;
    private readonly IPlaylistService? playlistService;
    private readonly IPlaylistService? videoPlaylistService;
    private readonly IPlaylistDialogService? playlistDialogService;
    private readonly IDialogService dialogService;
    private readonly IFileRevealService fileRevealService;
    private readonly ILyricSourceService? lyricSourceService;
    private readonly ILyricsService lyricsService;
    private readonly IFileQueueExpander fileQueueExpander;
    private readonly IUpdateCheckService updateCheckService;
    private readonly IPlaybackHistoryServiceFactory playbackHistoryServiceFactory;
    private readonly IClientMediaResolver mediaResolver;
    private readonly IUnifiedLibraryQueryService unifiedLibraryQueryService;
    private readonly IUnifiedVideoLibraryQueryService unifiedVideoLibraryQueryService;
    private readonly VideoShaderPackageCatalog shaderPackages;
    private ShellViewModel? shell;

    public AppBootstrapper()
    {
        var localServices = PlatformServices.LocalServicesFactory?.Invoke()
            ?? throw new InvalidOperationException("The application head must register PlatformServices.LocalServicesFactory before creating the UI.");

        stateStore = localServices.ShellState;
        settingsStore = localServices.Settings;
        PlatformServices.PhotoIndex = localServices.PhotoIndex;
        PlatformServices.AudioSeparation = localServices.AudioSeparation;
        themeService = new AvaloniaThemeService(settingsStore);
        libraryLocationService = localServices.LibraryLocation;
        libraryScanService = localServices.LibraryScan;
        libraryQueryService = localServices.LibraryQuery;
        videoLibraryQueryService = localServices.VideoLibraryQuery;
        subtitleFileBrowser = localServices.SubtitleFiles;
        dialogService = CreateDialogService();
        fileRevealService = PlatformServices.FileRevealService ?? new DesktopFileRevealService();
        PlatformServices.FileShareService ??= new DesktopFileShareService();
        playlistService = localServices.Playlists;
        videoPlaylistService = localServices.VideoPlaylists;
        playlistDialogService = CreatePlaylistDialogService(localServices.Playlists, localServices.VideoPlaylists);
        lyricSourceService = localServices.LyricSources;
        lyricsService = localServices.Lyrics;
        fileQueueExpander = localServices.FileQueueExpander;
        playbackHistoryServiceFactory = localServices.PlaybackHistory;
        updateCheckService = localServices.UpdateCheck;
        shaderPackages = PlatformServices.VideoShaderPackages ??= new VideoShaderPackageCatalog(
            AppPaths.DataSubdirectory("video-shader-packages"));
        var registry = PlatformServices.LibrarySources;
        if (registry is null)
        {
            registry = new LibrarySourceRegistry(new LocalLibrarySource(localServices), settingsStore, localServices.PhotoIndex);
            PlatformServices.LibrarySources = registry;
        }

        mediaResolver = new ClientMediaResolver(registry);

        // Sidecar lookup has to reach the media of whichever source an item came from.
        if (lyricsService is IMediaResolverAwareLyricsService resolverAware)
        {
            resolverAware.MediaResolver = mediaResolver;
        }

        unifiedLibraryQueryService = new UnifiedLibraryQueryService(
            registry,
            () => PlatformServices.RemoteLibraryManager?.VisibilitySettings ?? new LibraryVisibilitySettings());
        unifiedVideoLibraryQueryService = new UnifiedVideoLibraryQueryService(registry);
    }

    public MainWindow CreateMainWindow(IReadOnlyList<string> activationTargets)
    {
        var window = new MainWindow();
        PlatformServices.ConfigureWindowChrome?.Invoke(window);
        themeService.ApplyFromSettings();
        shell = CreateShellViewModel();
        if (activationTargets.Count > 0)
        {
            shell.OpenActivationTargets(activationTargets);
        }

        window.DataContext = new MainWindowViewModel(shell);
        window.ThemeService = themeService;

        // Restore the last window size / maximized state and persist it again on close.
        WindowPlacementManager.Attach(window, stateStore);

        // A window of its own is how the desktop detaches the picture. Wayland compositors place
        // windows themselves and keep none on top, so there it lands wherever they decide.
        PlatformServices.PictureInPicture ??= new DesktopPictureInPictureHost(stateStore);
        return window;
    }

    /// <summary>
    /// Builds the shell control for single-view platforms (Android/mobile), which host
    /// a <see cref="Control"/> as their <c>MainView</c> instead of a window.
    /// </summary>
    public Control CreateMainView(IReadOnlyList<string> activationTargets)
    {
        shell = CreateShellViewModel();
        if (activationTargets.Count > 0)
        {
            shell.OpenActivationTargets(activationTargets);
        }

        return new ShellView
        {
            DataContext = shell,
        };
    }

    /// <summary>True when the first-run setup should be shown before the main window.</summary>
    public bool NeedsInitialSetup => !settingsStore.InitialSetupCompleted;

    /// <summary>
    /// Opens externally activated local files. A file carrying a video track opens on
    /// the video player page; the rest replace the queue and start playing. No-op before
    /// the main window has been created.
    /// </summary>
    public void OpenFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0)
        {
            shell?.OpenActivationFiles(paths);
        }
    }

    /// <summary>Opens local files or an HTTP(S) stream received from a launch activation.</summary>
    public void OpenActivationTargets(IReadOnlyList<string> targets)
    {
        if (targets.Count > 0)
        {
            shell?.OpenActivationTargets(targets);
        }
    }

    /// <summary>Builds the first-run setup window; the host opens the main window on completion.</summary>
    public InitialSetupWindow CreateSetupWindow()
    {
        var window = new InitialSetupWindow();
        PlatformServices.ConfigureWindowChrome?.Invoke(window);
        themeService.ApplyFromSettings();
        window.DataContext = new InitialSetupViewModel(settingsStore, themeService, lyricSourceService);
        return window;
    }

    /// <summary>
    /// Builds the first-run setup control for single-view platforms (Android/mobile), which host
    /// a <see cref="Control"/> as their <c>MainView</c> instead of a window. The host swaps in the
    /// main view (see <see cref="CreateMainView"/>) when the view model raises
    /// <see cref="InitialSetupViewModel.SetupCompleted"/>.
    /// </summary>
    public InitialSetupView CreateSetupView() => new()
    {
        DataContext = new InitialSetupViewModel(settingsStore, themeService, lyricSourceService),
    };

    /// <summary>Starts the first library scan after setup.</summary>
    public void StartInitialScan()
    {
        if (!settingsStore.NativeLibrary.IsFileSystemEnabled(LibraryMediaCategory.Music) &&
            !settingsStore.NativeLibrary.IsFileSystemEnabled(LibraryMediaCategory.Video))
        {
            return;
        }

        if (shell?.RefreshLibraryCommand.CanExecute(null) == true)
        {
            shell.RefreshLibraryCommand.Execute(null);
        }
    }

    /// <summary>
    /// Runs a library scan right after the main window opens when the user enabled
    /// "scan at startup". First-run launches scan via <see cref="StartInitialScan"/> instead.
    /// </summary>
    public void StartStartupScanIfEnabled()
    {
        if (!settingsStore.ScanLibraryOnStartup ||
            (!settingsStore.NativeLibrary.IsFileSystemEnabled(LibraryMediaCategory.Music) &&
             !settingsStore.NativeLibrary.IsFileSystemEnabled(LibraryMediaCategory.Video)))
        {
            return;
        }

        if (shell?.RefreshLibraryCommand.CanExecute(null) == true)
        {
            shell.RefreshLibraryCommand.Execute(null);
        }
    }

    /// <summary>
    /// Offers to restore access to saved library folders through user gestures.
    /// </summary>
    public void PromptForFolderAccessRenewal()
    {
        if (PlatformServices.LibraryFolderAccess is not { } access)
        {
            return;
        }

        var stale = settingsStore.LibraryFolderPaths
            .Concat(settingsStore.VideoFolderPaths)
            .Distinct(StringComparer.Ordinal)
            .Where(access.RequiresAccessGrant)
            .ToArray();
        if (stale.Length > 0)
        {
            _ = RenewFolderAccessAsync(access, stale);
        }
    }

    private async Task RenewFolderAccessAsync(ILibraryFolderAccess access, IReadOnlyList<string> folders)
    {
        if (folders.Count > 1 && folders.Any(access.RequiresIndividualAccessGrant))
        {
            foreach (var folder in folders)
            {
                await RenewFolderAccessAsync(access, [folder]);
            }

            return;
        }

        var confirmed = await dialogService.ConfirmAsync(
            Strings.Resources.LibrarySettings_RenewFolderAccessTitle,
            string.Format(
                CultureInfo.CurrentCulture,
                Strings.Resources.LibrarySettings_RenewFolderAccessMessageFormat,
                string.Join(Environment.NewLine, folders.Select(path => access.GetDisplayName(path) ?? path))),
            Strings.Resources.LibrarySettings_GrantFolderAccess);

        // Nothing else may be awaited first: the click that confirmed is the gesture the browser
        // answers the permission request from.
        if (confirmed)
        {
            await access.RequestAccessAsync(folders);
        }
    }

    /// <summary>Confirms and queues a reset of the device-local index databases.</summary>
    internal async Task<bool> ConfirmIndexDatabaseResetAsync()
    {
        var queueReset = PlatformServices.QueueIndexDatabaseResetAsync
            ?? throw new InvalidOperationException("Index database reset is not available.");
        var browser = OperatingSystem.IsBrowser();
        var message = browser
            ? Strings.Resources.About_ResetIndexDatabaseBrowserConfirm
            : Strings.Resources.About_ResetIndexDatabaseConfirm;
        if (!PlatformServices.SupportsStartupLibraryScan || !settingsStore.ScanLibraryOnStartup)
            message += Environment.NewLine + Environment.NewLine + Strings.Resources.About_ResetIndexDatabaseManualScan;

        if (!await dialogService.ConfirmAsync(
                Strings.Resources.About_ResetIndexDatabase,
                message,
                browser ? Strings.Resources.About_ResetAndReload : Strings.Resources.About_ResetAndClose))
            return false;

        await queueReset();
        return true;
    }

    /// <summary>Persists playback state and tears down the audio engine on exit.</summary>
    public void Shutdown()
    {
        (libraryScanService as IDisposable)?.Dispose();
        shell?.Shutdown();
        PlatformServices.AudioSeparation?.Dispose();
        PlatformServices.AudioSeparation = null;
        if (ReferenceEquals(PlatformServices.VideoShaderPackages, shaderPackages))
        {
            PlatformServices.VideoShaderPackages = null;
            shaderPackages.Dispose();
        }
    }

    private ShellViewModel CreateShellViewModel()
    {
        var viewModel = new ShellViewModel(
            AppRoutes.All,
            stateStore,
            settingsStore,
            themeService,
            libraryLocationService,
            libraryScanService,
            playlistService,
            playlistDialogService,
            dialogService,
            lyricSourceService,
            updateCheckService,
            playbackHistoryServiceFactory,
            libraryQueryService,
            lyricsService,
            fileQueueExpander,
            fileRevealService,
            videoLibraryQueryService,
            subtitleFileBrowser,
            mediaResolver,
            unifiedLibraryQueryService,
            unifiedVideoLibraryQueryService,
            videoPlaylistService);

        // The single-view dialog services render inside the shell, so they need the shell view model
        // as their overlay host (injected here to break the construction-order cycle).
        if (dialogService is OverlayDialogService overlayDialogs)
        {
            overlayDialogs.Host = viewModel;
        }

        if (playlistDialogService is OverlayPlaylistDialogService overlayPlaylistDialogs)
        {
            overlayPlaylistDialogs.Host = viewModel;
        }

        return viewModel;
    }

    /// <summary>
    /// Chooses the dialog presentation strategy for the current platform: native windows on the
    /// classic desktop lifetime, in-shell overlays on single-view platforms (Android) where
    /// additional windows are unsupported.
    /// </summary>
    internal static IDialogService CreateDialogService() =>
        Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime
            ? new OverlayDialogService()
            : new DesktopDialogService();

    /// <summary>Platform-appropriate playlist dialogs: native windows on desktop, overlays on mobile.</summary>
    private static IPlaylistDialogService CreatePlaylistDialogService(
        IPlaylistService playlists,
        IPlaylistService videoPlaylists) =>
        Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime
            ? new OverlayPlaylistDialogService(playlists, videoPlaylists)
            : new DesktopPlaylistDialogService(playlists, videoPlaylists);

}