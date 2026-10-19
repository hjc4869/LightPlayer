using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer;

public partial class App : Application
{
    /// <summary>
    /// The single-instance coordinator for this process, created before Avalonia
    /// starts (see <see cref="Program"/>). On the primary instance it stays alive
    /// for the app lifetime and delivers file-open / activation requests forwarded
    /// by secondary launches.
    /// </summary>
    internal static ISingleInstanceService? SingleInstance { get; set; }

    internal static Func<Window>? StandaloneWindowFactory { get; set; }
    internal static Func<IReadOnlyList<string>, bool>? InitializeDesktopServices { get; set; }

    private AppBootstrapper? bootstrapper;
    private IClassicDesktopStyleApplicationLifetime? desktopLifetime;
    private bool awaitingDesktopActivation;

    // The single-view content host, whose content is swapped between the setup view and the shell.
    private ContentControl? viewHost;
    private Views.VideoKeyboardShortcutHandler? videoKeyboardShortcuts;

    // Files or stream URLs this launch was asked to open, replayed once the shell exists.
    private IReadOnlyList<string> startupActivationTargets = Array.Empty<string>();

    // Buffers file-open activations (e.g. an Android "Open with" intent) that arrive before the
    // shell exists during startup; drained by FlushPendingActivationFiles once the main view is
    // created so a cold-start file open is not lost.
    private readonly List<string> pendingActivationFiles = new();
    private bool shellReady;

    internal async System.Threading.Tasks.Task ResetIndexDatabaseAsync()
    {
        if (bootstrapper is null)
            throw new InvalidOperationException("The application is not initialized.");
        if (!await bootstrapper.ConfirmIndexDatabaseResetAsync())
            return;

        bootstrapper.Shutdown();
        bootstrapper = null;
        if (PlatformServices.ReloadAfterIndexDatabaseReset is { } reload)
        {
            reload();
        }
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            LightStudio.Logging.AppLog.Shutdown();
            Environment.Exit(0);
        }
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktopLifetime = desktop;
            var args = desktop.Args ?? Array.Empty<string>();
            var activationTargets = FileActivation.ExtractTargets(args);
            startupActivationTargets = activationTargets;

            desktop.ShutdownRequested += (_, _) =>
            {
                bootstrapper?.Shutdown();
                SingleInstance?.Dispose();
            };

            if (this.TryGetFeature<IActivatableLifetime>() is { } activatableLifetime)
            {
                activatableLifetime.Activated += OnFileActivated;
                awaitingDesktopActivation = OperatingSystem.IsMacOS() && activationTargets.Count == 0;
            }

            if (StandaloneWindowFactory is { } createStandalone)
            {
                desktop.MainWindow = createStandalone();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            if (awaitingDesktopActivation)
                Dispatcher.UIThread.Post(InitializePendingDesktopWindow, DispatcherPriority.Background);
            else
                InitializeDesktopWindow();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // Mobile/single-view platforms (Android) host a single Control as MainView. Unlike the
            // desktop lifetime, the Android activity reads MainView once at startup (via its
            // MainViewFactory) and never re-reads it, so reassigning MainView later does not update
            // the screen. Host a persistent ContentControl and swap ITS content instead to move
            // between the first-run setup view and the shell. The single-instance / file-forwarding
            // flow is desktop-only, so there is nothing else to wire here.
            var host = new ContentControl
            {
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            };
            TopLevel.SetAutoSafeAreaPadding(host, false);
            viewHost = host;
            videoKeyboardShortcuts = new Views.VideoKeyboardShortcutHandler(host);
            singleView.MainView = host;

            bootstrapper = new AppBootstrapper();
            if (bootstrapper.NeedsInitialSetup)
            {
                // First run: show the setup view, then swap in the shell on completion.
                // Mirrors the desktop setup-window flow without a second window.
                var setupView = bootstrapper.CreateSetupView();
                host.Content = setupView;
                if (setupView.DataContext is ViewModels.InitialSetupViewModel setupViewModel)
                {
                    setupViewModel.SetupCompleted += () => ShowMainView(initialScan: true);
                }
            }
            else
            {
                ShowMainView(initialScan: false);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeDesktopWindow()
    {
        if (desktopLifetime is not { } desktop)
            return;
        if (InitializeDesktopServices?.Invoke(startupActivationTargets) == false)
        {
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
            return;
        }

        if (SingleInstance is not null)
        {
            SingleInstance.FilesOpened += OnFilesOpenedFromSecondary;
            SingleInstance.Activated += OnActivatedFromSecondary;
        }

        bootstrapper = new AppBootstrapper();
        if (startupActivationTargets.Count == 0 && bootstrapper.NeedsInitialSetup)
        {
            var setupWindow = bootstrapper.CreateSetupWindow();
            desktop.MainWindow = setupWindow;
            if (setupWindow.DataContext is ViewModels.InitialSetupViewModel setupViewModel)
                setupViewModel.SetupCompleted += () => ShowMainWindow(setupWindow, initialScan: true);
        }
        else
        {
            desktop.MainWindow = bootstrapper.CreateMainWindow(startupActivationTargets);
            bootstrapper.StartStartupScanIfEnabled();
            bootstrapper.PromptForFolderAccessRenewal();
            FlushPendingActivationFiles();
        }
    }

    private void InitializePendingDesktopWindow()
    {
        if (!awaitingDesktopActivation)
            return;
        awaitingDesktopActivation = false;
        InitializeDesktopWindow();
        desktopLifetime?.MainWindow?.Show();
    }

    /// <summary>Replaces the window currently on screen with a freshly built shell.</summary>
    private void ShowMainWindow(Window? previous, bool initialScan)
    {
        if (desktopLifetime is null)
        {
            return;
        }

        bootstrapper = new AppBootstrapper();
        var mainWindow = bootstrapper.CreateMainWindow(startupActivationTargets);
        desktopLifetime.MainWindow = mainWindow;
        mainWindow.Show();
        previous?.Close();

        if (initialScan)
        {
            bootstrapper.StartInitialScan();
        }
        else
        {
            bootstrapper.StartStartupScanIfEnabled();
        }

        bootstrapper.PromptForFolderAccessRenewal();
        FlushPendingActivationFiles();
    }

    /// <summary>Single-view equivalent of <see cref="ShowMainWindow"/>.</summary>
    private void ShowMainView(bool initialScan)
    {
        if (viewHost is null)
        {
            return;
        }

        bootstrapper = new AppBootstrapper();
        var mainView = bootstrapper.CreateMainView(startupActivationTargets);
        viewHost.Content = mainView;
        videoKeyboardShortcuts?.Attach(
            (mainView.DataContext as ViewModels.ShellViewModel)?.VideoSheet);

        if (initialScan)
        {
            bootstrapper.StartInitialScan();
        }
        else
        {
            bootstrapper.StartStartupScanIfEnabled();
        }

        bootstrapper.PromptForFolderAccessRenewal();
        FlushPendingActivationFiles();
    }

    /// <summary>
    /// Delivers externally activated files (for example an Android "Open with" intent) to the
    /// shell, replacing the queue and starting playback. Safe to call from any thread and before
    /// the shell exists: requests that arrive during startup are buffered and applied once the
    /// main view is ready.
    /// </summary>
    public void OpenFiles(IReadOnlyList<string> paths)

    {
        if (paths is null || paths.Count == 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!shellReady)
            {
                pendingActivationFiles.AddRange(paths);
                return;
            }

            bootstrapper?.OpenFiles(paths);
            ActivateMainWindow();
        });
    }

    /// <summary>Leaves first-run setup after an external pairing has saved a remote library.</summary>
    internal void CompletePairingLaunch()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!shellReady && viewHost is not null)
            {
                ShowMainView(initialScan: false);
            }
        });
    }

    /// <summary>
    /// Marks the shell ready and delivers any file-open requests buffered during startup. Called
    /// once the main view has been created.
    /// </summary>
    private void FlushPendingActivationFiles()
    {
        shellReady = true;
        if (pendingActivationFiles.Count == 0)
        {
            return;
        }

        var paths = pendingActivationFiles.ToArray();
        pendingActivationFiles.Clear();
        bootstrapper?.OpenFiles(paths);
    }

    private void OnFileActivated(object? sender, ActivatedEventArgs e)
    {
        // Only file activations carry paths to enqueue; ignore other activation
        // kinds (reopen, background, protocol).
        if (e is not FileActivatedEventArgs fileActivated)
        {
            return;
        }

        var paths = new List<string>(fileActivated.Files.Count);
        foreach (var file in fileActivated.Files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                paths.Add(path);
            }
        }

        if (paths.Count == 0)
        {
            return;
        }

        // The activation callback runs on the UI thread; post so the shell is
        // mutated outside the callback, matching the secondary-instance path.
        Dispatcher.UIThread.Post(() =>
        {
            OpenDesktopActivation(paths);
        });
    }

    private void OnFilesOpenedFromSecondary(IReadOnlyList<string> paths)
    {
        // Raised on the D-Bus read loop; marshal to the UI thread before touching
        // the shell and windows.
        Dispatcher.UIThread.Post(() =>
        {
            OpenDesktopActivation(paths);
        });
    }

    private void OpenDesktopActivation(IReadOnlyList<string> paths)
    {
        var remaining = new List<string>();
        foreach (var path in paths)
        {
            if (PlatformServices.TryOpenInNewWindow?.Invoke(path) != true)
                remaining.Add(path);
        }

        if (awaitingDesktopActivation)
        {
            awaitingDesktopActivation = false;
            if (remaining.Count == 0)
                desktopLifetime?.Shutdown();
            else
            {
                startupActivationTargets = remaining;
                InitializeDesktopWindow();
                desktopLifetime?.MainWindow?.Show();
            }
        }
        else if (remaining.Count > 0)
        {
            bootstrapper?.OpenActivationTargets(remaining);
            ActivateMainWindow();
        }
    }

    private void OnActivatedFromSecondary()
    {
        Dispatcher.UIThread.Post(ActivateMainWindow);
    }

    private void ActivateMainWindow()
    {
        if (desktopLifetime?.MainWindow is not { } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }
}