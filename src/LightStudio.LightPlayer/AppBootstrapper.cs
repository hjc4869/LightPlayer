using System.Collections.Generic;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels;
using LightStudio.LightPlayer.Views;
using LightStudio.MediaLibraryCore;

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
    private readonly IShellStateStore stateStore;
    private readonly IThemeVariantService themeVariantService;
    private readonly IPlaylistService? playlistService;
    private readonly IPlaylistDialogService? playlistDialogService;
    private readonly IDialogService dialogService;
    private readonly ILyricSourceService? lyricSourceService;
    private ShellViewModel? shell;

    public AppBootstrapper()
    {
        stateStore = new JsonShellStateStore();
        settingsStore = new JsonAppSettingsStore();
        themeVariantService = new AvaloniaThemeVariantService();
        libraryLocationService = new AppSettingsLibraryLocationService(settingsStore);
        libraryScanService = CreateLibraryScanService(libraryLocationService);
        dialogService = new DesktopDialogService();

        var playlists = new JsonPlaylistService();
        playlists.InitializeAsync().GetAwaiter().GetResult();
        playlistService = playlists;
        playlistDialogService = new DesktopPlaylistDialogService(playlists);
        lyricSourceService = new JsonLyricSourceService();
    }

    public MainWindow CreateMainWindow(IReadOnlyList<string> fileActivationPaths)
    {
        shell = CreateShellViewModel();
        if (fileActivationPaths.Count > 0)
        {
            shell.AddFilesToQueue(fileActivationPaths, play: true);
        }

        return new MainWindow
        {
            DataContext = new MainWindowViewModel(shell),
        };
    }

    /// <summary>True when the first-run setup should be shown before the main window.</summary>
    public bool NeedsInitialSetup => !settingsStore.InitialSetupCompleted;

    /// <summary>
    /// Opens externally-activated files (file-manager "Open with", command line, or
    /// a request forwarded from a secondary instance), replacing the current queue
    /// and starting playback. No-op before the main window has been created.
    /// </summary>
    public void OpenFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0)
        {
            shell?.AddFilesToQueue(paths, play: true);
        }
    }

    /// <summary>Builds the first-run setup window; the host opens the main window on completion.</summary>
    public InitialSetupWindow CreateSetupWindow() => new()
    {
        DataContext = new InitialSetupViewModel(settingsStore, themeVariantService, lyricSourceService),
    };

    /// <summary>Kicks off the first library scan after setup, matching the original first-run flow.</summary>
    public void StartInitialScan()
    {
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
        if (!settingsStore.ScanLibraryOnStartup)
        {
            return;
        }

        if (shell?.RefreshLibraryCommand.CanExecute(null) == true)
        {
            shell.RefreshLibraryCommand.Execute(null);
        }
    }

    /// <summary>Persists playback state and tears down the audio engine on exit.</summary>
    public void Shutdown() => shell?.Shutdown();

    private ShellViewModel CreateShellViewModel()
    {
        return new ShellViewModel(
            AppRoutes.All,
            stateStore,
            settingsStore,
            themeVariantService,
            libraryLocationService,
            libraryScanService,
            playlistService,
            playlistDialogService,
            dialogService,
            lyricSourceService);
    }

    private static ILibraryScanService CreateLibraryScanService(ILibraryLocationService libraryLocationService)
    {
        var platform = new DesktopMultimediaPlatformIO();
        platform.InitializePlatform();
        ApplicationServiceBase.App.ConfigureServicesAsync().GetAwaiter().GetResult();
        return new LibraryScanService(libraryLocationService);
    }
}