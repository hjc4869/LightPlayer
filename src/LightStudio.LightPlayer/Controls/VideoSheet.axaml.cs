using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.ViewModels;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// The video player sheet: a running video session layered over the shell instead of navigated to.
/// Maximized the card covers the whole shell; minimized it shrinks to a small card in the
/// bottom-right corner that keeps playing while the library is browsed behind it.
///
/// The drag, the release rule and the settle glide are the now-playing sheet's, shared through
/// <see cref="SheetGesture"/>, so both sheets answer a pull the same way. What differs is where the
/// travel leads: the corner card keeps the picture's aspect ratio rather than the shell's, so the
/// card is interpolated by layout instead of by a render transform. That is affordable here because
/// the picture is drawn straight into the card's bounds, so a resize costs a different destination
/// rectangle and nothing else.
/// </summary>
public partial class VideoSheet : UserControl
{
    /// <summary>Gap between the corner card and the edges it sits against.</summary>
    private const double CardMargin = 12d;

    private const double MaxCardWidthRatio = 0.45d;
    private const double MaxCardHeightRatio = 0.4d;

    /// <summary>Shape of a card whose video has not reported its own yet.</summary>
    private const double DefaultAspectRatio = 16d / 9d;

    /// <summary>
    /// Depth of the maximized card's title bar, which is the only part of it that drags: everything
    /// below belongs to the picture, which has gestures of its own (a swipe seeks, a hold runs it
    /// fast).
    /// </summary>
    private const double HeaderHeight = 56d;

    /// <summary>
    /// How long the corner card's controls stay up after the last pointer activity. The same idle
    /// timeout the maximized deck uses, so the two presentations behave alike.
    /// </summary>
    private static readonly TimeSpan ControlsIdleTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long the card takes to rise into place when a session opens, and to leave when one ends.
    /// Both cover the whole travel (off the bottom edge to full screen), so they run a little longer
    /// than a drag settling over half of it.
    /// </summary>
    private const double EntranceDurationMs = 380d;
    private const double ExitDurationMs = 320d;

    /// <summary>
    /// Height of the shell chrome below the sheet (the bottom navigation) that the corner card must
    /// clear, mirroring <see cref="NowPlayingSheet.BottomInset"/>.
    /// </summary>
    public static readonly StyledProperty<double> BottomInsetProperty =
        AvaloniaProperty.Register<VideoSheet, double>(nameof(BottomInset), defaultValue: 0d);

    private readonly SheetGesture sheet;
    private readonly DispatcherTimer hideControlsTimer = new() { Interval = ControlsIdleTimeout };

    private VideoSheetViewModel? subscribedViewModel;
    private VideoPlayerPageViewModel? subscribedContent;
    private int? cardPointerId;
    private IPointer? resizePointer;
    private Control? activeResizeEdge;
    private Point resizeStartPosition;
    private Size resizeStartSize;

    // Set while the card is on its way off the bottom edge, so only a card that is actually leaving
    // ends the session when it lands there (an entrance starts from the very same position).
    private bool exiting;

    public VideoSheet()
    {
        InitializeComponent();

        // -1 is "gone": the corner card can be pulled further down to end the session, the same
        // gesture as the pull that maximizes it, in the other direction.
        sheet = new SheetGesture(this, ApplyProgress, minimum: -1d);

        hideControlsTimer.Tick += (_, _) => HideCornerControls();

        // Press is observed in the tunnel pass without handling it, so the card's own buttons keep
        // working; the gesture only takes over (capturing the pointer, which cancels any armed
        // press) once the finger has actually moved far enough. Move and release bubble back up,
        // and listen for handled events so the picture's own gestures cannot swallow the rest.
        Card.AddHandler(PointerPressedEvent, OnCardPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Card.AddHandler(PointerMovedEvent, OnCardPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        Card.AddHandler(PointerReleasedEvent, OnCardPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        Card.AddHandler(PointerCaptureLostEvent, OnCardPointerCaptureLost, handledEventsToo: true);
        AttachResizeEdge(LeftResizeEdge);
        AttachResizeEdge(TopResizeEdge);

        DataContextChanged += (_, _) => AttachViewModel();
        Loaded += (_, _) => sheet.Refresh();
        Unloaded += (_, _) =>
        {
            sheet.Stop();
            hideControlsTimer.Stop();
        };
    }

    /// <inheritdoc cref="BottomInsetProperty" />
    public double BottomInset
    {
        get => GetValue(BottomInsetProperty);
        set => SetValue(BottomInsetProperty, value);
    }

    private VideoSheetViewModel? ViewModel => DataContext as VideoSheetViewModel;

    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);

        // The sheet is an overlay: it fills whatever the shell gives it and asks for nothing of its
        // own, so the auto-sized rows it is layered over are never inflated by the card.
        return default;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // The card's geometry is derived from the sheet's own size and the chrome below it, so a
        // resize (or a rotation) has to re-place it at the same logical position.
        if (change.Property == BoundsProperty || change.Property == BottomInsetProperty)
        {
            sheet.Refresh();
        }
    }

    private void AttachViewModel()
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        subscribedViewModel = ViewModel;

        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            AttachContent();
            sheet.JumpTo(subscribedViewModel.IsExpanded ? 1d : 0d);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(VideoSheetViewModel.IsExpanded):
                // Maximized or minimized from outside the gesture (the deck's own buttons, a back
                // gesture): glide there from wherever the card currently is.
                exiting = false;
                sheet.SettleTo(viewModel.IsExpanded ? 1d : 0d);

                if (viewModel.IsExpanded)
                {
                    hideControlsTimer.Stop();
                }
                else
                {
                    ShowCornerControls();
                }

                break;

            case nameof(VideoSheetViewModel.IsClosing):
                if (viewModel.IsClosing)
                {
                    exiting = true;
                    hideControlsTimer.Stop();
                    sheet.SettleTo(-1d, ExitDurationMs);
                }

                break;

            case nameof(VideoSheetViewModel.Content):
                AttachContent();
                exiting = false;

                if (viewModel.Content is null)
                {
                    sheet.JumpTo(0d);
                    break;
                }

                // A session appearing rises in from below the bottom edge and expands, over the
                // same travel a drag covers, so opening and closing read as one another's reverse.
                sheet.JumpTo(-1d);
                sheet.SettleTo(viewModel.IsExpanded ? 1d : 0d, EntranceDurationMs);
                break;

            case nameof(VideoSheetViewModel.MinimizedWidth):
                sheet.Refresh();
                break;
        }
    }

    private void AttachContent()
    {
        if (subscribedContent is not null)
        {
            subscribedContent.PropertyChanged -= OnContentPropertyChanged;
        }

        subscribedContent = ViewModel?.Content;

        if (subscribedContent is not null)
        {
            subscribedContent.PropertyChanged += OnContentPropertyChanged;
        }
    }

    private void OnContentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The corner card is cut to the picture, so it is re-shaped as soon as the video says what
        // shape that is.
        if (e.PropertyName == nameof(VideoPlayerPageViewModel.PictureAspectRatio))
        {
            sheet.Refresh();
        }
        else if (e.PropertyName == nameof(VideoPlayerPageViewModel.AreControlsVisible))
        {
            UpdateCornerControls();
        }
    }

    // ---------------------------------------------------------------- corner controls

    /// <summary>
    /// Brings the corner card's controls up and restarts the idle countdown, exactly as working the
    /// maximized picture keeps its deck up.
    /// </summary>
    private void ShowCornerControls()
    {
        if (ViewModel?.Content is not { } player)
        {
            return;
        }

        player.AreControlsVisible = true;
        hideControlsTimer.Stop();
        hideControlsTimer.Start();
    }

    private void HideCornerControls()
    {
        hideControlsTimer.Stop();

        // Keep them up while paused: there is nothing to watch behind them.
        if (sheet.Progress < 0.98d && ViewModel?.Content is { IsPlaying: true } player)
        {
            player.AreControlsVisible = false;
        }
    }

    private void UpdateCornerControls()
    {
        var visible = ViewModel?.Content?.AreControlsVisible ?? true;

        // The corner controls clear out early and the picture's own deck arrives late (see
        // VideoSheetViewModel.OnProgressChanged), so the two never fight for the same pixels. Below
        // the corner state the whole card fades out with the throw, which this follows.
        var fade = Math.Clamp(1d - (Math.Max(0d, sheet.Progress) * 2.2d), 0d, 1d);

        MiniControls.Opacity = visible ? fade : 0d;
        MiniControls.IsHitTestVisible = visible;
    }

    // ---------------------------------------------------------------- drag

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsWithin(e.Source as Visual, LeftResizeEdge) || IsWithin(e.Source as Visual, TopResizeEdge))
        {
            return;
        }

        if (sheet.Progress > 0.5d && ViewModel?.IsFullScreen == true)
        {
            return;
        }

        sheet.Abandon();
        cardPointerId = null;

        if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(Card).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Touching the corner card is what brings its controls back, as tapping the picture brings
        // the deck back. Ahead of the checks below, so working a button counts as activity too.
        if (sheet.Progress < 0.98d)
        {
            ShowCornerControls();
        }

        if (e.Pointer.Type != PointerType.Touch)
        {
            return;
        }

        // Maximized, only the title bar drags (so pulling down from the top minimizes). The corner
        // card is small enough that all of it drags.
        if (sheet.Progress > 0.5d && e.GetPosition(Card).Y > HeaderHeight)
        {
            return;
        }

        if (SheetGesture.IsInteractive(e.Source as Visual, Card))
        {
            return;
        }

        // Measure against the sheet, not the card: the card itself moves with the gesture, so
        // card-relative coordinates would feed the drag back into itself.
        sheet.Press(e.GetPosition(this).Y);
        cardPointerId = e.Pointer.Id;
    }

    private void OnCardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sheet.Progress < 0.98d)
        {
            ShowCornerControls();
        }

        if (cardPointerId != e.Pointer.Id || e.Pointer.Type != PointerType.Touch)
        {
            return;
        }

        var wasDragging = sheet.IsDragging;

        // Only a drag that started on the corner card can throw it away: from the maximized card a
        // long pull down is meant to minimize, not to end playback.
        var dismissTravel = sheet.PressProgress < 0.5d ? DismissTravel : 0d;
        if (!sheet.Move(e.GetPosition(this).Y, SheetTravel, dismissTravel))
        {
            return;
        }

        if (!wasDragging)
        {
            e.Pointer.Capture(Card);
        }

        e.Handled = true;
    }

    private void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (cardPointerId != e.Pointer.Id || !sheet.IsPressed)
        {
            return;
        }

        cardPointerId = null;

        if (!sheet.Release(out var target))
        {
            // A tap on the corner card is left to the buttons drawn over it: the picture is too
            // small for a stray tap on it to be worth a state change.
            return;
        }

        Commit(target);
        e.Handled = true;
    }

    private void OnCardPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (cardPointerId != e.Pointer.Id || !sheet.IsPressed)
        {
            return;
        }

        cardPointerId = null;
        sheet.Abandon();
        Commit(sheet.Nearest);
    }

    private void Commit(double target)
    {
        // Below the corner state the card is on its way off the bottom edge; the session ends when
        // it lands (see ApplyProgress), so its logical state is left alone here.
        if (target < 0d)
        {
            exiting = true;
        }
        else if (ViewModel is { } viewModel)
        {
            // Setting this may raise the property change that also calls SettleTo; the glide is
            // restartable, so the redundant call is harmless and keeps a no-change commit animating.
            viewModel.IsExpanded = target >= 0.5d;
        }

        sheet.SettleTo(target);
    }

    // ---------------------------------------------------------------- resize

    private void AttachResizeEdge(Control edge)
    {
        edge.AddHandler(PointerPressedEvent, OnResizePointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        edge.AddHandler(PointerMovedEvent, OnResizePointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        edge.AddHandler(PointerReleasedEvent, OnResizePointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        edge.AddHandler(PointerCaptureLostEvent, OnResizePointerCaptureLost, handledEventsToo: true);
    }

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control edge || Math.Abs(sheet.Progress) > 0.02d || ViewModel is null)
        {
            return;
        }

        if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(edge).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var size = CornerCardSize();
        if (size.Width <= 0d || size.Height <= 0d)
        {
            return;
        }

        sheet.Abandon();
        ShowCornerControls();
        hideControlsTimer.Stop();
        resizePointer = e.Pointer;
        activeResizeEdge = edge;
        resizeStartPosition = e.GetPosition(this);
        resizeStartSize = size;
        e.Pointer.Capture(edge);
        e.Handled = true;
    }

    private void OnResizePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, resizePointer) || ViewModel is not { } viewModel)
        {
            return;
        }

        var delta = e.GetPosition(this) - resizeStartPosition;
        var requestedWidth = ReferenceEquals(activeResizeEdge, TopResizeEdge)
            ? resizeStartSize.Width - (delta.Y * resizeStartSize.Width / resizeStartSize.Height)
            : resizeStartSize.Width - delta.X;
        var constrainedWidth = CornerCardSize(requestedWidth).Width;
        viewModel.Resize(constrainedWidth);
        e.Handled = true;
    }

    private void OnResizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, resizePointer))
        {
            return;
        }

        resizePointer = null;
        activeResizeEdge = null;
        e.Pointer.Capture(null);
        ViewModel?.CompleteResize();
        ShowCornerControls();
        e.Handled = true;
    }

    private void OnResizePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, resizePointer))
        {
            return;
        }

        resizePointer = null;
        activeResizeEdge = null;
        ViewModel?.CompleteResize();
        ShowCornerControls();
    }

    private static bool IsWithin(Visual? source, Visual root)
    {
        for (var current = source; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- geometry

    // Distance the card's top edge travels between the corner and the whole shell.
    private double SheetTravel =>
        Math.Max(0d, Bounds.Height - CornerCardSize().Height - CardMargin - BottomInset);

    // Distance from the corner to clear of the bottom edge, i.e. how far the card is thrown.
    private double DismissTravel =>
        Math.Max(1d, CornerCardSize().Height + CardMargin + BottomInset);

    /// <summary>
    /// The corner card's size: as wide as it is allowed to be, cut to the picture's own shape so
    /// nothing is letterboxed, and short enough to leave the page behind it readable.
    /// </summary>
    private Size CornerCardSize() => CornerCardSize(ViewModel?.MinimizedWidth ?? VideoSheetViewModel.DefaultMinimizedWidth);

    private Size CornerCardSize(double preferredWidth) => CalculateCornerCardSize(
        Bounds.Size,
        BottomInset,
        preferredWidth,
        ViewModel?.Content?.PictureAspectRatio ?? DefaultAspectRatio);

    internal static Size CalculateCornerCardSize(
        Size available,
        double bottomInset,
        double preferredWidth,
        double reportedAspectRatio)
    {
        if (available.Width <= 0 || available.Height <= 0)
        {
            return default;
        }

        var aspect = double.IsFinite(reportedAspectRatio) && reportedAspectRatio > 0.01d
            ? reportedAspectRatio
            : DefaultAspectRatio;

        var edgeLimitedWidth = Math.Max(0d, available.Width - CardMargin);
        var edgeLimitedHeight = Math.Max(0d, available.Height - CardMargin - Math.Max(0d, bottomInset));
        var maxWidth = Math.Min(available.Width * MaxCardWidthRatio, edgeLimitedWidth);
        var maxHeight = Math.Min(available.Height * MaxCardHeightRatio, edgeLimitedHeight);
        if (maxWidth <= 0d || maxHeight <= 0d)
        {
            return default;
        }

        var normalizedWidth = double.IsFinite(preferredWidth) && preferredWidth > 0d
            ? Math.Max(VideoSheetViewModel.MinimumMinimizedWidth, preferredWidth)
            : VideoSheetViewModel.DefaultMinimizedWidth;
        var width = Math.Min(normalizedWidth, maxWidth);
        var height = width / aspect;

        if (height > maxHeight)
        {
            height = maxHeight;
            width = height * aspect;
        }

        return new Size(Math.Min(width, edgeLimitedWidth), Math.Min(height, edgeLimitedHeight));
    }

    // Sizes and places the card, and cross-fades the corner controls against the full deck. Called
    // for every drag move and animation frame.
    private void ApplyProgress(double progress)
    {
        var full = Bounds.Size;
        var corner = CornerCardSize();

        // Above the corner state the card grows into the shell; below it, it keeps its size and
        // slides off the bottom edge instead, fading as it goes.
        var expand = Math.Max(0d, progress);
        var thrown = Math.Max(0d, -progress);

        Card.Width = corner.Width + ((full.Width - corner.Width) * expand);
        Card.Height = corner.Height + ((full.Height - corner.Height) * expand);
        Card.Margin = new Thickness(
            0,
            0,
            CardMargin * (1d - expand),
            ((CardMargin + BottomInset) * (1d - expand)) - (thrown * DismissTravel));
        Card.Opacity = 1d - thrown;

        // Square off the corners as the card takes over the whole viewport; a rounded edge would
        // otherwise leave notches of the page showing behind a "full screen" surface.
        Card.CornerRadius = new CornerRadius(10d * (1d - expand));

        MiniOverlay.IsHitTestVisible = progress < 0.02d;
        PlayerHost.IsHitTestVisible = progress > 0.98d;
        UpdateCornerControls();

        if (ViewModel is { } viewModel)
        {
            viewModel.Progress = progress;
        }

        if (!exiting || progress > -0.995d)
        {
            return;
        }

        // The card has landed clear of the bottom edge. Ending the session tears this control's
        // content down, so it is left to the next dispatcher turn rather than run inside the frame
        // that is still placing the card.
        exiting = false;
        Dispatcher.UIThread.Post(() => ViewModel?.CompleteClose());
    }
}
