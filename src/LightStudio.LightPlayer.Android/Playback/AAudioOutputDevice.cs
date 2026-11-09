using LightMediaRenderer.Avalonia.Audio;
#nullable enable
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Core;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.Logging;
using LightStudio.NativeBindings.Types;

namespace LightStudio.LightPlayer.Android.Playback;

internal sealed class AAudioOutputDevice : IAudioOutputDevice
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultBufferTarget = TimeSpan.FromMilliseconds(750);
    private const long StateTransitionTimeoutNanoseconds = 2_000_000_000;
    private const int MaxTimestampLagSeconds = 2;

    // Format preference orders passed to the stream builder, most-preferred first.
    private static readonly int[] Formats24Then16 = { AAudioNative.AAUDIO_FORMAT_PCM_I24_PACKED, AAudioNative.AAUDIO_FORMAT_PCM_I16 };
    private static readonly int[] Formats16Only = { AAudioNative.AAUDIO_FORMAT_PCM_I16 };

    private readonly AAudioSettings settings;
    private readonly PlaybackThread playbackThread;
    private readonly TimeSpan bufferTarget;
    private readonly int bufferTargetBytes;
    private readonly byte[] ringBuffer;
    private readonly byte[] callbackScratch;
    private readonly bool supports24BitFormat;
    private readonly GCHandle callbackHandle;
    private readonly AudioTimelineMap timeline = new();
    private readonly object disposalGate = new();
    private AudioPlayoutSnapshot playoutSnapshot = AudioPlayoutSnapshot.Empty;
    private Task? disposal;
    private unsafe AAudioStreamStruct* stream;
    private AudioOutputPullCallback? pullAudio;
    private ReadOnlyMemory<byte> pendingPcm;
    private AudioOutputReadResult pendingResult;
    private int pendingOffset;
    private bool pendingEndsSource;
    private long ringReadPosition;
    private long ringWritePosition;
    private long submittedBytes;
    private long renderedBytes;
    private long consumedBytes;
    private long streamPositionBaseByte;
    private long timestampLagFrames;
    private long trackStartByte;
    private long sourceEndByte;
    private int callbackSequence;
    private int stateValue = (int)AudioOutputState.Stopped;
    private int sourceExhausted = 1;
    private int underrunCount;
    private int callbackStarvationCount;
    private int observedCallbackStarvationCount;
    private int observedNativeXRunCount;
    private int deviceSampleRate;
    private int outputBytesPerSample;
    private int outputBlockAlign;
    private long nextRecoveryTimestamp;
    private string? lastOpenError;
    private bool lastOpenErrorReported;
    private float volume = 1.0f;
    private bool exclusiveFallbackPending;
    private volatile bool disposed;

    internal AAudioOutputDevice(
        AudioFormat format,
        TimeSpan bufferTarget,
        AAudioSettings settings)
        : this(format, bufferTarget, settings, initialize: true)
    {
    }

    private AAudioOutputDevice(
        AudioFormat format,
        TimeSpan bufferTarget,
        AAudioSettings settings,
        bool initialize)
    {
        if (format.BitsPerSample is not (16 or 24) || format.ChannelCount is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"AAudio playback supports mono/stereo 16-bit or 24-bit PCM. Requested {format.ChannelCount} channel(s), {format.BitsPerSample}-bit.");
        }

        Format = format;
        Settings = settings;
        this.settings = settings;
        this.bufferTarget = bufferTarget > TimeSpan.Zero ? bufferTarget : DefaultBufferTarget;
        bufferTargetBytes = GetBufferTargetBytes(format, this.bufferTarget);
        ringBuffer = new byte[bufferTargetBytes];
        // Holds one callback's worth of source (packed 24-bit) PCM while it is narrowed to 16-bit on
        // devices/OS versions that cannot accept AAUDIO_FORMAT_PCM_I24_PACKED. Sized to the ring so a
        // single callback can never out-run it.
        callbackScratch = new byte[bufferTargetBytes];
        // AAUDIO_FORMAT_PCM_I24_PACKED was added in API 31; older devices play 16-bit only.
        supports24BitFormat = OperatingSystem.IsAndroidVersionAtLeast(31);
        outputBytesPerSample = format.BytesPerSample;
        outputBlockAlign = format.BlockAlign;
        playbackThread = new PlaybackThread("LightPlayer.AAudio");
        callbackHandle = GCHandle.Alloc(this);
        sourceEndByte = 0;
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
        AAudioSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var device = new AAudioOutputDevice(format, bufferTarget, settings, initialize: false);
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
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.Exclusive)
        {
            TryOpenOutputCore(reportError: false, throwOnError: true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        playbackThread.StartPeriodic(PumpSafely, PumpInterval);
    }

    public AudioFormat Format { get; }

    public IAudioOutputSettings Settings { get; }

    public int DeviceSampleRate => Volatile.Read(ref deviceSampleRate);

    public AudioOutputState State => (AudioOutputState)Volatile.Read(ref stateValue);

    public float Volume
    {
        get => Volatile.Read(ref volume);
        set => Volatile.Write(ref volume, Math.Clamp(value, 0.0f, 1.0f));
    }

    public TimeSpan BufferedDuration
    {
        get
        {
            // Everything written but not yet heard, so this is the end-to-end output latency; the
            // ring's own fill gate uses the endpoint cursor instead (see GetTotalBufferedBytes).
            var nativeBufferedBytes = Math.Max(
                0,
                Interlocked.Read(ref submittedBytes) - Interlocked.Read(ref renderedBytes));
            return Format.GetDuration(nativeBufferedBytes + GetRingBufferedBytes());
        }
    }

    public TimeSpan PlayedDuration => Format.GetDuration(Math.Max(
        0,
        Interlocked.Read(ref renderedBytes) - Interlocked.Read(ref trackStartByte)));

    public AudioTimelinePosition PlayedMediaPosition =>
        timeline.Resolve(Interlocked.Read(ref renderedBytes));

    public AudioPlayoutSnapshot PlayoutSnapshot => Volatile.Read(ref playoutSnapshot);

    public bool SourceExhausted => Volatile.Read(ref sourceExhausted) != 0;

    public bool IsDrained
    {
        get
        {
            var sourceEnd = Interlocked.Read(ref sourceEndByte);
            return SourceExhausted &&
                sourceEnd != long.MaxValue &&
                Interlocked.Read(ref renderedBytes) >= sourceEnd;
        }
    }

    public int UnderrunCount => Volatile.Read(ref underrunCount);

    public event EventHandler<AudioOutputError>? BackendError;

    public event EventHandler<string>? Underrun;

    public void Start(AudioOutputPullCallback pullAudioCallback)
    {
        ArgumentNullException.ThrowIfNull(pullAudioCallback);
        ThrowIfDisposed();
        playbackThread.Invoke(() => StartCore(pullAudioCallback));
    }

    public unsafe void Pause()
    {
        ThrowIfDisposed();
        playbackThread.Invoke(() =>
        {
            ThrowIfDisposed();
            if (State != AudioOutputState.Playing)
            {
                return;
            }

            UpdateRenderedPositionCore();
            var currentStream = stream;
            if (currentStream != null)
            {
                try
                {
                    ThrowIfError(AAudioNative.AAudioStream_requestPause(currentStream), "pause");
                    WaitForStateCore(
                        currentStream,
                        AAudioNative.AAUDIO_STREAM_STATE_PAUSING,
                        AAudioNative.AAUDIO_STREAM_STATE_PAUSED,
                        "pause");
                    UpdateRenderedPositionCore();
                }
                catch (Exception exception)
                {
                    SetState(AudioOutputState.Paused);
                    HandleStreamFailureCore(currentStream, exception, "pause");
                    throw;
                }
            }

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
            if (State != AudioOutputState.Paused)
            {
                return;
            }

            SetState(AudioOutputState.Playing);
            FillRingBufferCore();
            EnsureStartedCore();
            UpdateRenderedPositionCore();
        });
    }

    public void Stop()
    {
        var completion = StopAsync();
        if (!playbackThread.IsOnThread)
        {
            completion.GetAwaiter().GetResult();
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
        if (!playbackThread.IsOnThread)
        {
            completion.GetAwaiter().GetResult();
        }
    }

    public ValueTask StartAsync(AudioOutputPullCallback pullAudioCallback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pullAudioCallback);
        ThrowIfDisposed();
        return new ValueTask(playbackThread.InvokeAsync(() => StartCore(pullAudioCallback), cancellationToken));
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
        settings.Changed -= OnSettingsChanged;
        try
        {
            await playbackThread.InvokeAsync(ResetCore).ConfigureAwait(false);
        }
        finally
        {
            await playbackThread.DisposeAsync().ConfigureAwait(false);
            callbackHandle.Free();
        }
    }

    private void StartCore(AudioOutputPullCallback pullAudioCallback)
    {
        ThrowIfDisposed();
        pullAudio = pullAudioCallback;
        pendingPcm = ReadOnlyMemory<byte>.Empty;
        pendingResult = default;
        pendingOffset = 0;
        pendingEndsSource = false;
        Interlocked.Exchange(ref trackStartByte, GetQueuedTimelineEndByte());
        Interlocked.Exchange(ref sourceEndByte, long.MaxValue);
        Volatile.Write(ref sourceExhausted, 0);
        SetState(AudioOutputState.Playing);
        FillRingBufferCore();
        EnsureStartedCore();
        UpdateRenderedPositionCore();
    }

    private unsafe void PumpSafely()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            if (State == AudioOutputState.Playing)
            {
                UpdateRenderedPositionCore();
            }
            ReportUnderrunsCore();

            if (State is AudioOutputState.Playing or AudioOutputState.Paused)
            {
                FillRingBufferCore();
            }

            if (stream == null &&
                State != AudioOutputState.Stopped &&
                Stopwatch.GetTimestamp() >= nextRecoveryTimestamp &&
                TryOpenOutputCore(reportError: false) &&
                State == AudioOutputState.Playing)
            {
                EnsureStartedCore();
            }
        }
        catch (Exception ex)
        {
            BackendError?.Invoke(this, new AudioOutputError($"AAudio backend error: {ex.Message}", ex));
        }
    }

    private unsafe bool TryOpenOutputCore(bool reportError, bool throwOnError = false)
    {
        if (stream != null || disposed)
        {
            return stream != null;
        }

        // Preserve 16-bit source PCM byte-for-byte. Packed 24-bit sources prefer a matching native
        // stream and fall back to 16-bit only when the OS/device cannot honor it.
        var candidateFormats = Format.BitsPerSample == 24 && supports24BitFormat
            ? Formats24Then16
            : Formats16Only;
        Exception? lastError = null;
        foreach (var requestedFormat in candidateFormats)
        {
            if (disposed)
            {
                return false;
            }

            try
            {
                OpenStreamWithFormat(requestedFormat);
                lastOpenError = null;
                lastOpenErrorReported = false;
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        nextRecoveryTimestamp = Stopwatch.GetTimestamp()
            + (long)(RecoveryInterval.TotalSeconds * Stopwatch.Frequency);
        var message = $"AAudio could not open the selected output device: {lastError?.Message}";
        if (throwOnError)
        {
            throw new InvalidOperationException(message, lastError);
        }
        var errorChanged = !string.Equals(lastOpenError, message, StringComparison.Ordinal);
        if (errorChanged)
        {
            lastOpenError = message;
            lastOpenErrorReported = false;
        }
        if (reportError && !lastOpenErrorReported)
        {
            lastOpenErrorReported = true;
            BackendError?.Invoke(this, new AudioOutputError(message, lastError));
        }
        else if (!reportError && errorChanged)
        {
            AppLog.Debug("Audio.AAudio", message);
        }
        return false;
    }

    private unsafe void OpenStreamWithFormat(int requestedFormat)
    {
        AAudioStreamBuilderStruct* builder = null;
        AAudioStreamStruct* openedStream = null;
        try
        {
            ThrowIfError(AAudioNative.AAudio_createStreamBuilder(&builder), "create stream builder");
            if (builder == null)
            {
                throw new InvalidOperationException("AAudio returned a null stream builder.");
            }

            var selectedDeviceId = settings.SelectedNativeDeviceId;
            var exclusive = settings.Exclusive;
            var nativeBufferFrames = GetNativeBufferTargetFrames();
            AAudioNative.AAudioStreamBuilder_setDirection(builder, AAudioNative.AAUDIO_DIRECTION_OUTPUT);
            AAudioNative.AAudioStreamBuilder_setSampleRate(builder, checked((int)Format.SampleRate));
            AAudioNative.AAudioStreamBuilder_setChannelCount(builder, checked((int)Format.ChannelCount));
            AAudioNative.AAudioStreamBuilder_setFormat(builder, requestedFormat);
            AAudioNative.AAudioStreamBuilder_setSharingMode(
                builder,
                exclusive ? AAudioNative.AAUDIO_SHARING_MODE_EXCLUSIVE : AAudioNative.AAUDIO_SHARING_MODE_SHARED);
            AAudioNative.AAudioStreamBuilder_setPerformanceMode(
                builder,
                exclusive ? AAudioNative.AAUDIO_PERFORMANCE_MODE_LOW_LATENCY : AAudioNative.AAUDIO_PERFORMANCE_MODE_POWER_SAVING);
            AAudioNative.AAudioStreamBuilder_setBufferCapacityInFrames(builder, nativeBufferFrames);
            if (selectedDeviceId > 0)
            {
                AAudioNative.AAudioStreamBuilder_setDeviceId(builder, selectedDeviceId);
            }

            var callbackUserData = (void*)GCHandle.ToIntPtr(callbackHandle);
            AAudioNative.AAudioStreamBuilder_setDataCallback(
                builder,
                (delegate* unmanaged[Cdecl]<AAudioStreamStruct*, void*, void*, int, int>)&OnDataCallback,
                callbackUserData);
            AAudioNative.AAudioStreamBuilder_setErrorCallback(
                builder,
                (delegate* unmanaged[Cdecl]<AAudioStreamStruct*, void*, int, void>)&OnErrorCallback,
                callbackUserData);

            ThrowIfError(AAudioNative.AAudioStreamBuilder_openStream(builder, &openedStream), "open stream");
            if (openedStream == null)
            {
                throw new InvalidOperationException("AAudio returned a null stream.");
            }

            VerifyOpenedStream(openedStream, selectedDeviceId);

            // Adopt whatever PCM format the stream actually exposes so the data callback narrows the
            // decoder's packed 24-bit PCM only when the opened stream is 16-bit.
            outputBytesPerSample =
                AAudioNative.AAudioStream_getFormat(openedStream) == AAudioNative.AAUDIO_FORMAT_PCM_I24_PACKED
                    ? PcmConvert.Bytes24
                    : sizeof(short);
            outputBlockAlign = checked((int)Format.ChannelCount * outputBytesPerSample);

            TuneNativeBuffer(openedStream, nativeBufferFrames, exclusive);

            var streamRate = OperatingSystem.IsAndroidVersionAtLeast(34)
                ? AAudioNative.AAudioStream_getHardwareSampleRate(openedStream)
                : AAudioNative.AAudioStream_getSampleRate(openedStream);
            Volatile.Write(ref deviceSampleRate, Math.Max(0, streamRate));
            observedNativeXRunCount = Math.Max(0, AAudioNative.AAudioStream_getXRunCount(openedStream));
            streamPositionBaseByte = Interlocked.Read(ref submittedBytes);
            timestampLagFrames = 0;
            exclusiveFallbackPending = exclusive &&
                AAudioNative.AAudioStream_getSharingMode(openedStream) != AAudioNative.AAUDIO_SHARING_MODE_EXCLUSIVE;
            stream = openedStream;
            openedStream = null;
        }
        finally
        {
            if (openedStream != null)
            {
                AAudioNative.AAudioStream_close(openedStream);
            }

            if (builder != null)
            {
                AAudioNative.AAudioStreamBuilder_delete(builder);
            }
        }
    }

    private unsafe void VerifyOpenedStream(AAudioStreamStruct* openedStream, int selectedDeviceId)
    {
        var openedFormat = AAudioNative.AAudioStream_getFormat(openedStream);
        var isSupportedConversion = openedFormat == AAudioNative.AAUDIO_FORMAT_PCM_I16 ||
            (Format.BitsPerSample == 24 && openedFormat == AAudioNative.AAUDIO_FORMAT_PCM_I24_PACKED);
        if (AAudioNative.AAudioStream_getSampleRate(openedStream) != checked((int)Format.SampleRate) ||
            AAudioNative.AAudioStream_getChannelCount(openedStream) != checked((int)Format.ChannelCount) ||
            !isSupportedConversion)
        {
            throw new InvalidOperationException("AAudio did not honor the requested PCM format.");
        }

        if (selectedDeviceId > 0 && AAudioNative.AAudioStream_getDeviceId(openedStream) != selectedDeviceId)
        {
            throw new InvalidOperationException("AAudio did not route the stream to the selected output device.");
        }
    }

    private static unsafe void TuneNativeBuffer(AAudioStreamStruct* openedStream, int requestedFrames, bool exclusive)
    {
        var capacity = Math.Max(1, AAudioNative.AAudioStream_getBufferCapacityInFrames(openedStream));
        var burstFrames = Math.Max(1, AAudioNative.AAudioStream_getFramesPerBurst(openedStream));
        var desiredFrames = exclusive
            ? Math.Min(capacity, checked(burstFrames * 2))
            : Math.Min(capacity, requestedFrames);
        AAudioNative.AAudioStream_setBufferSizeInFrames(openedStream, Math.Max(1, desiredFrames));
    }

    private unsafe void EnsureStartedCore()
    {
        if (disposed || State != AudioOutputState.Playing)
        {
            return;
        }

        if (stream == null)
        {
            TryOpenOutputCore(reportError: true, throwOnError: true);
        }

        var currentStream = stream;
        var nativeState = AAudioNative.AAudioStream_getState(currentStream);
        if (nativeState == AAudioNative.AAUDIO_STREAM_STATE_STARTED)
        {
            return;
        }

        try
        {
            if (nativeState != AAudioNative.AAUDIO_STREAM_STATE_STARTING)
            {
                ThrowIfError(AAudioNative.AAudioStream_requestStart(currentStream), "start");
            }
            WaitForStateCore(
                currentStream,
                AAudioNative.AAUDIO_STREAM_STATE_STARTING,
                AAudioNative.AAUDIO_STREAM_STATE_STARTED,
                "start");
        }
        catch (Exception exception)
        {
            HandleStreamFailureCore(currentStream, exception, "start");
            throw;
        }

        if (exclusiveFallbackPending)
        {
            exclusiveFallbackPending = false;
            BackendError?.Invoke(
                this,
                new AudioOutputError("AAudio exclusive mode was unavailable; playback is using shared mode."));
        }
    }

    private void FillRingBufferCore()
    {
        while (!disposed && !SourceExhausted && GetTotalBufferedBytes() < bufferTargetBytes)
        {
            if (pendingOffset < pendingPcm.Length)
            {
                // The ring may only take part of the block, so anchor it once, when its first byte
                // enters the queued timeline.
                var anchorOffset = pendingOffset == 0 ? GetQueuedTimelineEndByte() : -1L;
                var written = WriteRingBuffer(pendingPcm.Span[pendingOffset..]);
                if (written <= 0)
                {
                    break;
                }

                if (anchorOffset >= 0)
                {
                    timeline.Append(anchorOffset, pendingPcm.Length, pendingResult);
                }

                pendingOffset += written;
                if (pendingOffset < pendingPcm.Length)
                {
                    continue;
                }

                pendingPcm = ReadOnlyMemory<byte>.Empty;
                pendingOffset = 0;
                if (pendingEndsSource)
                {
                    pendingEndsSource = false;
                    pullAudio = null;
                    Volatile.Write(ref sourceExhausted, 1);
                    break;
                }
                continue;
            }

            AudioOutputReadResult result;
            try
            {
                var requestedBytes = Math.Max(Format.BlockAlign, bufferTargetBytes - GetTotalBufferedBytes());
                result = pullAudio?.Invoke(Format.GetDuration(requestedBytes))
                    ?? AudioOutputReadResult.Complete;
                if (result.Buffer.IsEmpty && result.ByteLength > 0)
                {
                    throw new NotSupportedException("AAudio requires locally accessible PCM memory.");
                }
            }
            catch (Exception ex)
            {
                BackendError?.Invoke(this, new AudioOutputError($"PCM source failed: {ex.Message}", ex));
                result = AudioOutputReadResult.Complete;
            }

            if (!result.Buffer.IsEmpty)
            {
                var usableLength = result.Buffer.Length - result.Buffer.Length % Format.BlockAlign;
                if (usableLength > 0)
                {
                    pendingPcm = result.Buffer[..usableLength];
                    pendingResult = result;
                    pendingOffset = 0;
                    pendingEndsSource = result.EndOfStream;
                    continue;
                }
            }

            if (result.EndOfStream)
            {
                pullAudio = null;
                Volatile.Write(ref sourceExhausted, 1);
            }
            break;
        }
    }

    private unsafe void UpdateRenderedPositionCore() => UpdateRenderedPositionCore(stream);

    private unsafe void UpdateRenderedPositionCore(AAudioStreamStruct* currentStream)
    {
        if (currentStream == null)
        {
            return;
        }

        var framesRead = Math.Max(0, AAudioNative.AAudioStream_getFramesRead(currentStream));
        var submitted = Interlocked.Read(ref submittedBytes);
        AdvanceCursorCore(ref consumedBytes, submitted, framesRead);
        AdvanceCursorCore(ref renderedBytes, submitted, GetPresentedFramesCore(currentStream, framesRead));
        PublishPlayoutSnapshot();
    }

    private void PublishPlayoutSnapshot()
    {
        var rendered = Interlocked.Read(ref renderedBytes);
        Volatile.Write(ref playoutSnapshot, new AudioPlayoutSnapshot(
            timeline.Resolve(rendered),
            Format.GetDuration(Math.Max(0, rendered - Interlocked.Read(ref trackStartByte))),
            MediaTime.Now));
    }

    private void AdvanceCursorCore(ref long cursor, long submitted, long frames)
    {
        var candidate = Math.Min(
            submitted,
            streamPositionBaseByte + checked(frames * Format.BlockAlign));
        if (candidate > Interlocked.Read(ref cursor))
        {
            Interlocked.Exchange(ref cursor, candidate);
        }
    }

    /// <summary>
    /// Frames that have actually reached the speaker. <c>getFramesRead</c> stops at the endpoint's
    /// read cursor, so the mixer, HAL, and Bluetooth transport delay are invisible to it. The
    /// presentation timestamp includes these delays, which cause most A/V offset on a wireless headset.
    /// </summary>
    private unsafe long GetPresentedFramesCore(AAudioStreamStruct* currentStream, long framesRead)
    {
        long framePosition;
        long timeNanoseconds;
        if (AAudioNative.AAudioStream_getTimestamp(
                currentStream,
                AAudioNative.CLOCK_MONOTONIC,
                &framePosition,
                &timeNanoseconds) != AAudioNative.AAUDIO_OK)
        {
            // Timestamps exist only while the stream is started; holding the last measured distance
            // stops a pause from snapping the position forward onto the endpoint's read cursor.
            return Math.Max(0, framesRead - timestampLagFrames);
        }

        var elapsedNanoseconds = Math.Max(0, AAudioNative.MonotonicNanoseconds() - timeNanoseconds);
        // No real endpoint trails its read cursor by seconds, so a timestamp that does has stopped
        // advancing; bounding it keeps the master clock moving instead of freezing playback.
        var presentedFrames = Math.Clamp(
            framePosition + (long)(elapsedNanoseconds * (Format.SampleRate / 1_000_000_000.0)),
            Math.Max(0, framesRead - (Format.SampleRate * MaxTimestampLagSeconds)),
            framesRead);
        timestampLagFrames = framesRead - presentedFrames;
        return presentedFrames;
    }

    private unsafe void ReportUnderrunsCore()
    {
        var callbackStarvations = Volatile.Read(ref callbackStarvationCount);
        var callbackDelta = Math.Max(0, callbackStarvations - observedCallbackStarvationCount);
        observedCallbackStarvationCount = callbackStarvations;

        var nativeXRunCount = stream == null
            ? observedNativeXRunCount
            : Math.Max(0, AAudioNative.AAudioStream_getXRunCount(stream));
        var nativeDelta = Math.Max(0, nativeXRunCount - observedNativeXRunCount);
        observedNativeXRunCount = nativeXRunCount;

        var delta = callbackDelta + nativeDelta;
        if (delta <= 0)
        {
            return;
        }

        var total = Interlocked.Add(ref underrunCount, delta);
        Underrun?.Invoke(this, $"AAudio output underrun #{total}.");
    }

    private unsafe void HandleStreamFailureCore(AAudioStreamStruct* failedStream, Exception exception, string operation)
    {
        if (failedStream != stream)
        {
            return;
        }

        AppLog.Error(
            "Audio.AAudio",
            $"AAudio could not {operation} the output stream; reconnecting.",
            exception);
        CloseOutputCore(dropBufferedAudio: true);
        nextRecoveryTimestamp = Stopwatch.GetTimestamp();
    }

    private unsafe void HandleNativeErrorCore(AAudioStreamStruct* failedStream, int error)
    {
        if (disposed || failedStream != stream)
        {
            return;
        }

        AppLog.Debug(
            "Audio.AAudio",
            $"AAudio output device was disconnected; reconnecting: {AAudioNative.GetResultText(error)}.");
        CloseOutputCore(dropBufferedAudio: true);
        nextRecoveryTimestamp = Stopwatch.GetTimestamp();
    }

    private void ReopenOutputCore()
    {
        if (disposed)
        {
            return;
        }

        CloseOutputCore(dropBufferedAudio: true);
        if (State == AudioOutputState.Stopped)
        {
            if (!settings.Exclusive)
            {
                TryOpenOutputCore(reportError: false);
            }
            return;
        }

        if (TryOpenOutputCore(reportError: false) && State == AudioOutputState.Playing)
        {
            EnsureStartedCore();
        }
    }

    private unsafe void CloseOutputCore(bool dropBufferedAudio)
    {
        var closingStream = stream;
        if (closingStream == null)
        {
            return;
        }

        UpdateRenderedPositionCore(closingStream);
        stream = null;
        var result = AAudioNative.AAudioStream_close(closingStream);
        if (dropBufferedAudio)
        {
            Interlocked.Exchange(ref renderedBytes, Interlocked.Read(ref submittedBytes));
            Interlocked.Exchange(ref consumedBytes, Interlocked.Read(ref submittedBytes));
        }
        streamPositionBaseByte = Interlocked.Read(ref submittedBytes);
        timestampLagFrames = 0;
        PublishPlayoutSnapshot();
        ThrowIfError(result, "close");
    }

    private void ResetCore()
    {
        SetState(AudioOutputState.Stopped);
        CloseOutputCore(dropBufferedAudio: true);
        pullAudio = null;
        pendingPcm = ReadOnlyMemory<byte>.Empty;
        pendingResult = default;
        pendingOffset = 0;
        pendingEndsSource = false;
        Volatile.Write(ref sourceExhausted, 1);
        timeline.Reset();
        Interlocked.Exchange(ref ringReadPosition, 0);
        Interlocked.Exchange(ref ringWritePosition, 0);
        Interlocked.Exchange(ref submittedBytes, 0);
        Interlocked.Exchange(ref renderedBytes, 0);
        Interlocked.Exchange(ref consumedBytes, 0);
        Interlocked.Exchange(ref trackStartByte, 0);
        Interlocked.Exchange(ref sourceEndByte, 0);
        streamPositionBaseByte = 0;
        timestampLagFrames = 0;
        Volatile.Write(ref callbackStarvationCount, 0);
        observedCallbackStarvationCount = 0;
        observedNativeXRunCount = 0;
        PublishPlayoutSnapshot();

        if (!disposed && !settings.Exclusive)
        {
            TryOpenOutputCore(reportError: false);
        }
    }

    private void OnSettingsChanged(object? sender, AudioOutputSettingsChangedEventArgs e)
    {
        if (!e.ConfigurationChanged || disposed)
        {
            return;
        }

        try
        {
            playbackThread.Post(ReopenOutputCore);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private int GetNativeBufferTargetFrames()
    {
        var latencyMilliseconds = Math.Clamp(bufferTarget.TotalMilliseconds / 4, 20, 100);
        return Math.Max(1, checked((int)Math.Ceiling(Format.SampleRate * latencyMilliseconds / 1000)));
    }

    private int GetTotalBufferedBytes()
    {
        // The ring only ever feeds the AAudio buffer, so how much more it may hold is measured
        // against the endpoint's read cursor. Measuring against the presentation cursor instead
        // counts the mixer, HAL and Bluetooth delay as audio still owed to the device, which on a
        // video buffer target is already the whole allowance and starves the ring.
        var nativeBufferedBytes = Math.Max(
            0,
            Interlocked.Read(ref submittedBytes) - Interlocked.Read(ref consumedBytes));
        return checked((int)Math.Min(int.MaxValue, nativeBufferedBytes + GetRingBufferedBytes()));
    }

    private int GetRingBufferedBytes()
    {
        var available = Interlocked.Read(ref ringWritePosition) - Interlocked.Read(ref ringReadPosition);
        return (int)Math.Clamp(available, 0, ringBuffer.Length);
    }

    private int WriteRingBuffer(ReadOnlySpan<byte> source)
    {
        var readPosition = Interlocked.Read(ref ringReadPosition);
        var writePosition = Interlocked.Read(ref ringWritePosition);
        var freeBytes = ringBuffer.Length - (int)Math.Clamp(writePosition - readPosition, 0, ringBuffer.Length);
        var count = Format.AlignByteCount(Math.Min(source.Length, freeBytes));
        if (count <= 0)
        {
            return 0;
        }

        var writeIndex = (int)(writePosition % ringBuffer.Length);
        var firstCount = Math.Min(count, ringBuffer.Length - writeIndex);
        source[..firstCount].CopyTo(ringBuffer.AsSpan(writeIndex, firstCount));
        source.Slice(firstCount, count - firstCount).CopyTo(ringBuffer);
        Volatile.Write(ref ringWritePosition, writePosition + count);
        return count;
    }

    private int ReadRingBuffer(Span<byte> destination)
    {
        var readPosition = Interlocked.Read(ref ringReadPosition);
        var writePosition = Volatile.Read(ref ringWritePosition);
        var availableBytes = (int)Math.Clamp(writePosition - readPosition, 0, ringBuffer.Length);
        var count = Format.AlignByteCount(Math.Min(destination.Length, availableBytes));
        if (count <= 0)
        {
            return 0;
        }

        var readIndex = (int)(readPosition % ringBuffer.Length);
        var firstCount = Math.Min(count, ringBuffer.Length - readIndex);
        ringBuffer.AsSpan(readIndex, firstCount).CopyTo(destination);
        ringBuffer.AsSpan(0, count - firstCount).CopyTo(destination[firstCount..]);
        Volatile.Write(ref ringReadPosition, readPosition + count);
        return count;
    }

    private long GetQueuedTimelineEndByte()
    {
        var spinner = new SpinWait();
        while (true)
        {
            var sequence = Volatile.Read(ref callbackSequence);
            if ((sequence & 1) != 0)
            {
                spinner.SpinOnce();
                continue;
            }

            var queuedEnd = Interlocked.Read(ref submittedBytes) + GetRingBufferedBytes();
            if (sequence == Volatile.Read(ref callbackSequence))
            {
                return queuedEnd;
            }

            spinner.SpinOnce();
        }
    }

    private int FillAudioData(Span<byte> audioData, int numFrames)
    {
        Interlocked.Increment(ref callbackSequence);
        try
        {
            var sourceBlockAlign = Format.BlockAlign;
            var callbackStartByte = Interlocked.Read(ref submittedBytes);
            var copiedSourceBytes = 0;

            if (State == AudioOutputState.Playing)
            {
                if (outputBytesPerSample == Format.BytesPerSample)
                {
                    // The OS buffer matches the ring's sample layout exactly.
                    copiedSourceBytes = ReadRingBuffer(audioData);
                    audioData[copiedSourceBytes..].Clear();
                    if (Format.BitsPerSample == 24)
                    {
                        PcmConvert.ApplyVolumeS24(
                            audioData[..copiedSourceBytes],
                            Volatile.Read(ref volume));
                    }
                    else
                    {
                        ApplyVolume(audioData[..copiedSourceBytes], Volatile.Read(ref volume));
                    }
                }
                else
                {
                    // Fallback: narrow the decoder's packed 24-bit PCM to this 16-bit stream.
                    var wanted = Math.Min(checked(numFrames * sourceBlockAlign), callbackScratch.Length);
                    copiedSourceBytes = ReadRingBuffer(callbackScratch.AsSpan(0, wanted));
                    var writtenBytes = PcmConvert.PackedS24ToS16(
                        callbackScratch.AsSpan(0, copiedSourceBytes),
                        audioData);
                    audioData[writtenBytes..].Clear();
                    ApplyVolume(audioData[..writtenBytes], Volatile.Read(ref volume));
                }
            }
            else
            {
                audioData.Clear();
            }

            var producedFrames = sourceBlockAlign == 0 ? numFrames : copiedSourceBytes / sourceBlockAlign;
            if (producedFrames < numFrames &&
                State == AudioOutputState.Playing &&
                !SourceExhausted)
            {
                Interlocked.Increment(ref callbackStarvationCount);
            }

            if (SourceExhausted && GetRingBufferedBytes() == 0)
            {
                Interlocked.CompareExchange(
                    ref sourceEndByte,
                    callbackStartByte + copiedSourceBytes,
                    long.MaxValue);
            }

            // Advance by the full request in source (24-bit) units: any frames not filled above were
            // zeroed but are still handed to the device, so the play cursor must cover them too.
            Interlocked.Add(ref submittedBytes, checked(numFrames * sourceBlockAlign));
            return AAudioNative.AAUDIO_CALLBACK_RESULT_CONTINUE;
        }
        finally
        {
            Interlocked.Increment(ref callbackSequence);
        }
    }

    private static void ApplyVolume(Span<byte> pcm, float level)
    {
        if (level >= 1.0f)
        {
            return;
        }

        if (level <= 0.0f)
        {
            pcm.Clear();
            return;
        }

        for (var offset = 0; offset + sizeof(short) <= pcm.Length; offset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[offset..]);
            BinaryPrimitives.WriteInt16LittleEndian(pcm[offset..], (short)(sample * level));
        }
    }

    private static int GetBufferTargetBytes(AudioFormat format, TimeSpan bufferTarget)
    {
        var requestedBytes = checked((long)Math.Ceiling(format.ByteRate * bufferTarget.TotalSeconds));
        var alignedBytes = requestedBytes - requestedBytes % format.BlockAlign;
        return checked((int)Math.Max(format.BlockAlign, alignedBytes));
    }

    private static void ThrowIfError(int result, string operation)
    {
        if (result != AAudioNative.AAUDIO_OK)
        {
            throw new InvalidOperationException(
                $"AAudio could not {operation}: {AAudioNative.GetResultText(result)}.");
        }
    }

    private static unsafe void WaitForStateCore(AAudioStreamStruct* currentStream, int pendingState, int completedState, string operation)
    {
        int nextState;
        ThrowIfError(AAudioNative.AAudioStream_waitForStateChange(
            currentStream, pendingState, &nextState, StateTransitionTimeoutNanoseconds), operation);
        if (nextState != completedState)
        {
            throw new InvalidOperationException(
                $"AAudio could not {operation}: expected stream state {completedState}, received {nextState}.");
        }
    }

    private void SetState(AudioOutputState state) =>
        Volatile.Write(ref stateValue, (int)state);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnDataCallback(
        AAudioStreamStruct* callbackStream,
        void* userData,
        void* audioData,
        int numFrames)
    {
        try
        {
            if (userData == null || audioData == null || numFrames <= 0 ||
                GCHandle.FromIntPtr((nint)userData).Target is not AAudioOutputDevice owner)
            {
                return AAudioNative.AAUDIO_CALLBACK_RESULT_CONTINUE;
            }

            var byteCount = checked(numFrames * owner.outputBlockAlign);
            return owner.FillAudioData(new Span<byte>(audioData, byteCount), numFrames);
        }
        catch
        {
            return AAudioNative.AAUDIO_CALLBACK_RESULT_CONTINUE;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnErrorCallback(AAudioStreamStruct* callbackStream, void* userData, int error)
    {
        try
        {
            if (userData == null || GCHandle.FromIntPtr((nint)userData).Target is not AAudioOutputDevice owner)
            {
                return;
            }

            var failedStream = (nint)callbackStream;
            owner.playbackThread.Post(() => owner.HandleNativeErrorCore((AAudioStreamStruct*)failedStream, error));
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
        }
    }
}