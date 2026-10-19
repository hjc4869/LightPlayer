using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.Controls;
using LightStudio.LightPlayer.Services;
using LightMediaRenderer.Avalonia.Controls;
using LightStudio.LightPlayer.ViewModels;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Views.Pages;

/// <summary>
/// Hosts the photo viewer and handles the page-level gestures that move between pictures or
/// dismiss the page.
/// </summary>
public partial class PhotoViewerPageView : UserControl
{
    private static readonly TimeSpan DefaultDoubleTapWindow = TimeSpan.FromMilliseconds(150);

    /// <summary>How far a pointer travels before it is a swipe rather than a tap.</summary>
    private const double DragSlopPx = 12d;

    /// <summary>Fraction of the viewport a drag has to cross for the picture to change on release.</summary>
    private const double CommitFraction = 0.25d;

    /// <summary>A flick this fast changes the picture however short it was.</summary>
    private const double FlickPxPerSecond = 700d;

    /// <summary>How much of a drag towards an edge with nothing beyond it is actually followed.</summary>
    private const double EdgeResistance = 0.3d;

    private const double SwipeAnimationMs = 220d;

    /// <summary>Fraction of the viewport a downward drag has to cross for the page to close.</summary>
    private const double DismissFraction = 0.18d;

    /// <summary>A downward throw this fast closes the page however short it was.</summary>
    private const double DismissFlickPxPerSecond = 900d;

    /// <summary>How much of its size the picture has given up by the time it is a viewport down.</summary>
    private const double DismissShrink = 0.45d;

    private const double DismissAnimationMs = 200d;

    private readonly DispatcherTimer toggleControlsTimer = new();
    private readonly Stopwatch dragClock = Stopwatch.StartNew();
    private readonly Glide swipeGlide;
    private readonly Glide dismissGlide;
    private readonly TouchpadSwipe touchpadSwipe;

    private PhotoViewerPageViewModel? subscribed;
    private int? dragPointerId;
    private Point dragOrigin;
    private double dragStartSwipeOffset;
    private double dragStartSwipeTravel;
    private Size swipeViewportSize;
    private double? swipeRenderScaling;
    private TimeSpan dragStartedAt;
    private DragKind dragKind;
    private bool suppressNextTap;
    private int pendingControlsToggles;
    private bool gliding;
    private int pendingStep;
    private bool updatingZoom;
    private bool animateSliderZoom = true;

    /// <summary>Where the finger had carried the picture when it let go, which the glide undoes.</summary>
    private Vector dismissRelease;
    private Vector dismissTravel;

    public PhotoViewerPageView()
    {
        InitializeComponent();
        Strings.LocalizedStrings.Register(this);
        LayoutUpdated += (_, _) =>
        {
            if (HdrFlyout.IsOpen)
                UpdateToneMappingPopupSize();
        };
        BackPreview.SetValue(BrowseScrollMemory.IsReadOnlyProperty, true);

        BackPreview.LayoutUpdated += (_, _) =>
        {
            if (BackPreview.Content is PageViewModelBase page &&
                this.FindAncestorOfType<ShellView>() is { DataContext: ShellViewModel shell } shellView &&
                ReferenceEquals(shell.CurrentPage, ViewModel))
            {
                shell.ApplyLayout(page);
                BackPreview.Margin = GetBackPreviewMargin(shellView, shell);
            }
        };

        toggleControlsTimer.Interval = DefaultDoubleTapWindow;
        toggleControlsTimer.Tick += (_, _) =>
        {
            toggleControlsTimer.Stop();
            if ((pendingControlsToggles & 1) != 0 && ViewModel is { } model)
                model.AreControlsVisible = !model.AreControlsVisible;
            pendingControlsToggles = 0;
        };
        AddHandler(DoubleTappedEvent, OnPhotoDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);

        HdrButton.PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty)
                UpdateToolbarLayout();
        };
        HdrToggle.PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty)
                UpdateToolbarLayout();
        };
        RawModeButton.PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty)
                UpdateToolbarLayout();
        };
            Photo.PropertyChanged += (_, args) =>
            {
                if (args.Property == PhotoView.SupportsHdrOutputProperty || args.Property == PhotoView.IsExtendedRangeOutputProperty ||
                    args.Property == PhotoView.AutomaticToneMappingProperty)
                UpdateHdrControls();
            };
        Photo.ImageFailed += exception =>
        {
            if (ViewModel is { } model)
            {
                model.IsLoading = false;
                model.Failure = exception.Message;
            }
        };
        Photo.ViewportChanged += (_, _) =>
        {
            UpdatePreviewViewport();
            UpdateZoomControls();
            if (touchpadSwipe?.IsTracking == true &&
                (Photo.CanPan || Photo.TargetZoom != Photo.Zoom ||
                 Photo.Bounds.Size != swipeViewportSize ||
                 TopLevel.GetTopLevel(this)?.RenderScaling != swipeRenderScaling))
                touchpadSwipe.Cancel();
        };
        ZoomSlider.PropertyChanged += (_, args) =>
        {
            if (args.Property == RangeBase.ValueProperty && !updatingZoom)
            {
                Photo.ZoomTo(Math.Pow(2, ZoomSlider.Value), animateSliderZoom);
            }
        };
        ZoomSlider.AddHandler(PointerPressedEvent, (_, e) => animateSliderZoom = e.Pointer.Type == PointerType.Mouse,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        ZoomSlider.AddHandler(KeyDownEvent, (_, _) => animateSliderZoom = true,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        UpdateZoomControls();

        swipeGlide = new Glide(this, offset => Photo.SwipeOffset = offset) { Completed = OnSwipeLanded };
        dismissGlide = new Glide(this, fraction => ApplyDismiss(dismissRelease * fraction));
        touchpadSwipe = new TouchpadSwipe(Photo,
            () => dragPointerId is null && ViewModel is not null &&
                !Photo.CanPan && !Photo.IsGestureActive && !PreviewStrip.IsInteracting,
            OnTouchpadSwipe, FinishDrag, BeginSwipe);
        PreviewStrip.SelectionStarted += () =>
        {
            ResetTouchpadSwipe();
            dismissGlide.Stop();
            ApplyDismiss(default);
            dragPointerId = null;
            dragKind = DragKind.None;
            Photo.ZoomTo(1, false);
        };
        Photo.EffectiveViewportChanged += (_, e) =>
        {
            var visible = e.EffectiveViewport.Intersect(new Rect(Photo.Bounds.Size));
            if (!Photo.IsEffectivelyVisible || visible.Width <= 0 || visible.Height <= 0)
                ResetTouchpadSwipe();
        };
        DataContextChanged += (_, _) =>
        {
            Attach();
            UpdatePreviewViewport();
        };

        AttachedToVisualTree += async (_, _) =>
        {
            UpdatePreviewViewport();
            FocusShortcutSurface();
            if (OperatingSystem.IsBrowser() && TopLevel.GetTopLevel(this)?.Screens is { } screens)
            {
                try
                {
                    await screens.RequestScreenDetails();
                }
                catch (Exception exception)
                {
                    AppLog.Warning("Photo", "Could not request browser screen details.", exception);
                }
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Stage.ContextFlyout?.Hide();
            HdrFlyout.Hide();
            toggleControlsTimer.Stop();
            pendingControlsToggles = 0;
            touchpadSwipe.Reset();
            swipeGlide.Stop();
            dismissGlide.Stop();
            gliding = false;
            pendingStep = 0;
            Photo.SwipeOffset = 0;
            ApplyDismiss(default);
            dragPointerId = null;
            dragKind = DragKind.None;
            ViewModel?.EndSwipe();
        };

        // A drag holds the pointer on the stage, so the loss of that hold is reported there rather
        // than here: capture-lost does not travel up the tree the way the pointer events do.
        Stage.PointerCaptureLost += (_, e) =>
        {
            if (dragPointerId == e.Pointer.Id)
            {
                EndDrag(commit: false);
            }
        };

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);

        // Seen on the way down, before the stage's own flyout hears it, which is the only place a
        // request that is really the second finger of a pinch can still be turned away.
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    /// <summary>What a drag on a picture that fits the viewport turned out to mean.</summary>
    private enum DragKind
    {
        /// <summary>Not yet far enough from where it started to have decided.</summary>
        None,

        /// <summary>Sideways: the picture beside this one is being pulled in.</summary>
        Swipe,

        /// <summary>Downwards: the page itself is being pushed away.</summary>
        Dismiss,
    }

    private PhotoViewerPageViewModel? ViewModel => DataContext as PhotoViewerPageViewModel;

    internal void SetTransitionChrome(double opacity)
    {
        TitleBar.Opacity = opacity;
        Toolbar.Opacity = opacity;
        PreviewStrip.Opacity = opacity;
        foreach (Control control in Stage.Children)
        {
            if (control.Classes.Contains("photoStep"))
                control.Opacity = opacity;
        }
    }

    private void FocusShortcutSurface()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (VisualRoot is not null)
                {
                    Focus();
                }
            },
            DispatcherPriority.Loaded);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } model)
        {
            return;
        }

        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (HdrFlyout.IsOpen)
        {
            if (e.Key == Key.Escape)
                HdrFlyout.Hide();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (PhotoShortcutsPanel.IsVisible)
                PhotoShortcutsPanel.IsVisible = false;
            else
                model.BackCommand.Execute(null);

            e.Handled = true;
            return;
        }

        if (alt)
            return;

        if (!control && e.KeySymbol == "?")
        {
            PhotoShortcutsPanel.IsVisible = !PhotoShortcutsPanel.IsVisible;
            e.Handled = true;
            return;
        }

        if (PhotoShortcutsPanel.IsVisible)
            return;

        if (control)
        {
            if (e.Key == Key.OemPlus || e.KeySymbol is "=" or "+")
                model.ZoomInCommand.Execute(null);
            else if (e.Key == Key.OemMinus || e.KeySymbol == "-")
                model.ZoomOutCommand.Execute(null);
            else
                return;

            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Left when !ZoomSlider.IsKeyboardFocusWithin && Photo.CanPan:
                Photo.PanBy(new Vector(Photo.Bounds.Width * 0.2, 0));
                break;
            case Key.Right when !ZoomSlider.IsKeyboardFocusWithin && Photo.CanPan:
                Photo.PanBy(new Vector(-Photo.Bounds.Width * 0.2, 0));
                break;
            case Key.Up when !ZoomSlider.IsKeyboardFocusWithin && Photo.CanPan:
                Photo.PanBy(new Vector(0, Photo.Bounds.Height * 0.2));
                break;
            case Key.Down when !ZoomSlider.IsKeyboardFocusWithin && Photo.CanPan:
                Photo.PanBy(new Vector(0, -Photo.Bounds.Height * 0.2));
                break;
            case Key.Left when !ZoomSlider.IsKeyboardFocusWithin:
                model.PreviousCommand.Execute(null);
                break;
            case Key.Right when !ZoomSlider.IsKeyboardFocusWithin:
                model.NextCommand.Execute(null);
                break;
            case Key.F:
            case Key.F11:
                model.ToggleFullScreenCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnPhotoShortcutsBackgroundTapped(object? sender, TappedEventArgs e)
    {
        if (ReferenceEquals(e.Source, PhotoShortcutsPanel))
            PhotoShortcutsPanel.IsVisible = false;
    }

    private void OnPhotoShortcutsCloseClick(object? sender, RoutedEventArgs e) =>
        PhotoShortcutsPanel.IsVisible = false;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdatePreviewViewport();
        UpdateToolbarLayout();
        UpdateToneMappingPopupSize();
    }

    private void UpdatePreviewViewport()
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        ViewModel?.SetPreviewViewport(new Size(Photo.Bounds.Width * scaling, Photo.Bounds.Height * scaling));
    }

    private void UpdateToolbarLayout()
    {
        int extraButtons = (HdrButton.IsVisible || HdrToggle.IsVisible ? 1 : 0) + (RawModeButton.IsVisible ? 1 : 0);
        Toolbar.Classes.Set("compact", Bounds.Width < 600 + (RawModeButton.IsVisible ? 36 : 0));
        Toolbar.Classes.Set("small", Bounds.Width < 372 + extraButtons * 36);
        Toolbar.Classes.Set("tight", Bounds.Width < 318 + extraButtons * 30);
    }

    private void OnMoreClicked(object? sender, RoutedEventArgs e)
    {
        if (Stage.ContextFlyout is MenuFlyout menu)
        {
            menu.Placement = PlacementMode.Top;
            if (!NativeContextMenu.TryShow(menu, MoreButton))
            {
                menu.ShowAt(MoreButton);
            }
        }
    }

    private void UpdateZoomControls()
    {
        updatingZoom = true;
        ZoomModeButton.Classes.Set("fit", Photo.IsFitToWindow);
        ZoomSlider.Minimum = Math.Log2(Photo.MinimumZoom);
        ZoomSlider.Value = Math.Log2(Photo.TargetZoom);
        ZoomPercentageText.Text = $"{Photo.ZoomPercentage:0}%";
        ZoomOutButton.IsEnabled = Photo.Source is not null && Photo.TargetZoom > Photo.MinimumZoom;
        ZoomInButton.IsEnabled = Photo.Source is not null && Photo.TargetZoom < PhotoViewport.MaximumZoom;
        updatingZoom = false;
    }

    private void OnZoomModeClicked(object? sender, RoutedEventArgs e)
    {
        Photo.ToggleZoom();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdateDrag(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Cleared here rather than when the tap is swallowed, because a drag long enough to move a
        // picture does not raise one at all and the flag would outlive it.
        suppressNextTap = false;

        if (Photo.IsGestureActive)
        {
            EndDrag(commit: false);
            return;
        }

        // A zoomed picture answers the same drag by moving inside the viewport, and a picture
        // already held by two fingers is being worked on there.
        if (dragPointerId is not null ||
            PreviewStrip.IsInteracting ||
            Photo.CanPan ||
            !IsPictureHit(e.Source) ||
            !e.GetCurrentPoint(Stage).Properties.IsLeftButtonPressed)
        {
            return;
        }

        bool interrupted = gliding;
        BeginSwipe();
        dragPointerId = e.Pointer.Id;
        dragOrigin = e.GetPosition(Stage);
        dragStartedAt = dragClock.Elapsed;
        if (interrupted)
        {
            suppressNextTap = true;
            e.Pointer.Capture(Stage);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (dragPointerId == e.Pointer.Id)
        {
            EndDrag(commit: true);
        }
    }

    /// <summary>
    /// Turns away a request for the context menu that is really part of something else. Two fingers
    /// settling onto a picture before they move look exactly like one finger held down, which is
    /// the gesture that asks for the menu on a touch screen.
    /// </summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Photo.IsGestureActive || touchpadSwipe.IsTracking || dragKind != DragKind.None ||
            gliding || Photo.SwipeOffset != 0 || PreviewStrip.IsInteracting)
        {
            e.Handled = true;
        }
    }

    /// <summary>A single click or tap on the picture toggles the viewer controls.</summary>
    protected override void OnTapped(TappedEventArgs e)
    {
        base.OnTapped(e);

        if (ViewModel is not { } model || !IsPictureHit(e.Source))
            return;

        // The drag that just carried a picture across also arrives as a tap.
        if (suppressNextTap)
        {
            return;
        }

        TimeSpan doubleTapWindow = this.GetPlatformSettings()?.GetDoubleTapTime(e.Pointer.Type)
            ?? DefaultDoubleTapWindow;
        pendingControlsToggles++;
        toggleControlsTimer.Interval = doubleTapWindow;
        toggleControlsTimer.Stop();
        toggleControlsTimer.Start();
    }

    private void OnPhotoDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!IsPictureHit(e.Source))
            return;

        toggleControlsTimer.Stop();
        pendingControlsToggles = 0;
    }

    /// <summary>
    /// Follows the pointer once it has traveled far enough to say what it means. Sideways moves
    /// the pictures, and towards an edge with nothing beyond it the drag is resisted rather than
    /// refused, which is how the viewer says there is nothing more that way. Downwards pushes the
    /// picture, and the page with it, out of the way.
    /// </summary>
    private void UpdateDrag(PointerEventArgs e)
    {
        if (dragPointerId != e.Pointer.Id || ViewModel is not { } model)
        {
            return;
        }

        // A second finger has taken the picture over. Whatever this drag was going to be, it is not
        // that any more, and it must not pick up again from an origin the pinch has left behind.
        if (Photo.IsGestureActive)
        {
            EndDrag(commit: false);
            return;
        }

        Point point = e.GetPosition(Stage);
        double dx = point.X - dragOrigin.X;
        double dy = point.Y - dragOrigin.Y;

        if (dragKind == DragKind.None)
        {
            if (Math.Abs(dx) < DragSlopPx && (Math.Abs(dy) < DragSlopPx || dragStartSwipeOffset != 0))
            {
                return;
            }

            // Whichever way it has moved furthest is what it meant. Upwards means neither, and is
            // left undecided rather than refused, so the same press can still become either.
            if (Math.Abs(dx) > Math.Abs(dy) || dragStartSwipeOffset != 0)
            {
                if (e.Pointer.Type != PointerType.Touch)
                {
                    return;
                }

                dragKind = DragKind.Swipe;
            }
            else if (dy > 0 && e.Pointer.Type == PointerType.Touch)
            {
                dragKind = DragKind.Dismiss;
                if (this.FindAncestorOfType<ShellView>() is { DataContext: ShellViewModel shell } shellView)
                {
                    BackPreview.Margin = GetBackPreviewMargin(shellView, shell);
                    BackPreview.Content = shell.PreviewPhotoBack(model);
                }
            }
            else
            {
                return;
            }

            suppressNextTap = true;
            e.Pointer.Capture(Stage);
        }

        if (dragKind == DragKind.Swipe)
        {
            UpdateSwipe(dx, model);
        }
        else
        {
            ApplyDismiss(new Vector(dx, dy));
        }

        e.Handled = true;
    }

    private void UpdateSwipe(double movement, PhotoViewerPageViewModel model)
    {
        model.BeginSwipe();
        double span = Math.Max(1, Stage.Bounds.Width) + PhotoView.NeighborGap;
        double travel = dragStartSwipeTravel + movement;
        while ((travel < -span && model.HasNext) || (travel > span && model.HasPrevious))
        {
            int step = travel < 0 ? 1 : -1;
            travel += step * span;
            dragStartSwipeTravel += step * span;
            dragStartSwipeOffset += step * span;
            pendingStep = 0;
            Photo.SwipeOffset = travel;
            model.Step(step);
        }

        travel = Math.Clamp(travel, -span, span);
        bool leadsSomewhere = travel > 0 ? model.HasPrevious : model.HasNext;
        Photo.SwipeOffset = leadsSomewhere ? travel : travel * EdgeResistance;
    }

    private Thickness GetBackPreviewMargin(ShellView shellView, ShellViewModel shell)
    {
        if (shell.VideoSheet.IsShellChromeHidden)
            return default;

        bool showsPlaybackBar = !shell.VideoSheet.IsActive && shell.PlaybackBar.HasItem;
        bool expandedInline = shell.Navigation.IsExpanded &&
            shell.Navigation.DisplayMode is SplitViewDisplayMode.CompactInline or SplitViewDisplayMode.Inline;
        if (shell.IsAppleLayout)
        {
            Thickness chrome = shellView.MeasureAppleChromeInsets(showsPlaybackBar);
            return new Thickness(expandedInline ? ShellViewModel.AppleSidebarWidth : 56d, chrome.Top,
                shell.Queue.LayoutWidth, chrome.Bottom);
        }

        // Keep the rail widths linked to ShellViewModel.NavRailCompactWidth/NavPaneOpenWidth,
        // and the bar measurements linked to the content reservations in ShellView.axaml.
        double left = shell.IsNarrow ? 0 : expandedInline ? 320d : 56d;
        double top = shell.IsNarrow ? shellView.FindControl<Grid>("NarrowTopBar")!.Height : 0;
        double bottom = 0;
        var available = new Size(Stage.Bounds.Width, double.PositiveInfinity);
        if (shell.IsNarrow && shellView.FindControl<Border>("BottomNavBar") is { Child: { } navigation } bottomNav)
        {
            navigation.Measure(available);
            bottom = navigation.DesiredSize.Height + bottomNav.BorderThickness.Top + bottomNav.BorderThickness.Bottom;
        }

        if (showsPlaybackBar)
        {
            if (shell.IsNarrow)
                bottom += NowPlayingSheet.CompactHeight;
            else if (shellView.FindDescendantOfType<PlaybackBar>() is { } playbackBar)
            {
                playbackBar.Measure(available);
                bottom += playbackBar.DesiredSize.Height;
            }
        }

        return new Thickness(left, top, shell.Queue.LayoutWidth, bottom);
    }

    private void BeginSwipe()
    {
        dismissGlide.Stop();
        ApplyDismiss(default);
        swipeGlide.Stop();
        gliding = false;
        dragStartSwipeOffset = Photo.SwipeOffset;
        bool reachable = dragStartSwipeOffset > 0 ? ViewModel?.HasPrevious == true : ViewModel?.HasNext == true;
        dragStartSwipeTravel = reachable ? dragStartSwipeOffset : dragStartSwipeOffset / EdgeResistance;
        swipeViewportSize = Photo.Bounds.Size;
        swipeRenderScaling = TopLevel.GetTopLevel(this)?.RenderScaling;
        dragKind = DragKind.None;
    }

    private void OnTouchpadSwipe(double travel)
    {
        if (ViewModel is { } model)
        {
            dragKind = DragKind.Swipe;
            UpdateSwipe(travel, model);
        }
    }

    private void ResetTouchpadSwipe()
    {
        touchpadSwipe.Reset();
        swipeGlide.Stop();
        gliding = false;
        pendingStep = 0;
        Photo.SwipeOffset = 0;
        if (dragPointerId is null)
        {
            dragKind = DragKind.None;
            ViewModel?.EndSwipe();
        }
    }

    /// <summary>Moves and shrinks the picture as the drag pushing the page away carries it.</summary>
    private void ApplyDismiss(Vector travel)
    {
        dismissTravel = travel;

        double height = Math.Max(1d, Stage.Bounds.Height);
        double progress = Math.Clamp(travel.Y / height, 0d, 1d);
        Photo.DismissOffset = travel;
        Photo.DismissScale = 1d - (DismissShrink * progress);
        DismissBackdrop.Opacity = BackPreview.Content is null ? 1 : 1 - Math.Clamp(progress / (DismissFraction * 2), 0, 1);
        if (travel == default)
            BackPreview.Content = null;
    }

    /// <summary>Hands the drag to whichever of the two gestures it turned out to be.</summary>
    private void EndDrag(bool commit)
    {
        if (dragPointerId is null)
        {
            return;
        }

        TimeSpan elapsed = dragClock.Elapsed - dragStartedAt;
        dragPointerId = null;
        FinishDrag(commit, elapsed);
    }

    private void FinishDrag(bool commit, TimeSpan elapsed)
    {
        DragKind kind = dragKind;
        dragKind = DragKind.None;

        switch (kind)
        {
            case DragKind.None when Photo.SwipeOffset != 0 || pendingStep != 0:
                int step = commit ? pendingStep : 0;
                StartGlide(-step * (Math.Max(1, Stage.Bounds.Width) + PhotoView.NeighborGap), step);
                break;
            case DragKind.Swipe:
                SettleSwipe(commit, elapsed, Photo.SwipeOffset - dragStartSwipeOffset);
                break;
            case DragKind.Dismiss:
                ViewModel?.EndSwipe();
                SettleDismiss(commit, elapsed);
                break;
            default:
                ViewModel?.EndSwipe();
                break;
        }
    }

    /// <summary>
    /// Settles a sideways drag: far enough across, or fast enough, and the picture it was reaching
    /// for slides the rest of the way in; otherwise the one that was there slides back.
    /// </summary>
    private void SettleSwipe(bool commit, TimeSpan elapsed, double movement)
    {
        double traveled = Photo.SwipeOffset;

        if (!commit || ViewModel is not { } model)
        {
            StartGlide(0, step: 0);
            return;
        }

        double width = Math.Max(1d, Stage.Bounds.Width);
        double speed = elapsed.TotalSeconds > 0 ? Math.Abs(movement) / elapsed.TotalSeconds : 0;
        bool directed = Math.Abs(movement) >= width * CommitFraction ||
            (speed >= FlickPxPerSecond && Math.Abs(movement) >= DragSlopPx * 2);
        if (directed && Math.Sign(movement) != Math.Sign(traveled))
        {
            StartGlide(0, step: 0);
            return;
        }

        bool forward = traveled < 0;
        bool reachable = forward ? model.HasNext : model.HasPrevious;
        bool decided = Math.Abs(traveled) >= width * CommitFraction || directed;

        if (reachable && decided)
        {
            StartGlide(forward ? -(width + PhotoView.NeighborGap) : width + PhotoView.NeighborGap, forward ? 1 : -1);
        }
        else
        {
            StartGlide(0, step: 0);
        }
    }

    /// <summary>
    /// Settles a drag that was pushing the page away: carried far enough down, or thrown down fast
    /// enough, and the page goes; otherwise the picture comes back to where it belongs.
    /// </summary>
    private void SettleDismiss(bool commit, TimeSpan elapsed)
    {
        Vector traveled = dismissTravel;
        double height = Math.Max(1d, Stage.Bounds.Height);
        double speed = elapsed.TotalSeconds > 0 ? traveled.Y / elapsed.TotalSeconds : 0;

        if (commit &&
            ViewModel is { } model &&
            (traveled.Y >= height * DismissFraction ||
             (speed >= DismissFlickPxPerSecond && traveled.Y >= DragSlopPx * 2)))
        {
            model.Close();
            return;
        }

        dismissRelease = traveled;
        dismissGlide.Start(1d, 0d, DismissAnimationMs);
    }

    /// <summary>
    /// Slides the pictures to <paramref name="target"/>, and once they land takes the step that
    /// puts the one now in the middle of the viewport in the middle of the page.
    /// </summary>
    private void StartGlide(double target, int step)
    {
        ViewModel?.BeginSwipe();
        pendingStep = step;
        gliding = true;
        swipeGlide.Start(Photo.SwipeOffset, target, SwipeAnimationMs);
    }

    private void OnSwipeLanded()
    {
        gliding = false;
        int step = pendingStep;
        pendingStep = 0;

        // Put the row back before the step so the picture that slid into the middle is the one the
        // page then draws there, with no frame in between showing it still displaced.
        Photo.SwipeOffset = 0;
        if (step != 0)
        {
            ViewModel?.Step(step);
        }
        ViewModel?.EndSwipe();
    }

    /// <summary>
    /// Whether a press reached the picture itself rather than the bar or one of the step buttons.
    /// Anything in a flyout leaves the page's own tree, and counts as neither.
    /// </summary>
    private bool IsPictureHit(object? source)
    {
        for (Visual? current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, TitleBar) ||
                ReferenceEquals(current, Toolbar) ||
                ReferenceEquals(current, PreviewStrip) ||
                ReferenceEquals(current, PreviousButton) ||
                ReferenceEquals(current, NextButton))
            {
                return false;
            }

            if (ReferenceEquals(current, Stage))
            {
                return true;
            }
        }

        return false;
    }

    private void Attach()
    {
        if (touchpadSwipe is not null)
            ResetTouchpadSwipe();
        if (subscribed is not null)
        {
            subscribed.ActionRequested -= OnActionRequested;
            subscribed.PropertyChanged -= OnPhotoModelPropertyChanged;
        }

        subscribed = ViewModel;
        Photo.Opacity = subscribed?.IsTransitioning == true ? 0 : 1;
        SetTransitionChrome(subscribed?.IsTransitioning == true ? 0 : 1);
        if (subscribed is not null)
        {
            subscribed.ActionRequested += OnActionRequested;
            subscribed.PropertyChanged += OnPhotoModelPropertyChanged;
        }
        HdrFlyout.Hide();
        UpdateHdrControls();
    }

    private void OnActionRequested(PhotoViewerAction action)
    {
        switch (action)
        {
            case PhotoViewerAction.ZoomIn:
                Photo.ZoomIn();
                break;
            case PhotoViewerAction.ZoomOut:
                Photo.ZoomOut();
                break;
            case PhotoViewerAction.ZoomToActualSize:
                Photo.ZoomToActualSize();
                break;
            case PhotoViewerAction.ZoomToFit:
                Photo.ZoomToFit();
                break;
            case PhotoViewerAction.StepPrevious:
                Step(-1);
                break;
            case PhotoViewerAction.StepNext:
                Step(1);
                break;
            case PhotoViewerAction.CopyImage:
                _ = AppErrorReporter.GuardAsync(CopyImageAsync);
                break;
            case PhotoViewerAction.CopyPath:
                _ = AppErrorReporter.GuardAsync(CopyPathAsync);
                break;
        }
    }

    /// <summary>The same slide the swipe makes, for the buttons and anything else that asks.</summary>
    private void Step(int offset)
    {
        if (gliding || touchpadSwipe.IsTracking || dragKind != DragKind.None ||
            PreviewStrip.IsInteracting || ViewModel is not { } model)
        {
            return;
        }

        if (offset > 0 ? !model.HasNext : !model.HasPrevious)
        {
            return;
        }

        double width = Math.Max(1d, Stage.Bounds.Width) + PhotoView.NeighborGap;
        StartGlide(offset > 0 ? -width : width, offset);
    }

    /// <summary>
    /// Puts the picture itself on the clipboard, which is what another application pasting from it
    /// expects: a path would only be readable by one that can also reach this device's files.
    /// </summary>
    private async Task CopyImageAsync()
    {
        if (ViewModel is not { } model || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        if (await model.RenderBitmapAsync().ConfigureAwait(true) is { } bitmap)
        {
            await clipboard.SetBitmapAsync(bitmap).ConfigureAwait(true);
        }
    }

    private async Task CopyPathAsync()
    {
        if (ViewModel is { Path: { Length: > 0 } path } &&
            TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(path).ConfigureAwait(true);
        }
    }

}
