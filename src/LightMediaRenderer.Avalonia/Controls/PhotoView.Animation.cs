using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Controls;

public partial class PhotoView
{
    private readonly Stopwatch animationClock = new();
    private IPhotoAnimation? animation;
    private VideoFrame? animationOwner;
    private VideoFrame? animationNext;
    private CancellationTokenSource? animationCancellation;
    private IDisposable? animationTimer;
    private Task animationClosing = Task.CompletedTask;
    private int animationGeneration;
    private double animationDeadline;
    private bool animationReading;
    private bool animationFramePending;
    private bool animationCompleted;
    private bool animationViewportVisible = true;

    private bool IsAnimationVisible
    {
        get
        {
            if (topLevel is null || !IsEffectivelyVisible || Opacity <= 0 || !animationViewportVisible ||
                Bounds.Width <= 0 || Bounds.Height <= 0 || topLevel is Window { WindowState: WindowState.Minimized })
                return false;
            foreach (Visual ancestor in visibilityAncestors)
                if (!ancestor.IsVisible || ancestor.Opacity <= 0)
                    return false;
            return true;
        }
    }

    private void UpdatePhotoAnimation()
    {
        if (!IsAnimationVisible)
        {
            animationClock.Stop();
            animationTimer?.Dispose();
            animationTimer = null;
            return;
        }
        if (animationCompleted || Source?.PhotoAnimation is not { } source)
            return;
        if (animation is null)
        {
            animationOwner = Source.AddRef();
            try { animation = source.Open(); }
            catch (Exception exception)
            {
                animationOwner.Release();
                animationOwner = null;
                animationCompleted = true;
                AppLog.Error("Photo.Animation", "Could not open image animation.", exception);
                ImageFailed?.Invoke(exception);
                return;
            }
            animationCancellation = new CancellationTokenSource();
        }
        animationClock.Start();
        if (animationNext is not null)
            ScheduleAnimationFrame();
        else
            QueueAnimationFrame();
    }

    private void QueueAnimationFrame()
    {
        if (animationReading || animationCompleted || animationNext is not null || !IsAnimationVisible ||
            animation is not { } playback || animationCancellation is not { } cancellation)
            return;
        animationReading = true;
        _ = ReadAnimationFrameAsync(playback, cancellation.Token, animationGeneration);
    }

    private async Task ReadAnimationFrameAsync(IPhotoAnimation playback, CancellationToken cancellationToken, int generation)
    {
        VideoFrame? frame = null;
        try
        {
            await animationClosing.ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            frame = await playback.ReadNextAsync(cancellationToken).ConfigureAwait(true);
            if (generation != animationGeneration || cancellationToken.IsCancellationRequested)
                return;
            animationReading = false;
            if (frame is null)
            {
                StopPhotoAnimation();
                animationCompleted = true;
                return;
            }
            animationNext = frame;
            frame = null;
            ScheduleAnimationFrame();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Error("Photo.Animation", "Could not decode image animation.", exception);
            if (generation == animationGeneration)
            {
                StopPhotoAnimation();
                animationCompleted = true;
                ImageFailed?.Invoke(exception);
            }
        }
        finally { frame?.Release(); }
    }

    private void ScheduleAnimationFrame()
    {
        animationTimer?.Dispose();
        animationTimer = null;
        if (!IsAnimationVisible || animationNext is null)
            return;
        double delay = animationDeadline - animationClock.Elapsed.TotalSeconds;
        if (delay <= 0)
            RequestPhotoAnimationFrame();
        else
            animationTimer = DispatcherTimer.RunOnce(RequestPhotoAnimationFrame, TimeSpan.FromSeconds(delay), DispatcherPriority.Render);
    }

    private void RequestPhotoAnimationFrame()
    {
        animationTimer?.Dispose();
        animationTimer = null;
        if (animationFramePending || !IsAnimationVisible || animationNext is null || topLevel is null)
            return;
        animationFramePending = true;
        int generation = animationGeneration;
        topLevel.RequestAnimationFrame(_ =>
        {
            if (generation != animationGeneration)
                return;
            animationFramePending = false;
            PresentAnimationFrame();
        });
    }

    private void PresentAnimationFrame()
    {
        animationTimer?.Dispose();
        animationTimer = null;
        if (!IsAnimationVisible || animationNext is not { } frame)
            return;
        animationNext = null;
        current.Set(frame);
        double duration = double.IsFinite(frame.Duration) && frame.Duration > 0 ? frame.Duration : 0.1;
        animationDeadline = Math.Max(animationDeadline + Math.Clamp(duration, 0.01, 24 * 60 * 60),
            animationClock.Elapsed.TotalSeconds + 0.001);
        frame.Release();
        InvalidateVisual();
        QueueAnimationFrame();
    }

    private void StopPhotoAnimation()
    {
        animationGeneration++;
        animationTimer?.Dispose();
        animationTimer = null;
        animationClock.Reset();
        animationDeadline = 0;
        animationNext?.Release();
        animationNext = null;
        animationReading = false;
        animationFramePending = false;
        animationCompleted = false;
        IPhotoAnimation? playback = animation;
        animation = null;
        VideoFrame? owner = animationOwner;
        animationOwner = null;
        CancellationTokenSource? cancellation = animationCancellation;
        animationCancellation = null;
        cancellation?.Cancel();
        if (playback is not null)
            animationClosing = CloseAnimationAsync(animationClosing, playback, owner, cancellation);
        else
        {
            owner?.Release();
            cancellation?.Dispose();
        }
    }

    private static async Task CloseAnimationAsync(Task previous, IPhotoAnimation playback, VideoFrame? owner,
        CancellationTokenSource? cancellation)
    {
        try
        {
            await previous.ConfigureAwait(false);
            await playback.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Error("Photo.Animation", "Could not close image animation.", exception);
        }
        finally
        {
            owner?.Release();
            cancellation?.Dispose();
        }
    }
}