using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views;

/// <summary>
/// The small always-on-top window the picture is detached into on the desktop. Borderless bar the
/// resize frame, so the video is all there is to see; the whole surface drags the window about.
/// </summary>
public partial class PictureInPictureWindow : Window
{
    /// <summary>Width the window opens at when nothing was remembered; its height follows the picture.</summary>
    private const double DefaultWidth = 420d;

    /// <summary>
    /// How long the size has to hold still before it is squared up with the picture's shape. A
    /// resize the window asks for while the user is still dragging is answered by the frame alone:
    /// the content is laid out at whatever size the drag last reported, so the two come apart until
    /// the drag ends. Waiting for it to end means one request, which every backend honors.
    /// </summary>
    private static readonly TimeSpan ResizeSettleDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>How far a finger has to travel before it is taken to be dragging rather than tapping.</summary>
    private const double DragThreshold = 6d;

    private readonly DispatcherTimer resizeSettleTimer = new() { Interval = ResizeSettleDelay };

    private PictureInPictureView? view;
    private double aspect = 16d / 9d;
    private Size lastReportedSize;
    private Size sizeBeforeResize;
    private bool resizing;

    /// <summary>The pointer running a move the window is driving itself, and where it took hold.</summary>
    private IPointer? dragPointer;
    private Point dragOrigin;
    private bool dragMoved;

    public PictureInPictureWindow()
    {
        InitializeComponent();

        if (OperatingSystem.IsWindows())
        {
            // A window with a resize frame but no title bar still gets the frame's top edge, and
            // Windows paints that one opaque in the frame color: a white bar above the picture.
            // Laying the client area over the frame leaves the picture where the bar was, with the
            // resize edges still where they were, and only a hairline of frame left showing.
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = -1;
        }

        resizeSettleTimer.Tick += (_, _) => ApplyAspectRatio();
        Closed += (_, _) =>
        {
            resizeSettleTimer.Stop();
            if (Model is { } model)
            {
                model.PropertyChanged -= OnModelPropertyChanged;
            }
        };
    }

    public void Present(PictureInPictureView content, double aspectRatio, PictureInPicturePlacement? saved)
    {
        view = content;
        Host.Children.Clear();
        Host.Children.Add(content);

        if (Model is { } model)
        {
            model.PropertyChanged += OnModelPropertyChanged;
        }

        if (aspectRatio > 0.1)
        {
            aspect = aspectRatio;
        }

        if (!TryRestore(saved))
        {
            Width = DefaultWidth;
            Height = Math.Round(Math.Clamp(DefaultWidth / aspect, 150d, 420d));
            PlaceOnOwnersScreen();
        }

        sizeBeforeResize = new Size(Width, Height);
        lastReportedSize = sizeBeforeResize;
    }

    /// <summary>Locks the existing window to a newly opened video's shape.</summary>
    public void UpdateAspectRatio(double aspectRatio)
    {
        if (aspectRatio <= 0.1 || Math.Abs(aspect - aspectRatio) < 0.001)
        {
            return;
        }

        aspect = aspectRatio;
        resizeSettleTimer.Stop();

        if (WindowState != WindowState.Normal || ClientSize.Width <= 0)
        {
            return;
        }

        resizing = true;
        try
        {
            Height = Math.Round(ClientSize.Width / aspect);
        }
        finally
        {
            resizing = false;
        }
    }

    /// <summary>Where the window is now, for putting it back in the same place next time.</summary>
    public PictureInPicturePlacement CapturePlacement() =>
        new(Position.X, Position.Y, ClientSize.Width, ClientSize.Height);

    private VideoPlayerPageViewModel? Model => view?.DataContext as VideoPlayerPageViewModel;

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoPlayerPageViewModel.PictureAspectRatio) && Model is { } model)
        {
            UpdateAspectRatio(model.PictureAspectRatio);
        }
    }

    /// <summary>
    /// Takes the remembered size and place, unless the video has since changed shape or the screen
    /// that held the window is gone, in which case the default placement is used instead.
    /// </summary>
    private bool TryRestore(PictureInPicturePlacement? saved)
    {
        if (saved is not { Width: > 0, Height: > 0 } || saved.Width < MinWidth)
        {
            return false;
        }

        double width = saved.Width;
        double height = Math.Round(width / aspect);
        var position = new PixelPoint((int)saved.X, (int)saved.Y);
        Screens? screens = KnownScreens;
        if (screens is not null)
        {
            Screen? screen = screens.ScreenFromPoint(position);
            if (screen is null)
            {
                // The window would open on a display that is no longer there.
                return false;
            }

            double scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
            if (width * scaling > screen.WorkingArea.Width || height * scaling > screen.WorkingArea.Height)
            {
                return false;
            }
        }

        Width = width;
        Height = height;
        Position = position;
        return true;
    }

    /// <summary>
    /// Parks the window in the corner of whichever display the app is on. Compositors that place
    /// windows themselves (Wayland) ignore this, as they do <c>Topmost</c>.
    /// </summary>
    private void PlaceOnOwnersScreen()
    {
        Window? main = MainWindow;
        Screen? screen = main is not null
            ? main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary
            : KnownScreens?.Primary;
        if (screen is null)
        {
            return;
        }

        PixelRect area = screen.WorkingArea;
        int margin = (int)(24 * screen.Scaling);
        Position = new PixelPoint(
            area.Right - (int)(Width * screen.Scaling) - margin,
            area.Bottom - (int)(Height * screen.Scaling) - margin);
    }

    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>The displays as the already-shown main window sees them; this one is not up yet.</summary>
    private Screens? KnownScreens => MainWindow?.Screens ?? Screens;

    /// <summary>
    /// Holds the window to the picture's shape once the user lets go of it: whichever edge was
    /// dragged sets the size, the other one follows.
    /// </summary>
    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);

        Size previous = lastReportedSize;
        lastReportedSize = e.ClientSize;

        if (resizing || WindowState != WindowState.Normal)
        {
            return;
        }

        if (!resizeSettleTimer.IsEnabled)
        {
            sizeBeforeResize = previous;
        }

        resizeSettleTimer.Stop();
        resizeSettleTimer.Start();
    }

    private void ApplyAspectRatio()
    {
        resizeSettleTimer.Stop();

        Size size = ClientSize;
        if (aspect <= 0 || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        if (Math.Abs(size.Height - (size.Width / aspect)) < 1)
        {
            return;
        }

        resizing = true;
        try
        {
            if (Math.Abs(size.Width - sizeBeforeResize.Width) >= Math.Abs(size.Height - sizeBeforeResize.Height))
            {
                Height = Math.Round(size.Width / aspect);
            }
            else
            {
                Width = Math.Round(size.Height * aspect);
            }
        }
        finally
        {
            resizing = false;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Everything that is not a control of its own is grip: a window this small has no title bar
        // to take hold of.
        if (e.Handled || IsOverControl(e.Source))
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(this);
        if (e.Pointer.Type == PointerType.Touch)
        {
            // The platform's own move loop follows the mouse cursor, which a finger does not carry
            // with it, so a touch drag is run here instead: the window is moved to keep the spot
            // that was grabbed under the finger. Whether this is a drag at all is not known yet; a
            // finger that stays put is a tap, which the view answers on its own.
            dragPointer = e.Pointer;
            dragOrigin = point.Position;
            dragMoved = false;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginMoveDrag(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!ReferenceEquals(e.Pointer, dragPointer) || WindowState != WindowState.Normal)
        {
            return;
        }

        Point moved = e.GetPosition(this) - dragOrigin;
        if (!dragMoved)
        {
            if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
            {
                return;
            }

            dragMoved = true;
            SetWindowDragging(true);
        }

        double scaling = DesktopScaling <= 0 ? 1 : DesktopScaling;
        int dx = (int)Math.Round(moved.X * scaling);
        int dy = (int)Math.Round(moved.Y * scaling);
        if (dx != 0 || dy != 0)
        {
            Position = new PixelPoint(Position.X + dx, Position.Y + dy);
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (ReferenceEquals(e.Pointer, dragPointer))
        {
            e.Pointer.Capture(null);
            EndTouchDrag();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        if (ReferenceEquals(e.Pointer, dragPointer))
        {
            EndTouchDrag();
        }
    }

    private void EndTouchDrag()
    {
        dragPointer = null;
        dragMoved = false;
        SetWindowDragging(false);
    }

    private void SetWindowDragging(bool dragging)
    {
        if (view is not null)
        {
            view.IsWindowDragging = dragging;
        }
    }

    private static bool IsOverControl(object? source)
    {
        for (Visual? visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button or Slider or Controls.MediaSeekBar)
            {
                return true;
            }
        }

        return false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Model is not { } model)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape or Key.P:
                Close();
                break;
            case Key.Space or Key.K or Key.MediaPlayPause:
                model.PlayPauseCommand.Execute(null);
                break;
            case Key.Left:
                model.SeekRelative(-5);
                break;
            case Key.Right:
                model.SeekRelative(5);
                break;
            case Key.M:
                model.ToggleMuteCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
