using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AudioToolbox;
using AVFoundation;
using LightMediaRenderer.Avalonia.Audio;
using LightStudio.FfmpegShim.Core;
using LightStudio.LightPlayer.Desktop.Apple;
using LightStudio.Logging;
using AudioFormat = LightStudio.FfmpegShim.AudioFormat;

namespace LightStudio.LightPlayer.iOS.Playback;

internal sealed class AudioQueueOutputDevice : IAudioOutputDevice
{
    private readonly object disposalGate = new();
    private readonly PlaybackThread playbackThread;
    private readonly ConcurrentQueue<nint> available = new();
    private readonly HashSet<nint> queued = new();
    private readonly nint[] buffers = new nint[3];
    private readonly AudioTimelineMap timeline = new();
    private readonly int bufferSize;
    private OutputAudioQueue? queue;
    private AudioPlayoutSnapshot playoutSnapshot = AudioPlayoutSnapshot.Empty;
    private Task? disposal;
    private AudioOutputPullCallback? pullAudio;
    private ReadOnlyMemory<byte> pending;
    private bool pendingEndsSource;
    private bool nextSourceExpected;
    private volatile bool exhausted = true;
    private volatile bool drained = true;
    private bool running;
    private bool draining;
    private volatile bool disposed;
    private float volume = 1;
    private int deviceSampleRate;
    private long submitted;
    private long played;
    private double playedObservedAt;
    private long trackStart;
    private int underruns;
    private int stateValue = (int)AudioOutputState.Stopped;

    public AudioQueueOutputDevice(AudioFormat format, TimeSpan bufferTarget)
        : this(format, bufferTarget, initialize: true)
    {
    }

    private AudioQueueOutputDevice(AudioFormat format, TimeSpan bufferTarget, bool initialize)
    {
        if (format.BitsPerSample is not (16 or 24) || format.ChannelCount is < 1 or > 2)
            throw new NotSupportedException("iOS audio output requires mono/stereo 16-bit or 24-bit PCM.");

        Format = format;
        bufferSize = format.AlignByteCount((int)(format.ByteRate *
            Math.Clamp(bufferTarget.TotalSeconds / buffers.Length, 0.02, 0.1)));
        playbackThread = new PlaybackThread("LightPlayer.AudioQueue");
        if (!initialize) return;

        try
        {
            playbackThread.Invoke(() => InitializeCore(CancellationToken.None));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static async ValueTask<IAudioOutputDevice> CreateAsync(
        AudioFormat format, TimeSpan bufferTarget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var device = new AudioQueueOutputDevice(format, bufferTarget, initialize: false);
        try
        {
            await device.playbackThread.InvokeAsync(
                () => device.InitializeCore(cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return device;
        }
        catch
        {
            await device.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private unsafe void InitializeCore(CancellationToken cancellationToken)
    {
        var session = AVAudioSession.SharedInstance();
        if (session.SetCategory(AVAudioSessionCategory.Playback) is { } categoryError)
            throw new InvalidOperationException($"Could not configure the audio session: {categoryError}");
        if (!session.SetActive(true, out var activationError))
            throw new InvalidOperationException($"Could not activate the audio session: {activationError}");

        cancellationToken.ThrowIfCancellationRequested();
        queue = new OutputAudioQueue(new AudioStreamBasicDescription
        {
            SampleRate = Format.SampleRate,
            Format = AudioFormatType.LinearPCM,
            FormatFlags = AudioFormatFlags.LinearPCMIsSignedInteger | AudioFormatFlags.LinearPCMIsPacked,
            BytesPerPacket = Format.BlockAlign,
            FramesPerPacket = 1,
            BytesPerFrame = Format.BlockAlign,
            ChannelsPerFrame = (int)Format.ChannelCount,
            BitsPerChannel = (int)Format.BitsPerSample,
        });
        queue.BufferCompleted += (_, args) => available.Enqueue(args.IntPtrBuffer);
        for (var index = 0; index < buffers.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(queue.AllocateBuffer(bufferSize, out AudioQueueBuffer* buffer));
            buffers[index] = (nint)buffer;
            available.Enqueue(buffers[index]);
        }
        Volatile.Write(ref deviceSampleRate, (int)session.SampleRate);
        playbackThread.StartPeriodic(PumpSafely, TimeSpan.FromMilliseconds(10));
    }

    public AudioFormat Format { get; }
    public IAudioOutputSettings Settings => DefaultAudioOutputSettings.Instance;
    public int DeviceSampleRate => Volatile.Read(ref deviceSampleRate);
    public AudioOutputState State => (AudioOutputState)Volatile.Read(ref stateValue);
    public bool SourceExhausted => exhausted;
    public bool IsDrained => drained;
    public int UnderrunCount => Volatile.Read(ref underruns);
    public TimeSpan BufferedDuration => Format.GetDuration(Math.Max(0, Interlocked.Read(ref submitted) - Interlocked.Read(ref played)));
    public TimeSpan PlayedDuration => PlayoutSnapshot.PlayedDuration;
    public AudioTimelinePosition PlayedMediaPosition => PlayoutSnapshot.Position;
    public AudioPlayoutSnapshot PlayoutSnapshot => Volatile.Read(ref playoutSnapshot);

    public float Volume
    {
        get => Volatile.Read(ref volume);
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Interlocked.Exchange(ref volume, clamped) == clamped || disposed) return;
            try
            {
                playbackThread.Post(() =>
                {
                    if (!disposed && queue is not null) queue.Volume = Volatile.Read(ref volume);
                });
            }
            catch (ObjectDisposedException) when (disposed)
            {
            }
        }
    }

    public event EventHandler<AudioOutputError>? BackendError;
    public event EventHandler<string>? Underrun;

    public void Start(AudioOutputPullCallback pullAudioCallback)
    {
        ArgumentNullException.ThrowIfNull(pullAudioCallback);
        ThrowIfDisposed();
        playbackThread.Invoke(() =>
        {
            ThrowIfDisposed();
            if (draining) ResetCore();
            pullAudio = pullAudioCallback;
            pending = default;
            pendingEndsSource = false;
            nextSourceExpected = false;
            exhausted = false;
            drained = false;
            trackStart = submitted;
            SetState(AudioOutputState.Playing);
            PumpCore();
        });
    }

    public void Pause()
    {
        ThrowIfDisposed();
        playbackThread.Invoke(() =>
        {
            ThrowIfDisposed();
            if (State != AudioOutputState.Playing) return;
            Check(queue!.Pause());
            UpdatePlayedCore();
            running = false;
            SetState(AudioOutputState.Paused);
            PublishPlayoutSnapshot();
        });
    }

    public void Resume()
    {
        ThrowIfDisposed();
        playbackThread.Invoke(() =>
        {
            ThrowIfDisposed();
            if (State != AudioOutputState.Paused) return;
            draining = false;
            SetState(AudioOutputState.Playing);
            PumpCore();
        });
    }

    public void Stop()
    {
        if (disposed) return;
        try
        {
            playbackThread.Invoke(ResetCore);
        }
        catch (ObjectDisposedException) when (disposed)
        {
        }
    }

    public void ResetAfterSeek()
    {
        ThrowIfDisposed();
        playbackThread.Invoke(ResetCore);
    }

    public void Dispose()
    {
        var completion = DisposeAsync();
        if (!playbackThread.IsOnThread) completion.GetAwaiter().GetResult();
    }

    public ValueTask StartAsync(AudioOutputPullCallback pullAudioCallback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pullAudioCallback);
        ThrowIfDisposed();
        return new ValueTask(playbackThread.InvokeAsync(() => Start(pullAudioCallback), cancellationToken));
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
        new(playbackThread.InvokeAsync(Pause, cancellationToken));

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) =>
        new(playbackThread.InvokeAsync(Resume, cancellationToken));

    public ValueTask StopAsync()
    {
        lock (disposalGate)
        {
            return new ValueTask(disposal ?? playbackThread.InvokeAsync(ResetCore));
        }
    }

    public ValueTask ResetAfterSeekAsync(CancellationToken cancellationToken = default) =>
        new(playbackThread.InvokeAsync(ResetAfterSeek, cancellationToken));

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            return new ValueTask(disposal ??= DisposeOutputAsync());
        }
    }

    private async Task DisposeOutputAsync()
    {
        disposed = true;
        try
        {
            await playbackThread.InvokeAsync(() =>
            {
                try { ResetCore(); }
                finally
                {
                    queue?.Dispose();
                    queue = null;
                    available.Clear();
                    ClearPlaybackCore();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await playbackThread.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ResetCore()
    {
        SetState(AudioOutputState.Stopped);
        if (queue is not null)
        {
            Check(queue.Stop(true));
            Check(queue.Reset());
        }
        available.Clear();
        foreach (var buffer in buffers)
            if (buffer != 0) available.Enqueue(buffer);
        ClearPlaybackCore();
    }

    private void ClearPlaybackCore()
    {
        queued.Clear();
        pending = default;
        pullAudio = null;
        exhausted = true;
        drained = true;
        pendingEndsSource = nextSourceExpected = running = draining = false;
        submitted = played = trackStart = 0;
        playedObservedAt = 0;
        timeline.Reset();
        Volatile.Write(ref playoutSnapshot, AudioPlayoutSnapshot.Empty);
    }

    private void PumpSafely()
    {
        try
        {
            PumpCore();
        }
        catch (Exception exception)
        {
            AppLog.Error("iOS.Audio", "Audio Queue playback failed.", exception);
            SetState(AudioOutputState.Stopped);
            BackendError?.Invoke(this, new AudioOutputError("iOS audio output failed.", exception));
        }
    }

    private void PumpCore()
    {
        if (disposed || drained || State != AudioOutputState.Playing || queue is null) return;
        var availableCount = available.Count;
        for (var index = 0; index < availableCount && available.TryDequeue(out var buffer); index++)
        {
            queued.Remove(buffer);
            if (!FillBuffer(buffer)) available.Enqueue(buffer);
        }

        if (!running && (queued.Count > 0 || submitted > played))
        {
            Check(queue.Start());
            running = true;
        }
        if (exhausted && !nextSourceExpected && !draining)
        {
            if (running)
            {
                Check(queue.Stop(false));
                draining = true;
            }
            else drained = queued.Count == 0;
        }
        UpdatePlayedCore();
        if (draining && !queue.IsRunning)
        {
            running = false;
            drained = true;
            played = submitted;
            playedObservedAt = MediaTime.Now;
        }
        else if (exhausted && nextSourceExpected)
        {
            drained = played >= submitted;
        }
        if (queued.Count == 0 && !exhausted && running)
        {
            running = false;
            Interlocked.Increment(ref underruns);
            Underrun?.Invoke(this, "iOS audio queue ran out of PCM buffers.");
        }
        Volatile.Write(ref deviceSampleRate, (int)queue.SampleRate);
        PublishPlayoutSnapshot();
    }

    private void UpdatePlayedCore()
    {
        playedObservedAt = MediaTime.Now;
        if (!running || queue is null) return;
        var stamp = new AudioTimeStamp();
        var discontinuity = false;
        var result = queue.GetCurrentTime(null, ref stamp, ref discontinuity);
        playedObservedAt = MediaTime.Now;
        if (result == AudioQueueStatus.InvalidRunState) return;
        Check(result);
        if ((stamp.Flags & AudioTimeStamp.AtsFlags.SampleTimeValid) != 0 && double.IsFinite(stamp.SampleTime))
            played = Math.Clamp((long)(stamp.SampleTime * Format.BlockAlign), played, submitted);
    }

    private void PublishPlayoutSnapshot() => Volatile.Write(ref playoutSnapshot, new AudioPlayoutSnapshot(
        timeline.Resolve(played), Format.GetDuration(Math.Max(0, played - trackStart)), playedObservedAt));

    private unsafe bool FillBuffer(nint buffer)
    {
        if (exhausted || pullAudio is null) return false;
        var queueBuffer = (AudioQueueBuffer*)buffer;
        var destination = new Span<byte>((void*)queueBuffer->AudioData, (int)queueBuffer->AudioDataBytesCapacity);
        var written = 0;
        while (written < bufferSize && !exhausted && !disposed)
        {
            if (pending.IsEmpty)
            {
                var result = pullAudio?.Invoke(Format.GetDuration(bufferSize - written))
                    ?? AudioOutputReadResult.Complete;
                if (result.Resource is not null && result.Buffer.IsEmpty && result.ByteLength > 0)
                    throw new NotSupportedException("iOS audio output requires locally addressable PCM.");
                pending = result.Buffer[..Format.AlignByteCount(result.Buffer.Length)];
                pendingEndsSource = result.EndOfStream;
                nextSourceExpected = result.NextSourceExpected;
                timeline.Append(submitted + written, pending.Length, result);
                if (pending.IsEmpty)
                {
                    exhausted = result.EndOfStream;
                    if (exhausted) pullAudio = null;
                    break;
                }
            }
            var count = Math.Min(pending.Length, bufferSize - written);
            pending.Span[..count].CopyTo(destination[written..]);
            pending = pending[count..];
            written += count;
            if (pending.IsEmpty && pendingEndsSource)
            {
                exhausted = true;
                pullAudio = null;
            }
        }
        if (written == 0) return false;
        queueBuffer->AudioDataByteSize = (uint)written;
        Check(queue!.EnqueueBuffer(queueBuffer, []));
        queued.Add(buffer);
        submitted += written;
        return true;
    }

    private void SetState(AudioOutputState state) => Volatile.Write(ref stateValue, (int)state);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private static void Check(AudioQueueStatus result)
    {
        if (result != AudioQueueStatus.Ok)
            throw new InvalidOperationException($"Audio Queue returned {result} ({(int)result}).");
    }
}