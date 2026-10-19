using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Core;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.Desktop.Linux;

/// <summary>
/// PulseAudio playback backend. A dedicated <see cref="PlaybackThread"/> pumps decoded PCM into a
/// PulseAudio stream on a threaded mainloop every <see cref="PumpInterval"/>; the stream's own
/// playback buffer holds up to <c>bufferTarget</c> of audio. Playback position follows the
/// timestamped server read index and sink latency, with interpolated stream latency as a fallback.
/// </summary>
internal sealed class PulseAudioOutputDevice : IAudioOutputDevice
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan DefaultBufferTarget = TimeSpan.FromMilliseconds(750);

    // After the app applies a volume, ignore sink-input change notifications for this long: they are
    // almost always the server echoing our own write, and adopting them would fight a rapid app-side
    // change. Genuine external changes arrive once the app has been quiet.
    private static readonly long VolumeEchoSuppressionTicks = (long)(Stopwatch.Frequency * 0.15);

    private readonly PulseAudioSettings settings;
    private readonly PlaybackThread playbackThread;
    private readonly TimeSpan bufferTarget;
    private readonly PulseNative.PaSampleSpec sampleSpec;
    private readonly PulseNative.PaBufferAttr bufferAttr;
    private readonly AudioTimelineMap timeline = new();
    private readonly object disposalGate = new();
    private AudioPlayoutSnapshot playoutSnapshot = AudioPlayoutSnapshot.Empty;
    private Task? disposal;
    private IntPtr mainloop;
    private IntPtr api;
    private IntPtr context;
    private IntPtr stream;
    private bool mainloopStarted;
    private bool streamOperationSucceeded;
    private GCHandle selfHandle;
    private AudioOutputPullCallback? pullAudio;
    private int stateValue = (int)AudioOutputState.Stopped;
    private int sourceExhausted = 1;
    private int underrunCount;
    private int underflowSignals;
    private int deviceSampleRate;
    private long writtenBytes;
    private long playedBytes;
    private long trackStartByte;
    private long sourceEndByte;

    // The playback buffer the server settled on, which is what this stream may hand it at a time.
    private long streamBufferBytes;

    // Last read index seen in a timing snapshot, and when it was seen, so it can be carried forward.
    private long lastReadIndex;
    private long lastReadIndexAt;
    private float volume = 1.0f;
    private uint lastAppliedVolume = uint.MaxValue;
    private long lastVolumeSetTicks;
    private int disposed;

    public PulseAudioOutputDevice(
        AudioFormat format,
        TimeSpan bufferTarget,
        PulseAudioSettings settings)
        : this(format, bufferTarget, settings, initialize: true)
    {
    }

    private PulseAudioOutputDevice(
        AudioFormat format,
        TimeSpan bufferTarget,
        PulseAudioSettings settings,
        bool initialize)
    {
        if (format.BitsPerSample is not (16 or 24) || format.ChannelCount is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"PulseAudio playback supports mono/stereo 16-bit or 24-bit PCM. Requested {format.ChannelCount} channel(s), {format.BitsPerSample}-bit.");
        }

        Format = format;
        Settings = settings;
        this.settings = settings;
        this.bufferTarget = bufferTarget > TimeSpan.Zero ? bufferTarget : DefaultBufferTarget;
        // PulseAudio (and PipeWire's Pulse emulation) natively accept packed 24-bit samples, so the
        // decoder's PCM is streamed unmodified; only the sample format tag changes with bit depth.
        var littleEndian = BitConverter.IsLittleEndian;
        var sampleFormat = format.BitsPerSample == 24
            ? (littleEndian ? PulseNative.PA_SAMPLE_S24LE : PulseNative.PA_SAMPLE_S24BE)
            : (littleEndian ? PulseNative.PA_SAMPLE_S16LE : PulseNative.PA_SAMPLE_S16BE);
        sampleSpec = new PulseNative.PaSampleSpec
        {
            Format = sampleFormat,
            Rate = format.SampleRate,
            Channels = (byte)format.ChannelCount,
        };
        var targetBytes = checked((uint)Format.AlignByteCount(
            (int)Math.Clamp(Format.ByteRate * this.bufferTarget.TotalSeconds, Format.BlockAlign, int.MaxValue)));
        bufferAttr = new PulseNative.PaBufferAttr
        {
            MaxLength = (uint)Math.Min(uint.MaxValue, 2L * targetBytes),
            TLength = targetBytes,
            PreBuf = targetBytes,
            // (uint32_t)-1 asks the server to pick the field's default.
            MinReq = uint.MaxValue,
            FragSize = uint.MaxValue,
        };
        playbackThread = new PlaybackThread("LightPlayer.PulseAudio");
        settings.Changed += OnSettingsChanged;

        if (!initialize)
        {
            return;
        }

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
        AudioFormat format,
        TimeSpan bufferTarget,
        PulseAudioSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var device = new PulseAudioOutputDevice(format, bufferTarget, settings, initialize: false);
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

    private void InitializeCore(CancellationToken cancellationToken)
    {
        try
        {
            ConnectPulse(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref deviceSampleRate, ResolveDeviceSampleRate());
            playbackThread.StartPeriodic(PumpSafely, PumpInterval);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "PulseAudio client library (libpulse.so.0) was not found. Install PulseAudio or a PipeWire PulseAudio interface (pipewire-pulse).",
                ex);
        }
    }

    public AudioFormat Format { get; }

    public IAudioOutputSettings Settings { get; }

    public int DeviceSampleRate => Volatile.Read(ref deviceSampleRate);

    public AudioOutputState State => (AudioOutputState)Volatile.Read(ref stateValue);

    public float Volume
    {
        get => Volatile.Read(ref volume);
        set
        {
            var clamped = Math.Clamp(value, 0.0f, 1.0f);
            // The playback loop re-asserts Volume on every iteration. Forward only an actual change
            // to PulseAudio so a level the user set on this sink input elsewhere (e.g. the system
            // mixer) is not immediately clobbered by an unchanged re-push.
            if (Interlocked.Exchange(ref volume, clamped) == clamped || IsDisposed)
            {
                return;
            }

            try
            {
                playbackThread.Post(ApplyVolumeCore);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public TimeSpan BufferedDuration
    {
        get
        {
            var end = Volatile.Read(ref sourceExhausted) != 0
                ? Interlocked.Read(ref sourceEndByte)
                : Interlocked.Read(ref writtenBytes);
            return Format.GetDuration(Math.Max(0, end - Interlocked.Read(ref playedBytes)));
        }
    }

    public TimeSpan PlayedDuration => Format.GetDuration(Math.Max(
        0,
        Interlocked.Read(ref playedBytes) - Interlocked.Read(ref trackStartByte)));

    public AudioTimelinePosition PlayedMediaPosition =>
        timeline.Resolve(Interlocked.Read(ref playedBytes));

    public AudioPlayoutSnapshot PlayoutSnapshot => Volatile.Read(ref playoutSnapshot);

    public bool SourceExhausted => Volatile.Read(ref sourceExhausted) != 0;

    public bool IsDrained => SourceExhausted &&
        Interlocked.Read(ref playedBytes) >= Interlocked.Read(ref sourceEndByte);

    public int UnderrunCount => Volatile.Read(ref underrunCount);

    public event EventHandler<AudioOutputError>? BackendError;

    public event EventHandler<string>? Underrun;

    public event EventHandler<float>? VolumeChanged;

    public void Start(AudioOutputPullCallback pullAudioCallback)
    {
        ArgumentNullException.ThrowIfNull(pullAudioCallback);
        ThrowIfDisposed();
        playbackThread.Invoke(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            PulseNative.pa_threaded_mainloop_lock(mainloop);
            try
            {
                pullAudio = pullAudioCallback;
                Volatile.Write(ref sourceExhausted, 0);
                Interlocked.Exchange(ref trackStartByte, Interlocked.Read(ref writtenBytes));
                Interlocked.Exchange(ref sourceEndByte, long.MaxValue);
                Interlocked.Exchange(ref underflowSignals, 0);
                lastReadIndexAt = Stopwatch.GetTimestamp();
                SetState(AudioOutputState.Playing);
                CorkCore(false);
                FeedCore();
                PublishPlayoutSnapshot();
            }
            finally
            {
                PulseNative.pa_threaded_mainloop_unlock(mainloop);
            }
        });
    }

    public void Pause()
    {
        ThrowIfDisposed();
        playbackThread.Invoke(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            PulseNative.pa_threaded_mainloop_lock(mainloop);
            try
            {
                if (State != AudioOutputState.Playing)
                {
                    return;
                }

                CorkCore(true);
                UpdatePlayedCore();
                SetState(AudioOutputState.Paused);
                PublishPlayoutSnapshot();
            }
            finally
            {
                PulseNative.pa_threaded_mainloop_unlock(mainloop);
            }
        });
    }

    public void Resume()
    {
        ThrowIfDisposed();
        playbackThread.Invoke(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            PulseNative.pa_threaded_mainloop_lock(mainloop);
            try
            {
                if (State != AudioOutputState.Paused)
                {
                    return;
                }

                SetState(AudioOutputState.Playing);
                CorkCore(false);
                FeedCore();
                PublishPlayoutSnapshot();
            }
            finally
            {
                PulseNative.pa_threaded_mainloop_unlock(mainloop);
            }
        });
    }

    public void Stop()
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            playbackThread.Invoke(LockedReset);
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
        }
    }

    public void ResetAfterSeek()
    {
        ThrowIfDisposed();
        playbackThread.Invoke(LockedReset);
    }

    public void Dispose()
    {
        var completion = DisposeAsync();
        if (!playbackThread.IsOnThread)
        {
            completion.GetAwaiter().GetResult();
        }
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
            return new ValueTask(disposal ?? playbackThread.InvokeAsync(LockedReset));
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
        Interlocked.Exchange(ref disposed, 1);
        settings.Changed -= OnSettingsChanged;
        try
        {
            await playbackThread.InvokeAsync(DisposeCore).ConfigureAwait(false);
        }
        finally
        {
            await playbackThread.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void PumpSafely()
    {
        if (IsDisposed || State == AudioOutputState.Stopped)
        {
            return;
        }

        PulseNative.pa_threaded_mainloop_lock(mainloop);
        try
        {
            if (!IsStreamReadyCore())
            {
                throw new InvalidOperationException("PulseAudio playback stream disconnected.");
            }

            UpdatePlayedCore();
            ProcessUnderflowsCore();
            PublishPlayoutSnapshot();
            if (State == AudioOutputState.Playing)
            {
                FeedCore();
            }
        }
        catch (Exception ex)
        {
            pullAudio = null;
            Volatile.Write(ref sourceExhausted, 1);
            Interlocked.Exchange(ref sourceEndByte, Interlocked.Read(ref playedBytes));
            SetState(AudioOutputState.Stopped);
            BackendError?.Invoke(this, new AudioOutputError($"PulseAudio backend error: {ex.Message}", ex));
        }
        finally
        {
            PulseNative.pa_threaded_mainloop_unlock(mainloop);
        }
    }

    private void FeedCore()
    {
        while (State == AudioOutputState.Playing && Volatile.Read(ref sourceExhausted) == 0)
        {
            // What the server asks for is not what it can hold: it keeps asking for as long as the
            // stream starves, so a stream fed again after an idle end or a long underrun is handed
            // the whole backlog at once, and everything past the buffer is dropped instead of
            // played, which jumps the sound forward and leaves the picture chasing it.
            var room = Interlocked.Read(ref streamBufferBytes) - QueuedBytesCore();
            if (room <= 0)
            {
                break;
            }

            var writable = PulseNative.pa_stream_writable_size(stream);
            if (writable == 0 || writable == PulseNative.InvalidSize)
            {
                break;
            }

            writable = Math.Min(writable, (nuint)room);

            AudioOutputReadResult result;
            try
            {
                var request = Format.GetDuration((long)Math.Min(writable, (nuint)int.MaxValue));
                result = pullAudio?.Invoke(request) ?? AudioOutputReadResult.Complete;
            }
            catch (Exception ex)
            {
                BackendError?.Invoke(this, new AudioOutputError($"PCM source failed: {ex.Message}", ex));
                result = AudioOutputReadResult.Complete;
            }

            if (result.Resource is not null && result.Buffer.IsEmpty && result.ByteLength > 0)
            {
                throw new NotSupportedException("PulseAudio output requires locally addressable PCM.");
            }

            if (!result.Buffer.IsEmpty)
            {
                WriteBlockCore(result);
            }

            if (result.EndOfStream)
            {
                pullAudio = null;
                Interlocked.Exchange(ref sourceEndByte, Interlocked.Read(ref writtenBytes));
                Volatile.Write(ref sourceExhausted, 1);

                // A track that ends inside the prebuffer would otherwise wait for audio that is
                // never coming.
                var trigger = PulseNative.pa_stream_trigger(stream, IntPtr.Zero, IntPtr.Zero);
                if (trigger != IntPtr.Zero)
                {
                    PulseNative.pa_operation_unref(trigger);
                }

                break;
            }

            if (result.Buffer.IsEmpty)
            {
                break;
            }
        }
    }

    private unsafe void WriteBlockCore(in AudioOutputReadResult result)
    {
        var buffer = result.Buffer;
        var length = Format.AlignByteCount(buffer.Length);
        if (length <= 0)
        {
            return;
        }

        // Volume is applied server-side by pa_context_set_sink_input_volume, so the decoded PCM is
        // handed to PulseAudio unmodified.
        using var handle = buffer.Pin();
        var writeResult = PulseNative.pa_stream_write(
            stream,
            (IntPtr)handle.Pointer,
            (nuint)length,
            IntPtr.Zero,
            0,
            PulseNative.PA_SEEK_RELATIVE);

        if (writeResult < 0)
        {
            BackendError?.Invoke(
                this,
                new AudioOutputError("PulseAudio failed to write audio to the playback stream."));
            return;
        }

        timeline.Append(Interlocked.Read(ref writtenBytes), length, result);
        Interlocked.Add(ref writtenBytes, length);
    }

    /// <summary>How much of what was written the server still holds and has not played, in bytes.</summary>
    /// <remarks>
    /// The read index is exact but only as fresh as the timing snapshot it arrived in, so it is
    /// carried forward at the rate the sink consumes. The reported latency is deliberately not used
    /// for this: it has the sink's own buffer in it, which on a Bluetooth headset is larger than the
    /// queue being measured and would read as an empty one.
    /// </remarks>
    private unsafe long QueuedBytesCore()
    {
        var timing = (PulseNative.PaTimingInfo*)PulseNative.pa_stream_get_timing_info(stream);
        if (timing is null || timing->WriteIndexCorrupt != 0 || timing->ReadIndexCorrupt != 0)
        {
            return 0;
        }

        if (timing->ReadIndex != lastReadIndex || State != AudioOutputState.Playing)
        {
            lastReadIndex = timing->ReadIndex;
            lastReadIndexAt = Stopwatch.GetTimestamp();
        }

        var consumed = (long)(Format.ByteRate * Stopwatch.GetElapsedTime(lastReadIndexAt).TotalSeconds);
        return Math.Max(0, timing->WriteIndex - lastReadIndex - consumed);
    }

    private unsafe void UpdatePlayedCore()
    {
        if (State != AudioOutputState.Playing || stream == IntPtr.Zero ||
            PulseNative.pa_stream_get_latency(stream, out var latencyUsec, out var negative) != 0)
        {
            return;
        }

        var written = Interlocked.Read(ref writtenBytes);
        long buffered = negative != 0
            ? 0
            : (long)(latencyUsec * (ulong)Format.ByteRate / 1_000_000UL);
        var timing = (PulseNative.PaTimingInfo*)PulseNative.pa_stream_get_timing_info(stream);
        if (timing is not null && timing->WriteIndexCorrupt == 0 && timing->ReadIndexCorrupt == 0)
        {
            long elapsedUsec = State == AudioOutputState.Playing && timing->Playing != 0
                ? Math.Max(0, (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10 -
                    timing->TimestampSeconds * 1_000_000 - timing->TimestampMicroseconds)
                : 0;
            long pending = timing->WriteIndex - timing->ReadIndex +
                (long)(((double)timing->SinkMicroseconds - elapsedUsec) * Format.ByteRate / 1_000_000);
            buffered = pending;
        }
        buffered = Math.Clamp(buffered, 0, written);

        var candidate = written - buffered;
        if (candidate > Interlocked.Read(ref playedBytes))
        {
            Interlocked.Exchange(ref playedBytes, candidate);
        }
    }

    private void PublishPlayoutSnapshot()
    {
        var played = Interlocked.Read(ref playedBytes);
        Volatile.Write(ref playoutSnapshot, new AudioPlayoutSnapshot(
            timeline.Resolve(played),
            Format.GetDuration(Math.Max(0, played - Interlocked.Read(ref trackStartByte))),
            MediaTime.Now));
    }

    private void ProcessUnderflowsCore()
    {
        var signals = Interlocked.Exchange(ref underflowSignals, 0);
        if (signals <= 0)
        {
            return;
        }

        if (Volatile.Read(ref sourceExhausted) != 0)
        {
            // Expected end-of-track drain: snap the played position to the end so IsDrained latches.
            Interlocked.Exchange(ref playedBytes, Interlocked.Read(ref sourceEndByte));
            return;
        }

        var count = Interlocked.Add(ref underrunCount, signals);
        Underrun?.Invoke(this, $"PulseAudio stream underrun #{count}.");
    }

    private unsafe void ConnectPulse(CancellationToken cancellationToken)
    {
        mainloop = PulseNative.pa_threaded_mainloop_new();
        if (mainloop == IntPtr.Zero)
        {
            throw new InvalidOperationException("PulseAudio could not create its mainloop.");
        }

        api = PulseNative.pa_threaded_mainloop_get_api(mainloop);
        if (PulseNative.pa_threaded_mainloop_start(mainloop) < 0)
        {
            throw new InvalidOperationException("PulseAudio could not start its mainloop.");
        }
        mainloopStarted = true;

        selfHandle = GCHandle.Alloc(this);
        var userdata = GCHandle.ToIntPtr(selfHandle);

        Task? cancellationWake = null;
        var cancellationRegistration = cancellationToken.Register(
            () => cancellationWake = Task.Run(SignalMainloop));
        PulseNative.pa_threaded_mainloop_lock(mainloop);
        try
        {
            context = PulseNative.pa_context_new(api, "LightPlayer");
            if (context == IntPtr.Zero)
            {
                throw new InvalidOperationException("PulseAudio could not create a client context.");
            }

            PulseNative.pa_context_set_state_callback(
                context,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnContextState,
                userdata);
            if (PulseNative.pa_context_connect(context, null, PulseNative.PA_CONTEXT_NOFLAGS, IntPtr.Zero) < 0)
            {
                throw new InvalidOperationException("PulseAudio could not connect to the sound server.");
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = PulseNative.pa_context_get_state(context);
                if (state == PulseNative.PA_CONTEXT_READY)
                {
                    break;
                }

                if (state is PulseNative.PA_CONTEXT_FAILED or PulseNative.PA_CONTEXT_TERMINATED)
                {
                    throw new InvalidOperationException("PulseAudio refused the client connection.");
                }

                PulseNative.pa_threaded_mainloop_wait(mainloop);
            }

            // Watch this context's sink inputs so a volume the user changes in the system mixer is
            // reflected back into the app (see OnSubscribe / OnSinkInputInfo).
            PulseNative.pa_context_set_subscribe_callback(
                context,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, uint, IntPtr, void>)&OnSubscribe,
                userdata);
            var subscribe = PulseNative.pa_context_subscribe(
                context,
                PulseNative.PA_SUBSCRIPTION_MASK_SINK_INPUT,
                IntPtr.Zero,
                IntPtr.Zero);
            if (subscribe != IntPtr.Zero)
            {
                PulseNative.pa_operation_unref(subscribe);
            }

            ConnectStreamCore(userdata, cancellationToken);
        }
        finally
        {
            PulseNative.pa_threaded_mainloop_unlock(mainloop);
            cancellationRegistration.Dispose();
            cancellationWake?.GetAwaiter().GetResult();
        }
    }

    private void SignalMainloop()
    {
        PulseNative.pa_threaded_mainloop_lock(mainloop);
        try
        {
            PulseNative.pa_threaded_mainloop_signal(mainloop, 0);
        }
        finally
        {
            PulseNative.pa_threaded_mainloop_unlock(mainloop);
        }
    }

    private unsafe void ConnectStreamCore(IntPtr userdata, CancellationToken cancellationToken = default)
    {
        stream = PulseNative.pa_stream_new(context, "Playback", in sampleSpec, IntPtr.Zero);
        if (stream == IntPtr.Zero)
        {
            throw new InvalidOperationException("PulseAudio could not create the playback stream.");
        }

        PulseNative.pa_stream_set_state_callback(
            stream,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnStreamState,
            userdata);
        PulseNative.pa_stream_set_underflow_callback(
            stream,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnUnderflow,
            userdata);

        const int flags = PulseNative.PA_STREAM_START_CORKED |
            PulseNative.PA_STREAM_INTERPOLATE_TIMING |
            PulseNative.PA_STREAM_AUTO_TIMING_UPDATE;
        if (PulseNative.pa_stream_connect_playback(
                stream,
                settings.SelectedSinkName,
                in bufferAttr,
                flags,
                IntPtr.Zero,
                IntPtr.Zero) < 0)
        {
            throw new InvalidOperationException("PulseAudio could not open the selected output device.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = PulseNative.pa_stream_get_state(stream);
            if (state == PulseNative.PA_STREAM_READY)
            {
                break;
            }

            if (state is PulseNative.PA_STREAM_FAILED or PulseNative.PA_STREAM_TERMINATED)
            {
                throw new InvalidOperationException("PulseAudio could not open the selected output device.");
            }

            PulseNative.pa_threaded_mainloop_wait(mainloop);
        }

        // The server may settle on a buffer other than the one asked for, and it is the one it
        // settled on that bounds what may be queued.
        var negotiated = (PulseNative.PaBufferAttr*)PulseNative.pa_stream_get_buffer_attr(stream);
        Interlocked.Exchange(ref streamBufferBytes, negotiated is null ? bufferAttr.TLength : negotiated->TLength);

        // The stream now owns a server-side sink-input index; push the current volume onto it. This
        // also re-applies after a device switch, which connects a fresh sink input with a new index.
        ApplyVolumeLocked();
    }

    private void DisconnectStreamCore()
    {
        if (stream == IntPtr.Zero)
        {
            return;
        }

        PulseNative.pa_stream_set_state_callback(stream, IntPtr.Zero, IntPtr.Zero);
        PulseNative.pa_stream_set_underflow_callback(stream, IntPtr.Zero, IntPtr.Zero);
        PulseNative.pa_stream_disconnect(stream);
        PulseNative.pa_stream_unref(stream);
        stream = IntPtr.Zero;
    }

    private void ReopenCore()
    {
        if (IsDisposed)
        {
            return;
        }

        PulseNative.pa_threaded_mainloop_lock(mainloop);
        try
        {
            if (context == IntPtr.Zero)
            {
                return;
            }

            DisconnectStreamCore();
            // Discarding the buffered audio counts it as played, keeping the position monotonic.
            Interlocked.Exchange(ref playedBytes, Interlocked.Read(ref writtenBytes));
            ConnectStreamCore(GCHandle.ToIntPtr(selfHandle));

            if (State == AudioOutputState.Playing)
            {
                CorkCore(false);
                FeedCore();
            }
            PublishPlayoutSnapshot();
        }
        catch (Exception ex)
        {
            BackendError?.Invoke(
                this,
                new AudioOutputError($"PulseAudio could not switch output devices: {ex.Message}", ex));
        }
        finally
        {
            PulseNative.pa_threaded_mainloop_unlock(mainloop);
        }

        // Keep DeviceSampleRate tied to the rate this output instance was created against. The
        // queue compares it with the selected sink's live rate at the next track boundary and
        // recreates the output when they differ, allowing PulseAudio to renegotiate cleanly.
    }

    private void LockedReset()
    {
        if (IsDisposed)
        {
            return;
        }

        PulseNative.pa_threaded_mainloop_lock(mainloop);
        try
        {
            ResetCore();
        }
        finally
        {
            PulseNative.pa_threaded_mainloop_unlock(mainloop);
        }
    }

    private void ResetCore()
    {
        try
        {
            if (IsStreamReadyCore())
            {
                CorkCore(true);
                FlushCore();
            }
        }
        catch (InvalidOperationException) when (!IsStreamReadyCore())
        {
        }

        pullAudio = null;
        Volatile.Write(ref sourceExhausted, 1);
        timeline.Reset();
        Interlocked.Exchange(ref writtenBytes, 0);
        Interlocked.Exchange(ref playedBytes, 0);
        Interlocked.Exchange(ref trackStartByte, 0);
        Interlocked.Exchange(ref sourceEndByte, 0);
        Interlocked.Exchange(ref underflowSignals, 0);
        lastReadIndex = 0;
        lastReadIndexAt = 0;
        Volatile.Write(ref playoutSnapshot, AudioPlayoutSnapshot.Empty);
        SetState(AudioOutputState.Stopped);
    }

    private bool IsStreamReadyCore() =>
        context != IntPtr.Zero && stream != IntPtr.Zero &&
        PulseNative.pa_context_get_state(context) == PulseNative.PA_CONTEXT_READY &&
        PulseNative.pa_stream_get_state(stream) == PulseNative.PA_STREAM_READY;

    private void DisposeCore()
    {
        TeardownPulse();
        ResetCore();
    }

    private void TeardownPulse()
    {
        // Stopping the mainloop first guarantees no callback runs while the objects are released,
        // so the remaining teardown does not need to hold the mainloop lock.
        if (mainloopStarted)
        {
            PulseNative.pa_threaded_mainloop_stop(mainloop);
            mainloopStarted = false;
        }

        if (stream != IntPtr.Zero)
        {
            PulseNative.pa_stream_disconnect(stream);
            PulseNative.pa_stream_unref(stream);
            stream = IntPtr.Zero;
        }

        if (context != IntPtr.Zero)
        {
            PulseNative.pa_context_disconnect(context);
            PulseNative.pa_context_unref(context);
            context = IntPtr.Zero;
        }

        if (mainloop != IntPtr.Zero)
        {
            PulseNative.pa_threaded_mainloop_free(mainloop);
            mainloop = IntPtr.Zero;
        }

        if (selfHandle.IsAllocated)
        {
            selfHandle.Free();
        }
    }

    private unsafe void CorkCore(bool cork)
    {
        if (stream == IntPtr.Zero)
        {
            return;
        }

        streamOperationSucceeded = false;
        var operation = PulseNative.pa_stream_cork(
            stream,
            cork ? 1 : 0,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)&OnStreamSuccess,
            GCHandle.ToIntPtr(selfHandle));
        WaitForStreamOperation(operation, cork ? "pause" : "resume");
    }

    private unsafe void FlushCore()
    {
        if (stream == IntPtr.Zero)
        {
            return;
        }

        streamOperationSucceeded = false;
        var operation = PulseNative.pa_stream_flush(
            stream,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)&OnStreamSuccess,
            GCHandle.ToIntPtr(selfHandle));
        WaitForStreamOperation(operation, "flush");
    }

    private void WaitForStreamOperation(IntPtr operation, string operationName)
    {
        if (operation == IntPtr.Zero)
        {
            throw new InvalidOperationException($"PulseAudio could not {operationName} the playback stream.");
        }

        try
        {
            while (PulseNative.pa_operation_get_state(operation) == PulseNative.PA_OPERATION_RUNNING)
            {
                if (!IsStreamReadyCore())
                {
                    throw new InvalidOperationException($"PulseAudio disconnected while attempting to {operationName} the playback stream.");
                }

                PulseNative.pa_threaded_mainloop_wait(mainloop);
            }

            if (!streamOperationSucceeded)
            {
                throw new InvalidOperationException($"PulseAudio failed to {operationName} the playback stream.");
            }
        }
        finally
        {
            PulseNative.pa_operation_unref(operation);
        }
    }

    private int ResolveDeviceSampleRate()
    {
        var rate = settings.GetSystemSampleRate();
        return rate > 0 ? rate : (int)Format.SampleRate;
    }

    private void OnSettingsChanged(object? sender, AudioOutputSettingsChangedEventArgs e)
    {
        if (!e.ConfigurationChanged || IsDisposed)
        {
            return;
        }

        try
        {
            playbackThread.Post(ReopenCore);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ApplyVolumeCore()
    {
        if (IsDisposed)
        {
            return;
        }

        PulseNative.pa_threaded_mainloop_lock(mainloop);
        try
        {
            ApplyVolumeLocked();
        }
        finally
        {
            PulseNative.pa_threaded_mainloop_unlock(mainloop);
        }
    }

    private unsafe void ApplyVolumeLocked()
    {
        if (stream == IntPtr.Zero || context == IntPtr.Zero)
        {
            return;
        }

        var index = PulseNative.pa_stream_get_index(stream);
        if (index == PulseNative.PA_INVALID_INDEX)
        {
            return;
        }

        // Map the 0..1 level linearly onto pa_volume_t (PA_VOLUME_NORM == 100%) so the app's volume
        // matches the percentage the system mixer (pavucontrol / KDE) shows and applies. This is the
        // same perceptual taper every native PulseAudio client uses.
        var level = Volatile.Read(ref volume);
        var scaled = level >= 1.0f
            ? PulseNative.PA_VOLUME_NORM
            : level <= 0.0f
                ? PulseNative.PA_VOLUME_MUTED
                : (uint)Math.Clamp(
                    (long)Math.Round(level * (double)PulseNative.PA_VOLUME_NORM),
                    0L,
                    PulseNative.PA_VOLUME_NORM);

        var cvolume = default(PulseNative.PaCVolume);
        cvolume.Channels = (byte)Format.ChannelCount;
        for (var channel = 0; channel < Format.ChannelCount; channel++)
        {
            cvolume.Values[channel] = scaled;
        }

        var operation = PulseNative.pa_context_set_sink_input_volume(
            context,
            index,
            in cvolume,
            IntPtr.Zero,
            IntPtr.Zero);
        if (operation != IntPtr.Zero)
        {
            PulseNative.pa_operation_unref(operation);
        }

        // Remember what we pushed so the sink-input change notification it triggers is recognized as
        // our own echo rather than an external change.
        lastAppliedVolume = scaled;
        lastVolumeSetTicks = Stopwatch.GetTimestamp();
    }

    private void SetState(AudioOutputState state) =>
        Volatile.Write(ref stateValue, (int)state);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnContextState(IntPtr context, IntPtr userdata)
    {
        if (FromHandle(userdata) is { } self)
        {
            PulseNative.pa_threaded_mainloop_signal(self.mainloop, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStreamState(IntPtr stream, IntPtr userdata)
    {
        if (FromHandle(userdata) is { } self)
        {
            PulseNative.pa_threaded_mainloop_signal(self.mainloop, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStreamSuccess(IntPtr stream, int success, IntPtr userdata)
    {
        if (FromHandle(userdata) is { } self)
        {
            self.streamOperationSucceeded = success != 0;
            PulseNative.pa_threaded_mainloop_signal(self.mainloop, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUnderflow(IntPtr stream, IntPtr userdata)
    {
        if (FromHandle(userdata) is { } self)
        {
            Interlocked.Increment(ref self.underflowSignals);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnSubscribe(IntPtr context, uint eventType, uint index, IntPtr userdata)
    {
        if (FromHandle(userdata) is not { } self || self.stream == IntPtr.Zero)
        {
            return;
        }

        var facility = eventType & PulseNative.PA_SUBSCRIPTION_EVENT_FACILITY_MASK;
        var change = eventType & PulseNative.PA_SUBSCRIPTION_EVENT_TYPE_MASK;
        if (facility != PulseNative.PA_SUBSCRIPTION_EVENT_SINK_INPUT ||
            change != PulseNative.PA_SUBSCRIPTION_EVENT_CHANGE ||
            index != PulseNative.pa_stream_get_index(self.stream))
        {
            return;
        }

        // On the mainloop thread already, so issuing the info query here is safe; the value is
        // handled asynchronously in OnSinkInputInfo.
        var operation = PulseNative.pa_context_get_sink_input_info(
            context,
            index,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, void>)&OnSinkInputInfo,
            userdata);
        if (operation != IntPtr.Zero)
        {
            PulseNative.pa_operation_unref(operation);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSinkInputInfo(IntPtr context, IntPtr info, int endOfList, IntPtr userdata)
    {
        if (endOfList != 0 || info == IntPtr.Zero || FromHandle(userdata) is not { } self)
        {
            return;
        }

        self.HandleSinkInputVolume(info);
    }

    private void HandleSinkInputVolume(IntPtr info)
    {
        int channels = Marshal.ReadByte(info, PulseNative.SinkInputInfoVolumeOffset);
        if (channels <= 0)
        {
            return;
        }

        // pa_cvolume: byte channels, 3 pad bytes, then uint values[] (aligned to offset 4). Use the
        // loudest channel as the single level the app tracks.
        const int valuesOffset = 4;
        uint peak = 0;
        for (var channel = 0; channel < channels && channel < PulseNative.PA_CHANNELS_MAX; channel++)
        {
            var value = (uint)Marshal.ReadInt32(
                info,
                PulseNative.SinkInputInfoVolumeOffset + valuesOffset + (channel * sizeof(uint)));
            if (value > peak)
            {
                peak = value;
            }
        }

        // Ignore our own value and in-flight echoes of a volume we just applied; only adopt a level
        // the user set elsewhere (e.g. the system mixer).
        if (peak == lastAppliedVolume ||
            Stopwatch.GetTimestamp() - lastVolumeSetTicks < VolumeEchoSuppressionTicks)
        {
            return;
        }

        lastAppliedVolume = peak;
        var level = Math.Clamp((float)peak / PulseNative.PA_VOLUME_NORM, 0.0f, 1.0f);
        Volatile.Write(ref volume, level);
        RaiseVolumeChanged(level);
    }

    private void RaiseVolumeChanged(float level)
    {
        if (IsDisposed)
        {
            return;
        }

        // Never invoke handlers on the mainloop thread: one that calls back into this device would
        // deadlock re-acquiring the mainloop lock. Hand the notification to the thread pool.
        ThreadPool.QueueUserWorkItem(
            static state => state.Self.VolumeChanged?.Invoke(state.Self, state.Level),
            (Self: this, Level: level),
            preferLocal: false);
    }

    private static PulseAudioOutputDevice? FromHandle(IntPtr userdata)
    {
        if (userdata == IntPtr.Zero)
        {
            return null;
        }

        var handle = GCHandle.FromIntPtr(userdata);
        return handle.IsAllocated ? handle.Target as PulseAudioOutputDevice : null;
    }

    private bool IsDisposed => Volatile.Read(ref disposed) != 0;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
