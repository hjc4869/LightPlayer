using System;
using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Scrolling lyric viewer. Renders a list of timed lines and auto-scrolls so the
/// current line (driven by <see cref="CurrentIndex"/>) stays vertically centered.
/// Highlight styling is data-bound to each line's <c>IsCurrent</c> flag. Manual
/// scrolling is permitted with the mouse wheel only; touch panning is disabled and
/// the view recenters once the mouse leaves the control.
/// </summary>
public partial class LyricsPresenter : UserControl
{
    public static readonly StyledProperty<IEnumerable?> LinesSourceProperty =
        AvaloniaProperty.Register<LyricsPresenter, IEnumerable?>(nameof(LinesSource));

    public static readonly StyledProperty<int> CurrentIndexProperty =
        AvaloniaProperty.Register<LyricsPresenter, int>(nameof(CurrentIndex), defaultValue: -1);

    // Suspends auto-centering while the user is manually scrolling with the mouse
    // wheel. Reset when the pointer leaves the control or the source changes.
    private bool _userScrolling;

    public LyricsPresenter()
    {
        InitializeComponent();

        // Catch the wheel even after the ScrollViewer marks it handled so we can
        // switch into manual-scroll mode. Only the mouse enables this.
        AddHandler(PointerWheelChangedEvent, OnPointerWheel,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        // Swallow touch pan gestures before the ScrollViewer's presenter consumes
        // them so touch screens cannot scroll the lyrics manually.
        Lines.ScrollGesture += OnScrollGesture;
        Lines.ScrollGestureEnded += OnScrollGestureEnded;

        Loaded += (_, _) => ScrollToCurrent();
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LinesSourceProperty)
        {
            Lines.ItemsSource = LinesSource;
            _userScrolling = false;
            ScrollToCurrent();
        }
        else if (change.Property == CurrentIndexProperty)
        {
            ScrollToCurrent();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        // Mirror the original behavior: when the mouse leaves the lyrics region,
        // drop out of manual-scroll mode and snap back to the current line.
        if (e.Pointer.Type == PointerType.Mouse && _userScrolling)
        {
            _userScrolling = false;
            ScrollToCurrent();
        }
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse)
        {
            _userScrolling = true;
        }
    }

    // Touch-driven panning is disabled; mouse wheel scrolling is unaffected because
    // it is delivered as a pointer-wheel event rather than a scroll gesture.
    private void OnScrollGesture(object? sender, ScrollGestureEventArgs e) => e.Handled = true;

    private void OnScrollGestureEnded(object? sender, ScrollGestureEndedEventArgs e) => e.Handled = true;

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
        // offset so that point lands in the middle of the viewport.
        if (container.TranslatePoint(new Point(0, container.Bounds.Height / 2), Scroll) is not { } point)
        {
            return;
        }

        var delta = point.Y - Scroll.Viewport.Height / 2;
        var max = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
        var target = Math.Clamp(Scroll.Offset.Y + delta, 0, max);
        Scroll.Offset = new Vector(Scroll.Offset.X, target);
    }
}
