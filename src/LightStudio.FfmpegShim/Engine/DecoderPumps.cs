#nullable enable
using System;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;

namespace LightStudio.FfmpegShim.Engine;

/// <summary>Result of offering a frame to the output.</summary>
internal enum Delivery
{
    Delivered,
    Dropped,
    Blocked,
}

/// <summary>
/// Shared run handling for the decoder pumps: which run is being decoded, where its seek target is, and
/// handing frames to an output that only takes the run it is on.
/// </summary>
public abstract class DecoderPump<TFrame> : IDisposable where TFrame : class, IMediaQueueItem
{
    private readonly PumpMode mode;
    private IFrameProcessor<TFrame>? output;
    private TFrame? heldFrame;

    protected DecoderPump(PacketLink input, PumpMode mode)
    {
        Input = input;
        this.mode = mode;
    }

    public PacketLink Input { get; }

    /// <summary>Interrupts a blocked hand-off so the owner can run a command. Blocking mode only.</summary>
    public CancellationToken Interrupt { get; set; }

    protected int ActiveSerial { get; private set; } = -1;

    protected double Target { get; private set; } = double.NaN;

    /// <summary>The decoder delivered end of stream for the active run.</summary>
    protected bool Ended { get; set; }

    public void SetOutput(IFrameProcessor<TFrame> processor) => Volatile.Write(ref output, processor);

    public abstract PumpResult Step();

    public virtual void Dispose()
    {
        if (heldFrame is not null)
            ReleaseFrame(heldFrame);
        heldFrame = null;
        Input.Close();
    }

    protected bool HasOutput => Volatile.Read(ref output) is not null;

    /// <summary>Retries a frame the output refused earlier. False while it is still refused.</summary>
    protected bool FlushHeldFrame()
    {
        if (heldFrame is null)
            return true;
        if (Deliver(heldFrame) == Delivery.Blocked)
            return false;
        heldFrame = null;
        return true;
    }

    /// <summary>Hands over a frame of the active run. False when the output is full; the frame is kept.</summary>
    protected bool Emit(TFrame frame)
    {
        if (Deliver(frame) != Delivery.Blocked)
            return true;
        heldFrame = frame;
        return false;
    }

    protected bool TakePacket(out MediaPacket packet) =>
        mode == PumpMode.Blocking
            ? Input.Queue.TryDequeue(out packet, Interrupt)
            : Input.Queue.TryDequeueNow(out packet);

    /// <summary>True while a newer run is waiting behind the one being finished.</summary>
    protected bool NewerRunQueued => Input.Queue.Serial != ActiveSerial;

    /// <summary>
    /// Adopts the run of <paramref name="packet"/>. Returns whether the decoder has to forget what it
    /// holds, which is every new run except one that continues the previous.
    /// </summary>
    protected bool BeginRun(MediaPacket packet)
    {
        bool reset = !Input.Continues(ActiveSerial, packet.Serial);
        ActiveSerial = packet.Serial;
        Target = Input.GetTarget(packet.Serial);
        Ended = false;
        return reset;
    }

    protected void AbandonRun()
    {
        ActiveSerial = -1;
        Target = double.NaN;
        if (heldFrame is not null)
            ReleaseFrame(heldFrame);
        heldFrame = null;
    }

    /// <summary>
    /// True while the pipeline is still working through the material between the keyframe a seek
    /// landed on and the position that was asked for. The frame straddling the target is kept, so no
    /// gap opens at the seek point.
    /// </summary>
    protected bool IsBeforeTarget(double pts, double duration) =>
        !double.IsNaN(Target) && MediaTime.IsValid(pts) && pts + Math.Max(duration, 0) <= Target;

    protected abstract void ReleaseFrame(TFrame frame);

    private Delivery Deliver(TFrame frame)
    {
        IFrameProcessor<TFrame>? target = Volatile.Read(ref output);
        if (target is null)
            return Delivery.Blocked;

        // The output switches runs only after the seek that started the new one completed.
        int serial = target.Serial;
        if (frame.Serial < serial)
        {
            ReleaseFrame(frame);
            return Delivery.Dropped;
        }

        if (frame.Serial > serial)
            return Delivery.Blocked;

        bool written = mode == PumpMode.Blocking
            ? target.Enqueue(frame, Interrupt, keepOnAbort: true)
            : target.TryWrite(frame);
        return written ? Delivery.Delivered : Delivery.Blocked;
    }
}

/// <summary>
/// Decodes the packets of one video stream. Recovery from a failed decoder is decided by the caller:
/// the pump reports the fault, stops consuming, and waits for <see cref="Recover"/>.
/// </summary>
public sealed class VideoDecoderPump : DecoderPump<VideoFrame>
{
    private readonly IPacketSource source;
    private readonly VideoDecoderStageOptions options;
    private readonly Action<DecoderFault> onFault;
    private IVideoDecoder decoder;
    private VideoOutputRequest? requestedOutput;
    private MediaPacket? pendingPacket;
    private bool receiving;
    private bool retryingSend;
    private bool madeRoom;
    private bool draining;
    private bool faulted;
    private bool recovered;
    private int failedSerial = -1;
    private bool everDecoded;

    public VideoDecoderPump(
        IVideoDecoder decoder,
        IPacketSource source,
        PacketLink input,
        PumpMode mode,
        VideoDecoderStageOptions options,
        Action<DecoderFault> onFault)
        : base(input, mode)
    {
        this.decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        this.source = source;
        this.options = options;
        this.onFault = onFault;
    }

    public IVideoDecoder Decoder => decoder;

    public VideoDecoderDiagnostics Diagnostics => decoder.Diagnostics;

    /// <summary>Applied before each receive, so a replacement decoder inherits it.</summary>
    public void RequestOutput(VideoOutputRequest? request) => Volatile.Write(ref requestedOutput, request);

    public void CancelPendingOperations() => decoder.CancelPendingOperations();

    public override PumpResult Step()
    {
        if (!HasOutput || !FlushHeldFrame())
            return PumpResult.Idle;
        if (faulted && !recovered)
            return PumpResult.Finished;

        if (receiving)
        {
            DecoderStatus drained = Receive(out bool progress);
            if (drained == DecoderStatus.Ok)
                return PumpResult.Idle;
            if (drained == DecoderStatus.Fatal)
                return Fault("The video decoder failed.");
            if (drained == DecoderStatus.EndOfStream)
            {
                receiving = draining = false;
                return PumpResult.Progressed;
            }

            if (draining)
            {
                // An asynchronous platform decoder may need time to finish; a seek ends the wait.
                if (!NewerRunQueued)
                    return progress ? PumpResult.Progressed : PumpResult.Idle;
                draining = false;
            }

            receiving = false;
            if (retryingSend && !madeRoom)
            {
                // A decoder that refused the end-of-stream marker because output was pending takes it
                // once receiving made room; an asynchronous one may need a moment first.
                if (pendingPacket is { IsEndOfStream: true })
                    return PumpResult.Idle;

                // EAGAIN from both send and receive violates FFmpeg's contract.
                options.DecodeError?.Invoke();
                return Fault("The video decoder accepted neither input nor output.");
            }
        }

        MediaPacket? packet = pendingPacket;
        pendingPacket = null;
        if (packet is null && !TakePacket(out packet))
            return PumpResult.Idle;

        if (packet.Serial != Input.Queue.Serial)
        {
            packet.Recycle();
            return PumpResult.Progressed;
        }

        if (faulted)
        {
            // A replacement decoder starts on a keyframe: the next run, or a later keyframe of this one.
            if (packet.Serial == failedSerial && !packet.IsKeyFrame && !packet.IsEndOfStream)
            {
                packet.Recycle();
                return PumpResult.Progressed;
            }

            faulted = recovered = false;
            failedSerial = -1;
        }

        if (packet.Serial != ActiveSerial)
        {
            if (BeginRun(packet))
                decoder.Flush();
            receiving = draining = retryingSend = false;
        }
        else if (Ended)
        {
            packet.Recycle();
            return PumpResult.Progressed;
        }

        DecoderStatus sent;
        try
        {
            sent = decoder.Send(packet);
        }
        catch
        {
            packet.Recycle();
            throw;
        }

        switch (sent)
        {
            case DecoderStatus.Fatal:
                packet.Recycle();
                options.DecodeError?.Invoke();
                return Fault("The video decoder rejected a packet.");

            case DecoderStatus.EndOfStream when packet.IsEndOfStream:
                packet.Recycle();
                return EmitEndOfStream();

            case DecoderStatus.NeedMoreInput:
                // Not consumed: receiving has to make room before this exact packet is retried.
                pendingPacket = packet;
                retryingSend = true;
                madeRoom = false;
                receiving = true;
                return PumpResult.Progressed;

            case DecoderStatus.Recovered:
                options.DecodeError?.Invoke();
                break;
        }

        draining = packet.IsEndOfStream;
        retryingSend = false;
        packet.Recycle();
        receiving = true;
        return PumpResult.Progressed;
    }

    /// <summary>Replaces a faulted decoder. Runs on the pump's owner.</summary>
    public bool Recover(VideoDecoderRecovery recovery, string reason)
    {
        IVideoDecoder? replacement = null;
        try
        {
            if (recovery == VideoDecoderRecovery.Software)
            {
                if (decoder.TryFallbackToSoftware())
                {
                    MarkRecovered();
                    return true;
                }

                if (decoder is FfmpegVideoDecoder { Diagnostics.IsHardwareAccelerated: false })
                    return false;
                replacement = new FfmpegVideoDecoder(source, options.Decoder with
                {
                    EnableHardwareDecoding = false,
                    SoftwareFallbackReason = reason,
                });
            }
            else
            {
                replacement = VideoDecoderFactory.Create(source, options.Decoder);
                if (!replacement.CanRestart)
                {
                    replacement.Dispose();
                    return false;
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Warning("Video.Decoder", $"Could not recover the video decoder ({recovery}).", exception);
            replacement?.Dispose();
            return false;
        }

        IVideoDecoder failed = decoder;
        decoder = replacement;
        failed.Dispose();
        MarkRecovered();
        return true;
    }

    public override void Dispose()
    {
        pendingPacket?.Recycle();
        pendingPacket = null;
        base.Dispose();
        decoder.Dispose();
    }

    protected override void ReleaseFrame(VideoFrame frame) => frame.Release();

    private void MarkRecovered()
    {
        recovered = true;
        AbandonRun();
    }

    /// <summary>
    /// Pulls frames until the decoder wants input. Ok means the output refused a frame and the pump
    /// has to wait; <paramref name="progress"/> reports whether the decoder produced anything.
    /// </summary>
    private DecoderStatus Receive(out bool progress)
    {
        progress = false;
        decoder.RequestOutput(Volatile.Read(ref requestedOutput)!);
        while (true)
        {
            DecoderStatus status = decoder.Receive(out VideoFrame? frame);
            switch (status)
            {
                case DecoderStatus.Ok when frame is not null:
                    progress = madeRoom = true;
                    if (decoder.CanRestart)
                        everDecoded = true;
                    if (IsBeforeTarget(frame.Pts, frame.Duration))
                    {
                        frame.Release();
                        continue;
                    }

                    frame.Serial = ActiveSerial;
                    options.FrameDecoded?.Invoke();
                    if (!Emit(frame))
                        return DecoderStatus.Ok;
                    continue;

                case DecoderStatus.EndOfStream:
                    EmitEndOfStream();
                    return DecoderStatus.EndOfStream;

                case DecoderStatus.Recovered:
                    progress = madeRoom = true;
                    options.DecodeError?.Invoke();
                    continue;

                case DecoderStatus.Fatal:
                    options.DecodeError?.Invoke();
                    return DecoderStatus.Fatal;

                default:
                    return DecoderStatus.NeedMoreInput;
            }
        }
    }

    private PumpResult EmitEndOfStream()
    {
        Ended = true;
        VideoFrame marker = VideoFrame.CreateEndOfStreamMarker();
        marker.Serial = ActiveSerial;
        Emit(marker);
        return PumpResult.Progressed;
    }

    private PumpResult Fault(string message)
    {
        int serial = ActiveSerial;
        faulted = true;
        recovered = false;
        failedSerial = serial;
        receiving = draining = retryingSend = false;
        pendingPacket?.Recycle();
        pendingPacket = null;
        AbandonRun();
        VideoDecoderDiagnostics diagnostics = decoder.Diagnostics;
        onFault(new DecoderFault(
            message,
            serial,
            diagnostics,
            decoder.RequiredSoftwareFallbackReason,
            decoder.CanRestart,
            everDecoded,
            decoder is FfmpegVideoDecoder && !diagnostics.IsHardwareAccelerated));
        return PumpResult.Finished;
    }
}

/// <summary>Decodes the packets of one audio stream, resampled and time-stretched to the output format.</summary>
public sealed class AudioDecoderPump : DecoderPump<AudioFrame>
{
    private readonly IAudioDecoder decoder;
    private readonly AudioDecoderStageOptions options;
    private readonly Action<DecoderFault> onFault;
    private double requestedSpeed;
    private double appliedSpeed;
    private bool receiving;
    private bool faulted;

    public AudioDecoderPump(
        IAudioDecoder decoder,
        PacketLink input,
        PumpMode mode,
        AudioDecoderStageOptions options,
        Action<DecoderFault> onFault)
        : base(input, mode)
    {
        this.decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        this.options = options;
        this.onFault = onFault;
        requestedSpeed = appliedSpeed = options.Speed;
    }

    public IAudioDecoder Decoder => decoder;

    /// <summary>Safe from any thread; applied before the next packet.</summary>
    public void SetSpeed(double speed) => Volatile.Write(ref requestedSpeed, speed);

    public void CancelPendingOperations() => decoder.CancelPendingOperations();

    public override PumpResult Step()
    {
        if (!HasOutput || !FlushHeldFrame())
            return PumpResult.Idle;
        if (faulted)
            return PumpResult.Finished;

        if (receiving)
        {
            DecoderStatus drained = Receive();
            if (drained == DecoderStatus.Ok)
                return PumpResult.Idle;
            receiving = false;
        }

        if (!TakePacket(out MediaPacket packet))
            return PumpResult.Idle;

        try
        {
            if (packet.Serial != Input.Queue.Serial)
                return PumpResult.Progressed;

            // Rate changes are applied here, on the thread that owns the resampler.
            double speed = Volatile.Read(ref requestedSpeed);
            if (Math.Abs(appliedSpeed - speed) > 1e-6)
            {
                appliedSpeed = speed;
                decoder.Configure(decoder.OutputFormat, appliedSpeed);
            }

            if (packet.Serial != ActiveSerial)
            {
                if (BeginRun(packet))
                    decoder.Flush();
            }
            else if (Ended)
            {
                return PumpResult.Progressed;
            }

            DecoderStatus sent = decoder.Send(packet);
            if (sent == DecoderStatus.Recovered)
                options.DecodeError?.Invoke();
            if (sent == DecoderStatus.Fatal)
            {
                faulted = true;
                options.DecodeError?.Invoke();
                AbandonRun();
                onFault(new DecoderFault("The audio decoder failed.", packet.Serial,
                    VideoDecoderDiagnostics.None, string.Empty, false, false, true));
                return PumpResult.Finished;
            }

            receiving = true;
            return PumpResult.Progressed;
        }
        finally
        {
            packet.Recycle();
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        decoder.Dispose();
    }

    protected override void ReleaseFrame(AudioFrame frame) => frame.Recycle();

    /// <summary>Ok means the output refused a frame and the pump has to wait.</summary>
    private DecoderStatus Receive()
    {
        while (true)
        {
            DecoderStatus status = decoder.Receive(out AudioFrame? frame);
            switch (status)
            {
                case DecoderStatus.Ok when frame is not null:
                    if (IsBeforeTarget(frame.Pts, frame.Duration))
                    {
                        frame.Recycle();
                        continue;
                    }

                    frame.Serial = ActiveSerial;
                    if (!Emit(frame))
                        return DecoderStatus.Ok;
                    continue;

                case DecoderStatus.EndOfStream:
                    Ended = true;
                    AudioFrame marker = AudioFrame.CreateEndOfStreamMarker();
                    marker.Serial = ActiveSerial;
                    Emit(marker);
                    return DecoderStatus.EndOfStream;

                case DecoderStatus.Recovered:
                    options.DecodeError?.Invoke();
                    return DecoderStatus.Recovered;

                default:
                    return status;
            }
        }
    }
}
