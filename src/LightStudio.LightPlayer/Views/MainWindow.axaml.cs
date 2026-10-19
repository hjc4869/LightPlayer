using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels;
using LightStudio.LightPlayer.ViewModels.Pages;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Views;

public partial class MainWindow : Window
{
    private IThemeService? themeService;
    private ShellViewModel? shell;
    private SettingsWindow? settingsWindow;
    private readonly VideoKeyboardShortcutHandler videoKeyboardShortcuts;

    public MainWindow()
    {
        InitializeComponent();
        videoKeyboardShortcuts = new VideoKeyboardShortcutHandler(this, useGlobalHandlers: true);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (shell is not null)
        {
            shell.PropertyChanged -= OnShellPropertyChanged;
            shell.ShowSettingsWindow = null;
        }

        shell = (DataContext as MainWindowViewModel)?.Shell;
        if (shell is not null)
        {
            shell.PropertyChanged += OnShellPropertyChanged;
            if (OperatingSystem.IsMacOS() || PlatformServices.SupportsClientSideWindowControls)
            {
                shell.ShowSettingsWindow = ShowSettingsWindowAsync;
            }
        }

        videoKeyboardShortcuts.Attach(shell?.VideoSheet);
        ApplyTitleBarExtension();
    }

    private async Task ShowSettingsWindowAsync(SettingsPageViewModel settings)
    {
        if (settingsWindow is { } open)
        {
            open.Activate();
            return;
        }

        var window = new SettingsWindow { DataContext = settings, ThemeService = themeService };
        window.Closed += (_, _) => settingsWindow = null;
        settingsWindow = window;
        var tooltipServiceEnabled = ToolTip.GetServiceEnabled(this);
        SetCurrentValue(ToolTip.ServiceEnabledProperty, false);
        try
        {
            foreach (var visual in this.GetVisualDescendants())
            {
                if (visual is Control control && ToolTip.GetIsOpen(control))
                {
                    ToolTip.SetIsOpen(control, false);
                }
            }

            await window.ShowDialog(this);
        }
        finally
        {
            SetCurrentValue(ToolTip.ServiceEnabledProperty, tooltipServiceEnabled);
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(ShellViewModel.ExtendsIntoTitleBar))
        {
            ApplyTitleBarExtension();
        }
    }

    // The Apple layout draws its sidebar and toolbar into the title bar, under the window controls.
    private void ApplyTitleBarExtension()
    {
        if (PlatformServices.SupportsClientSideWindowControls)
        {
            WindowDecorations = shell?.UseCustomWindowControls == true
                ? WindowDecorations.BorderOnly
                : WindowDecorations.Full;
        }

        ExtendClientAreaToDecorationsHint = shell?.ExtendsIntoTitleBar == true;
        UpdateWindowControls();
        ApplyToolbarTitleBar();
    }

    private void UpdateWindowControls()
    {
        Classes.Set("appleCsd", shell?.UseCustomWindowControls == true);
        Classes.Set("roundedCsd", shell?.UseCustomWindowControls == true && WindowState == WindowState.Normal);
        ClientWindowControls.IsVisible = shell?.UseCustomWindowControls == true && WindowState != WindowState.FullScreen;
        MaximizeWindowButton.Classes.Set("maximized", WindowState == WindowState.Maximized);
    }

    private void OnCloseWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => Close();

    private void OnMinimizeWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // Applied again whenever the client area extension changes, which resets the title bar. Full
    // screen has no window controls to line up.
    private void ApplyToolbarTitleBar()
    {
        if (PlatformServices.ApplyToolbarTitleBar is { } apply && TryGetPlatformHandle() is { } handle)
        {
            apply(handle.Handle, ExtendClientAreaToDecorationsHint && WindowState != WindowState.FullScreen);
        }
    }

    /// <summary>
    /// Theme service that owns the backdrop preference. Set by the composition root before the
    /// window is shown; left null in the designer and on heads that host the shell without a
    /// window, where the shell keeps its opaque surfaces.
    /// </summary>
    public IThemeService? ThemeService
    {
        get => themeService;
        set
        {
            if (ReferenceEquals(themeService, value))
            {
                return;
            }

            if (themeService is not null)
            {
                themeService.ThemesChanged -= ApplyBackdrop;
            }

            themeService = value;

            if (themeService is not null)
            {
                themeService.ThemesChanged += ApplyBackdrop;
            }

            ApplyBackdrop();
        }
    }

    // Puts the window into the plain transparent presentation the compositor's backdrop needs, and
    // paints nothing of its own so that backdrop shows. Deliberately not the toolkit's "Mica"
    // level: that composes its own approximation of the material, in the client area only, which no
    // caption can ever match. The real material is set by the platform hook once the window has a
    // native handle. Dropped altogether when the preference is off, leaving the classic opaque
    // window untouched.
    private void ApplyBackdrop()
    {
        if (themeService is not { IsMicaSupported: true } service)
        {
            return;
        }

        if (service.IsMicaEnabled)
        {
            Background = Brushes.Transparent;

            // Painted behind the client area of a transparent window, and white by default, which
            // would veil the very backdrop this is all for.
            TransparencyBackgroundFallback = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        }
        else
        {
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            ClearValue(TransparencyBackgroundFallbackProperty);
            ClearValue(BackgroundProperty);
        }

        EvaluateBackdrop();
    }

    // Settles what the window actually ended up with: a transparent presentation from the toolkit,
    // and a backdrop from the platform behind it. Anything less and the shell goes back to its
    // opaque surfaces -- and the window takes its own background back, or it would be left as a
    // hole onto the desktop.
    private void EvaluateBackdrop()
    {
        if (themeService is not { IsMicaSupported: true, IsMicaEnabled: true } service ||
            TryGetPlatformHandle() is not { } handle)
        {
            // No native window yet; OnOpened asks again once there is one.
            return;
        }

        var applied = ActualTransparencyLevel != WindowTransparencyLevel.None &&
            PlatformServices.ApplyWindowBackdrop is { } apply &&
            apply(handle.Handle);

        service.ReportMicaApplied(applied);
        if (!applied)
        {
            ClearValue(TransparencyBackgroundFallbackProperty);
            ClearValue(BackgroundProperty);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        EvaluateBackdrop();
        ApplyToolbarTitleBar();
    }

    protected override void OnClosed(EventArgs e)
    {
        DataContextChanged -= OnDataContextChanged;
        if (shell is not null)
        {
            shell.PropertyChanged -= OnShellPropertyChanged;
            shell.ShowSettingsWindow = null;
            shell = null;
        }

        videoKeyboardShortcuts.Dispose();
        base.OnClosed(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ActualTransparencyLevelProperty)
        {
            EvaluateBackdrop();
        }
        else if (change.Property == WindowStateProperty)
        {
            UpdateWindowControls();
            ApplyToolbarTitleBar();
        }
    }

    // Native File menu "Open file...": mirrors the shell's More-menu picker. The window is itself a
    // TopLevel, so its StorageProvider drives the picker; picked audio replaces the queue and plays,
    // picked video opens the player.
    private async void OnMenuOpenFileClick(object? sender, EventArgs e)
    {
        if (!PlatformServices.SupportsLocalFileAccess ||
            DataContext is not MainWindowViewModel { Shell: { } shell })
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Resources.Shell_OpenMediaFilesPickerTitle,
            AllowMultiple = true,
            FileTypeFilter = new[] { ShellView.MediaFiles, FilePickerFileTypes.All },
        });

        var paths = new List<string>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
        }

        if (paths.Count > 0)
        {
            shell.OpenActivationFiles(paths);
        }
    }

    // Native File menu "Exit": shut the app down through the desktop lifetime so window placement is
    // persisted on the way out, falling back to closing this window if the lifetime is unavailable.
    private void OnMenuExitClick(object? sender, EventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Close();
        }
    }
}
