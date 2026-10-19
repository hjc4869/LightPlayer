using System;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Media;
using LightMediaRenderer.Avalonia.Diagnostics;
using LightMediaRenderer.Avalonia.Rendering;
using LightMediaRenderer.Avalonia.Threading;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Pipeline;

public sealed record VideoSyncOptions
{
    public static VideoSyncOptions Default { get; } = new();

    /// <summary>Frames whose PTS is within this of the master clock are shown immediately.</summary>
    public double SyncToleranceSeconds { get; init; } = 0.01;

    /// <summary>Longest single wait, keeping pause and seek cancellation responsive.</summary>
    public double MaximumSleepSeconds { get; init; } = 0.02;

    /// <summary>Frames later than this are counted as late (still shown if nothing newer exists).</summary>
    public double LateThresholdSeconds { get; init; } = 0.04;

    /// <summary>Frames later than this are dropped when a newer frame is already decoded.</summary>
    public double DropThresholdSeconds { get; init; } = 0.1;

    /// <summary>Video later than this halts the master clock until the picture has caught up.</summary>
    public double ResyncThresholdSeconds { get; init; } = 1.0;

    /// <summary>
    /// Abandons a resync once the video has gone this long without closing any of the gap: the
    /// decoder is not merely slow but stuck, and waiting on it would only silence playback.
    /// </summary>
    public double ResyncProgressTimeoutSeconds { get; init; } = 0.5;

    /// <summary>Backstop on a single resync, for a decoder that crawls forward without catching up.</summary>
    public double MaximumResyncHoldSeconds { get; init; } = 5.0;

    /// <summary>Quiet period after a resync before the next one may start.</summary>
    public double ResyncCooldownSeconds { get; init; } = 0.5;

    /// <summary>
    /// Holds a due frame until a sink implementing <see cref="IVideoDrawFeedback"/> has taken the previous
    /// one for drawing, so a frame presented late does not land in the same render as the next one and go
    /// unseen. In a browser, presentation timers and rendering share one thread, which makes that common.
    /// </summary>
    public bool PaceToDraws { get; init; } = OperatingSystem.IsBrowser();

    /// <summary>
    /// Longest a due frame waits for the previous one to be drawn. A frame later than its own duration is
    /// presented without waiting.
    /// </summary>
    public double MaximumDrawWaitSeconds { get; init; } = 0.05;
}

/// <summary>
/// Schedules video presentation cooperatively: decides <em>when</em> each decoded frame is shown by
/// comparing its PTS against the master clock, and hands it to the sink at that moment.
/// </summary>
/// <remarks>
/// Presentation is driven entirely by timestamps, never by an assumed frame rate, so variable frame
/// rate content is handled without special casing. Early frames wait in short slices (so a seek or
/// pause is still responsive), and frames that are hopelessly late are dropped rather than shown in
/// a burst -- but only when a newer frame is already available, so a slow decoder never blanks the
/// picture.
/// </remarks>
public sealed class VideoScheduler
{
    private readonly IMediaFrameSource<VideoFrame> frames;
    private readonly MasterClock clock;
    private readonly StatisticsCollector statistics;
    private readonly VideoSyncOptions options;
    private readonly Func<int> currentSerial;
    private readonly AsyncSignal wake = new();
    private readonly object frameSync = new();

    private Task? running;
    private CancellationTokenSource? cancellation;
    private IDisposable? highResolutionTimerPeriod;
    private VideoFrame? lastPresented;
    private Action? stepContinuation;
    private volatile bool paused = true;
    private int bufferGeneration = 1;
    private int bufferedGeneration;
    private int stepRequests;
    private int sinkFailureReported;
    private int controlGeneration;
    private volatile bool resyncHeld;
    private double resyncStarted;
    private double resyncEnded = double.NegativeInfinity;
    private double resyncProgressAt;
    private double resyncBestLead;
    private (int Serial, double Pts) drawWaitFrame = (-1, double.NaN);
    private double drawWaitStarted;

    public VideoScheduler(
        IMediaFrameSource<VideoFrame> frames,
        MasterClock clock,
        StatisticsCollector statistics,
        Func<int> currentSerial,
        VideoSyncOptions? options = null)
    {
        this.frames = frames;
        this.clock = clock;
        this.statistics = statistics;
        this.currentSerial = currentSerial;
        this.options = options ?? VideoSyncOptions.Default;
    }

    /// <summary>Destination for frames that are due. May be swapped while playing.</summary>
    public IVideoSink? Sink { get; set; }

    /// <summary>
    /// Stops (<c>true</c>) and restarts (<c>false</c>) the master clock and the audio device on
    /// behalf of buffering or a resync.
    /// </summary>
    public Action<bool>? HoldClock { get; set; }

    public Func<bool, Task>? HoldClockAsync { get; set; }

    public bool IsBuffering =>
        Volatile.Read(ref bufferGeneration) != Volatile.Read(ref bufferedGeneration) || resyncHeld;

    /// <summary>Reachable queue depth after seconds without growth, or zero when holding blocks input.</summary>
    public Func<double, int>? GetBufferCapacity { get; set; }

    public Func<bool>? IsAudioPlaybackPending { get; set; }

    /// <summary>Raised on the owner context once the end-of-stream marker has been consumed.</summary>
    public event EventHandler? EndOfStream;

    public event EventHandler<Exception>? Failed;

    public void Start()
    {
        if (running is not null)
            return;

        cancellation = new CancellationTokenSource();
        running = RunGuardedAsync(cancellation.Token);
    }

    public void Stop() => Observe(StopAsync());

    public async Task StopAsync()
    {
        cancellation?.Cancel();
        wake.Pulse();
        if (running is not null)
        {
            try { await running; }
            catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { }
        }
        running = null;
        cancellation?.Dispose();
        cancellation = null;
        ReleaseHighResolutionTimerPeriod();
        ReleaseLastPresented();
    }

    public void Pause()
    {
        Interlocked.Increment(ref controlGeneration);
        paused = true;
        ReleaseHighResolutionTimerPeriod();
        wake.Pulse();
    }

    public void Resume() => Observe(ResumeAsync());

    public async Task ResumeAsync()
    {
        int generation = Interlocked.Increment(ref controlGeneration);
        RequestHighResolutionTimerPeriod();
        await SetHoldAsync(IsBuffering);
        if (cancellation?.IsCancellationRequested == true || generation != Volatile.Read(ref controlGeneration))
            return;
        paused = false;
        wake.Pulse();
    }

    public void RequireBuffering()
    {
        Interlocked.Increment(ref bufferGeneration);
        wake.Pulse();
    }

    /// <summary>Presents exactly one more frame while paused.</summary>
    /// <param name="onPresented">
    /// Invoked on the owner context once the frame has reached the sink, so a seek can be
    /// completed only after its picture is actually on screen. Replaces any continuation still
    /// outstanding from an earlier step.
    /// </param>
    public void StepFrame(Action? onPresented = null)
    {
        Interlocked.Exchange(ref stepContinuation, onPresented);
        Interlocked.Exchange(ref stepRequests, 1);
        wake.Pulse();
    }

    /// <summary>
    /// The frame currently on screen, with an extra reference for the caller (screenshots).
    /// The caller must <see cref="VideoFrame.Release"/> it.
    /// </summary>
    public VideoFrame? AcquireLastPresentedFrame()
    {
        lock (frameSync)
            return lastPresented?.AddRef();
    }

    public void NotifyClockChanged() => wake.Pulse();

    private async Task RunGuardedAsync(CancellationToken token)
    {
        try { await RunAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            AppLog.Error("Video.Scheduling", "The video scheduler failed.", exception);
            Failed?.Invoke(this, exception);
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        bool bufferBypassed = false;
        int observedGeneration = 0;
        int bufferedSerial = -1;
        int bufferCapacity = int.MaxValue;
        int bufferedCount = 0;
        double bufferProgressAt = MediaTime.Now;
        int iterations = 0;
        while (!token.IsCancellationRequested)
        {
            if (++iterations >= 16)
            {
                iterations = 0;
                await Task.Delay(TimeSpan.FromMilliseconds(1), token);
            }
            int frameVersion = frames.Version;
            int wakeVersion = wake.Version;
            bool stepping = false;
            if (paused)
            {
                // The request stands until a frame has actually been shown: right after a seek the
                // queue is empty and the decoder flushes it once more when it picks up the new
                // serial, and a step consumed in between would leave a stale picture on screen.
                if (Volatile.Read(ref stepRequests) > 0)
                {
                    stepping = true;
                }
                else
                {
                    await EndResyncAsync();
                    await WaitAsync(frameVersion, wakeVersion, null, token);
                    continue;
                }
            }

            int generation = Volatile.Read(ref bufferGeneration);
            if (!stepping && generation != Volatile.Read(ref bufferedGeneration))
            {
                await SetHoldAsync(true);
                int serial = currentSerial();
                if (serial != bufferedSerial)
                {
                    bufferedSerial = serial;
                    bufferCapacity = int.MaxValue;
                }

                int count = frames.Count;
                if (generation != observedGeneration || count != bufferedCount)
                {
                    observedGeneration = generation;
                    bufferedCount = count;
                    bufferProgressAt = MediaTime.Now;
                }

                int availableCapacity = GetBufferCapacity?.Invoke(MediaTime.Now - bufferProgressAt) ?? int.MaxValue;
                bufferBypassed = availableCapacity == 0;
                if (availableCapacity > 0)
                    bufferCapacity = Math.Min(bufferCapacity, availableCapacity);

                if (!frames.IsFull && count < bufferCapacity && !bufferBypassed &&
                    !frames.ContainsEndOfStream(currentSerial()))
                {
                    await WaitAsync(frameVersion, wakeVersion, 0.05, token);
                    continue;
                }

                Volatile.Write(ref bufferedGeneration, generation);
                await SetHoldAsync(IsBuffering);
            }

            if (!frames.TryPeek(out VideoFrame frame))
            {
                if (resyncHeld)
                    await CheckResyncStallAsync();
                else if (!stepping && !bufferBypassed && IsNextFrameDue())
                    RequireBuffering();

                await WaitAsync(frameVersion, wakeVersion, options.MaximumSleepSeconds, token);
                continue;
            }

            if (frame.Serial != currentSerial())
            {
                // Left over from before the last seek.
                frames.DropHead(frame);
                continue;
            }

            bufferBypassed = false;
            if (frame.IsEndOfStream)
            {
                frames.DropHead(frame);

                // No picture will ever arrive for a step that is still waiting; release it so a
                // seek past the last frame does not leave the caller parked forever.
                CompleteStep();
                await EndResyncAsync();
                paused = true;
                ReleaseHighResolutionTimerPeriod();
                EndOfStream?.Invoke(this, EventArgs.Empty);
                continue;
            }

            if (stepping)
            {
                PresentAndDrop(frame);
                continue;
            }

            if (IsAudioPlaybackPending?.Invoke() == true)
            {
                frame.Release();
                await WaitAsync(frameVersion, wakeVersion, options.MaximumSleepSeconds, token);
                continue;
            }

            double master = clock.Get();
            if (!MediaTime.IsValid(master))
            {
                // No timeline yet (first frame after open or seek): show it and let it anchor the
                // clock, otherwise nothing would ever start.
                PresentAndDrop(frame);
                continue;
            }

            double lead = (frame.Pts - master) / Math.Max(0.01, clock.Speed);
            statistics.VideoLatency = lead;
            int control = Volatile.Read(ref controlGeneration);
            try { await UpdateResyncAsync(lead); }
            catch
            {
                frame.Release();
                throw;
            }
            if (paused || token.IsCancellationRequested || frame.Serial != currentSerial() || control != Volatile.Read(ref controlGeneration))
            {
                frame.Release();
                continue;
            }
            if (!resyncHeld && IsBuffering)
            {
                frame.Release();
                continue;
            }

            double delay = GetPresentationDelay(lead, options);
            if (delay > 0)
            {
                frame.Release();
                // Stop at the leading edge of the tolerance window rather than sleeping all the
                // way to the PTS. The remaining lead is the UI/compositor's time to commit the new
                // draw before its intended vsync; consuming it here makes that headroom depend on
                // the frame rate and on how the maximum wait slices happen to divide the interval.
                await WaitAsync(frameVersion, wakeVersion, delay, token);
                continue;
            }

            if (lead < -options.DropThresholdSeconds && frames.Count > 1)
            {
                statistics.CountDroppedVideoFrame();
                frames.DropHead(frame);
                continue;
            }

            if (Sink is IVideoDrawFeedback feedback && ShouldAwaitDraw(feedback, frame, lead))
            {
                frame.Release();
                await WaitForDrawAsync(feedback, wakeVersion, token);
                continue;
            }

            if (lead < -options.LateThresholdSeconds)
                statistics.CountLateVideoFrame();

            PresentAndDrop(frame, lead);
        }
    }

    private bool ShouldAwaitDraw(IVideoDrawFeedback feedback, VideoFrame frame, double lead)
    {
        if (!options.PaceToDraws || !feedback.IsDrawPending)
            return false;

        double interval = (frame.Duration > 0 ? frame.Duration : 1.0 / 60) / Math.Max(0.01, clock.Speed);
        if (lead < -interval)
            return false;

        double now = MediaTime.Now;
        if (drawWaitFrame != (frame.Serial, frame.Pts))
        {
            drawWaitFrame = (frame.Serial, frame.Pts);
            drawWaitStarted = now;
        }

        return now - drawWaitStarted < options.MaximumDrawWaitSeconds;
    }

    private async Task WaitForDrawAsync(IVideoDrawFeedback feedback, int wakeVersion, CancellationToken token)
    {
        double remaining = options.MaximumDrawWaitSeconds - (MediaTime.Now - drawWaitStarted);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task completed = await Task.WhenAny(
            feedback.WaitForDrawAsync(wait.Token),
            wake.WaitAsync(wakeVersion, wait.Token),
            Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining * 1000))), wait.Token));
        wait.Cancel();
        token.ThrowIfCancellationRequested();
        if (!completed.IsCanceled)
            await completed;
    }

    internal static double GetPresentationDelay(double lead, VideoSyncOptions options)
    {
        double untilPresentationWindow = lead - options.SyncToleranceSeconds;
        return untilPresentationWindow > 0
            ? Math.Min(untilPresentationWindow, options.MaximumSleepSeconds)
            : 0;
    }

    private bool IsNextFrameDue()
    {
        lock (frameSync)
        {
            if (lastPresented is not { Duration: > 0 } frame || frame.Serial != currentSerial())
                return true;

            double master = clock.Get();
            return !MediaTime.IsValid(master) ||
                frame.Pts + frame.Duration <= master + options.SyncToleranceSeconds * clock.Speed;
        }
    }

    private void RequestHighResolutionTimerPeriod()
    {
        IDisposable request = MediaTime.RequestHighResolutionTimerPeriod();
        if (Interlocked.CompareExchange(ref highResolutionTimerPeriod, request, null) is not null)
            request.Dispose();
    }

    private void ReleaseHighResolutionTimerPeriod() =>
        Interlocked.Exchange(ref highResolutionTimerPeriod, null)?.Dispose();

    /// <param name="lead">
    /// Wall-clock seconds still to run until the frame's PTS. Frames are shown up to the sync
    /// tolerance early, and anchoring the clock at the moment of handover would credit the timeline
    /// with that lead, shortening every frame interval by it once video is the master clock.
    /// </param>
    private void PresentAndDrop(VideoFrame frame, double lead = 0)
    {
        clock.Video.SetAt(frame.Pts, frame.Serial, MediaTime.Now + Math.Max(0, lead));
        statistics.AudioVideoDrift = frame.Pts - clock.Get();

        VideoFrame? previous;
        lock (frameSync)
        {
            previous = lastPresented;
            lastPresented = frame.AddRef();
        }

        previous?.Release();

        try
        {
            Sink?.Present(frame);
            Volatile.Write(ref sinkFailureReported, 0);
        }
        catch (Exception exception)
        {
            // A failing sink must not tear down playback; the next frame gets another chance.
            if (Interlocked.Exchange(ref sinkFailureReported, 1) == 0)
                AppLog.Error("Video.Rendering", "The video sink failed to present a frame.", exception);
        }

        statistics.CountPresentedVideoFrame();
        frames.DropHead(frame);
        CompleteStep();
    }

    private void CompleteStep()
    {
        if (Interlocked.Exchange(ref stepRequests, 0) == 0)
            return;

        Interlocked.Exchange(ref stepContinuation, null)?.Invoke();
    }

    /// <summary>
    /// Stops the clock once video has fallen further behind than dropping frames could ever make
    /// up, and starts it again once the picture is back in step. With the clock stopped the queue is
    /// drained against a frozen timeline, so every frame decoded closes part of the gap.
    /// </summary>
    /// <remarks>
    /// The hold lasts as long as the gap keeps closing rather than for a fixed time: catching up a
    /// second of video takes about a second of decoding, and releasing before that only leaves the
    /// drift to grow again from where it was. It is abandoned when the video stops gaining ground,
    /// which is the case a timer would otherwise have to guess at, so a stuck decoder cannot hold
    /// the sound indefinitely.
    /// </remarks>
    private async Task UpdateResyncAsync(double lead)
    {
        if (HoldClock is null && HoldClockAsync is null)
            return;

        double now = MediaTime.Now;
        if (resyncHeld)
        {
            if (lead >= -options.SyncToleranceSeconds)
            {
                RequireBuffering();
                await EndResyncAsync();
            }
            else if (now - resyncStarted >= options.MaximumResyncHoldSeconds)
            {
                await EndResyncAsync();
            }
            else if (lead > resyncBestLead + 1e-4)
            {
                resyncBestLead = lead;
                resyncProgressAt = now;
            }
            else if (now - resyncProgressAt >= options.ResyncProgressTimeoutSeconds)
            {
                await EndResyncAsync();
            }

            return;
        }

        if (lead >= -options.ResyncThresholdSeconds || now - resyncEnded < options.ResyncCooldownSeconds)
            return;

        resyncHeld = true;
        resyncStarted = now;
        resyncProgressAt = now;
        resyncBestLead = lead;
        await SetHoldAsync(true);
    }

    /// <summary>Ends a hold the empty queue has stopped feeding.</summary>
    private async Task CheckResyncStallAsync()
    {
        if (MediaTime.Now - resyncProgressAt >= options.ResyncProgressTimeoutSeconds)
            await EndResyncAsync();
    }

    private async Task EndResyncAsync()
    {
        if (!resyncHeld)
            return;

        resyncHeld = false;
        resyncEnded = MediaTime.Now;
        await SetHoldAsync(IsBuffering);
    }

    private Task SetHoldAsync(bool hold)
    {
        if (HoldClockAsync is { } callback)
            return callback(hold);
        HoldClock?.Invoke(hold);
        return Task.CompletedTask;
    }

    private async Task WaitAsync(int frameVersion, int wakeVersion, double? delay, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task available = frames.WaitForChangeAsync(frameVersion, wait.Token);
        Task control = wake.WaitAsync(wakeVersion, wait.Token);
        Task completed = delay is { } seconds
            ? await Task.WhenAny(available, control, Task.Delay(
                TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(seconds * 1000))), wait.Token))
            : await Task.WhenAny(available, control);
        wait.Cancel();
        token.ThrowIfCancellationRequested();
        if (!completed.IsCanceled)
            await completed;
    }

    private static async void Observe(Task operation)
    {
        try { await operation; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppLog.Error("Video.Scheduling", "A video scheduling operation failed.", exception);
        }
    }

    private void ReleaseLastPresented()
    {
        VideoFrame? previous;
        lock (frameSync)
        {
            previous = lastPresented;
            lastPresented = null;
        }

        previous?.Release();
    }
}
