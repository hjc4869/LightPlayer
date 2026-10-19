using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Audio;

/// <summary>
/// Consumes PCM at exactly the rate a real device would but discards it. Used when no backend is
/// registered and as the fallback swapped in when a device fails, so a broken audio device degrades
/// playback to "silent" instead of "stuck" -- the timeline keeps advancing either way.
/// </summary>
public sealed class NullAudioOutputDevice : IAudioOutputDevice
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan DefaultBufferTarget = TimeSpan.FromMilliseconds(200);

    private readonly AudioTimelineMap timeline = new();
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task pump;
    private Task? disposal;
    private readonly int bufferTargetBytes;

    private AudioOutputPullCallback? pullAudio;
    private long writtenBytes;
    private long playedBytes;
    private long trackStartByte;
    private long sourceEndByte;
    private long lastAdvanceTimestamp;
    private float volume = 1.0f;
    private bool sourceExhausted = true;
    private bool disposed;

    public NullAudioOutputDevice(AudioFormat format, TimeSpan bufferTarget)
    {
        Format = format;
        var target = bufferTarget > TimeSpan.Zero ? bufferTarget : DefaultBufferTarget;
        bufferTargetBytes = Math.Max(
            format.BlockAlign,
            format.AlignByteCount((int)Math.Clamp(format.ByteRate * target.TotalSeconds, format.BlockAlign, int.MaxValue)));

        pump = PumpAsync();
    }

    public AudioFormat Format { get; }

    public IAudioOutputSettings Settings => DefaultAudioOutputSettings.Instance;

    public int DeviceSampleRate => (int)Format.SampleRate;

    public AudioOutputState State { get; private set; } = AudioOutputState.Stopped;

    public float Volume
    {
        get => volume;
        set => volume = Math.Clamp(value, 0.0f, 1.0f);
    }

    public TimeSpan BufferedDuration
    {
        get
        {
            lock (gate)
            {
                var end = sourceExhausted ? sourceEndByte : writtenBytes;
                return Format.GetDuration(Math.Max(0, end - playedBytes));
            }
        }
    }

    public TimeSpan PlayedDuration
    {
        get
        {
            lock (gate)
            {
                return Format.GetDuration(Math.Max(0, playedBytes - trackStartByte));
            }
        }
    }

    public AudioTimelinePosition PlayedMediaPosition
    {
        get
        {
            lock (gate)
            {
                return timeline.Resolve(playedBytes);
            }
        }
    }

    public bool SourceExhausted
    {
        get
        {
            lock (gate)
            {
                return sourceExhausted;
            }
        }
    }

    public bool IsDrained
    {
        get
        {
            lock (gate)
            {
                return sourceExhausted && playedBytes >= sourceEndByte;
            }
        }
    }

    public int UnderrunCount => 0;

    public event EventHandler<AudioOutputError>? BackendError
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? Underrun
    {
        add { }
        remove { }
    }

    public void Start(AudioOutputPullCallback pullAudioCallback)
    {
        ArgumentNullException.ThrowIfNull(pullAudioCallback);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pullAudio = pullAudioCallback;
            sourceExhausted = false;
            trackStartByte = writtenBytes;
            sourceEndByte = long.MaxValue;
            lastAdvanceTimestamp = Stopwatch.GetTimestamp();
            State = AudioOutputState.Playing;
        }
    }

    public void Pause()
    {
        lock (gate)
        {
            if (State != AudioOutputState.Playing)
            {
                return;
            }

            AdvanceLocked();
            State = AudioOutputState.Paused;
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            if (State != AudioOutputState.Paused)
            {
                return;
            }

            lastAdvanceTimestamp = Stopwatch.GetTimestamp();
            State = AudioOutputState.Playing;
        }
    }

    public void Stop() => Reset();

    public void ResetAfterSeek() => Reset();

    public void Dispose() => _ = DisposeAsync();

    public ValueTask DisposeAsync()
    {
        lock (gate)
            return new ValueTask(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        disposed = true;
        Reset();
        cancellation.Cancel();
        try { await pump; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        cancellation.Dispose();
    }

    private void Reset()
    {
        lock (gate)
        {
            pullAudio = null;
            sourceExhausted = true;
            writtenBytes = 0;
            playedBytes = 0;
            trackStartByte = 0;
            sourceEndByte = 0;
            timeline.Reset();
            State = AudioOutputState.Stopped;
        }
    }

    private async Task PumpAsync()
    {
        var token = cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            lock (gate)
            {
                if (State == AudioOutputState.Playing)
                {
                    AdvanceLocked();
                    FillLocked();
                }
            }

            await Task.Delay(PumpInterval, token);
        }
    }

    /// <summary>Moves the play cursor by the wall-clock time a real device would have consumed.</summary>
    private void AdvanceLocked()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(lastAdvanceTimestamp, now);
        lastAdvanceTimestamp = now;

        var advanced = playedBytes + (long)(Format.ByteRate * elapsed.TotalSeconds);
        var end = sourceExhausted ? sourceEndByte : writtenBytes;
        playedBytes = Math.Clamp(advanced, playedBytes, Math.Max(playedBytes, end));
    }

    private void FillLocked()
    {
        for (int count = 0; count < 64 && !sourceExhausted && writtenBytes - playedBytes < bufferTargetBytes; count++)
        {
            var requested = Format.GetDuration(bufferTargetBytes - (writtenBytes - playedBytes));
            AudioOutputReadResult result;
            try
            {
                result = pullAudio?.Invoke(requested) ?? AudioOutputReadResult.Complete;
            }
            catch (Exception exception)
            {
                AppLog.Error("Audio.Output", "The silent audio output source failed.", exception);
                result = AudioOutputReadResult.Complete;
            }

            if (result.EndOfStream)
            {
                pullAudio = null;
                sourceEndByte = writtenBytes;
                sourceExhausted = true;
                return;
            }

            var length = Format.AlignByteCount(result.ByteLength);
            if (length <= 0)
            {
                return;
            }

            timeline.Append(writtenBytes, length, result);
            writtenBytes += length;
        }
    }
}
