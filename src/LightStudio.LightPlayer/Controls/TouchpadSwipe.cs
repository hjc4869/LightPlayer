using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LightStudio.LightPlayer.Controls;

internal sealed class TouchpadSwipe
{
    private const double DipsPerWheelDelta = 50;
    private const double DirectionThreshold = 12;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMilliseconds(140);

    private readonly Control owner;
    private readonly Func<bool> canStart;
    private readonly Action<double> moved;
    private readonly Action<bool, TimeSpan> ended;
    private readonly Action? started;
    private readonly DispatcherTimer idleTimer = new() { Interval = IdleTimeout };
    private readonly Stopwatch clock = new();
#if LMR_AVALONIA_FORK
    private Vector travel;
#endif
    private TimeSpan lastMovement;
    private State state;
    private bool nativeLifecycle;

    public TouchpadSwipe(Control owner, Func<bool> canStart, Action<double> moved,
        Action<bool, TimeSpan> ended, Action? started = null)
    {
        this.owner = owner;
        this.canStart = canStart;
        this.moved = moved;
        this.ended = ended;
        this.started = started;
        idleTimer.Tick += OnIdle;
        owner.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        owner.AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, (_, _) => Cancel(),
            RoutingStrategies.Bubble, handledEventsToo: true);
        owner.AddHandler(InputElement.PointerPressedEvent, (_, _) => Cancel(), RoutingStrategies.Tunnel);
        owner.DetachedFromVisualTree += (_, _) => Reset();
    }

    public bool IsTracking => state is State.Pending or State.Horizontal;

    public bool IsActive => state == State.Horizontal;

    private bool HasInteraction => IsActive || (state == State.Pending && started is not null);

    public void Reset()
    {
        idleTimer.Stop();
        clock.Reset();
    #if LMR_AVALONIA_FORK
        travel = default;
    #endif
        lastMovement = default;
        state = State.None;
        nativeLifecycle = false;
    }

    public void Cancel()
    {
        bool engaged = HasInteraction;
        TimeSpan elapsed = Elapsed;
        state = State.Ignored;
        RestartIdleTimer();
        if (engaged)
            ended(false, elapsed);
    }

    private TimeSpan Elapsed => TimeSpan.FromMilliseconds(Math.Max(16,
        (nativeLifecycle ? clock.Elapsed : lastMovement).TotalMilliseconds));

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
#if LMR_AVALONIA_FORK
        if (!e.IsTouchpad ||
            !double.IsFinite(e.Delta.X) || !double.IsFinite(e.Delta.Y))
            return;

        if (e.GesturePhase == TouchpadGesturePhase.Began)
        {
            bool engaged = HasInteraction;
            TimeSpan elapsed = Elapsed;
            Reset();
            if (engaged)
                ended(false, elapsed);
            clock.Restart();
            state = canStart() ? State.Pending : State.Ignored;
            if (state == State.Pending)
                started?.Invoke();
        }
        else if (e.GesturePhase == TouchpadGesturePhase.None && state == State.Completed)
            Reset();

        if (e.GesturePhase != TouchpadGesturePhase.None)
        {
            nativeLifecycle = true;
            idleTimer.Stop();
            if (e.GesturePhase is TouchpadGesturePhase.Ended or TouchpadGesturePhase.Cancelled)
            {
                e.Handled = IsActive || state == State.Completed;
                FinishNative(e.GesturePhase == TouchpadGesturePhase.Ended);
                return;
            }
            if (e.GesturePhase == TouchpadGesturePhase.Inertia)
            {
                FinishNative(true);
                e.Handled = state == State.Completed;
                return;
            }
        }

        if (e.Delta == default)
            return;

        if (state == State.Completed)
        {
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Cancel();
            return;
        }

        if (state == State.None)
        {
            clock.Restart();
            state = canStart() ? State.Pending : State.Ignored;
            if (state == State.Pending)
                started?.Invoke();
        }
        else if (IsTracking && !canStart())
        {
            Cancel();
        }

        lastMovement = clock.Elapsed;
        RestartIdleTimer();
        if (state == State.Ignored)
            return;

        travel += e.Delta * DipsPerWheelDelta;
        if (state == State.Pending)
        {
            if (Math.Abs(travel.Y) > Math.Abs(travel.X))
            {
                if (Math.Abs(travel.Y) >= DirectionThreshold)
                {
                    state = State.Ignored;
                    if (started is not null)
                        ended(true, Elapsed);
                }
                return;
            }

            e.Handled = true;
            if (Math.Abs(travel.X) < DirectionThreshold)
                return;

            state = State.Horizontal;
        }

        e.Handled = true;
        moved(travel.X);
#endif
    }

    private void RestartIdleTimer()
    {
        idleTimer.Stop();
        if (!nativeLifecycle && owner.IsEffectivelyVisible && TopLevel.GetTopLevel(owner) is not null)
            idleTimer.Start();
    }

    private void FinishNative(bool commit)
    {
        if (state == State.Completed)
            return;

        bool active = IsActive;
        bool engaged = HasInteraction;
        TimeSpan elapsed = Elapsed;
        Reset();
        if (active)
        {
            nativeLifecycle = true;
            state = State.Completed;
        }
        if (engaged)
            ended(commit && owner.IsEffectivelyVisible && canStart(), elapsed);
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        bool engaged = HasInteraction;
        TimeSpan elapsed = Elapsed;
        Reset();
        if (engaged)
            ended(owner.IsEffectivelyVisible && canStart(), elapsed);
    }

    private enum State
    {
        None,
        Pending,
        Horizontal,
        Ignored,
        Completed
    }
}