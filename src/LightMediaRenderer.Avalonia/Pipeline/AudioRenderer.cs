using System;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Media;
using LightMediaRenderer.Avalonia.Audio;
using LightMediaRenderer.Avalonia.Diagnostics;
using LightMediaRenderer.Avalonia.Threading;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>
/// Feeds the shared audio output device from the decoded PCM queue and publishes the resulting
/// playout position as the master clock.
/// </summary>
/// <remarks>
/// <para>
/// The device pulls: <c>PullAudio</c> runs on whichever thread the backend drives it from, which on
/// some platforms is a realtime audio thread, so it consumes ready PCM without source I/O or decoding.
/// </para>
/// <para>
/// Audio is never stretched or delayed to chase video. The clock is read back from the device as
/// "which sample is reaching the speakers right now", which is exact to the backend's own byte
/// accounting and self-corrects continuously rather than once per decoded block.
/// </para>
/// </remarks>
public sealed class AudioRenderer
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromMilliseconds(15);

    private readonly IMediaFrameSource<AudioFrame> frames;
    private readonly MasterClock clock;
    private readonly StatisticsCollector statistics;
    private readonly Func<int> currentSerial;
    private readonly TimeSpan bufferTarget;
    private readonly object deviceSync = new();
    private readonly SemaphoreSlim deviceControl = new(1, 1);
    private readonly AsyncSignal wake = new();

    private IAudioOutputDevice device;
    // Handed to the device and still owned by it: backends fully consume one pulled block before
    // asking for the next, so it can be released when that next pull arrives.
    private AudioFrame? inFlight;
    private Task? monitoring;
    private CancellationTokenSource? cancellation;
    private volatile bool paused = true;
    private volatile bool sourceComplete;
    private volatile bool endOfStreamRaised;
    private volatile bool deviceFaulted;
    private AudioOutputError? pendingError;
    private float volume = 1.0f;

    public AudioRenderer(
        IMediaFrameSource<AudioFrame> frames,
        IAudioOutputDevice device,
        MasterClock clock,
        StatisticsCollector statistics,
        Func<int> currentSerial,
        TimeSpan bufferTarget)
    {
        this.frames = frames;
        this.device = device;
        this.clock = clock;
        this.statistics = statistics;
        this.currentSerial = currentSerial;
        this.bufferTarget = bufferTarget;
        device.BackendError += OnBackendError;
    }

    public IAudioOutputDevice Device
    {
        get { lock (deviceSync) return device; }
    }

    /// <summary>Linear gain, 0..1. Applied by the backend, which knows how its endpoint scales.</summary>
    public float Volume
    {
        get => volume;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            volume = clamped;
            Device.Volume = clamped;
        }
    }

    public TimeSpan Latency => Device.BufferedDuration;

    public int UnderrunCount => Device.UnderrunCount;

    public bool IsWaitingForPlayback => !endOfStreamRaised &&
        (clock.Audio.Serial != currentSerial() || !MediaTime.IsValid(clock.Audio.Get()));

    public event EventHandler? EndOfStream;

    public event EventHandler<string>? DeviceFailed;

    public event EventHandler? ClockChanged;

    public void Start()
    {
        if (monitoring is not null)
            return;

        cancellation = new CancellationTokenSource();
        monitoring = MonitorLoopAsync(cancellation.Token);
    }

    public void Stop() => Observe(StopAsync());

    public async Task StopAsync()
    {
        paused = true;
        cancellation?.Cancel();
        wake.Pulse();
        if (monitoring is not null)
        {
            try { await monitoring; }
            catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { }
        }
        monitoring = null;
        cancellation?.Dispose();
        cancellation = null;

        await deviceControl.WaitAsync();
        try
        {
            await Device.StopAsync();
            ReleaseInFlight();
        }
        finally { deviceControl.Release(); }
    }

    public void Pause() => Hold();

    public void Resume()
    {
        sourceComplete = false;
        endOfStreamRaised = false;
        Release();
    }

    /// <summary>
    /// Stops the device and the clock updates without touching end-of-stream state, so playback can
    /// be held back briefly (a video resync) and picked up exactly where it left off.
    /// </summary>
    public void Hold() => Observe(HoldAsync());

    public async Task HoldAsync()
    {
        paused = true;
        await deviceControl.WaitAsync();
        try { await Device.PauseAsync(); }
        finally { deviceControl.Release(); }
    }

    public void Release() => Observe(ReleaseAsync());

    public async Task ReleaseAsync()
    {
        paused = false;
        await deviceControl.WaitAsync();
        try
        {
            if (!paused)
                await StartOrResumeDeviceAsync();
        }
        finally { deviceControl.Release(); }
    }

    /// <summary>Drops audio already handed to the device, so a seek is not preceded by stale sound.</summary>
    public void FlushDevice() => Observe(FlushDeviceAsync());

    public async Task FlushDeviceAsync()
    {
        await deviceControl.WaitAsync();
        try
        {
            await Device.ResetAfterSeekAsync();
            ReleaseInFlight();
            sourceComplete = false;
            endOfStreamRaised = false;
            if (!paused)
                await StartOrResumeDeviceAsync();
        }
        finally { deviceControl.Release(); }
    }

    private async Task StartOrResumeDeviceAsync()
    {
        IAudioOutputDevice current = Device;
        try
        {
            if (current.State == AudioOutputState.Paused)
                await current.ResumeAsync();
            else if (current.State == AudioOutputState.Stopped)
                await current.StartAsync(PullAudio);
        }
        catch (Exception exception)
        {
            ReportDeviceFailure(exception.Message, exception);
        }
    }

    private AudioOutputReadResult PullAudio(TimeSpan requestedDuration)
    {
        ReleaseInFlight();

        for (int discarded = 0; discarded < 64; discarded++)
        {
            if (!frames.TryRead(out AudioFrame frame))
            {
                // Nothing decoded yet: an empty, non-final result lets the backend account for the
                // gap as an underrun and ask again instead of ending the stream.
                return default;
            }

            if (frame.Serial != currentSerial())
            {
                frame.Recycle();
                continue;
            }

            if (frame.IsEndOfStream)
            {
                frame.Recycle();
                sourceComplete = true;
                wake.Pulse();
                return AudioOutputReadResult.Complete;
            }

            if (frame.Length == 0)
            {
                frame.Recycle();
                continue;
            }

            inFlight = frame;
            var duration = TimeSpan.FromSeconds(Math.Max(0, frame.Duration));
            return new AudioOutputReadResult(frame.Buffer, duration, false,
                MediaPosition: MediaTime.IsValid(frame.Pts) ? TimeSpan.FromSeconds(frame.Pts) : null,
                Serial: frame.Serial, Resource: frame.Resource?.Handle, ResourceByteLength: frame.Length);
        }
        return default;
    }

    private void ReleaseInFlight()
    {
        AudioFrame? previous = Interlocked.Exchange(ref inFlight, null);
        previous?.Recycle();
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int version = wake.Version;
            try
            {
                if (Interlocked.Exchange(ref pendingError, null) is { } error)
                    ReportDeviceFailure(error.Message, error.Cause);
                if (deviceFaulted)
                    await SwapInSilentDeviceAsync();

                IAudioOutputDevice current = Device;
                AudioPlayoutSnapshot snapshot = current.PlayoutSnapshot;
                AudioTimelinePosition position = snapshot.Position;
                if (position.HasValue && position.Serial == currentSerial() &&
                    snapshot.PlayedDuration > TimeSpan.Zero && !paused)
                {
                    clock.Audio.SetAt(position.MediaPosition.TotalSeconds, position.Serial, snapshot.ObservedAt);
                    ClockChanged?.Invoke(this, EventArgs.Empty);
                }

                if (sourceComplete && !endOfStreamRaised && current.IsDrained)
                {
                    endOfStreamRaised = true;
                    paused = true;
                    EndOfStream?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception exception)
            {
                ReportDeviceFailure(exception.Message, exception);
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            await Task.WhenAny(wake.WaitAsync(version, wait.Token), Task.Delay(MonitorInterval, wait.Token));
            wait.Cancel();
        }
    }

    private void OnBackendError(object? sender, AudioOutputError error)
    {
        deviceFaulted = true;
        Interlocked.Exchange(ref pendingError, error);
        wake.Pulse();
    }

    private void ReportDeviceFailure(string message, Exception? exception = null)
    {
        AppLog.Warning("Audio.Output", message, exception);
        DeviceFailed?.Invoke(this, message);
    }

    /// <summary>
    /// Degrades to silent playback: the timeline keeps advancing and the user keeps their video
    /// instead of the session dying with the audio device.
    /// </summary>
    private async Task SwapInSilentDeviceAsync()
    {
        await deviceControl.WaitAsync();
        try
        {
            deviceFaulted = false;
            IAudioOutputDevice failed = Device;
            if (failed is NullAudioOutputDevice)
                return;
            failed.BackendError -= OnBackendError;
            try { await failed.StopAsync(); }
            finally { await failed.DisposeAsync(); }
            ReleaseInFlight();
            var replacement = new NullAudioOutputDevice(failed.Format, bufferTarget) { Volume = volume };
            lock (deviceSync)
                device = replacement;
            if (!paused)
                await StartOrResumeDeviceAsync();
        }
        finally { deviceControl.Release(); }
    }

    private static async void Observe(Task operation)
    {
        try { await operation; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppLog.Error("Audio.Output", "An audio control operation failed.", exception);
        }
    }
}
