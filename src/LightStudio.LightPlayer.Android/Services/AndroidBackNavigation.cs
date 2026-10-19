#nullable enable
using System;
using System.Threading.Tasks;
using Android.Views;
using AndroidX.Activity;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Views;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Android.Services;

internal sealed class AndroidBackNavigation(AvaloniaActivity activity, Func<MotionEvent, bool> dispatchTouchEvent)
    : OnBackPressedCallback(true)
{
    private ShellView? shell;
    private MotionEvent? pendingEdgeDown;
    private IDisposable? pendingEdgeHold;
    private MotionEvent? contentTouch;
    private int pendingEdgeTouchSlop;
    private int generation;
    private bool gestureStarted;
    private bool suppressTouch;
    private bool completing;
    private bool stopped;

    internal bool DispatchTouchEvent(MotionEvent motionEvent)
    {
        MotionEventActions action = motionEvent.ActionMasked;
        if (action == MotionEventActions.Down)
        {
            ClearPendingEdgeDown();
            CancelContentTouch();
            suppressTouch = gestureStarted;
            if (!suppressTouch && motionEvent.GetToolType(0) == MotionEventToolType.Finger && IsBackEdge(motionEvent))
            {
                pendingEdgeDown = MotionEvent.ObtainNoHistory(motionEvent);
                pendingEdgeTouchSlop = ViewConfiguration.Get(activity)!.ScaledTouchSlop;
                pendingEdgeHold = DispatcherTimer.RunOnce(ReleasePendingEdgeDown,
                    TimeSpan.FromMilliseconds(ViewConfiguration.LongPressTimeout));
                return true;
            }
        }

        if (suppressTouch)
            return true;

        if (pendingEdgeDown is { } down)
        {
            if (action == MotionEventActions.Cancel)
            {
                suppressTouch = true;
                ClearPendingEdgeDown();
                return true;
            }
            if (action == MotionEventActions.Move)
            {
                double horizontal = Math.Abs(motionEvent.GetX() - down.GetX());
                double vertical = Math.Abs(motionEvent.GetY() - down.GetY());
                if (vertical <= pendingEdgeTouchSlop || vertical <= horizontal)
                    return true;
            }
            else if (action is not (MotionEventActions.Up or MotionEventActions.PointerDown))
            {
                return true;
            }

            ReleasePendingEdgeDown();
        }

        return DispatchContentTouch(motionEvent);
    }

    private bool IsBackEdge(MotionEvent motionEvent)
    {
        var decor = activity.Window?.DecorView;
        var insets = decor?.RootWindowInsets?.GetInsets(WindowInsets.Type.SystemGestures());
        if (decor is null || insets is null)
            return false;

        float position = motionEvent.GetX();
        return (insets.Left > 0 && position < insets.Left) ||
            (insets.Right > 0 && position >= decor.Width - insets.Right);
    }

    private bool DispatchContentTouch(MotionEvent motionEvent)
    {
        RecycleMotionEvent(contentTouch);
        contentTouch = motionEvent.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel
            ? null
            : MotionEvent.ObtainNoHistory(motionEvent);
        return dispatchTouchEvent(motionEvent);
    }

    private void CancelContentTouch()
    {
        if (contentTouch is not { } motionEvent)
            return;
        contentTouch = null;
        try
        {
            motionEvent.Action = MotionEventActions.Cancel;
            dispatchTouchEvent(motionEvent);
        }
        finally
        {
            RecycleMotionEvent(motionEvent);
        }
    }

    private void ReleasePendingEdgeDown()
    {
        MotionEvent? motionEvent = pendingEdgeDown;
        pendingEdgeDown = null;
        ClearPendingEdgeDown();
        try
        {
            if (motionEvent is not null)
                DispatchContentTouch(motionEvent);
        }
        finally
        {
            RecycleMotionEvent(motionEvent);
        }
    }

    private void ClearPendingEdgeDown()
    {
        pendingEdgeHold?.Dispose();
        pendingEdgeHold = null;
        RecycleMotionEvent(pendingEdgeDown);
        pendingEdgeDown = null;
    }

    private static void RecycleMotionEvent(MotionEvent? motionEvent)
    {
        if (motionEvent is null)
            return;
        motionEvent.Recycle();
        motionEvent.Dispose();
    }

    public override void HandleOnBackStarted(BackEventCompat backEvent)
    {
        if (stopped)
            return;
        generation++;
        completing = false;
        gestureStarted = true;
        suppressTouch = true;
        ClearPendingEdgeDown();
        CancelContentTouch();
        shell?.CancelPredictiveBack();
        shell = null;
        try
        {
            if (activity.Content is Control root &&
                root.FindDescendantOfType<ShellView>(includeSelf: true) is { } view &&
                view.TryBeginPredictiveBack(fromRightEdge: backEvent.SwipeEdge == BackEventCompat.EdgeRight))
            {
                shell = view;
                shell.UpdatePredictiveBack(backEvent.Progress);
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("Navigation", "Starting an Android back gesture failed.", exception);
        }
    }

    public override void HandleOnBackProgressed(BackEventCompat backEvent) =>
        shell?.UpdatePredictiveBack(backEvent.Progress);

    public override void HandleOnBackCancelled() => _ = CompleteAsync(commit: false);

    public override void HandleOnBackPressed() => _ = CompleteAsync(commit: true);

    internal void Stop()
    {
        stopped = true;
        generation++;
        gestureStarted = false;
        ClearPendingEdgeDown();
        CancelContentTouch();
        shell?.CancelPredictiveBack();
        shell = null;
        Remove();
    }

    private async Task CompleteAsync(bool commit)
    {
        if (stopped || completing)
            return;
        completing = true;
        int currentGeneration = generation;
        ShellView? current = shell;
        try
        {
            if (current is not null)
                await current.CompletePredictiveBackAsync(commit);
            else if (commit)
            {
                Enabled = false;
                try
                {
                    activity.OnBackPressedDispatcher.OnBackPressed();
                }
                finally
                {
                    if (!stopped)
                        Enabled = true;
                }
            }
        }
        catch (Exception exception)
        {
            if (currentGeneration == generation)
                current?.CancelPredictiveBack();
            AppLog.Error("Navigation", "Completing an Android back gesture failed.", exception);
        }
        finally
        {
            if (currentGeneration == generation)
            {
                shell = null;
                gestureStarted = false;
                completing = false;
            }
        }
    }
}