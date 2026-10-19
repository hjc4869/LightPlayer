using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace LightStudio.LightPlayer.Desktop.Windows;

internal sealed class WindowsAudioOutputDevice : IAudioOutputDevice, IWaveProvider
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultBufferTarget = TimeSpan.FromMilliseconds(750);
    private const int GaplessHandoffTimeoutMilliseconds = 100;

    // Ignore session-volume notifications for a short window after the app pushes a value: they are
    // almost always the mixer echoing our own write. Genuine external changes arrive once quiet.
    private static readonly long VolumeEchoSuppressionTicks = (long)(Stopwatch.Frequency * 0.15);
    private const float VolumeEpsilon = 0.0005f;

    private readonly WindowsAudioSettings settings;
    private readonly PlaybackThread playbackThread;
    private readonly object dataGate = new();
    private readonly TimeSpan bufferTarget;
    private readonly int[] outputFormatCandidates;
    private readonly AudioTimelineMap timeline = new();
    private AudioPlayoutSnapshot playoutSnapshot = AudioPlayoutSnapshot.Empty;
    private WaveFormat waveFormat;
    private int outputBitsPerSample;
    private byte[] readScratch = Array.Empty<byte>();
    private WasapiPlayer? output;
    private MMDevice? endpoint;
    private WindowsAudioDeviceClock? deviceClock;
    private bool deviceClockRejected;
    private AudioOutputPullCallback? pullAudio;
    private ReadOnlyMemory<byte> pendingPcm;
    private int pendingOffset;
    private bool pendingEndsSource;
    private bool pendingNextSourceExpected;
    private bool nextSourceExpected;
    private int stateValue = (int)AudioOutputState.Stopped;
    private int sourceExhausted = 1;
    private int underrunCount;
    private int deviceSampleRate;
    private long submittedBytes;
    private long renderedBytes;
    private long trackStartByte;
    private long sourceEndByte;
    private long outputPositionBaseByte;
    private long pausedSilenceFrames;
    private long nextRecoveryTimestamp;
    private string? lastOpenError;
    private bool lastOpenErrorReported;
    private float volume = 1.0f;
    private AudioSessionControl? sessionControl;
    private SimpleAudioVolume? sessionVolume;
    private SessionEventsHandler? sessionEvents;
    private volatile bool sessionVolumeActive;
    private float lastAppliedVolume = -1.0f;
    private long lastVolumeSetTicks;
    private volatile bool disposed;
    private readonly object disposalGate = new();
    private Task? disposal;

    public WindowsAudioOutputDevice(
        AudioFormat format,
        TimeSpan bufferTarget,
        WindowsAudioSettings settings)
        : this(format, bufferTarget, settings, initialize: true)
    {
    }

    private WindowsAudioOutputDevice(
        AudioFormat format,
        TimeSpan bufferTarget,
        WindowsAudioSettings settings,
        bool initialize)
    {
        if (format.BitsPerSample is not (16 or 24) || format.ChannelCount is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"Windows audio playback supports mono/stereo 16-bit or 24-bit PCM. Requested {format.ChannelCount} channel(s), {format.BitsPerSample}-bit.");
        }

        Format = format;
        Settings = settings;
        this.settings = settings;
        this.bufferTarget = bufferTarget > TimeSpan.Zero ? bufferTarget : DefaultBufferTarget;
        // Prefer handing the device the decoder's native 24-bit PCM; fall back to 16-bit only when a
        // 24-bit client format cannot be opened (e.g. an exclusive-mode DAC that rejects it).
        outputFormatCandidates = format.BitsPerSample == 24
            ? new[] { 24, 16 }
            : new[] { checked((int)format.BitsPerSample) };
        outputBitsPerSample = checked((int)format.BitsPerSample);
        waveFormat = new WaveFormat(
            checked((int)format.SampleRate),
            outputBitsPerSample,
            checked((int)format.ChannelCount));
        playbackThread = new PlaybackThread("LightPlayer.WindowsAudio");
        settings.Changed += OnSettingsChanged;

        if (!initialize)
            return;

        try
        {
            playbackThread.Invoke(() => TryOpenOutputCore(reportError: false));
            playbackThread.StartPeriodic(PumpSafely, PumpInterval);
        }
        catch
        {
            settings.Changed -= OnSettingsChanged;
            playbackThread.Dispose();
            throw;
        }
    }

    public AudioFormat Format { get; }

    internal static async ValueTask<IAudioOutputDevice> CreateAsync(
        AudioFormat format, TimeSpan bufferTarget, WindowsAudioSettings settings, CancellationToken cancellationToken)
    {
        var device = new WindowsAudioOutputDevice(format, bufferTarget, settings, initialize: false);
        try
        {
            await device.playbackThread.InvokeAsync(() =>
            {
                device.TryOpenOutputCore(reportError: false);
                device.playbackThread.StartPeriodic(device.PumpSafely, PumpInterval);
            }, cancellationToken).ConfigureAwait(false);
            return device;
        }
        catch
        {
            await device.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public IAudioOutputSettings Settings { get; }

    public WaveFormat WaveFormat => waveFormat;

    public int DeviceSampleRate => Volatile.Read(ref deviceSampleRate);

    public AudioOutputState State => (AudioOutputState)Volatile.Read(ref stateValue);

    public float Volume
    {
        get => Volatile.Read(ref volume);
        set
        {
            var clamped = Math.Clamp(value, 0.0f, 1.0f);
            // Only forward an actual change: the play loop re-asserts Volume every iteration, and an
            // unchanged re-push would clobber a level the user set in the Windows Volume Mixer. In
            // exclusive mode there is no session, so Read() uses the updated field instead.
            if (Interlocked.Exchange(ref volume, clamped) == clamped || disposed)
            {
                return;
            }

            try
            {
                playbackThread.Post(ApplyVolumeToSessionCore);
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
            var rendered = Interlocked.Read(ref renderedBytes);
            var end = SourceExhausted
                ? Interlocked.Read(ref sourceEndByte)
                : Interlocked.Read(ref submittedBytes);
            return Format.GetDuration(Math.Max(0, end - rendered));
        }
    }

    public TimeSpan PlayedDuration => Format.GetDuration(Math.Max(
        0,
        Interlocked.Read(ref renderedBytes) - Interlocked.Read(ref trackStartByte)));

    public AudioTimelinePosition PlayedMediaPosition =>
        timeline.Resolve(Interlocked.Read(ref renderedBytes));

    public AudioPlayoutSnapshot PlayoutSnapshot => Volatile.Read(ref playoutSnapshot);

    public bool SourceExhausted => Volatile.Read(ref sourceExhausted) != 0;

    public bool IsDrained => SourceExhausted &&
        Interlocked.Read(ref renderedBytes) >= Interlocked.Read(ref sourceEndByte);

    public int UnderrunCount => Volatile.Read(ref underrunCount);

    public event EventHandler<AudioOutputError>? BackendError;

    public event EventHandler<string>? Underrun;

    public event EventHandler<float>? VolumeChanged;

    public void Start(AudioOutputPullCallback pullAudioCallback)
    {
        ArgumentNullException.ThrowIfNull(pullAudioCallback);
        ThrowIfDisposed();
        playbackThread.Invoke(() => StartCore(pullAudioCallback));
    }

    public ValueTask StartAsync(AudioOutputPullCallback pullAudioCallback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pullAudioCallback);
        ThrowIfDisposed();
        return new ValueTask(playbackThread.InvokeAsync(() => StartCore(pullAudioCallback), cancellationToken));
    }

    private void StartCore(AudioOutputPullCallback pullAudioCallback)
    {
        lock (dataGate)
        {
            var trackStart = State == AudioOutputState.Playing && SourceExhausted
                ? Interlocked.Read(ref sourceEndByte)
                : Interlocked.Read(ref submittedBytes);
            pullAudio = pullAudioCallback;
            pendingPcm = ReadOnlyMemory<byte>.Empty;
            pendingOffset = 0;
            pendingEndsSource = false;
            pendingNextSourceExpected = false;
            nextSourceExpected = false;
            Interlocked.Exchange(ref trackStartByte, trackStart);
            Interlocked.Exchange(ref sourceEndByte, long.MaxValue);
            Volatile.Write(ref sourceExhausted, 0);
            Monitor.PulseAll(dataGate);
        }

        SetState(AudioOutputState.Playing);
        if (output is null)
            TryOpenOutputCore(reportError: true);
        output?.Play();
    }

    public void Pause()
    {
        ThrowIfDisposed();
        playbackThread.Invoke(() =>
        {
            if (State != AudioOutputState.Playing)
            {
                return;
            }

            UpdateRenderedPositionCore();
            // The stream deliberately keeps running: WasapiPlayer.Pause only stops refilling the
            // render buffer, and an exclusive-mode device whose buffer is never refilled has its
            // driver cycle the frames it already holds, which is heard as a short segment looping.
            // Read() feeds silence for as long as the state is paused instead.
            SetState(AudioOutputState.Paused);
        });
    }

    public void Resume()
    {
        ThrowIfDisposed();
        playbackThread.Invoke(() =>
        {
            if (State != AudioOutputState.Paused)
            {
                return;
            }

            SetState(AudioOutputState.Playing);
            if (output is null)
            {
                TryOpenOutputCore(reportError: true);
            }

            output?.Play();
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

    public ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
        new(playbackThread.InvokeAsync(Pause, cancellationToken));

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) =>
        new(playbackThread.InvokeAsync(Resume, cancellationToken));

    public ValueTask StopAsync() => disposed
        ? ValueTask.CompletedTask
        : new(playbackThread.InvokeAsync(ResetCore));

    public ValueTask ResetAfterSeekAsync(CancellationToken cancellationToken = default) =>
        new(playbackThread.InvokeAsync(ResetAfterSeek, cancellationToken));

    public int Read(Span<byte> buffer)
    {
        if (disposed)
        {
            return 0;
        }

        var state = State;
        if (state == AudioOutputState.Paused)
        {
            return ReadPausedSilence(buffer);
        }

        if (state != AudioOutputState.Playing)
        {
            return 0;
        }

        var outputBlockAlign = waveFormat.BlockAlign;
        var alignedOut = buffer.Length - buffer.Length % outputBlockAlign;
        if (alignedOut <= 0)
        {
            return 0;
        }

        var frames = alignedOut / outputBlockAlign;
        var sourceBlockAlign = Format.BlockAlign;
        var alignedCount = frames * sourceBlockAlign;
        var narrow = outputBitsPerSample != (int)Format.BitsPerSample;

        // Fill the decoder's PCM in its native (source) layout, straight into the output buffer when
        // the device accepts that format, or into a scratch that is narrowed to 16-bit afterwards.
        var fill = narrow ? EnsureReadScratch(alignedCount) : buffer[..alignedCount];

        var written = 0;
        lock (dataGate)
        {
            // Usually the handoff completes in the same Read call that observed EOS below. This
            // also covers a timed-out handoff whose next callback arrives before NAudio asks again.
            WaitForNextSourceCore();

            while (written < alignedCount && !SourceExhausted)
            {
                if (pendingOffset < pendingPcm.Length)
                {
                    var copyLength = Math.Min(alignedCount - written, pendingPcm.Length - pendingOffset);
                    pendingPcm.Span.Slice(pendingOffset, copyLength)
                        .CopyTo(fill.Slice(written, copyLength));
                    pendingOffset += copyLength;
                    written += copyLength;
                    if (pendingOffset >= pendingPcm.Length)
                    {
                        pendingPcm = ReadOnlyMemory<byte>.Empty;
                        pendingOffset = 0;
                        if (pendingEndsSource)
                        {
                            pendingEndsSource = false;
                            pullAudio = null;
                            nextSourceExpected = pendingNextSourceExpected;
                            pendingNextSourceExpected = false;
                            Volatile.Write(ref sourceExhausted, 1);
                            Interlocked.Exchange(
                                ref sourceEndByte,
                                Interlocked.Read(ref submittedBytes) + written);
                            if (WaitForNextSourceCore())
                            {
                                continue;
                            }
                            break;
                        }
                    }
                    continue;
                }

                AudioOutputReadResult result;
                try
                {
                    result = pullAudio?.Invoke(Format.GetDuration(alignedCount - written))
                        ?? AudioOutputReadResult.Complete;
                }
                catch (Exception ex)
                {
                    BackendError?.Invoke(this, new AudioOutputError($"PCM source failed: {ex.Message}", ex));
                    result = AudioOutputReadResult.Complete;
                }

                if (result.Resource is not null && result.Buffer.IsEmpty && result.ByteLength > 0)
                {
                    BackendError?.Invoke(this, new AudioOutputError("Windows audio output requires locally addressable PCM."));
                    result = AudioOutputReadResult.Complete;
                }

                if (!result.Buffer.IsEmpty)
                {
                    var usableLength = result.Buffer.Length - result.Buffer.Length % Format.BlockAlign;
                    if (usableLength > 0)
                    {
                        timeline.Append(
                            Interlocked.Read(ref submittedBytes) + written,
                            usableLength,
                            result);
                        pendingPcm = result.Buffer[..usableLength];
                        pendingOffset = 0;
                        pendingEndsSource = result.EndOfStream;
                        pendingNextSourceExpected = result.NextSourceExpected;
                        continue;
                    }
                }

                if (result.EndOfStream)
                {
                    pullAudio = null;
                    nextSourceExpected = result.NextSourceExpected;
                    Volatile.Write(ref sourceExhausted, 1);
                    Interlocked.Exchange(
                        ref sourceEndByte,
                        Interlocked.Read(ref submittedBytes) + written);
                    if (WaitForNextSourceCore())
                    {
                        continue;
                    }
                    break;
                }

                var underrun = Interlocked.Increment(ref underrunCount);
                Underrun?.Invoke(this, $"Windows audio output underrun #{underrun}.");
                break;
            }

            var writtenFrames = written / sourceBlockAlign;
            var outputBytesWritten = writtenFrames * outputBlockAlign;
            var level = Volatile.Read(ref volume);
            if (narrow)
            {
                // Narrow the decoder's packed 24-bit PCM to the 16-bit format the device opened with.
                PcmConvert.PackedS24ToS16(fill[..written], buffer[..outputBytesWritten]);
                if (!sessionVolumeActive)
                {
                    ApplyVolume(buffer[..outputBytesWritten], level);
                }
            }
            else if (!sessionVolumeActive)
            {
                if (Format.BitsPerSample == 24)
                {
                    PcmConvert.ApplyVolumeS24(buffer[..outputBytesWritten], level);
                }
                else
                {
                    ApplyVolume(buffer[..outputBytesWritten], level);
                }
            }

            Interlocked.Add(ref submittedBytes, written);
            return outputBytesWritten;
        }
    }

    /// <summary>
    /// Keeps the device fed while paused. The written frames are counted so
    /// <see cref="UpdateRenderedPositionCore"/> can discount them and leave the playback position
    /// exactly where the pause happened.
    /// </summary>
    private int ReadPausedSilence(Span<byte> buffer)
    {
        var outputBlockAlign = waveFormat.BlockAlign;
        var length = buffer.Length - buffer.Length % outputBlockAlign;
        if (length <= 0)
        {
            return 0;
        }

        buffer[..length].Clear();
        Interlocked.Add(ref pausedSilenceFrames, length / outputBlockAlign);
        return length;
    }

    private Span<byte> EnsureReadScratch(int length)
    {
        if (readScratch.Length < length)
        {
            readScratch = new byte[length];
        }

        return readScratch.AsSpan(0, length);
    }

    private bool WaitForNextSourceCore()
    {
        if (!SourceExhausted || !nextSourceExpected || disposed)
        {
            return false;
        }

        var deadline = Environment.TickCount64 + GaplessHandoffTimeoutMilliseconds;
        while (SourceExhausted && nextSourceExpected && !disposed)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return false;
            }

            Monitor.Wait(dataGate, (int)remaining);
        }

        return !SourceExhausted;
    }

    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
            return new ValueTask(disposal ??= DisposeOutputAsync());
    }

    private async Task DisposeOutputAsync()
    {
        if (disposed)
        {
            return;
        }

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
        if (disposed)
        {
            return;
        }

        try
        {
            UpdateRenderedPositionCore();
            if (output is null &&
                State != AudioOutputState.Stopped &&
                Stopwatch.GetTimestamp() >= nextRecoveryTimestamp)
            {
                TryOpenOutputCore(reportError: false);
                if (State == AudioOutputState.Playing)
                {
                    output?.Play();
                }
            }
        }
        catch (CoreAudioException ex)
        {
            HandleDeviceDisconnectedCore(ex);
        }
        catch (Exception ex)
        {
            BackendError?.Invoke(this, new AudioOutputError($"Windows audio backend error: {ex.Message}", ex));
        }
    }

    private void HandleDeviceDisconnectedCore(CoreAudioException exception)
    {
        AppLog.Debug(
            "Audio.Windows",
            $"Windows audio device was reconfigured; reconnecting: {exception.Message}");
        CloseOutputCore(dropBufferedAudio: true, updateRenderedPosition: false);
        nextRecoveryTimestamp = Stopwatch.GetTimestamp();
    }

    private bool TryOpenOutputCore(bool reportError)
    {
        if (output is not null || disposed)
        {
            return output is not null;
        }

        MMDevice? openedEndpoint = null;
        WasapiPlayer? openedOutput = null;
        try
        {
            openedEndpoint = settings.OpenSelectedDevice();
            Exception? lastError = null;
            var openedBits = 0;
            foreach (var bits in outputFormatCandidates)
            {
                try
                {
                    openedOutput = BuildOutputCore(openedEndpoint, bits);
                    openedBits = bits;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    openedOutput?.Dispose();
                    openedOutput = null;
                }
            }

            if (openedOutput is null)
            {
                throw lastError ?? new InvalidOperationException("The output device could not be opened.");
            }

            openedOutput.PlaybackStopped += OnPlaybackStopped;

            endpoint = openedEndpoint;
            output = openedOutput;
            outputBitsPerSample = openedBits;
            openedEndpoint = null;
            openedOutput = null;
            deviceClock = WindowsAudioDeviceClock.TryCreate(output);
            deviceClockRejected = false;
            outputPositionBaseByte = Interlocked.Read(ref renderedBytes);
            Interlocked.Exchange(ref pausedSilenceFrames, 0);
            var systemRate = settings.GetSystemSampleRate();
            if (Volatile.Read(ref deviceSampleRate) <= 0)
            {
                Volatile.Write(
                    ref deviceSampleRate,
                    systemRate > 0 ? systemRate : output.OutputWaveFormat.SampleRate);
            }
            AcquireSessionVolumeCore();
            lastOpenError = null;
            lastOpenErrorReported = false;
            return true;
        }
        catch (Exception ex)
        {
            openedOutput?.Dispose();
            openedEndpoint?.Dispose();
            nextRecoveryTimestamp = Stopwatch.GetTimestamp()
                + (long)(RecoveryInterval.TotalSeconds * Stopwatch.Frequency);
            var message = $"Windows audio could not open the selected output device: {ex.Message}";
            var errorChanged = !string.Equals(lastOpenError, message, StringComparison.Ordinal);
            if (errorChanged)
            {
                lastOpenError = message;
                lastOpenErrorReported = false;
            }
            if (reportError && !lastOpenErrorReported)
            {
                lastOpenErrorReported = true;
                BackendError?.Invoke(this, new AudioOutputError(message, ex));
            }
            else if (!reportError && errorChanged)
            {
                AppLog.Debug("Audio.Windows", message);
            }
            return false;
        }
    }

    private WasapiPlayer BuildOutputCore(MMDevice device, int bitsPerSample)
    {
        // The IWaveProvider format handed to NAudio matches the bit depth being attempted; Read then
        // produces exactly this format, narrowing the decoder's 24-bit PCM when it is 16-bit.
        waveFormat = new WaveFormat(
            checked((int)Format.SampleRate),
            bitsPerSample,
            checked((int)Format.ChannelCount));
        outputBitsPerSample = bitsPerSample;
        var latencyMilliseconds = (int)Math.Clamp(bufferTarget.TotalMilliseconds, 20, 200);
        var builder = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithEventSync()
            .WithLatency(latencyMilliseconds)
            .WithMmcssThreadPriority("Pro Audio");
        if (settings.Exclusive)
        {
            builder.WithExclusiveMode();
        }
        else
        {
            builder.WithSharedMode();
        }

        var player = builder.Build();
        player.Init(this);
        return player;
    }

    private void ReopenOutputCore()
    {
        CloseOutputCore(dropBufferedAudio: true);
        if (State == AudioOutputState.Stopped)
        {
            return;
        }

        if (TryOpenOutputCore(reportError: false) && State == AudioOutputState.Playing)
        {
            output!.Play();
        }
    }

    private void CloseOutputCore(bool dropBufferedAudio, bool updateRenderedPosition = true)
    {
        ReleaseSessionVolumeCore();
        if (updateRenderedPosition)
        {
            UpdateRenderedPositionCore();
        }
        if (dropBufferedAudio)
        {
            var submitted = Interlocked.Read(ref submittedBytes);
            Interlocked.Exchange(ref renderedBytes, submitted);
            outputPositionBaseByte = submitted;
        }

        // The device clock dies with the client, so the silence discount starts over as well.
        Interlocked.Exchange(ref pausedSilenceFrames, 0);

        var closingOutput = output;
        var closingEndpoint = endpoint;
        output = null;
        endpoint = null;
        deviceClock?.Dispose();
        deviceClock = null;
        deviceClockRejected = false;

        if (closingOutput is not null)
        {
            closingOutput.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                closingOutput.Dispose();
            }
            catch (CoreAudioException) when (!updateRenderedPosition)
            {
                // The endpoint is already invalid; recovery only requires releasing our references.
            }
        }
        closingEndpoint?.Dispose();
    }

    private void UpdateRenderedPositionCore()
    {
        var currentOutput = output;
        if (currentOutput is null || currentOutput.PlaybackState == PlaybackState.Stopped)
        {
            return;
        }

        var outputByteRate = currentOutput.OutputWaveFormat.AverageBytesPerSecond;
        if (outputByteRate <= 0)
        {
            return;
        }

        var submitted = Interlocked.Read(ref submittedBytes);
        var playedBytes = TryGetDevicePlayedBytes(submitted, out var devicePlayedBytes)
            ? devicePlayedBytes
            : (long)(Math.Max(0, currentOutput.GetPosition()) * ((double)Format.ByteRate / outputByteRate));
        // Frames map one-to-one between the provider and the device (only bit depth and channel
        // count are ever adapted), so the silence written while paused is discounted in frames.
        var inputBytesAtPosition = Math.Max(
            0,
            playedBytes - (Interlocked.Read(ref pausedSilenceFrames) * Format.BlockAlign));
        var candidate = Math.Min(submitted, outputPositionBaseByte + inputBytesAtPosition);
        if (candidate > Interlocked.Read(ref renderedBytes))
        {
            Interlocked.Exchange(ref renderedBytes, candidate);
        }
        Volatile.Write(ref playoutSnapshot, new AudioPlayoutSnapshot(
            PlayedMediaPosition, PlayedDuration, LightStudio.FfmpegShim.Core.MediaTime.Now));
    }

    /// <summary>
    /// Position of the sample reaching the speakers, converted from WASAPI clock units to the
    /// provider's bytes.
    /// </summary>
    private bool TryGetDevicePlayedBytes(long submitted, out long bytes)
    {
        bytes = 0;
        if (deviceClock is null || deviceClockRejected)
        {
            return false;
        }

        if (!deviceClock.TryGetPlayedSeconds(out var playedSeconds))
        {
            return false;
        }

        bytes = (long)(playedSeconds * Format.ByteRate);
        // The endpoint cannot have played more than was written to it. A driver reporting anything
        // else is not reporting this stream's position, so its clock is dropped for good.
        if (bytes > submitted + Format.ByteRate)
        {
            deviceClockRejected = true;
            bytes = 0;
            return false;
        }

        return true;
    }

    private void ResetCore()
    {
        SetState(AudioOutputState.Stopped);
        output?.Stop();
        lock (dataGate)
        {
            pullAudio = null;
            pendingPcm = ReadOnlyMemory<byte>.Empty;
            pendingOffset = 0;
            pendingEndsSource = false;
            pendingNextSourceExpected = false;
            nextSourceExpected = false;
            Volatile.Write(ref sourceExhausted, 1);
            Monitor.PulseAll(dataGate);
            timeline.Reset();
            Volatile.Write(ref playoutSnapshot, AudioPlayoutSnapshot.Empty);
            Interlocked.Exchange(ref submittedBytes, 0);
            Interlocked.Exchange(ref renderedBytes, 0);
            Interlocked.Exchange(ref trackStartByte, 0);
            Interlocked.Exchange(ref sourceEndByte, 0);
            Interlocked.Exchange(ref pausedSilenceFrames, 0);
            outputPositionBaseByte = 0;
        }
    }

    private void DisposeCore()
    {
        ResetCore();
        disposed = true;
        CloseOutputCore(dropBufferedAudio: false);
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

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        try
        {
            playbackThread.Post(() =>
            {
                if (!ReferenceEquals(sender, output) || State != AudioOutputState.Playing)
                {
                    return;
                }

                if (e.Exception is not null)
                {
                    AppLog.Debug(
                        "Audio.Windows",
                        $"Windows audio device was lost; reconnecting: {e.Exception.Message}");
                }
                ReopenOutputCore();
            });
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void AcquireSessionVolumeCore()
    {
        // Exclusive mode bypasses the shared audio engine, so the session (Volume Mixer) volume has
        // no effect there; keep per-sample scaling in Read(). Shared mode routes volume through the
        // session so the app matches and follows the Windows Volume Mixer slider for this app.
        if (endpoint is null || settings.Exclusive)
        {
            return;
        }

        try
        {
            var manager = endpoint.AudioSessionManager;
            sessionControl = manager.AudioSessionControl;
            sessionVolume = manager.SimpleAudioVolume;
            sessionEvents ??= new SessionEventsHandler(this);
            sessionControl.RegisterEventClient(sessionEvents);
            sessionVolumeActive = true;

            // Push the app's current volume onto the freshly acquired session; the echo this raises
            // is recognized as ours by OnSessionVolumeChanged.
            ApplyVolumeToSessionCore();
        }
        catch (Exception ex)
        {
            sessionVolumeActive = false;
            sessionControl = null;
            sessionVolume = null;
            BackendError?.Invoke(
                this,
                new AudioOutputError(
                    $"Windows audio session volume unavailable; using software volume: {ex.Message}",
                    ex));
        }
    }

    private void ReleaseSessionVolumeCore()
    {
        sessionVolumeActive = false;
        var control = sessionControl;
        var events = sessionEvents;
        sessionControl = null;
        sessionVolume = null;
        if (control is null || events is null)
        {
            return;
        }

        try
        {
            control.UnRegisterEventClient(events);
        }
        catch (Exception)
        {
            // The endpoint may already be gone; the session objects are released with it.
        }
    }

    private void ApplyVolumeToSessionCore()
    {
        var session = sessionVolume;
        if (session is null || disposed)
        {
            return;
        }

        var level = Volatile.Read(ref volume);
        try
        {
            // Record before the write so the OnVolumeChanged echo it raises is recognized as ours.
            Volatile.Write(ref lastAppliedVolume, level);
            Volatile.Write(ref lastVolumeSetTicks, Stopwatch.GetTimestamp());
            session.Volume = level;
        }
        catch (Exception)
        {
            // A failed set is non-fatal: the device-recovery path re-applies after reopening.
        }
    }

    private void OnSessionVolumeChanged(float sessionScalar, bool isMuted)
    {
        if (disposed)
        {
            return;
        }

        var level = isMuted ? 0.0f : Math.Clamp(sessionScalar, 0.0f, 1.0f);

        // Ignore our own writes and their in-flight echoes; only adopt a level the user set in the
        // Windows Volume Mixer.
        if (Math.Abs(level - Volatile.Read(ref lastAppliedVolume)) < VolumeEpsilon ||
            Stopwatch.GetTimestamp() - Volatile.Read(ref lastVolumeSetTicks) < VolumeEchoSuppressionTicks)
        {
            return;
        }

        Volatile.Write(ref lastAppliedVolume, level);
        Volatile.Write(ref volume, level);
        RaiseVolumeChanged(level);
    }

    private void RaiseVolumeChanged(float level)
    {
        if (disposed)
        {
            return;
        }

        // The session callback arrives on a system thread; hand off so handlers (which marshal to
        // the UI) never run inline on it.
        ThreadPool.QueueUserWorkItem(
            static state => state.Self.VolumeChanged?.Invoke(state.Self, state.Level),
            (Self: this, Level: level),
            preferLocal: false);
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

    private void SetState(AudioOutputState state) =>
        Volatile.Write(ref stateValue, (int)state);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    /// <summary>
    /// Bridges NAudio's managed session notifications to <see cref="OnSessionVolumeChanged"/> so a
    /// volume the user changes in the Windows Volume Mixer flows back to the app. Only the volume
    /// notification is of interest; the others are ignored.
    /// </summary>
    private sealed class SessionEventsHandler(WindowsAudioOutputDevice owner) : IAudioSessionEventsHandler
    {
        public void OnVolumeChanged(float volume, bool isMuted) =>
            owner.OnSessionVolumeChanged(volume, isMuted);

        public void OnDisplayNameChanged(string displayName)
        {
        }

        public void OnIconPathChanged(string iconPath)
        {
        }

        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex)
        {
        }

        public void OnGroupingParamChanged(ref Guid groupingId)
        {
        }

        public void OnStateChanged(AudioSessionState state)
        {
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
        }
    }
}