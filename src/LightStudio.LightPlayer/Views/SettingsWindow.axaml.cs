using System;
using Avalonia;
using Avalonia.Controls;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Views;

/// <summary>
/// Settings in a window of its own over the main window, as the Apple layout presents it on macOS.
/// </summary>
public partial class SettingsWindow : Window
{
    private IThemeService? themeService;

    public SettingsWindow()
    {
        InitializeComponent();
    }

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
                themeService.BackdropStyleChanged -= OnBackdropStyleChanged;
            }

            themeService = value;
            if (themeService is not null)
            {
                themeService.BackdropStyleChanged += OnBackdropStyleChanged;
            }

            ApplyClientWindowChrome();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyClientWindowChrome();
        ApplyToolbarTitleBar();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (themeService is not null)
        {
            themeService.BackdropStyleChanged -= OnBackdropStyleChanged;
        }

        base.OnClosed(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty)
        {
            UpdateWindowControls();
            ApplyToolbarTitleBar();
        }
    }

    private void OnBackdropStyleChanged() => ApplyClientWindowChrome();

    private bool UseCustomWindowControls =>
        PlatformServices.SupportsClientSideWindowControls && themeService?.IsAppleChromeEnabled == true;

    private void ApplyClientWindowChrome()
    {
        if (PlatformServices.SupportsClientSideWindowControls)
        {
            WindowDecorations = UseCustomWindowControls ? WindowDecorations.BorderOnly : WindowDecorations.Full;
            ExtendClientAreaToDecorationsHint = UseCustomWindowControls;
        }

        UpdateWindowControls();
    }

    private void UpdateWindowControls()
    {
        if (!IsInitialized)
        {
            return;
        }

        Classes.Set("appleCsd", UseCustomWindowControls);
        Classes.Set("roundedCsd", UseCustomWindowControls && WindowState == WindowState.Normal);
        ClientWindowControls.IsVisible = UseCustomWindowControls && WindowState != WindowState.FullScreen;
        MaximizeWindowButton.Classes.Set("maximized", WindowState == WindowState.Maximized);
    }

    private void OnCloseWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => Close();

    private void OnMinimizeWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // Lines the window controls up with the main window's, over the sidebar. Full screen has none.
    private void ApplyToolbarTitleBar()
    {
        if (PlatformServices.ApplyToolbarTitleBar is { } apply && TryGetPlatformHandle() is { } handle)
        {
            apply(handle.Handle, WindowState != WindowState.FullScreen);
        }
    }
}
