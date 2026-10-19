using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using LightStudio.FfmpegShim.Core;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal static class WebAudioPlaybackBackend
{
    public static void Register()
    {
        var settings = DefaultAudioOutputSettings.Instance;
        AudioOutputDeviceFactory.Register(
            (format, bufferTarget) => new WebAudioOutputDevice(format, bufferTarget, settings),
            settings: settings,
            asyncAudioOutputFactory: async (format, bufferTarget, token) =>
            {
                var device = new WebAudioOutputDevice(format, bufferTarget, settings);
                try { await device.WaitForReadyAsync(token); return device; }
                catch { await device.DisposeAsync(); throw; }
            });
    }
}

[SupportedOSPlatform("browser")]
internal sealed class WebAudioOutputDevice : IAudioOutputDevice
{
    private static readonly TimeSpan FillInterval = TimeSpan.FromMilliseconds(10);

    // The page's thread also lays out and renders the interface, and the queued sound has to outlast its stalls.
    private static readonly TimeSpan MinimumBufferTarget = TimeSpan.FromMilliseconds(750);

    private readonly CancellationTokenSource disposeCts = new();
    private readonly TimeSpan bufferTarget;
    private readonly AudioTimelineMap timeline = new();
    private readonly ConcurrentQueue<(Func<Task> Action, TaskCompletionSource Completion, CancellationToken Token)> commands = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object disposalGate = new();
    private Task? disposal;
    private Exception? pumpFailure;
    private bool pumpStopped;
    private AudioPlayoutSnapshot snapshot = AudioPlayoutSnapshot.Empty;
    private AudioOutputPullCallback? pullAudio;
    private Task? pumpTask;
    private int deviceId;
    private int deviceSampleRate;
    private long bufferedFrames;
    private long writtenFrames;
    private long playedFrames;
    private int underrunCount;
    private long trackStartFrame;
    private int reportedUnderruns;
    private float volume = 1.0f;
    private bool sourceExhausted = true;
    private bool outputActive;
    private bool sourceEndedPublished;
    private bool disposed;

    public WebAudioOutputDevice(
        AudioFormat format,
        TimeSpan bufferTarget,
        IAudioOutputSettings? settings = null)
    {
        if (format.BitsPerSample is not (16 or 24) || format.ChannelCount is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"WebAudio playback supports mono/stereo 16-bit or 24-bit PCM. Requested {format.ChannelCount} channel(s), {format.BitsPerSample}-bit.");
        }

        Format = format;
        Settings = settings ?? DefaultAudioOutputSettings.Instance;
        this.bufferTarget = bufferTarget > MinimumBufferTarget ? bufferTarget : MinimumBufferTarget;
        deviceSampleRate = checked((int)format.SampleRate);
        pumpTask = RunPumpAsync(disposeCts.Token);
    }

    public AudioFormat Format { get; }

    public IAudioOutputSettings Settings { get; }

    public int DeviceSampleRate => Volatile.Read(ref deviceSampleRate);

    public AudioOutputState State { get; private set; } = AudioOutputState.Stopped;

    public float Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0.0f, 1.0f);
            if (!disposed && deviceId != 0)
            {
                ObserveInterop(WebAudioInterop.SetVolumeAsync(deviceId, volume));
            }
        }
    }

    public TimeSpan BufferedDuration =>
        FramesToDuration(Math.Max(0, Interlocked.Read(ref writtenFrames) - GetPlayedFrames()));

    public TimeSpan PlayedDuration =>
        FramesToDuration(Math.Max(0, GetPlayedFrames() - trackStartFrame));

    public AudioTimelinePosition PlayedMediaPosition => timeline.Resolve(GetPlayedFrames());

    public AudioPlayoutSnapshot PlayoutSnapshot => Volatile.Read(ref snapshot);

    public bool SourceExhausted => sourceExhausted;

    public bool IsDrained => sourceExhausted && GetPlayedFrames() >= Interlocked.Read(ref writtenFrames);

    public int UnderrunCount => Volatile.Read(ref underrunCount);

    public event EventHandler<AudioOutputError>? BackendError;

    public event EventHandler<string>? Underrun;

    internal Task WaitForReadyAsync(CancellationToken token) => ready.Task.WaitAsync(token);

    private ValueTask EnqueueAsync(Func<Task> action, CancellationToken token = default)
    {
        lock (disposalGate)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            if (pumpStopped)
                return new ValueTask(Task.FromException(pumpFailure ?? new InvalidOperationException("WebAudio output is no longer running.")));
            if (commands.Count >= 32)
                return new ValueTask(Task.FromException(new InvalidOperationException("WebAudio command capacity exceeded.")));
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            commands.Enqueue((action, completion, token));
            return new ValueTask(completion.Task);
        }
    }

    public void Start(AudioOutputPullCallback callback) => ObserveInterop(StartAsync(callback).AsTask());
    public void Pause() => ObserveInterop(PauseAsync().AsTask());
    public void Resume() => ObserveInterop(ResumeAsync().AsTask());
    public void Stop() => ObserveInterop(StopAsync().AsTask());
    public void ResetAfterSeek() => ObserveInterop(ResetAfterSeekAsync().AsTask());
    public void Dispose() => ObserveInterop(DisposeAsync().AsTask());

    public ValueTask StartAsync(AudioOutputPullCallback callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return EnqueueAsync(async () =>
        {
            pullAudio = callback;
            sourceExhausted = false;
            trackStartFrame = Interlocked.Read(ref writtenFrames);
            sourceEndedPublished = false;
            State = AudioOutputState.Playing;
            await WebAudioInterop.SetSourceEndedAsync(deviceId, false);
            await FillBufferAsync();
            await WebAudioInterop.StartAsync(deviceId);
            outputActive = true;
        }, cancellationToken);
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default) => EnqueueAsync(async () =>
    {
        if (State != AudioOutputState.Playing) return;
        await WebAudioInterop.PauseAsync(deviceId);
        outputActive = false;
        State = AudioOutputState.Paused;
        RefreshPlaybackState();
    }, cancellationToken);

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => EnqueueAsync(async () =>
    {
        if (State != AudioOutputState.Paused) return;
        State = AudioOutputState.Playing;
        await FillBufferAsync();
        await WebAudioInterop.StartAsync(deviceId);
        outputActive = true;
    }, cancellationToken);

    public ValueTask ResetAfterSeekAsync(CancellationToken cancellationToken = default) =>
        EnqueueAsync(ResetCoreAsync, cancellationToken);

    public ValueTask StopAsync()
    {
        lock (disposalGate) return disposal is { } closing ? new ValueTask(closing) : EnqueueAsync(ResetCoreAsync);
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            disposed = true;
            return new ValueTask(disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        disposeCts.Cancel();
        try { if (pumpTask is not null) await pumpTask; }
        finally
        {
            pullAudio = null;
            outputActive = false;
            State = AudioOutputState.Stopped;
            ClearCounters();
            disposeCts.Dispose();
        }
    }

    private async Task RunPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var createdDeviceId = await WebAudioInterop.CreateAsync(
                checked((int)Format.SampleRate),
                checked((int)Format.ChannelCount));
            if (disposed || cancellationToken.IsCancellationRequested)
            {
                await WebAudioInterop.DisposeAsync(createdDeviceId);
                return;
            }

            deviceId = createdDeviceId;
            Volatile.Write(ref deviceSampleRate, await WebAudioInterop.GetSampleRateAsync(deviceId));
            await WebAudioInterop.SetVolumeAsync(deviceId, volume);
            ready.TrySetResult();

            while (!cancellationToken.IsCancellationRequested)
            {
                while (!cancellationToken.IsCancellationRequested && commands.TryDequeue(out var command))
                {
                    try
                    {
                        command.Token.ThrowIfCancellationRequested();
                        await command.Action();
                        command.Completion.TrySetResult();
                    }
                    catch (OperationCanceledException exception) { command.Completion.TrySetCanceled(exception.CancellationToken); }
                    catch (Exception exception) { command.Completion.TrySetException(exception); }
                }

                cancellationToken.ThrowIfCancellationRequested();
                RefreshPlaybackState();
                if (State == AudioOutputState.Playing)
                {
                    if (sourceEndedPublished && !sourceExhausted)
                    {
                        sourceEndedPublished = false;
                        await WebAudioInterop.SetSourceEndedAsync(deviceId, false);
                    }

                    await FillBufferAsync();
                    if (!outputActive && GetBufferedFrames() > 0)
                    {
                        await WebAudioInterop.StartAsync(deviceId);
                        outputActive = true;
                    }
                }
                else if (outputActive)
                {
                    await WebAudioInterop.PauseAsync(deviceId);
                    outputActive = false;
                }

                PublishUnderruns();
                await Task.Delay(FillInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { ready.TrySetCanceled(cancellationToken); }
        catch (Exception ex)
        {
            pumpFailure = ex;
            ready.TrySetException(ex);
            sourceExhausted = true;
            BackendError?.Invoke(this, new AudioOutputError($"WebAudio backend error: {ex.Message}", ex));
            throw;
        }
        finally
        {
            lock (disposalGate) pumpStopped = true;
            ready.TrySetException(pumpFailure ?? new ObjectDisposedException(nameof(WebAudioOutputDevice)));
            while (commands.TryDequeue(out var command))
                command.Completion.TrySetException(new ObjectDisposedException(nameof(WebAudioOutputDevice)));
            var currentDeviceId = Interlocked.Exchange(ref deviceId, 0);
            if (currentDeviceId != 0)
            {
                await WebAudioInterop.DisposeAsync(currentDeviceId);
            }
        }
    }

    private async Task FillBufferAsync()
    {
        var targetFrames = Math.Max(
            128L,
            (long)Math.Ceiling(bufferTarget.TotalSeconds * Format.SampleRate));

        int blocks = 0;
        while (!sourceExhausted && GetBufferedFrames() < targetFrames && blocks++ < 8 && !disposed)
        {
            var pull = pullAudio;
            if (pull is null)
            {
                break;
            }

            var remainingFrames = targetFrames - GetBufferedFrames();
            var requestedDuration = FramesToDuration(remainingFrames);
            AudioOutputReadResult result;
            try
            {
                result = pull(requestedDuration);
            }
            catch (Exception ex)
            {
                BackendError?.Invoke(this, new AudioOutputError($"PCM source failed: {ex.Message}", ex));
                result = AudioOutputReadResult.Complete;
            }

            if (result.Buffer.IsEmpty && !result.EndOfStream)
            {
                if (result.ByteLength > 0)
                    throw new NotSupportedException("WebAudio requires locally accessible PCM.");
                break;
            }

            // Web Audio processes float samples, so packed 24-bit PCM is expanded to Float32 in the
            // browser (queuePcm24) rather than narrowed to 16-bit, preserving the extra precision.
            Span<byte> pcm = MemoryMarshal.AsMemory(result.Buffer).Span;
            var queuedFrames = pcm.IsEmpty ? 0 : Format.BitsPerSample == 24
                ? WebAudioInterop.QueuePcm24(deviceId, pcm)
                : WebAudioInterop.QueuePcm16(deviceId, pcm);

            timeline.Append(Interlocked.Read(ref writtenFrames), queuedFrames, result);
            Interlocked.Add(ref writtenFrames, queuedFrames);
            Interlocked.Add(ref bufferedFrames, queuedFrames);
            if (result.EndOfStream)
            {
                sourceExhausted = true;
                pullAudio = null;
                sourceEndedPublished = !result.NextSourceExpected;
                await WebAudioInterop.SetSourceEndedAsync(deviceId, sourceEndedPublished);
                break;
            }
        }
    }

    private void RefreshPlaybackState()
    {
        var reading = WebAudioInterop.ReadSnapshot(deviceId);
        Interlocked.Exchange(ref bufferedFrames, Math.Max(0, (long)reading.Buffered));
        Interlocked.Exchange(ref playedFrames, Math.Max(0, (long)reading.Played));
        Volatile.Write(ref underrunCount, Math.Max(0, reading.Underruns));
        Volatile.Write(ref snapshot, new AudioPlayoutSnapshot(timeline.Resolve(GetPlayedFrames()), PlayedDuration, reading.ObservedAt));
    }

    private void PublishUnderruns()
    {
        var current = UnderrunCount;
        if (current <= reportedUnderruns)
        {
            return;
        }

        reportedUnderruns = current;
        Underrun?.Invoke(this, $"WebAudio output underrun #{current}.");
    }

    private async Task ResetCoreAsync()
    {
        pullAudio = null;
        await WebAudioInterop.ResetAsync(deviceId);
        outputActive = false;
        sourceEndedPublished = true;
        sourceExhausted = true;
        trackStartFrame = 0;
        State = AudioOutputState.Stopped;
        ClearCounters();

    }

    private void ClearCounters()
    {
        timeline.Reset();
        Interlocked.Exchange(ref bufferedFrames, 0);
        Interlocked.Exchange(ref writtenFrames, 0);
        Interlocked.Exchange(ref playedFrames, 0);
        Volatile.Write(ref underrunCount, 0);
        Volatile.Write(ref snapshot, AudioPlayoutSnapshot.Empty);
    }

    private long GetBufferedFrames() =>
        deviceId == 0 ? 0 : Interlocked.Read(ref bufferedFrames);

    private long GetPlayedFrames() =>
        deviceId == 0 ? 0 : Interlocked.Read(ref playedFrames);

    private TimeSpan FramesToDuration(long frameCount) =>
        Format.SampleRate == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)frameCount / Format.SampleRate);

    private void ObserveInterop(Task operation) => _ = ObserveInteropAsync(operation);

    private async Task ObserveInteropAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BackendError?.Invoke(this, new AudioOutputError($"WebAudio backend error: {ex.Message}", ex));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}

[SupportedOSPlatform("browser")]
internal static partial class WebAudioInterop
{
    internal static Task<int> CreateAsync(int sampleRate, int channels) => BrowserMainThread.InvokeAsync(() => Create(sampleRate, channels));
    internal static Task StartAsync(int id) => BrowserMainThread.InvokeAsync(() => Start(id));
    internal static Task PauseAsync(int id) => BrowserMainThread.InvokeAsync(() => Pause(id));
    internal static Task ResetAsync(int id) => BrowserMainThread.InvokeAsync(() => Reset(id));
    internal static Task DisposeAsync(int id) => BrowserMainThread.InvokeAsync(() => Dispose(id));
    internal static Task SetVolumeAsync(int id, float volume) => BrowserMainThread.InvokeAsync(() => SetVolume(id, volume));
    internal static Task SetSourceEndedAsync(int id, bool ended) => BrowserMainThread.InvokeAsync(() => SetSourceEnded(id, ended));
    internal static Task<int> GetSampleRateAsync(int id) => BrowserMainThread.InvokeAsync(() => GetSampleRate(id));

    /// <summary>Reads the device's buffered and played frames and its underruns, with when they were observed.</summary>
    internal static (double Buffered, double Played, int Underruns, double ObservedAt) ReadSnapshot(int id)
    {
        Span<double> values = stackalloc double[4];
        ReadSnapshot(id, values);
        return (values[0], values[1], (int)values[2], MediaTime.Now - (Now() - values[3]));
    }

    [JSImport("create", "WebAudio")]
    private static partial Task<int> Create(int sampleRate, int channelCount);

    [JSImport("isUnlocked", "WebAudio")]
    internal static partial bool IsUnlocked();

    [JSImport("start", "WebAudio")]
    private static partial Task Start(int deviceId);

    [JSImport("pause", "WebAudio")]
    private static partial Task Pause(int deviceId);

    [JSImport("reset", "WebAudio")]
    private static partial Task Reset(int deviceId);

    [JSImport("dispose", "WebAudio")]
    private static partial Task Dispose(int deviceId);

    [JSImport("setVolume", "WebAudio")]
    private static partial Task SetVolume(int deviceId, float volume);

    /// <summary>Queues 16-bit PCM and returns the frames it holds.</summary>
    [JSImport("queuePcm16", "WebAudio")]
    internal static partial int QueuePcm16(int deviceId, [JSMarshalAs<JSType.MemoryView>] Span<byte> pcm);

    /// <summary>Queues packed 24-bit PCM and returns the frames it holds.</summary>
    [JSImport("queuePcm24", "WebAudio")]
    internal static partial int QueuePcm24(int deviceId, [JSMarshalAs<JSType.MemoryView>] Span<byte> pcm);

    [JSImport("setSourceEnded", "WebAudio")]
    private static partial Task SetSourceEnded(int deviceId, bool ended);

    [JSImport("getSampleRate", "WebAudio")]
    private static partial Task<int> GetSampleRate(int deviceId);

    [JSImport("readSnapshot", "WebAudio")]
    private static partial void ReadSnapshot(int deviceId, [JSMarshalAs<JSType.MemoryView>] Span<double> destination);
    [JSImport("now", "WebAudio")]
    private static partial double Now();
}