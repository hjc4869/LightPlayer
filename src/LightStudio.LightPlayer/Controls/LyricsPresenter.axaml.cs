using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Displays timed lyrics and keeps the current line centered in its parent page. Manual scrolling
/// temporarily suspends auto-centering.
/// </summary>
public partial class LyricsPresenter : UserControl
{
    public static readonly StyledProperty<IEnumerable?> LinesSourceProperty =
        AvaloniaProperty.Register<LyricsPresenter, IEnumerable?>(nameof(LinesSource));

    public static readonly StyledProperty<int> CurrentIndexProperty =
        AvaloniaProperty.Register<LyricsPresenter, int>(nameof(CurrentIndex), defaultValue: -1);

    public static readonly StyledProperty<double> PageTopInsetProperty =
        AvaloniaProperty.Register<LyricsPresenter, double>(nameof(PageTopInset));

    public static readonly StyledProperty<double> PageBottomInsetProperty =
        AvaloniaProperty.Register<LyricsPresenter, double>(nameof(PageBottomInset));

    private bool _userScrolling;
    private bool _recenterAfterLayout;

    // Wall-clock timing keeps the recenter glide duration stable across uneven timer ticks.
    // A new target retargets the active glide.
    private const double ScrollDurationMs = 360d;
    private static readonly TimeSpan ScrollFrameInterval = TimeSpan.FromMilliseconds(15);

    private const double ScrollSnapEpsilon = 0.5d;

    private DispatcherTimer? _scrollTimer;
    private readonly Stopwatch _scrollClock = new();
    private double _scrollStartY;
    private double _scrollTargetY;

    public LyricsPresenter()
    {
        InitializeComponent();

        // Catch wheel input even after the ScrollViewer handles it.
        AddHandler(PointerWheelChangedEvent, OnPointerWheel,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(ScrollGestureEvent, OnScrollGesture,
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnTouchPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble);

        Scroll.LayoutUpdated += (_, _) =>
        {
            if (!_recenterAfterLayout)
            {
                return;
            }

            _recenterAfterLayout = false;
            ScrollToCurrent();
        };
        Scroll.SizeChanged += (_, _) =>
        {
            UpdateEndpointPadding();
            _recenterAfterLayout = true;
        };
        Lines.SizeChanged += (_, _) =>
        {
            UpdateEndpointPadding();
            _recenterAfterLayout = true;
        };

        Loaded += (_, _) =>
        {
            UpdateEndpointPadding();
            _recenterAfterLayout = true;
            ScrollToCurrent();
        };
        Unloaded += (_, _) => StopScrollAnimation();
    }

    public IEnumerable? LinesSource
    {
        get => GetValue(LinesSourceProperty);
        set => SetValue(LinesSourceProperty, value);
    }

    public int CurrentIndex
    {
        get => GetValue(CurrentIndexProperty);
        set => SetValue(CurrentIndexProperty, value);
    }

    public double PageTopInset
    {
        get => GetValue(PageTopInsetProperty);
        set => SetValue(PageTopInsetProperty, value);
    }

    public double PageBottomInset
    {
        get => GetValue(PageBottomInsetProperty);
        set => SetValue(PageBottomInsetProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LinesSourceProperty)
        {
            Lines.ItemsSource = LinesSource;
            _userScrolling = false;
            UpdateEndpointPadding();
            _recenterAfterLayout = true;
            ScrollToCurrent();
        }
        else if (change.Property == CurrentIndexProperty)
        {
            ScrollToCurrent();
        }
        else if (change.Property == PageTopInsetProperty || change.Property == PageBottomInsetProperty)
        {
            UpdateEndpointPadding();
            _recenterAfterLayout = true;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        if (e.Pointer.Type == PointerType.Mouse && _userScrolling)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_userScrolling && !IsPointerOver)
                {
                    _userScrolling = false;
                    ScrollToCurrent();
                }
            }, DispatcherPriority.Background);
        }
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        _userScrolling = true;
        StopScrollAnimation();
    }

    private void OnScrollGesture(object? sender, ScrollGestureEventArgs e)
    {
        _userScrolling = true;
        StopScrollAnimation();
    }

    private void OnTouchPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_userScrolling || e.Pointer.Type != PointerType.Touch ||
            Scroll.FindDescendantOfType<ScrollContentPresenter>() is not { } presenter)
        {
            return;
        }

        foreach (var recognizer in presenter.GestureRecognizers)
        {
            if (recognizer is ScrollGestureRecognizer scrollRecognizer)
            {
                EndScrollGesture(scrollRecognizer);
            }
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "EndGesture")]
    private static extern void EndScrollGesture(ScrollGestureRecognizer recognizer);

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (!_userScrolling || e.Pointer.Type == PointerType.Mouse)
        {
            return;
        }

        _userScrolling = false;
        ScrollToCurrent();
    }

    private void ScrollToCurrent()
    {
        if (_userScrolling || CurrentIndex < 0)
        {
            return;
        }

        // Containers and layout may not be ready the instant the index or source
        // changes, so retry a few times until the line can be measured.
        TryCenter(CurrentIndex, attempt: 0);
    }

    private void TryCenter(int index, int attempt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_userScrolling || index != CurrentIndex)
            {
                return;
            }

            if (Lines.ContainerFromIndex(index) is not Control container ||
                Scroll.Viewport.Height <= 0 || container.Bounds.Height <= 0)
            {
                if (attempt < 5)
                {
                    TryCenter(index, attempt + 1);
                }

                return;
            }

            CenterOn(container);
        }, DispatcherPriority.Background);
    }

    private void CenterOn(Control container)
    {
        // Translate the line's vertical center into the ScrollViewer's viewport so
        // the math accounts for margins and the current offset, then shift the
        // offset so that point lands on the parent page's center line.
        if (container.TranslatePoint(new Point(0, container.Bounds.Height / 2), Scroll) is not { } point)
        {
            return;
        }

        var delta = point.Y - TargetCenterY;
        var max = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
        var target = Math.Clamp(Scroll.Offset.Y + delta, 0, max);
        AnimateScrollTo(target);
    }

    private double TargetCenterY => Math.Clamp(
        (Scroll.Viewport.Height + PageBottomInset - PageTopInset) / 2,
        0,
        Scroll.Viewport.Height);

    private void UpdateEndpointPadding()
    {
        var center = TargetCenterY;
        StartPadding.Height = center;
        EndPadding.Height = Scroll.Viewport.Height - center;
    }

    // Eases the vertical scroll offset toward <paramref name="targetY"/> with a cubic ease-out
    // glide. If a glide is already running it retargets from the live offset so rapid line changes
    // flow into one continuous scroll rather than a series of jumps.
    private void AnimateScrollTo(double targetY)
    {
        // Already centered (or close enough): snap and drop any glide in flight.
        if (Math.Abs(targetY - Scroll.Offset.Y) <= ScrollSnapEpsilon)
        {
            StopScrollAnimation();
            Scroll.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(Scroll.Offset.X, targetY));
            return;
        }

        _scrollStartY = Scroll.Offset.Y;
        _scrollTargetY = targetY;
        _scrollClock.Restart();

        if (_scrollTimer is null)
        {
            _scrollTimer = new DispatcherTimer { Interval = ScrollFrameInterval };
            _scrollTimer.Tick += OnScrollTick;
        }

        _scrollTimer.Start();
    }

    private void OnScrollTick(object? sender, EventArgs e)
    {
        // The user grabbed the wheel mid-glide: stop and leave the offset where they took over.
        if (_userScrolling)
        {
            StopScrollAnimation();
            return;
        }

        var progress = Math.Clamp(_scrollClock.Elapsed.TotalMilliseconds / ScrollDurationMs, 0d, 1d);

        // Cubic ease-out: quick start, gentle settle.
        var eased = 1d - Math.Pow(1d - progress, 3d);
        var y = _scrollStartY + (_scrollTargetY - _scrollStartY) * eased;
        Scroll.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(Scroll.Offset.X, y));

        if (progress >= 1d)
        {
            Scroll.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(Scroll.Offset.X, _scrollTargetY));
            StopScrollAnimation();
        }
    }

    private void StopScrollAnimation()
    {
        _scrollTimer?.Stop();
        _scrollClock.Reset();
    }
}
