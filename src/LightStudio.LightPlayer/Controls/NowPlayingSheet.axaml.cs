using LightMediaRenderer.Avalonia.Controls;
using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.ViewModels;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Narrow (mobile) now-playing bottom sheet. Presents a compact mini player card docked above the
/// shell's bottom navigation that the user can tap or drag up into a full-screen now-playing
/// surface, layered over the current page rather than navigating away from it.
///
/// The card is always laid out at the sheet's full height and positioned with a render transform,
/// so dragging never re-measures the full page. The drag follows the finger directly; on release
/// the recent pointer samples decide whether to settle open or closed, and a cubic ease-out glide
/// (fast first, slow at the end) carries it the rest of the way. The compact and full presentations
/// cross-fade against the same travel, so how far the user has pulled is always legible.
/// </summary>
public partial class NowPlayingSheet : UserControl
{
    /// <summary>
    /// Height of the compact (closed) card, i.e. of the sheet's header strip. The card floats over
    /// the page instead of taking part in its layout, so the shell reserves this much space below
    /// the routed page to keep its content reachable; see <c>ShellView.axaml</c>.
    /// </summary>
    public const double CompactHeight = 76d;

    /// <summary>
    /// Height of the shell chrome below the sheet (the bottom navigation) that the compact card
    /// must clear. The card stops with its header just above it.
    /// </summary>
    public static readonly StyledProperty<double> BottomInsetProperty =
        AvaloniaProperty.Register<NowPlayingSheet, double>(nameof(BottomInset), defaultValue: 58d);

    // Page-change glide for the horizontal pager.
    private const double PagerDurationMs = 240d;

    // Pointer travel before a press turns into a drag rather than a tap. The vertical sheet drag
    // uses the shared gesture's own threshold; this one only guards the horizontal pager.
    private const double DragThresholdPx = 6d;

    // 0 = compact card, 1 = full page. The single source of truth for the card's position and the
    // compact/full cross-fade, including the drag and the settle glide that reach it.
    private readonly SheetGesture sheet;
    private readonly Glide pagerGlide;

    // Both surfaces are positioned with render transforms rather than layout, so a drag or a glide
    // frame never re-measures the page they carry.
    private readonly TranslateTransform cardTransform = new();
    private readonly TranslateTransform pagerTransform = new();

    private NowPlayingSheetViewModel? subscribedViewModel;

    private int pageIndex;
    private int? sheetPointerId;
    private PointerType sheetPointerType;
    private Point sheetPressPosition;
    private bool pagerPressed;
    private bool pagerDragging;
    private double pagerPressX;
    private double pagerPressY;
    private double pagerOffset;

    public NowPlayingSheet()
    {
        InitializeComponent();

        sheet = new SheetGesture(this, ApplyProgress);
        pagerGlide = new Glide(this, ApplyPagerOffset);

        Card.RenderTransform = cardTransform;
        PagerTrack.RenderTransform = pagerTransform;

        // The sheet starts closed, and the first progress update only arrives once it is loaded.
        Card.Classes.Add("compact");

        // Vertical sheet drag. Press is observed in the tunnel pass without handling it, so buttons
        // in the header keep working; the gesture only takes over (capturing the pointer, which
        // cancels any armed button press) once the finger has actually moved far enough. Move and
        // release bubble back up, and listen for handled events so a scrolling child cannot swallow
        // the rest of the gesture.
        Card.AddHandler(PointerPressedEvent, OnCardPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Card.AddHandler(PointerMovedEvent, OnCardPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        Card.AddHandler(PointerReleasedEvent, OnCardPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        Card.AddHandler(PointerCaptureLostEvent, OnCardPointerCaptureLost, handledEventsToo: true);

        // Horizontal pager swipe across the cover / lyrics / up-next pages.
        PagerHost.AddHandler(PointerPressedEvent, OnPagerPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        PagerHost.AddHandler(PointerMovedEvent, OnPagerPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        PagerHost.AddHandler(PointerReleasedEvent, OnPagerPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        PagerHost.AddHandler(PointerCaptureLostEvent, OnPagerPointerCaptureLost, handledEventsToo: true);

        // Scrubbing the position sliders must suppress engine position updates for the duration of
        // the drag, exactly as the wide layout's playback bar does.
        AttachScrub(CompactSlider);
        AttachScrub(ExpandedSlider);

        PagerHost.SizeChanged += OnPagerHostSizeChanged;
        Card.SizeChanged += (_, _) => UpdateLyricsPageInsets();
        LyricsControl.SizeChanged += (_, _) => UpdateLyricsPageInsets();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            SyncPageWidths();
            UpdateLyricsPageInsets();
            sheet.Refresh();
        };
        Unloaded += (_, _) =>
        {
            sheet.Stop();
            pagerGlide.Stop();
        };
    }

    /// <inheritdoc cref="BottomInsetProperty" />
    public double BottomInset
    {
        get => GetValue(BottomInsetProperty);
        set => SetValue(BottomInsetProperty, value);
    }

    private NowPlayingSheetViewModel? ViewModel => DataContext as NowPlayingSheetViewModel;

    private void OnMoreClick(object? sender, RoutedEventArgs e) =>
        AudioSeparationMenu.Populate(AudioSeparationMenuItem.Items, ViewModel?.Playback);

    // Distance the card travels between the compact and full presentations: everything except the
    // header strip that stays visible and the bottom chrome it must clear.
    private double SheetTravel =>
        Math.Max(0d, Bounds.Height - HeaderHost.Bounds.Height - BottomInset);

    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);

        // The sheet is an overlay: it fills whatever the shell gives it and asks for nothing of its
        // own. Reporting the card's real (full page) height would let it inflate the auto-sized
        // playback bar and bottom navigation rows it is layered over.
        return default;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // The travel depends on the sheet's own height and the chrome below it, so a resize (or a
        // rotation) has to re-place the card at the same logical position.
        if (change.Property == BoundsProperty || change.Property == BottomInsetProperty)
        {
            sheet.Refresh();
            UpdateLyricsPageInsets();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        subscribedViewModel = ViewModel;

        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            SetPage(subscribedViewModel.SelectedTab, animate: false);
            sheet.SettleTo(subscribedViewModel.IsExpanded ? 1d : 0d);
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
            case nameof(NowPlayingSheetViewModel.IsExpanded):
                // Opened or closed from outside the gesture (the close button, the bottom
                // navigation, a back gesture, or a layout switch): glide there from wherever the
                // card currently is.
                sheet.SettleTo(viewModel.IsExpanded ? 1d : 0d);
                break;
            case nameof(NowPlayingSheetViewModel.SelectedTab):
                SetPage(viewModel.SelectedTab, animate: true);
                break;
        }
    }

    // ---------------------------------------------------------------- vertical sheet gesture

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        sheet.Abandon();
        sheetPointerId = null;

        if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(Card).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Only the header strip drags: the compact mini player when closed, and the full page's
        // title bar when open (so pulling down from the top minimizes). Everything below it belongs
        // to the page content, which scrolls and swipes on its own.
        var cardPoint = e.GetPosition(Card);
        if (cardPoint.Y < 0 || cardPoint.Y > HeaderHost.Bounds.Height)
        {
            return;
        }

        if (SheetGesture.IsInteractive(e.Source as Visual, Card))
        {
            return;
        }

        // Measure against the sheet, not the card: the card itself moves with the gesture, so
        // card-relative coordinates would feed the drag back into itself.
        var pressPosition = e.GetPosition(this);
        sheet.Press(pressPosition.Y);
        sheetPointerId = e.Pointer.Id;
        sheetPointerType = e.Pointer.Type;
        sheetPressPosition = pressPosition;

        // Build the lyrics / metadata content now so it is populated by the time it fades in.
        ViewModel?.EnsureContent();
    }

    private void OnCardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sheetPointerId != e.Pointer.Id)
        {
            return;
        }

        if (sheetPointerType != PointerType.Touch)
        {
            var movement = e.GetPosition(this) - sheetPressPosition;
            if (Math.Abs(movement.X) >= DragThresholdPx || Math.Abs(movement.Y) >= DragThresholdPx)
            {
                sheet.Abandon();
                sheetPointerId = null;
            }

            return;
        }

        var wasDragging = sheet.IsDragging;
        if (!sheet.Move(e.GetPosition(this).Y, SheetTravel))
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
        if (sheetPointerId != e.Pointer.Id || !sheet.IsPressed)
        {
            return;
        }

        sheetPointerId = null;

        if (!sheet.Release(out var target))
        {
            // A tap on the compact card (its artwork, title, or empty space) opens the sheet.
            if (sheet.Progress < 0.5d)
            {
                Commit(1d);
                e.Handled = true;
            }

            return;
        }

        Commit(target);
        e.Handled = true;
    }

    private void OnCardPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sheetPointerId != e.Pointer.Id || !sheet.IsPressed)
        {
            return;
        }

        sheetPointerId = null;
        sheet.Abandon();
        Commit(sheet.Nearest);
    }

    private void Commit(double target)
    {
        var expanded = target >= 0.5d;
        if (ViewModel is { } viewModel)
        {
            // Setting this may raise the property change that also calls SettleTo; the glide is
            // restartable, so the redundant call is harmless and keeps a no-change commit animating.
            viewModel.IsExpanded = expanded;
        }

        sheet.SettleTo(target);
    }

    // Places the card and cross-fades the compact mini player against the full page. Called for
    // every drag move and animation frame, so it only touches render-level properties.
    private void ApplyProgress(double progress)
    {
        cardTransform.Y = (1d - progress) * SheetTravel;

        // The compact row clears out early and the full page arrives late, so the two never fight
        // for the same pixels midway through the pull.
        var compactOpacity = Math.Clamp(1d - (progress * 2.2d), 0d, 1d);
        var fullOpacity = Math.Clamp((progress - 0.25d) / 0.75d, 0d, 1d);

        CompactContent.Opacity = compactOpacity;
        CompactContent.IsHitTestVisible = progress < 0.02d;
        ExpandedHeader.Opacity = fullOpacity;
        ExpandedHeader.IsHitTestVisible = progress > 0.98d;
        FullContent.Opacity = fullOpacity;
        FullContent.IsHitTestVisible = progress > 0.98d;

        // Square off the corners as the card takes over the whole viewport; a rounded top would
        // otherwise leave two notches of the page showing behind a "full screen" surface. The
        // outline goes with them: it only reads as a separator while the compact card floats over
        // the page.
        var radius = 20d * (1d - progress);
        Card.CornerRadius = new CornerRadius(radius, radius, 0, 0);
        Card.Classes.Set("open", progress > 0.98d);
        Card.Classes.Set("compact", progress < 0.02d);

        if (ViewModel is { } viewModel)
        {
            viewModel.Progress = progress;
        }
    }

    // ---------------------------------------------------------------- horizontal pager

    private void OnPagerHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        SyncPageWidths();
        SetPage(pageIndex, animate: false);
        UpdateLyricsPageInsets();
    }

    private void UpdateLyricsPageInsets()
    {
        var pageHeight = Card.Bounds.Height;
        if (pageHeight <= 0 || LyricsControl.Bounds.Height <= 0 ||
            LyricsControl.TranslatePoint(new Point(0, 0), Card) is not { } position)
        {
            return;
        }

        LyricsControl.PageTopInset = Math.Max(0d, position.Y);
        LyricsControl.PageBottomInset = Math.Max(0d, pageHeight - position.Y - LyricsControl.Bounds.Height);
    }

    private void SyncPageWidths()
    {
        // Every page carries an explicit width, including before the sheet has been measured. A
        // horizontal StackPanel measures its children with infinite width, and a page measured that
        // way lays its virtualizing content (the "Up next" queue list) out at an absurd size, which
        // is then what its viewport is derived from -- leaving the page blank once a real width
        // arrives. Pinning to zero until the host reports a size avoids that unbounded pass.
        var width = Math.Max(0d, PagerHost.Bounds.Width);

        foreach (var child in PagerTrack.Children)
        {
            if (child is Control control)
            {
                // The pages sit end to end, so each one's layout slot must be exactly one viewport
                // wide. A page's own margin is part of that slot, so it comes out of the width;
                // otherwise every page would drift a little further past the viewport edge.
                var margin = control.Margin;
                control.Width = Math.Max(0d, width - margin.Left - margin.Right);
            }
        }
    }

    private void SetPage(int index, bool animate)
    {
        pageIndex = Math.Clamp(index, 0, Math.Max(0, PagerTrack.Children.Count - 1));
        var target = -pageIndex * PagerHost.Bounds.Width;

        if (!animate)
        {
            pagerGlide.Stop();
            ApplyPagerOffset(target);
            return;
        }

        pagerGlide.Start(pagerOffset, target, PagerDurationMs);
    }

    private void ApplyPagerOffset(double value)
    {
        if (pagerOffset.Equals(value))
        {
            return;
        }

        pagerOffset = value;
        pagerTransform.X = value;

        // Moving the track is a render-level change, but the virtualizing queue list on the "Up
        // next" page realizes its items from the *effective viewport*, which Avalonia recomputes
        // only at the end of a layout pass -- and which it derives through this very render
        // transform. Without a pass here the page sliding in keeps the (empty) viewport it had
        // while parked off to the side and shows a single row until some unrelated interaction
        // happens to trigger layout. Re-arranging the track is cheap (its children keep their rects
        // and skip re-arranging) and makes the incoming page realize exactly what the swipe has
        // actually revealed. Same workaround as LibraryPager.
        PagerTrack.InvalidateArrange();
    }

    private void OnPagerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        pagerPressed = false;
        pagerDragging = false;

        if (sheet.Progress < 0.98d || e.Pointer.Type != PointerType.Touch)
        {
            return;
        }

        var point = e.GetPosition(PagerHost);
        pagerPressed = true;
        pagerPressX = point.X;
        pagerPressY = point.Y;
    }

    private void OnPagerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!pagerPressed)
        {
            return;
        }

        var point = e.GetPosition(PagerHost);
        var deltaX = point.X - pagerPressX;
        var deltaY = point.Y - pagerPressY;

        if (!pagerDragging)
        {
            // Only claim clearly horizontal movement, so the lyric view and the queue list keep
            // their vertical scrolling.
            if (Math.Abs(deltaX) < DragThresholdPx * 2d || Math.Abs(deltaX) < Math.Abs(deltaY) * 1.5d)
            {
                return;
            }

            pagerDragging = true;
            pagerGlide.Stop();
            e.Pointer.Capture(PagerHost);
        }

        var width = PagerHost.Bounds.Width;
        var rest = -pageIndex * width;

        // Resist dragging past the first and last pages instead of exposing empty space.
        if ((pageIndex == 0 && deltaX > 0) || (pageIndex == PagerTrack.Children.Count - 1 && deltaX < 0))
        {
            deltaX *= 0.35d;
        }

        ApplyPagerOffset(rest + deltaX);
        e.Handled = true;
    }

    private void OnPagerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!pagerDragging)
        {
            pagerPressed = false;
            return;
        }

        pagerPressed = false;
        pagerDragging = false;

        var width = PagerHost.Bounds.Width;
        var traveled = pagerOffset + (pageIndex * width);
        var next = pageIndex;
        if (width > 0d && Math.Abs(traveled) > width * 0.2d)
        {
            next = traveled < 0 ? pageIndex + 1 : pageIndex - 1;
        }

        next = Math.Clamp(next, 0, Math.Max(0, PagerTrack.Children.Count - 1));
        if (ViewModel is { } viewModel && viewModel.SelectedTab != next)
        {
            // Raises SelectedTab, whose handler animates the pager to the new page.
            viewModel.SelectedTab = next;
        }
        else
        {
            SetPage(next, animate: true);
        }

        e.Handled = true;
    }

    private void OnPagerPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!pagerPressed && !pagerDragging)
        {
            return;
        }

        pagerPressed = false;
        pagerDragging = false;
        SetPage(pageIndex, animate: true);
    }

    // ---------------------------------------------------------------- helpers

    private void AttachScrub(PlaybackProgressSlider slider)
    {
        slider.ScrubStarted += (_, _) => (DataContext as NowPlayingSheetViewModel)?.Playback.BeginScrub();
        slider.ScrubCompleted += (_, _) => (DataContext as NowPlayingSheetViewModel)?.Playback.EndScrub();
    }
}
