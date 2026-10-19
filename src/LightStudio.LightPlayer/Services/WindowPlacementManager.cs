using System;
using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Restores and persists the desktop main window's size and maximized state across launches.
///
/// On close the current placement is written to the shell state store. On open a saved maximized
/// state is restored directly; a saved windowed size is only reapplied when it still fits the screen
/// the window opens on, otherwise the window keeps its default (XAML) size. When nothing is saved the
/// default windowed size and state are left untouched, so no persistence is required for a first run.
/// </summary>
public sealed class WindowPlacementManager
{
    private readonly Window window;
    private readonly IShellStateStore store;

    private double lastNormalWidth;
    private double lastNormalHeight;
    private bool hasNormalSize;
    private bool restored;

    private WindowPlacementManager(Window window, IShellStateStore store)
    {
        this.window = window;
        this.store = store;

        // Seed the windowed size from the saved value so closing while maximized (without ever
        // returning to the Normal state this session) still round-trips the last windowed size.
        var saved = store.WindowPlacement;
        if (saved is { Width: > 0, Height: > 0 })
        {
            lastNormalWidth = saved.Width;
            lastNormalHeight = saved.Height;
            hasNormalSize = true;
        }

        window.Opened += OnOpened;
        window.Closing += OnClosing;
        window.PropertyChanged += OnWindowPropertyChanged;
    }

    /// <summary>Wires size/maximized persistence onto the desktop main window.</summary>
    public static void Attach(Window window, IShellStateStore store) =>
        _ = new WindowPlacementManager(window, store);

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Track the latest windowed (Normal) client size so it can be persisted even when the window
        // is maximized at exit. Ignore the transient sizes reported before the initial restore.
        if (!restored || e.Property != TopLevel.ClientSizeProperty || window.WindowState != WindowState.Normal)
        {
            return;
        }

        var size = window.ClientSize;
        if (size is { Width: > 0, Height: > 0 })
        {
            lastNormalWidth = size.Width;
            lastNormalHeight = size.Height;
            hasNormalSize = true;
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (restored)
        {
            return;
        }

        // Tracking starts now; earlier client-size reports were the default startup size, not a user
        // choice, and must not overwrite the seeded windowed size.
        restored = true;

        var saved = store.WindowPlacement;
        if (saved is null)
        {
            // Nothing saved: keep the XAML default size and windowed state.
            return;
        }

        if (saved.Maximized)
        {
            window.WindowState = WindowState.Maximized;
            return;
        }

        // Restore the windowed size only when it still fits the screen the window opened on; otherwise
        // fall back to the default size.
        if (saved.Width > 0 && saved.Height > 0 && FitsCurrentScreen(saved.Width, saved.Height))
        {
            window.WindowState = WindowState.Normal;
            window.Width = saved.Width;
            window.Height = saved.Height;
        }
    }

    private bool FitsCurrentScreen(double width, double height)
    {
        var screen = window.Screens?.ScreenFromTopLevel(window) ?? window.Screens?.Primary;
        if (screen is null)
        {
            // Screen information unavailable: assume it fits rather than discarding the saved size.
            return true;
        }

        // WorkingArea is in physical pixels; the window size is in device-independent units.
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var availableWidth = screen.WorkingArea.Width / scaling;
        var availableHeight = screen.WorkingArea.Height / scaling;
        return width <= availableWidth && height <= availableHeight;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var maximized = window.WindowState == WindowState.Maximized;

        // Prefer the tracked windowed size; if the window was never in the Normal state this session
        // (launched and closed while maximized with nothing seeded) fall back to the current size.
        var width = hasNormalSize ? lastNormalWidth : window.ClientSize.Width;
        var height = hasNormalSize ? lastNormalHeight : window.ClientSize.Height;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        store.WindowPlacement = new WindowPlacement(maximized, width, height);
    }
}
