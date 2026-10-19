using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using LightStudio.LightPlayer.Views;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Desktop picture in picture: a small always-on-top window holding a second view of the same
/// player. Registered by the desktop head; the shell hides the button where it is not.
/// </summary>
public sealed class DesktopPictureInPictureHost(IShellStateStore store) : IPictureInPictureHost
{
    private PictureInPictureWindow? window;
    private PictureInPictureView? view;
    private Window? owner;

    public PictureInPictureKind Kind => PictureInPictureKind.DetachedView;

    public bool IsAvailable =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;

    public bool IsActive => window is not null;

    public event Action<bool>? ActiveChanged;

    public void Enter(PictureInPictureRequest request)
    {
        if (window is not null || !IsAvailable)
        {
            return;
        }

        var content = new PictureInPictureView { DataContext = request.DataContext };
        var created = new PictureInPictureWindow();
        created.Present(content, request.AspectRatio, store.PictureInPicturePlacement);
        created.Closing += OnClosing;
        created.Closed += OnClosed;
        window = created;
        view = content;

        // Not an owned window on purpose: an owned one follows the main window into the background
        // and into a minimize, which is the opposite of what this is for. It does have to go when
        // the app does, though, or the last window standing would be one with no way back.
        owner = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is not null)
        {
            owner.Closed += OnOwnerClosed;
        }

        created.Show();
        content.ClaimVideo();
        ActiveChanged?.Invoke(true);
    }

    public void Exit() => window?.Close();

    private void OnOwnerClosed(object? sender, EventArgs e) => Exit();

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Read while the window still has a position to report; best effort, and skipped when the
        // platform reports nothing usable.
        if (sender is PictureInPictureWindow closing && closing.CapturePlacement() is { Width: > 0, Height: > 0 } placement)
        {
            store.PictureInPicturePlacement = placement;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (sender is not PictureInPictureWindow closed)
        {
            return;
        }

        closed.Closing -= OnClosing;
        closed.Closed -= OnClosed;
        if (!ReferenceEquals(closed, window))
        {
            return;
        }

        if (owner is not null)
        {
            owner.Closed -= OnOwnerClosed;
            owner = null;
        }

        // Dropping the binding source is what makes the view let go of the player: it unhooks its
        // events, gives up the sink and releases the frame it is holding, which at 8K is 130 MB.
        if (view is not null)
        {
            view.DataContext = null;
            view = null;
        }

        window = null;
        ActiveChanged?.Invoke(false);
    }
}
