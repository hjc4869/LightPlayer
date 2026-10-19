using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Core;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>
/// CoreAudio playback backend built on an AUHAL output audio unit
/// (<c>kAudioUnitSubType_HALOutput</c>). A dedicated <see cref="PlaybackThread"/> buffers decoded
/// PCM ahead of the realtime render callback and owns opening, closing, device switching,
/// recovery, and periodic position updates. The interleaved 16-bit or 24-bit PCM client
/// format is converted to the device's mix format by the audio unit. Playback position is derived
/// from the render callback's host timestamp, interpolated at each read and adjusted for device
/// and stream latency. The "exclusive" option acquires CoreAudio hog mode, the closest equivalent
/// to WASAPI exclusive-mode playback.
/// </summary>
internal sealed class CoreAudioOutputDevice : IAudioOutputDevice
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LatencyRefreshInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultBufferTarget = TimeSpan.FromMilliseconds(750);
    private static readonly double HostClockFrequency = CoreAudioNative.AudioGetHostClockFrequency();

    private readonly CoreAudioSettings settings;
    private readonly PlaybackThread playbackThread;
    private readonly object dataGate = new();
    private readonly object positionGate = new();
    private readonly object disposalGate = new();
    private readonly TimeSpan bufferTarget;
    private readonly long bufferTargetBytes;
    private readonly Queue<ReadOnlyMemory<byte>> pcmQueue = new();
    private readonly CoreAudioNative.AudioStreamBasicDescription asbd;
    private readonly AudioTimelineMap timeline = new();
    private AudioPlayoutSnapshot playoutSnapshot = AudioPlayoutSnapshot.Empty;
    private Task? disposal;
    private GCHandle selfHandle;
    private IntPtr unit;
    private uint currentDeviceId;
    private bool hogModeAcquired;
    private bool appliedExclusive;
    private string? exclusiveDisabledForSelection;
    private AudioOutputPullCallback? pullAudio;
    private ReadOnlyMemory<byte> pendingPcm;
    private int pendingOffset;
    private long bufferedBytes;
    private int stateValue = (int)AudioOutputState.Stopped;
    private int sourceExhausted = 1;
    private int underrunCount;
    private int deviceSampleRate;
    private long submittedBytes;
    private long renderedBytes;
    private long trackStartByte;
    private long sourceEndByte;
    private long latencyBytes;
    private long outputLatencyBytes;
    private long renderStartByte;
    private ulong renderHostTime;
    private long nextLatencyRefreshTimestamp;
    private long nextRecoveryTimestamp;
    private long startBaselineBytes;
    private long startDeadline;
    private string? lastOpenError;
    private float volume = 1.0f;
    private volatile bool disposed;

    public CoreAudioOutputDevice(
        AudioFormat format,
        TimeSpan bufferTarget,
        CoreAudioSettings settings)
        : this(format, bufferTarget, settings, initialize: true)
    {
    }

    private CoreAudioOutputDevice(
        AudioFormat format,
        TimeSpan bufferTarget,
        CoreAudioSettings settings,
        bool initialize)
    {
        if (format.BitsPerSample is not (16 or 24) || format.ChannelCount is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"CoreAudio playback supports mono/stereo 16-bit or 24-bit PCM. Requested {format.ChannelCount} channel(s), {format.BitsPerSample}-bit.");
        }

        Format = format;
        Settings = settings;
        this.settings = settings;
        this.bufferTarget = bufferTarget > TimeSpan.Zero ? bufferTarget : DefaultBufferTarget;
        bufferTargetBytes = Math.Max(format.BlockAlign, (long)(this.bufferTarget.TotalSeconds * format.ByteRate));
        // The client stream format mirrors the decoder's PCM exactly: packed signed integer,
        // native-endian (little on every supported Mac), 16- or 24-bit. The AUHAL unit's built-in
        // converter turns this into the device's mix format, so packed 24-bit is played natively.
        asbd = new CoreAudioNative.AudioStreamBasicDescription
        {
            SampleRate = format.SampleRate,
            FormatId = CoreAudioNative.FormatLinearPCM,
            FormatFlags = CoreAudioNative.FormatFlagIsSignedInteger | CoreAudioNative.FormatFlagIsPacked,
            BytesPerPacket = (uint)format.BlockAlign,
            FramesPerPacket = 1,
            BytesPerFrame = (uint)format.BlockAlign,
            ChannelsPerFrame = format.ChannelCount,
            BitsPerChannel = format.BitsPerSample,
            Reserved = 0,
        };
        selfHandle = GCHandle.Alloc(this);
        playbackThread = new PlaybackThread("LightPlayer.CoreAudio");
        settings.Changed += OnSettingsChanged;

        if (!initialize)
        {
            return;
        }

        try
        {
            playbackThread.Invoke(InitializeCore);
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
        CoreAudioSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var device = new CoreAudioOutputDevice(format, bufferTarget, settings, initialize: false);
        try
        {
            await device.playbackThread.InvokeAsync(device.InitializeCore, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return device;
        }
        catch
        {
            await device.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void InitializeCore()
    {
        TryOpenOutputCore(reportError: false);
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
            var rendered = Interlocked.Read(ref renderedBytes);
            var end = SourceExhausted
                ? Interlocked.Read(ref sourceEndByte)
                : Interlocked.Read(ref submittedBytes) + Interlocked.Read(ref bufferedBytes);
            return Format.GetDuration(Math.Max(0, end - rendered));
        }
    }

    public TimeSpan PlayedDuration => PlayoutSnapshot.PlayedDuration;

    public AudioTimelinePosition PlayedMediaPosition => PlayoutSnapshot.Position;

    public AudioPlayoutSnapshot PlayoutSnapshot => Volatile.Read(ref playoutSnapshot);

    public bool SourceExhausted => Volatile.Read(ref sourceExhausted) != 0;

    public bool IsDrained => SourceExhausted &&
        Interlocked.Read(ref renderedBytes) >= Interlocked.Read(ref sourceEndByte);

    public int UnderrunCount => Volatile.Read(ref underrunCount);

    public event EventHandler<AudioOutputError>? BackendError;

    public event EventHandler<string>? Underrun;

    public void Start(AudioOutputPullCallback pullAudioCallback)
    {
        ArgumentNullException.ThrowIfNull(pullAudioCallback);
        ThrowIfDisposed();
        playbackThread.Invoke(() =>
        {
            ThrowIfDisposed();
            var alreadyPlaying = State == AudioOutputState.Playing && unit != IntPtr.Zero;
            lock (dataGate)
            {
                pullAudio = pullAudioCallback;
                Interlocked.Exchange(ref trackStartByte,
                    Interlocked.Read(ref submittedBytes) + Interlocked.Read(ref bufferedBytes));
                Interlocked.Exchange(ref sourceEndByte, long.MaxValue);
                Volatile.Write(ref sourceExhausted, 0);
            }

            SetState(AudioOutputState.Playing);
            if (unit == IntPtr.Zero)
            {
                TryOpenOutputCore(reportError: true);
            }

            FillPcmCore();
            if (!alreadyPlaying)
            {
                StartUnitCore();
            }
            PublishPlayoutSnapshot();
        });
    }

    public void Pause()
    {
        ThrowIfDisposed();
        playbackThread.Invoke(() =>
        {
            ThrowIfDisposed();
            if (State != AudioOutputState.Playing)
            {
                return;
            }

            DisarmStartWatchdogCore();
            if (unit != IntPtr.Zero)
            {
                CheckStatus(CoreAudioNative.AudioOutputUnitStop(unit), "pause the output audio unit");
            }

            lock (positionGate)
            {
                UpdateRenderedPositionCore();
                SetState(AudioOutputState.Paused);
                renderHostTime = 0;
            }

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
            if (unit == IntPtr.Zero)
            {
                TryOpenOutputCore(reportError: true);
            }

            StartUnitCore();
            PublishPlayoutSnapshot();
        });
    }

    public void Stop()
    {
        if (disposed)
        {
            return;
        }

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
            await playbackThread.InvokeAsync(DisposeCore).ConfigureAwait(false);
        }
        finally
        {
            await playbackThread.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Realtime render entry point. Copies queued PCM into the audio unit's buffer, pads with
    /// silence, and applies volume. Shared playback state is guarded by <see cref="dataGate"/>.
    /// </summary>
    private unsafe int Render(IntPtr actionFlagsPtr, IntPtr timeStampPtr, uint numberFrames, IntPtr bufferListPtr)
    {
        var list = (CoreAudioNative.AudioBufferList*)bufferListPtr;
        if (list is null || list->Buffers.Data == IntPtr.Zero)
        {
            return CoreAudioNative.NoErr;
        }

        var capacity = (int)list->Buffers.DataByteSize;
        var requested = checked((int)numberFrames) * Format.BlockAlign;
        var alignedCount = requested > 0 && requested < capacity ? requested : capacity;
        alignedCount -= alignedCount % Format.BlockAlign;
        var buffer = new Span<byte>((void*)list->Buffers.Data, capacity);
        if (alignedCount <= 0)
        {
            buffer.Clear();
            list->Buffers.DataByteSize = (uint)capacity;
            MarkSilence(actionFlagsPtr);
            return CoreAudioNative.NoErr;
        }

        var written = 0;
        if (!disposed && State == AudioOutputState.Playing)
        {
            lock (dataGate)
            {
                lock (positionGate)
                {
                    var timeStamp = (CoreAudioNative.AudioTimeStamp*)timeStampPtr;
                    if (timeStamp is not null &&
                        (timeStamp->Flags & CoreAudioNative.AudioTimeStampHostTimeValid) != 0)
                    {
                        renderHostTime = timeStamp->HostTime;
                        renderStartByte = Interlocked.Read(ref submittedBytes) - Interlocked.Read(ref outputLatencyBytes);
                    }
                    else
                    {
                        renderHostTime = CoreAudioNative.AudioGetCurrentHostTime();
                        renderStartByte = Interlocked.Read(ref submittedBytes) + alignedCount - Interlocked.Read(ref latencyBytes);
                    }
                }

                while (written < alignedCount && bufferedBytes > 0)
                {
                    if (pendingPcm.IsEmpty)
                    {
                        pendingPcm = pcmQueue.Dequeue();
                    }

                    var copyLength = Math.Min(alignedCount - written, pendingPcm.Length - pendingOffset);
                    pendingPcm.Span.Slice(pendingOffset, copyLength)
                        .CopyTo(buffer.Slice(written, copyLength));
                    pendingOffset += copyLength;
                    written += copyLength;
                    Interlocked.Add(ref bufferedBytes, -copyLength);
                    if (pendingOffset >= pendingPcm.Length)
                    {
                        pendingPcm = ReadOnlyMemory<byte>.Empty;
                        pendingOffset = 0;
                    }
                }

                if (written < alignedCount && !SourceExhausted)
                {
                    var underrun = Interlocked.Increment(ref underrunCount);
                    Underrun?.Invoke(this, $"CoreAudio output underrun #{underrun}.");
                }

                buffer[written..alignedCount].Clear();
                ApplyOutputVolume(buffer[..alignedCount], Volatile.Read(ref volume));
                Interlocked.Add(ref submittedBytes, alignedCount);
            }
        }
        else
        {
            buffer[..alignedCount].Clear();
        }

        if (alignedCount < capacity)
        {
            buffer[alignedCount..].Clear();
        }

        list->Buffers.DataByteSize = (uint)alignedCount;
        if (written == 0)
        {
            MarkSilence(actionFlagsPtr);
        }

        return CoreAudioNative.NoErr;
    }

    private void FillPcmCore()
    {
        if (State != AudioOutputState.Playing)
        {
            return;
        }

        lock (dataGate)
        {
            while (!SourceExhausted && bufferedBytes < bufferTargetBytes)
            {
                AudioOutputReadResult result;
                try
                {
                    result = pullAudio?.Invoke(Format.GetDuration(bufferTargetBytes - bufferedBytes))
                        ?? AudioOutputReadResult.Complete;
                    if (result.Resource is not null && result.Buffer.IsEmpty && result.ByteLength > 0)
                    {
                        throw new NotSupportedException("CoreAudio output requires locally addressable PCM.");
                    }
                }
                catch (Exception ex)
                {
                    BackendError?.Invoke(this, new AudioOutputError($"PCM source failed: {ex.Message}", ex));
                    result = AudioOutputReadResult.Complete;
                }

                var usableLength = result.Buffer.Length - result.Buffer.Length % Format.BlockAlign;
                if (usableLength > 0)
                {
                    var pcm = result.Buffer[..usableLength].ToArray();
                    timeline.Append(Interlocked.Read(ref submittedBytes) + bufferedBytes, usableLength, result);
                    pcmQueue.Enqueue(pcm);
                    Interlocked.Add(ref bufferedBytes, usableLength);
                }

                if (result.EndOfStream)
                {
                    pullAudio = null;
                    Interlocked.Exchange(ref sourceEndByte, Interlocked.Read(ref submittedBytes) + bufferedBytes);
                    Volatile.Write(ref sourceExhausted, 1);
                    break;
                }

                if (usableLength == 0)
                {
                    break;
                }
            }
        }
    }

    private void PumpSafely()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            FillPcmCore();
            RefreshLatencyCore();
            PublishPlayoutSnapshot();
            CheckStartWatchdogCore();
            if (unit == IntPtr.Zero &&
                State != AudioOutputState.Stopped &&
                Stopwatch.GetTimestamp() >= nextRecoveryTimestamp)
            {
                if (TryOpenOutputCore(reportError: true) && State == AudioOutputState.Playing)
                {
                    StartUnitCore();
                }
            }
        }
        catch (Exception ex)
        {
            BackendError?.Invoke(this, new AudioOutputError($"CoreAudio backend error: {ex.Message}", ex));
        }
    }

    private unsafe bool TryOpenOutputCore(bool reportError)
    {
        if (unit != IntPtr.Zero || disposed)
        {
            return unit != IntPtr.Zero;
        }

        IntPtr openedUnit = IntPtr.Zero;
        uint device = 0;
        var hog = false;
        var exclusive = ShouldUseExclusiveCore();
        try
        {
            device = settings.ResolveDeviceId();
            if (device == 0)
            {
                throw new InvalidOperationException("no output device is available.");
            }

            var description = new CoreAudioNative.AudioComponentDescription
            {
                Type = CoreAudioNative.AudioUnitType_Output,
                SubType = CoreAudioNative.AudioUnitSubType_HALOutput,
                Manufacturer = CoreAudioNative.AudioUnitManufacturer_Apple,
            };
            var component = CoreAudioNative.AudioComponentFindNext(IntPtr.Zero, &description);
            if (component == IntPtr.Zero)
            {
                throw new InvalidOperationException("the AUHAL output audio unit is unavailable.");
            }

            IntPtr instance;
            CheckStatus(CoreAudioNative.AudioComponentInstanceNew(component, &instance), "create the output audio unit");
            openedUnit = instance;

            CheckStatus(
                CoreAudioNative.AudioUnitSetProperty(
                    openedUnit,
                    CoreAudioNative.AudioOutputUnitProperty_CurrentDevice,
                    CoreAudioNative.AudioUnitScope_Global,
                    0,
                    &device,
                    sizeof(uint)),
                "select the output device");

            if (exclusive)
            {
                hog = TryAcquireHogModeCore(device);
                if (!hog)
                {
                    exclusiveDisabledForSelection = settings.DeviceId;
                }
            }

            var format = asbd;
            CheckStatus(
                CoreAudioNative.AudioUnitSetProperty(
                    openedUnit,
                    CoreAudioNative.AudioUnitProperty_StreamFormat,
                    CoreAudioNative.AudioUnitScope_Input,
                    0,
                    &format,
                    (uint)sizeof(CoreAudioNative.AudioStreamBasicDescription)),
                "set the client stream format");

            TrySetBufferFrameSizeCore(device);
            var bufferSizeAddress = CoreAudioNative.Address(CoreAudioNative.DeviceBufferFrameSize);
            var bufferSize = (uint)sizeof(uint);
            uint bufferFrames;
            CheckStatus(
                CoreAudioNative.AudioObjectGetPropertyData(
                    device, &bufferSizeAddress, 0, null, &bufferSize, &bufferFrames),
                "read the output buffer frame size");
            CheckStatus(
                CoreAudioNative.AudioUnitSetProperty(
                    openedUnit,
                    CoreAudioNative.AudioUnitProperty_MaximumFramesPerSlice,
                    CoreAudioNative.AudioUnitScope_Global,
                    0,
                    &bufferFrames,
                    sizeof(uint)),
                "set the maximum render frame count");

            var callback = new CoreAudioNative.AURenderCallbackStruct
            {
                InputProc = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, uint, uint, IntPtr, int>)&RenderCallback,
                InputProcRefCon = GCHandle.ToIntPtr(selfHandle),
            };
            CheckStatus(
                CoreAudioNative.AudioUnitSetProperty(
                    openedUnit,
                    CoreAudioNative.AudioUnitProperty_SetRenderCallback,
                    CoreAudioNative.AudioUnitScope_Input,
                    0,
                    &callback,
                    (uint)sizeof(CoreAudioNative.AURenderCallbackStruct)),
                "install the render callback");

            CheckStatus(CoreAudioNative.AudioUnitInitialize(openedUnit), "initialize the output audio unit");

            unit = openedUnit;
            currentDeviceId = device;
            hogModeAcquired = hog;
            appliedExclusive = hog;
            openedUnit = IntPtr.Zero;
            UpdateLatencyCore(device);
            nextLatencyRefreshTimestamp = Stopwatch.GetTimestamp()
                + (long)(LatencyRefreshInterval.TotalSeconds * Stopwatch.Frequency);
            var systemRate = settings.GetSystemSampleRate();
            if (Volatile.Read(ref deviceSampleRate) <= 0)
            {
                Volatile.Write(
                    ref deviceSampleRate,
                    systemRate > 0 ? systemRate : checked((int)Format.SampleRate));
            }

            lastOpenError = null;
            return true;
        }
        catch (Exception ex)
        {
            if (openedUnit != IntPtr.Zero)
            {
                CoreAudioNative.AudioComponentInstanceDispose(openedUnit);
            }

            if (hog && device != 0)
            {
                ReleaseHogModeCore(device);
            }

            nextRecoveryTimestamp = Stopwatch.GetTimestamp()
                + (long)(RecoveryInterval.TotalSeconds * Stopwatch.Frequency);
            if (!reportError)
            {
                throw;
            }

            var message = $"CoreAudio could not open the selected output device: {ex.Message}";
            if (reportError && !string.Equals(lastOpenError, message, StringComparison.Ordinal))
            {
                lastOpenError = message;
                BackendError?.Invoke(this, new AudioOutputError(message, ex));
            }

            return false;
        }
    }

    private void StartUnitCore()
    {
        if (unit == IntPtr.Zero)
        {
            return;
        }

        ArmStartWatchdogCore();
        var status = CoreAudioNative.AudioOutputUnitStart(unit);
        if (status == CoreAudioNative.NoErr)
        {
            return;
        }

        DisarmStartWatchdogCore();
        if (hogModeAcquired)
        {
            DisableExclusiveForSelectionCore(
                $"CoreAudio exclusive mode could not start (OSStatus {status})");
            return;
        }

        BackendError?.Invoke(
            this,
            new AudioOutputError($"CoreAudio could not start playback (OSStatus {status})."));
        CloseOutputCore(dropBufferedAudio: false);
        nextRecoveryTimestamp = Stopwatch.GetTimestamp()
            + (long)(RecoveryInterval.TotalSeconds * Stopwatch.Frequency);
    }

    private void ReopenOutputCore()
    {
        var requestedExclusive = ShouldUseExclusiveCore();
        if (unit != IntPtr.Zero &&
            appliedExclusive == requestedExclusive &&
            (currentDeviceId == settings.ResolveDeviceId() ||
                (appliedExclusive && State != AudioOutputState.Stopped && IsDefaultSelected())))
        {
            return;
        }

        CloseOutputCore(dropBufferedAudio: true);
        if (State == AudioOutputState.Stopped)
        {
            return;
        }

        if (TryOpenOutputCore(reportError: true) && State == AudioOutputState.Playing)
        {
            StartUnitCore();
        }
    }

    private void CloseOutputCore(bool dropBufferedAudio)
    {
        UpdateRenderedPositionCore();
        var closingUnit = unit;
        var closingDevice = currentDeviceId;
        var releaseHog = hogModeAcquired;
        unit = IntPtr.Zero;
        currentDeviceId = 0;
        hogModeAcquired = false;
        appliedExclusive = false;
        DisarmStartWatchdogCore();

        if (closingUnit != IntPtr.Zero)
        {
            CoreAudioNative.AudioOutputUnitStop(closingUnit);
            CoreAudioNative.AudioUnitUninitialize(closingUnit);
            CheckStatus(CoreAudioNative.AudioComponentInstanceDispose(closingUnit), "dispose the output audio unit");
        }

        lock (positionGate)
        {
            renderHostTime = 0;
            Interlocked.Exchange(ref latencyBytes, 0);
            Interlocked.Exchange(ref outputLatencyBytes, 0);
            if (dropBufferedAudio)
            {
                Interlocked.Exchange(ref renderedBytes, Interlocked.Read(ref submittedBytes));
            }
        }

        if (releaseHog && closingDevice != 0)
        {
            ReleaseHogModeCore(closingDevice);
        }
    }

    /// <summary>
    /// Re-reads the output latency instead of fixing it at open: a Bluetooth endpoint renegotiating
    /// its codec changes how far ahead of the speaker the render callback runs.
    /// </summary>
    private void RefreshLatencyCore()
    {
        if (unit == IntPtr.Zero || currentDeviceId == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now < nextLatencyRefreshTimestamp)
        {
            return;
        }

        nextLatencyRefreshTimestamp = now
            + (long)(LatencyRefreshInterval.TotalSeconds * Stopwatch.Frequency);
        UpdateLatencyCore(currentDeviceId);
    }

    private void PublishPlayoutSnapshot()
    {
        UpdateRenderedPositionCore();
        var rendered = Interlocked.Read(ref renderedBytes);
        Volatile.Write(ref playoutSnapshot, new AudioPlayoutSnapshot(
            timeline.Resolve(rendered),
            Format.GetDuration(Math.Max(0, rendered - Interlocked.Read(ref trackStartByte))),
            MediaTime.Now));
    }

    private void UpdateRenderedPositionCore()
    {
        lock (positionGate)
        {
            if (unit == IntPtr.Zero || State != AudioOutputState.Playing || renderHostTime == 0)
            {
                return;
            }

            var now = CoreAudioNative.AudioGetCurrentHostTime();
            var elapsed = now >= renderHostTime
                ? (double)(now - renderHostTime) / HostClockFrequency
                : -(double)(renderHostTime - now) / HostClockFrequency;
            var candidate = Math.Clamp(
                renderStartByte + (long)(elapsed * Format.ByteRate),
                0,
                Interlocked.Read(ref submittedBytes));
            if (candidate > Interlocked.Read(ref renderedBytes))
            {
                Interlocked.Exchange(ref renderedBytes, candidate);
            }
        }
    }

    private void ResetCore()
    {
        SetState(AudioOutputState.Stopped);
        DisarmStartWatchdogCore();
        if (unit != IntPtr.Zero)
        {
            CheckStatus(CoreAudioNative.AudioOutputUnitStop(unit), "stop the output audio unit");
            CheckStatus(
                CoreAudioNative.AudioUnitReset(unit, CoreAudioNative.AudioUnitScope_Global, 0),
                "reset the output audio unit");
        }

        lock (dataGate)
        {
            pullAudio = null;
            pcmQueue.Clear();
            pendingPcm = ReadOnlyMemory<byte>.Empty;
            pendingOffset = 0;
            Interlocked.Exchange(ref bufferedBytes, 0);
            lock (positionGate)
            {
                renderHostTime = 0;
                Interlocked.Exchange(ref renderedBytes, 0);
            }
            Volatile.Write(ref sourceExhausted, 1);
            timeline.Reset();
            Interlocked.Exchange(ref submittedBytes, 0);
            Interlocked.Exchange(ref trackStartByte, 0);
            Interlocked.Exchange(ref sourceEndByte, 0);
            Volatile.Write(ref playoutSnapshot, AudioPlayoutSnapshot.Empty);
        }
    }

    private void DisposeCore()
    {
        try
        {
            ResetCore();
        }
        finally
        {
            CloseOutputCore(dropBufferedAudio: false);
            if (selfHandle.IsAllocated)
            {
                selfHandle.Free();
            }
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
            playbackThread.Post(() =>
            {
                if (disposed)
                {
                    return;
                }

                if (!settings.Exclusive ||
                    !string.Equals(exclusiveDisabledForSelection, settings.DeviceId, StringComparison.Ordinal))
                {
                    exclusiveDisabledForSelection = null;
                }

                ReopenOutputCore();
            });
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private unsafe bool TryAcquireHogModeCore(uint device)
    {
        var address = CoreAudioNative.Address(CoreAudioNative.DeviceHogMode);
        var size = (uint)sizeof(int);
        int owner;
        if (CoreAudioNative.AudioObjectGetPropertyData(device, &address, 0, null, &size, &owner) != CoreAudioNative.NoErr)
        {
            return false;
        }

        var processId = Environment.ProcessId;
        if (owner == processId)
        {
            return true;
        }

        if (owner != -1)
        {
            BackendError?.Invoke(
                this,
                new AudioOutputError(
                    "CoreAudio exclusive mode is unavailable: the output device is in use by another process."));
            return false;
        }

        var claim = processId;
        if (CoreAudioNative.AudioObjectSetPropertyData(device, &address, 0, null, sizeof(int), &claim) != CoreAudioNative.NoErr)
        {
            BackendError?.Invoke(
                this,
                new AudioOutputError(
                    "CoreAudio could not acquire exclusive (hog) mode; continuing in shared mode."));
            return false;
        }

        return true;
    }

    private unsafe void ReleaseHogModeCore(uint device)
    {
        var address = CoreAudioNative.Address(CoreAudioNative.DeviceHogMode);
        var release = -1;
        _ = CoreAudioNative.AudioObjectSetPropertyData(device, &address, 0, null, sizeof(int), &release);
    }

    private bool ShouldUseExclusiveCore() =>
        settings.Exclusive &&
        !string.Equals(exclusiveDisabledForSelection, settings.DeviceId, StringComparison.Ordinal);

    private bool IsDefaultSelected() =>
        string.Equals(
            settings.DeviceId,
            IAudioOutputSettings.DefaultDeviceId,
            StringComparison.Ordinal);

    private void ArmStartWatchdogCore()
    {
        startBaselineBytes = Interlocked.Read(ref submittedBytes);
        startDeadline = Stopwatch.GetTimestamp()
            + (long)(StartTimeout.TotalSeconds * Stopwatch.Frequency);
    }

    private void DisarmStartWatchdogCore() => startDeadline = 0;

    private void CheckStartWatchdogCore()
    {
        if (startDeadline == 0 || State != AudioOutputState.Playing)
        {
            return;
        }

        if (Interlocked.Read(ref submittedBytes) != startBaselineBytes)
        {
            DisarmStartWatchdogCore();
            return;
        }

        if (Stopwatch.GetTimestamp() < startDeadline)
        {
            return;
        }

        DisarmStartWatchdogCore();
        if (hogModeAcquired)
        {
            DisableExclusiveForSelectionCore(
                "CoreAudio exclusive mode started but produced no render callbacks");
            return;
        }

        AppLog.Warning(
            "Audio.CoreAudio",
            $"CoreAudio output device {currentDeviceId} started but produced no render callbacks; reopening the output audio unit.");
        CloseOutputCore(dropBufferedAudio: false);
        nextRecoveryTimestamp = Stopwatch.GetTimestamp()
            + (long)(RecoveryInterval.TotalSeconds * Stopwatch.Frequency);
    }

    private void DisableExclusiveForSelectionCore(string reason)
    {
        exclusiveDisabledForSelection = settings.DeviceId;
        AppLog.Warning("Audio.CoreAudio", $"{reason}; continuing in shared mode.");
        CloseOutputCore(dropBufferedAudio: true);
        if (State == AudioOutputState.Stopped)
        {
            return;
        }

        if (TryOpenOutputCore(reportError: true) && State == AudioOutputState.Playing)
        {
            StartUnitCore();
        }
    }

    private unsafe void TrySetBufferFrameSizeCore(uint device)
    {
        try
        {
            var deviceRate = CoreAudioNative.TryGetFloat64(
                device, CoreAudioNative.DeviceNominalSampleRate, CoreAudioNative.ScopeGlobal, out var rate) && rate > 0
                ? rate
                : Format.SampleRate;
            var targetMilliseconds = Math.Clamp(bufferTarget.TotalMilliseconds / 4, 20, 100);
            var frames = (uint)Math.Max(1, deviceRate * targetMilliseconds / 1000.0);

            var rangeAddress = CoreAudioNative.Address(CoreAudioNative.DeviceBufferFrameSizeRange);
            CoreAudioNative.AudioValueRange range;
            var rangeSize = (uint)sizeof(CoreAudioNative.AudioValueRange);
            if (CoreAudioNative.AudioObjectGetPropertyData(device, &rangeAddress, 0, null, &rangeSize, &range) == CoreAudioNative.NoErr)
            {
                if (range.Minimum > 0 && frames < range.Minimum)
                {
                    frames = (uint)range.Minimum;
                }

                if (range.Maximum > 0 && frames > range.Maximum)
                {
                    frames = (uint)range.Maximum;
                }
            }

            var sizeAddress = CoreAudioNative.Address(CoreAudioNative.DeviceBufferFrameSize);
            _ = CoreAudioNative.AudioObjectSetPropertyData(device, &sizeAddress, 0, null, sizeof(uint), &frames);
        }
        catch
        {
            // The device may not permit changing its buffer size; fall back to its current latency.
        }
    }

    private unsafe void UpdateLatencyCore(uint device)
    {
        long bufferFrames = CoreAudioNative.TryGetUInt32(
            device, CoreAudioNative.DeviceBufferFrameSize, CoreAudioNative.ScopeGlobal, out var frameCount)
            ? frameCount
            : 512;
        if (CoreAudioNative.TryGetUInt32(device, CoreAudioNative.DeviceSafetyOffset, CoreAudioNative.ScopeOutput, out var safety))
        {
            bufferFrames += safety;
        }

        long outputFrames = 0;
        if (CoreAudioNative.TryGetUInt32(device, CoreAudioNative.DeviceLatency, CoreAudioNative.ScopeOutput, out var latency))
        {
            outputFrames += latency;
        }

        // A stream's latency is reported separately from its device's and the two add up; Bluetooth
        // endpoints put most of their delay on the stream.
        if (CoreAudioNative.TryGetFirstOutputStream(device, out var streamId) &&
            CoreAudioNative.TryGetUInt32(streamId, CoreAudioNative.DeviceLatency, CoreAudioNative.ScopeGlobal, out var streamLatency))
        {
            outputFrames += streamLatency;
        }

        // The counts above are in device frames; convert to the client rate the byte counters use.
        double rateRatio = 1;
        if (CoreAudioNative.TryGetFloat64(device, CoreAudioNative.DeviceNominalSampleRate, CoreAudioNative.ScopeGlobal, out var deviceRate) &&
            deviceRate > 0 &&
            Format.SampleRate > 0)
        {
            rateRatio = Format.SampleRate / deviceRate;
        }

        Interlocked.Exchange(ref outputLatencyBytes, (long)(outputFrames * rateRatio) * Format.BlockAlign);
        Interlocked.Exchange(ref latencyBytes, (long)((bufferFrames + outputFrames) * rateRatio) * Format.BlockAlign);
    }

    private static unsafe void MarkSilence(IntPtr actionFlagsPtr)
    {
        if (actionFlagsPtr != IntPtr.Zero)
        {
            *(uint*)actionFlagsPtr |= CoreAudioNative.RenderActionOutputIsSilence;
        }
    }

    private void ApplyOutputVolume(Span<byte> pcm, float level)
    {
        if (Format.BitsPerSample == 24)
        {
            PcmConvert.ApplyVolumeS24(pcm, level);
            return;
        }

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

    private static void CheckStatus(int status, string operation)
    {
        if (status != CoreAudioNative.NoErr)
        {
            throw new InvalidOperationException($"failed to {operation} (OSStatus {status}).");
        }
    }

    private void SetState(AudioOutputState state) =>
        Volatile.Write(ref stateValue, (int)state);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RenderCallback(
        IntPtr refCon,
        IntPtr actionFlags,
        IntPtr timeStamp,
        uint busNumber,
        uint numberFrames,
        IntPtr data)
    {
        if (FromHandle(refCon) is { } self)
        {
            return self.Render(actionFlags, timeStamp, numberFrames, data);
        }

        return CoreAudioNative.NoErr;
    }

    private static CoreAudioOutputDevice? FromHandle(IntPtr refCon)
    {
        if (refCon == IntPtr.Zero)
        {
            return null;
        }

        var handle = GCHandle.FromIntPtr(refCon);
        return handle.IsAllocated ? handle.Target as CoreAudioOutputDevice : null;
    }
}
