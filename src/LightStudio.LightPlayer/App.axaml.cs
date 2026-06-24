using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

    private AppBootstrapper? bootstrapper;
    private IClassicDesktopStyleApplicationLifetime? desktopLifetime;

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
            var fileActivationPaths = FileActivation.ExtractPaths(args);

            bootstrapper = new AppBootstrapper();
            desktop.ShutdownRequested += (_, _) =>
            {
                bootstrapper.Shutdown();
                SingleInstance?.Dispose();
            };

            // Deliver file-open / activation requests forwarded from secondary
            // launches to this (primary) instance.
            if (SingleInstance is not null)
            {
                SingleInstance.FilesOpened += OnFilesOpenedFromSecondary;
                SingleInstance.Activated += OnActivatedFromSecondary;
            }

            if (fileActivationPaths.Count == 0 && bootstrapper.NeedsInitialSetup)
            {
                var setupWindow = bootstrapper.CreateSetupWindow();
                desktop.MainWindow = setupWindow;
                if (setupWindow.DataContext is ViewModels.InitialSetupViewModel setupViewModel)
                {
                    setupViewModel.SetupCompleted += () =>
                    {
                        var mainWindow = bootstrapper.CreateMainWindow(Array.Empty<string>());
                        desktop.MainWindow = mainWindow;
                        mainWindow.Show();
                        setupWindow.Close();
                        bootstrapper.StartInitialScan();
                    };
                }
            }
            else
            {
                desktop.MainWindow = bootstrapper.CreateMainWindow(fileActivationPaths);
                bootstrapper.StartStartupScanIfEnabled();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnFilesOpenedFromSecondary(IReadOnlyList<string> paths)
    {
        // Raised on the D-Bus read loop; marshal to the UI thread before touching
        // the shell and windows.
        Dispatcher.UIThread.Post(() =>
        {
            bootstrapper?.OpenFiles(paths);
            ActivateMainWindow();
        });
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