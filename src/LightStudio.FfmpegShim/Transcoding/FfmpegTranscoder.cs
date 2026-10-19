#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Interop;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Transcoding;

/// <summary>
/// Re-encodes one elementary stream: packets in, packets out, in the same time base they arrived in.
/// </summary>
/// <remarks>
/// <para>
/// The whole pipeline -- decoder, filter chain and encoder -- is stood up before a single packet has
/// been seen, so the track's own codec parameters, extradata and all, are knowable at the moment it
/// is offered to a client. A hardware decoder's frame pool is built here rather than left to the
/// decoder's first packet, which is the only way the filter chain and the encoder behind it can be
/// configured that early.
/// </para>
/// <para>
/// Frames stay on the device wherever the device can do what is being asked of it: hardware decode
/// hands its surfaces straight to the device's own scaler and from there to the encoder, and nothing
/// crosses the bus. Software decode is the fallback, and uploads once, after the picture has been
/// brought down to the size being sent.
/// </para>
/// <para>
/// Nothing here is thread safe. One of these belongs to one run of one track.
/// </para>
/// </remarks>
public sealed unsafe partial class FfmpegTranscoder : IDisposable
{
    private static long nextId;

    /// <summary><c>AV_BUFFERSRC_FLAG_KEEP_REF</c>.</summary>
    private const int KeepFrameReference = 8;

    /// <summary>
    /// Surfaces added to what the decoder asked for, covering the ones the filter chain and the
    /// encoder hold on to. A pool that runs dry stalls the decoder outright.
    /// </summary>
    private const int ExtraSurfaces = 8;

    /// <summary>Bits per pixel per second assumed when a client names no bitrate of its own.</summary>
    private const double DefaultBitsPerPixel = 0.10;

    private static readonly AVSampleFormat[] CandidateSampleFormats =
    [
        AVSampleFormat.AV_SAMPLE_FMT_FLTP,
        AVSampleFormat.AV_SAMPLE_FMT_FLT,
        AVSampleFormat.AV_SAMPLE_FMT_S16,
        AVSampleFormat.AV_SAMPLE_FMT_S16P,
        AVSampleFormat.AV_SAMPLE_FMT_S32,
        AVSampleFormat.AV_SAMPLE_FMT_S32P,
    ];

    /// <summary>Rates to fall back to when the encoder will not take the source's own.</summary>
    private static readonly int[] FallbackSampleRates = [48000, 24000, 16000];

    private readonly TranscodeOptions options;
    private readonly TranscodeEncoder choice;
    private readonly HardwareDecoderSettings hardwareSettings;
    private readonly AVRational streamTimeBase;
    private readonly Queue<IntPtr> ready = new();
    private readonly Queue<byte[]?> decodedHdr10Plus = new();
    private readonly long id;

    private AVCodecParameters* sourceParameters;
    private AVCodec* videoEncoderCodec;
    private TranscodeVideoTarget? videoTarget;
    private int sourceFrameRateNum;
    private int sourceFrameRateDen = 1;
    private readonly int sourceRotation;
    private readonly bool sourceMirrorHorizontally;
    private readonly double sourceHdrPeakLuminance;
    private readonly LightMediaRenderer.Contracts.VideoLuminance? sourceHdrMasteringLuminance;
    private AVBufferRef* hardwareDevice;
    private AVCodecContext* decoder;
    private AVCodecContext* encoder;
    private AVFilterGraph* graph;
    private AVFilterContext* graphSource;
    private AVFilterContext* graphSink;
    private AVFrame* decoded;
    private AVFrame* filtered;
    private AVRational graphTimeBase;
    private AVRational encoderTimeBase;
    private GCHandle owner;
    private AVPixelFormat hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool waitingForVideoKeyFrame;
    private bool graphReady;
    private bool graphFlushed;
    private bool encoderFlushed;
    private bool encoderCopiesOpaque;
    private bool disposed;

    private FfmpegTranscoder(CodecParameters source, TranscodeOptions options, TranscodeEncoder choice)
    {
        id = Interlocked.Increment(ref nextId);
        this.options = options;
        this.choice = choice;
        hardwareSettings = options.HardwareSettings ?? HardwareDecoderSettings.Empty;
        streamTimeBase = source.NativeTimeBase();
        sourceFrameRateNum = source.FrameRateNum;
        sourceFrameRateDen = source.FrameRateDen > 0 ? source.FrameRateDen : 1;
        sourceRotation = source.Rotation;
        sourceMirrorHorizontally = source.MirrorHorizontally;
        sourceHdrPeakLuminance = source.HdrPeakLuminance;
        sourceHdrMasteringLuminance = source.HdrMasteringLuminance;
        OutputParameters = source;

        sourceParameters = source.ToNative();
        decoded = av_frame_alloc();
        filtered = av_frame_alloc();
        if (decoded == null || filtered == null)
        {
            throw new FfmpegException("av_frame_alloc returned null", 0);
        }
    }

    /// <summary>
    /// Everything a client needs to decode what this produces. The time base is the source stream's,
    /// so timestamps mean on the client exactly what they meant in the container.
    /// </summary>
    /// <remarks>
    /// Only meaningful once <see cref="IsReady"/> is set. A video encoder cannot be configured until
    /// a picture has come out of the decoder, because whether that picture is on the device, and the
    /// pool it belongs to if it is, is settled by libavcodec as it decodes.
    /// </remarks>
    public CodecParameters OutputParameters { get; private set; }

    /// <summary>Whether the encoder is open, and <see cref="OutputParameters"/> therefore describe it.</summary>
    public bool IsReady { get; private set; }

    /// <summary>What the pipeline turned out to be, for the server's own logging.</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Whether the pictures are decoded on the device they are re-encoded on.</summary>
    public bool DecodesOnDevice { get; private set; }

    public static FfmpegTranscoder CreateVideo(
        CodecParameters source,
        TranscodeVideoTarget target,
        TranscodeCapabilities capabilities,
        TranscodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(options);

        TranscodeEncoder choice = capabilities.FindVideo(target.CodecName)
            ?? throw new NotSupportedException($"This server cannot encode video as {target.CodecName}.");

        var transcoder = new FfmpegTranscoder(source, options, choice);
        AppLog.Information(
            "FFmpeg.Transcode",
            $"#{transcoder.id} creating video pipeline: source={source.CodecName} {source.Width}x{source.Height}; " +
            $"target={target.CodecName} max={target.MaxWidth}x{target.MaxHeight} bitRate={target.BitRate}; " +
            $"encoder={choice.EncoderName}; encoderInterface={(choice.IsHardware ? choice.EncoderInterface : "cpu")}; " +
            $"deviceType={(choice.IsHardware ? choice.DeviceType : "cpu")}; " +
            $"hardwareDecode={options.EnableHardware}; hardwareScaling={options.EnableHardwareScaling}.");
        try
        {
            transcoder.OpenVideo(source, target);
            return transcoder;
        }
        catch (Exception exception)
        {
            AppLog.Error("FFmpeg.Transcode", $"#{transcoder.id} video pipeline creation failed.", exception);
            transcoder.Dispose();
            throw;
        }
    }

    public static FfmpegTranscoder CreateAudio(
        CodecParameters source,
        TranscodeAudioTarget target,
        TranscodeCapabilities capabilities,
        TranscodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(options);

        TranscodeEncoder choice = capabilities.FindAudio(target.CodecName)
            ?? throw new NotSupportedException($"This server cannot encode audio as {target.CodecName}.");

        var transcoder = new FfmpegTranscoder(source, options, choice);
        AppLog.Information(
            "FFmpeg.Transcode",
            $"#{transcoder.id} creating audio pipeline: source={source.CodecName} {source.SampleRate} Hz " +
            $"{source.ChannelCount} ch; target={target.CodecName} bitRate={target.BitRate}; encoder={choice.EncoderName}.");
        try
        {
            transcoder.OpenAudio(source, target);
            return transcoder;
        }
        catch (Exception exception)
        {
            AppLog.Error("FFmpeg.Transcode", $"#{transcoder.id} audio pipeline creation failed.", exception);
            transcoder.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Hands one source packet to the pipeline; null tells it the track has ended. Whatever comes
    /// out is left for <see cref="TryReceive"/>.
    /// </summary>
    public void Send(MediaPacket? packet)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        // A null packet is the drain marker libavcodec itself takes, so the end of the track needs
        // nothing of its own here.
        AVPacket* handle = packet is null ? null : packet.Handle;
        if (handle != null && waitingForVideoKeyFrame)
        {
            if ((handle->flags & AV_PKT_FLAG_KEY) == 0)
            {
                return;
            }

            waitingForVideoKeyFrame = false;
        }

        while (true)
        {
            int sent = avcodec_send_packet(decoder, handle);
            if (sent >= 0 || FfmpegError.IsEndOfFile(sent))
            {
                return;
            }

            if (!FfmpegError.IsTryAgain(sent))
            {
                FfmpegError.ThrowIfError(sent, "avcodec_send_packet");
            }

            // The decoder will not take another packet until what it has already produced has been
            // taken off it, so the whole chain is drained before trying again.
            Pump();
        }
    }

    /// <summary>Takes the next re-encoded packet. False once nothing more can be produced yet.</summary>
    public bool TryReceive(MediaPacket destination, int streamIndex)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(destination);

        if (ready.Count == 0)
        {
            Pump();
        }

        if (ready.Count == 0)
        {
            return false;
        }

        var packet = (AVPacket*)ready.Dequeue();
        av_packet_move_ref(destination.Handle, packet);
        av_packet_free(&packet);
        destination.CaptureFromEncoder(streamIndex);
        return true;
    }

    /// <summary>
    /// Runs everything the pipeline can do without more input: decoded frames into the filter chain,
    /// filtered frames into the encoder, encoded packets onto the queue.
    /// </summary>
    private void Pump()
    {
        while (true)
        {
            if (DrainEncoder())
            {
                continue;
            }

            if (graphReady && ForwardFilteredFrame())
            {
                continue;
            }

            if (ForwardDecodedFrame())
            {
                continue;
            }

            return;
        }
    }

    /// <summary>Moves everything the encoder has onto the queue. True when the chain moved on.</summary>
    private bool DrainEncoder()
    {
        if (encoder == null)
        {
            return false;
        }

        var produced = false;
        while (true)
        {
            AVPacket* packet = av_packet_alloc();
            if (packet == null)
            {
                throw new FfmpegException("av_packet_alloc returned null", 0);
            }

            int received = avcodec_receive_packet(encoder, packet);
            if (received >= 0)
            {
                try
                {
                    RestoreHdr10PlusMetadata(packet);
                    // The client reads timestamps in the container's own units, so what the encoder
                    // counted in comes off here rather than being explained on the wire.
                    av_packet_rescale_ts(packet, encoderTimeBase, streamTimeBase);
                    ready.Enqueue((IntPtr)packet);
                    produced = true;
                }
                catch
                {
                    av_packet_free(&packet);
                    throw;
                }
                continue;
            }

            av_packet_free(&packet);
            if (FfmpegError.IsTryAgain(received) || FfmpegError.IsEndOfFile(received))
            {
                return produced;
            }

            FfmpegError.ThrowIfError(received, "avcodec_receive_packet");
        }
    }

    /// <summary>Takes one filtered frame and gives it to the encoder. True when something happened.</summary>
    private bool ForwardFilteredFrame()
    {
        int received = av_buffersink_get_frame(graphSink, filtered);
        if (received >= 0)
        {
            try
            {
                byte[]? hdr10PlusMetadata = null;
                if (videoTarget is not null)
                {
                    hdr10PlusMetadata = CopyHdr10PlusMetadata(filtered);
                    if (decodedHdr10Plus.Count > 0)
                    {
                        byte[]? decodedMetadata = decodedHdr10Plus.Dequeue();
                        hdr10PlusMetadata ??= decodedMetadata;
                    }
                }

                RetimeForEncoder(filtered);
                if (hdr10PlusMetadata is not null && !encoderCopiesOpaque)
                {
                    throw new FfmpegException(
                        $"Encoder '{choice.EncoderName}' cannot preserve HDR10+ frame metadata.",
                        0);
                }

                StoreHdr10PlusInOpaqueReference(filtered, hdr10PlusMetadata);
                FfmpegError.ThrowIfError(avcodec_send_frame(encoder, filtered), "avcodec_send_frame");
            }
            finally
            {
                av_frame_unref(filtered);
            }

            return true;
        }

        if (FfmpegError.IsEndOfFile(received) && !encoderFlushed)
        {
            encoderFlushed = true;
            FfmpegError.ThrowIfError(avcodec_send_frame(encoder, null), "avcodec_send_frame");
            return true;
        }

        if (!FfmpegError.IsTryAgain(received) && !FfmpegError.IsEndOfFile(received))
        {
            FfmpegError.ThrowIfError(received, "av_buffersink_get_frame");
        }

        return false;
    }

    /// <summary>Takes one decoded frame and gives it to the filter chain.</summary>
    private bool ForwardDecodedFrame()
    {
        int received = avcodec_receive_frame(decoder, decoded);
        if (received >= 0)
        {
            try
            {
                if (sourceParameters->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO &&
                    sourceParameters->sample_aspect_ratio.num > 0 &&
                    sourceParameters->sample_aspect_ratio.den > 0)
                {
                    decoded->sample_aspect_ratio = sourceParameters->sample_aspect_ratio;
                }

                EnsureGraph(decoded);
                byte[]? hdr10PlusMetadata = sourceParameters->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO
                    ? CopyHdr10PlusMetadata(decoded)
                    : null;
                FfmpegError.ThrowIfError(
                    av_buffersrc_add_frame_flags(graphSource, decoded, KeepFrameReference),
                    "av_buffersrc_add_frame_flags");
                if (sourceParameters->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    // Every configured video chain changes pixels only, so decoded and filtered
                    // frames stay one-for-one even when a hardware filter drops arbitrary side data.
                    decodedHdr10Plus.Enqueue(hdr10PlusMetadata);
                }
            }
            finally
            {
                av_frame_unref(decoded);
            }

            return true;
        }

        if (FfmpegError.IsEndOfFile(received) && graphReady && !graphFlushed)
        {
            graphFlushed = true;
            FfmpegError.ThrowIfError(
                av_buffersrc_add_frame_flags(graphSource, null, 0),
                "av_buffersrc_add_frame_flags");
            return true;
        }

        if (FfmpegError.IsEndOfFile(received) && !graphReady && !encoderFlushed && encoder != null)
        {
            // The track ended before a single frame arrived, so there is no chain to flush and the
            // encoder has nothing to give back.
            encoderFlushed = true;
            FfmpegError.ThrowIfError(avcodec_send_frame(encoder, null), "avcodec_send_frame");
            return true;
        }

        if (!FfmpegError.IsTryAgain(received) && !FfmpegError.IsEndOfFile(received))
        {
            FfmpegError.ThrowIfError(received, "avcodec_receive_frame");
        }

        return false;
    }

    private static byte[]? CopyHdr10PlusMetadata(AVFrame* frame)
    {
        AVFrameSideData* sideData = frame == null ? null :
            av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS);
        if (sideData == null || sideData->data == null || sideData->size < (nuint)sizeof(AVDynamicHDRPlus))
        {
            return null;
        }

        return new ReadOnlySpan<byte>(sideData->data, sizeof(AVDynamicHDRPlus)).ToArray();
    }

    private static void StoreHdr10PlusInOpaqueReference(AVFrame* frame, byte[]? metadata)
    {
        if (frame->opaque_ref != null)
        {
            AVBufferRef* previous = frame->opaque_ref;
            frame->opaque_ref = null;
            av_buffer_unref(&previous);
        }

        if (metadata is null)
        {
            return;
        }

        AVBufferRef* reference = av_buffer_alloc((nuint)metadata.Length);
        if (reference == null)
        {
            throw new OutOfMemoryException("Could not preserve transcoded HDR10+ metadata.");
        }

        metadata.CopyTo(new Span<byte>(reference->data, metadata.Length));
        frame->opaque_ref = reference;
    }

    private void RestoreHdr10PlusMetadata(AVPacket* packet)
    {
        if (packet->opaque_ref == null)
        {
            return;
        }

        AddHdr10PlusMetadata(packet, new ReadOnlySpan<byte>(
            packet->opaque_ref->data,
            sizeof(AVDynamicHDRPlus)));
    }

    private static void AddHdr10PlusMetadata(AVPacket* packet, ReadOnlySpan<byte> metadata)
    {
        nuint existingSize = 0;
        if (av_packet_get_side_data(
                packet,
                AVPacketSideDataType.AV_PKT_DATA_DYNAMIC_HDR10_PLUS,
                &existingSize) != null && existingSize > 0)
        {
            return;
        }

        byte* destination = av_packet_new_side_data(
            packet,
            AVPacketSideDataType.AV_PKT_DATA_DYNAMIC_HDR10_PLUS,
            (nuint)metadata.Length);
        if (destination == null)
        {
            throw new OutOfMemoryException("Could not preserve transcoded HDR10+ metadata.");
        }

        metadata.CopyTo(new Span<byte>(destination, metadata.Length));
    }

    /// <summary>
    /// Puts a filtered frame into the encoder's own units. The two agree for video, where the
    /// encoder took the chain's time base; audio resampling gives the chain one of its own.
    /// </summary>
    private void RetimeForEncoder(AVFrame* frame)
    {
        if (graphTimeBase.num == encoderTimeBase.num && graphTimeBase.den == encoderTimeBase.den)
        {
            return;
        }

        if (frame->pts != AV_NOPTS_VALUE)
        {
            frame->pts = av_rescale_q(frame->pts, graphTimeBase, encoderTimeBase);
        }

        long duration = frame->duration;
        if (duration > 0)
        {
            frame->duration = av_rescale_q(duration, graphTimeBase, encoderTimeBase);
        }
    }

    private void OpenVideo(CodecParameters source, TranscodeVideoTarget target)
    {
        videoEncoderCodec = avcodec_find_encoder_by_name(choice.EncoderName);
        if (videoEncoderCodec == null)
        {
            throw new FfmpegException($"This build has no encoder named '{choice.EncoderName}'.", 0);
        }

        videoTarget = target;
        waitingForVideoKeyFrame = true;
        if (choice.IsHardware)
        {
            hardwareDevice = FfmpegTranscodeCapabilities.TryCreateDevice(choice.DeviceType, options.HardwareSettings);
            if (hardwareDevice == null)
            {
                throw new FfmpegException($"The {choice.DeviceType} device would not open.", 0);
            }
        }

        OpenVideoDecoder();
        Description = $"{choice.EncoderName}, not yet configured";
        AppLog.Information("FFmpeg.Transcode", $"#{id} decoder opened; encoder waits for the first decoded frame.");
    }

    private void OpenVideoDecoder()
    {
        AVCodec* software = avcodec_find_decoder(sourceParameters->codec_id);
        if (software == null)
        {
            throw new FfmpegException("This build cannot decode the source video.", 0);
        }

        if (hardwareDevice != null && options.EnableHardware && TryOpenVideoDecoderOnDevice(software))
        {
            AppLog.Information(
                "FFmpeg.Transcode",
                $"#{id} opened hardware video decoder '{FfmpegTranscodeCapabilities.NameOf(decoder->codec)}' " +
                $"on {choice.DeviceType}; requestedPixelFormat={PixelFormatName(hardwarePixelFormat)}.");
            return;
        }

        if (hardwareDevice != null && options.EnableHardware)
        {
            AppLog.Warning(
                "FFmpeg.Transcode",
                $"#{id} could not open a {choice.DeviceType} decoder for the source; falling back to software decode.");
        }

        decoder = CreateDecoderContext(software);
        FfmpegError.ThrowIfError(avcodec_open2(decoder, software, null), "avcodec_open2");
        AppLog.Information(
            "FFmpeg.Transcode",
            $"#{id} opened software video decoder '{FfmpegTranscodeCapabilities.NameOf(software)}' with {decoder->thread_count} threads.");
    }

    /// <summary>
    /// Opens the decoder against the encoder's device, so its pictures are already where they have
    /// to be to be re-encoded.
    /// </summary>
    /// <remarks>
    /// Whether it actually decodes there is not settled here: the format callback runs when the
    /// first packet arrives and libavcodec falls back to a software format if the device turns out
    /// not to take this profile. <see cref="DecodesOnDevice"/> reports what the frames turned out
    /// to be, not what was asked for.
    /// </remarks>
    private bool TryOpenVideoDecoderOnDevice(AVCodec* software)
    {
        AVHWDeviceType deviceType = FfmpegHardwareDevices.FindType(choice.DeviceType);
        if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            AppLog.Warning("FFmpeg.Transcode", $"#{id} FFmpeg does not recognize device type '{choice.DeviceType}'.");
            return false;
        }

        AVCodec* candidate = software;
        AVCodecHWConfig* config = FindHardwareConfig(candidate, deviceType);
        if (config == null)
        {
            candidate = FindDecoderForDevice(deviceType, out config);
            if (candidate == null)
            {
                AppLog.Warning(
                    "FFmpeg.Transcode",
                    $"#{id} no decoder for codec id {sourceParameters->codec_id} advertises {choice.DeviceType}.");
                return false;
            }
        }

        AVCodecContext* context = CreateDecoderContext(candidate);
        try
        {
            context->hw_device_ctx = av_buffer_ref(hardwareDevice);
            hardwarePixelFormat = config->pix_fmt;
            owner = GCHandle.Alloc(this);
            context->opaque = (void*)GCHandle.ToIntPtr(owner);
            context->get_format = &SelectHardwareFormat;

            // Room for the surfaces the filter chain and the encoder hold on to. A pool sized for
            // the decoder alone runs dry and stalls it.
            context->extra_hw_frames = ExtraSurfaces;

            int opened = avcodec_open2(context, candidate, null);
            if (opened < 0)
            {
                AppLog.Warning(
                    "FFmpeg.Transcode",
                    $"#{id} hardware decoder '{FfmpegTranscodeCapabilities.NameOf(candidate)}' would not open: " +
                    FfmpegError.Describe(opened));
                return false;
            }

            decoder = context;
            context = null;
            return true;
        }
        finally
        {
            if (context != null)
            {
                avcodec_free_context(&context);
                if (owner.IsAllocated)
                {
                    owner.Free();
                }
            }
        }
    }

    private AVCodec* FindDecoderForDevice(AVHWDeviceType deviceType, out AVCodecHWConfig* config)
    {
        void* iterator = null;
        AVCodec* candidate;
        while ((candidate = av_codec_iterate(&iterator)) != null)
        {
            if (candidate->id != sourceParameters->codec_id ||
                candidate->type != AVMediaType.AVMEDIA_TYPE_VIDEO ||
                av_codec_is_decoder(candidate) == 0)
            {
                continue;
            }

            config = FindHardwareConfig(candidate, deviceType);
            if (config != null)
            {
                return candidate;
            }
        }

        config = null;
        return null;
    }

    private AVCodecContext* CreateDecoderContext(AVCodec* codec)
    {
        AVCodecContext* context = avcodec_alloc_context3(codec);
        if (context == null)
        {
            throw new FfmpegException("avcodec_alloc_context3 returned null", 0);
        }

        int copied = avcodec_parameters_to_context(context, sourceParameters);
        if (copied < 0)
        {
            avcodec_free_context(&context);
            FfmpegError.ThrowIfError(copied, "avcodec_parameters_to_context");
        }

        context->pkt_timebase = streamTimeBase;
        context->thread_count = ThreadCount();

        // Frame and slice threading is what makes software decoding of a large picture viable at
        // all, and the encoder behind it is waiting on every frame this produces.
        context->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
        return context;
    }

    /// <summary>
    /// Stands the chain and the encoder up from the first picture the decoder produced.
    /// </summary>
    /// <remarks>
    /// It has to be the frame rather than the container's own description: whether the picture came
    /// back on the device, and the frames context it belongs to if it did, is settled by libavcodec
    /// when it decodes, and neither can be asked for before then.
    /// </remarks>
    private void BuildVideoGraph(AVFrame* frame)
    {
        DecodesOnDevice = frame->hw_frames_ctx != null;
        FillMissingColorMetadata(frame);

        AVPixelFormat inputFormat = (AVPixelFormat)frame->format;
        if (inputFormat == AVPixelFormat.AV_PIX_FMT_NONE)
        {
            throw new FfmpegException("The decoder produced a picture of no stated format.", 0);
        }

        AVRational aspect = frame->sample_aspect_ratio.num > 0 && frame->sample_aspect_ratio.den > 0
            ? frame->sample_aspect_ratio
            : new AVRational { num = 1, den = 1 };

        (int width, int height) = FitWithin(
            frame->width,
            frame->height,
            videoTarget!.MaxWidth,
            videoTarget.MaxHeight);
        AVPixelFormat hardwareSoftwareFormat = HardwareSoftwareFormat(frame);
        string encoderPixelFormat = SelectEncoderPixelFormat(inputFormat, hardwareSoftwareFormat);

        (IReadOnlyList<string> Filters, bool UsesHardwareScaler) plan =
            BuildVideoChain(
                width,
                height,
                hardwareSoftwareFormat,
                encoderPixelFormat,
                options.EnableHardwareScaling);
        LogVideoPlan(frame, inputFormat, hardwareSoftwareFormat, width, height, plan.Filters);
        try
        {
            ConfigureVideoGraph(frame, inputFormat, aspect, plan.Filters);
            graphReady = true;
            OpenVideoEncoder(frame);
        }
        catch (FfmpegException exception) when (plan.UsesHardwareScaler)
        {
            AppLog.Warning(
                "FFmpeg.Transcode",
                $"#{id} hardware scaler '{plan.Filters[0]}' was rejected; retrying with copy-back scaling.",
                exception);
            ResetVideoGraph();
            plan = BuildVideoChain(
                width,
                height,
                hardwareSoftwareFormat,
                encoderPixelFormat,
                enableHardwareScaling: false);
            LogVideoPlan(frame, inputFormat, hardwareSoftwareFormat, width, height, plan.Filters);
            ConfigureVideoGraph(frame, inputFormat, aspect, plan.Filters);
            graphReady = true;
            OpenVideoEncoder(frame);
        }

        PublishOutput();

        Description = string.Create(
            CultureInfo.InvariantCulture,
            $"{choice.EncoderName} {encoder->width}x{encoder->height} @ {encoder->bit_rate / 1000} kb/s, " +
            $"decoded {(DecodesOnDevice ? "on " + choice.DeviceType : "in software")}");
        AppLog.Information("FFmpeg.Transcode", $"#{id} video pipeline ready: {Description}.");
    }

    private void ConfigureVideoGraph(
        AVFrame* frame,
        AVPixelFormat inputFormat,
        AVRational aspect,
        IReadOnlyList<string> chain)
    {
        AllocateGraph();
        graphSource = AllocateFilter("buffer", "in");

        // The pool has to be on the source before it is initialized: a buffer told to expect a
        // hardware format with no frames context yet refuses to open at all.
        if (frame->hw_frames_ctx != null)
        {
            AVBufferSrcParameters* parameters = av_buffersrc_parameters_alloc();
            if (parameters == null)
            {
                throw new FfmpegException("av_buffersrc_parameters_alloc returned null", 0);
            }

            parameters->hw_frames_ctx = frame->hw_frames_ctx;
            int applied = av_buffersrc_parameters_set(graphSource, parameters);
            av_free(parameters);
            FfmpegError.ThrowIfError(applied, "av_buffersrc_parameters_set");
        }

        InitializeFilter(
            graphSource,
            "buffer",
            string.Create(
                CultureInfo.InvariantCulture,
                $"video_size={frame->width}x{frame->height}:pix_fmt={(int)inputFormat}:" +
                $"time_base={streamTimeBase.num}/{streamTimeBase.den}:" +
                $"pixel_aspect={aspect.num}/{aspect.den}:" +
                $"colorspace={(int)frame->colorspace}:range={(int)frame->color_range}"));

        graphSink = AllocateFilter("buffersink", "out");
        InitializeFilter(graphSink, "buffersink", null);
        BuildChain(chain);
        ConfigureGraph();
    }

    private void LogVideoPlan(
        AVFrame* frame,
        AVPixelFormat inputFormat,
        AVPixelFormat hardwareSoftwareFormat,
        int width,
        int height,
        IReadOnlyList<string> chain) =>
        AppLog.Information(
            "FFmpeg.Transcode",
            $"#{id} video graph attempt: size={frame->width}x{frame->height}; " +
            $"format={PixelFormatName(inputFormat)}; hardwareFrames={frame->hw_frames_ctx != null}; " +
            $"hardwareSoftwareFormat={PixelFormatName(hardwareSoftwareFormat)}; " +
            $"target={width}x{height}; filters=[{string.Join(" -> ", chain)}].");

    private void ResetVideoGraph()
    {
        graphReady = false;
        if (encoder != null)
        {
            AVCodecContext* encoderHandle = encoder;
            avcodec_free_context(&encoderHandle);
            encoder = null;
        }

        if (graph != null)
        {
            AVFilterGraph* graphHandle = graph;
            avfilter_graph_free(&graphHandle);
            graph = null;
        }

        graphSource = null;
        graphSink = null;
    }

    /// <summary>
    /// The filters between the decoder and the encoder. On-device scaling saves the picture a round
    /// trip across the bus, but is asked for rather than assumed: a device scaler is the part of the
    /// hardware path most likely to refuse a picture or hand one back discolored.
    /// </summary>
    private (IReadOnlyList<string> Filters, bool UsesHardwareScaler) BuildVideoChain(
        int width,
        int height,
        AVPixelFormat hardwareSoftwareFormat,
        string encoderPixelFormat,
        bool enableHardwareScaling)
    {
        var chain = new List<string>();
        var usesHardwareScaler = false;

        if (choice.IsHardware && DecodesOnDevice)
        {
            TranscodeEncoderInterface? encoderInterface = TranscodeEncoderInterfaces.Find(choice.EncoderInterface);
            string scaler = encoderInterface?.ScalerName ?? string.Empty;
            if (enableHardwareScaling &&
                scaler.Length > 0 &&
                (encoderInterface!.ScalerConvertsPixelFormat ||
                    string.Equals(PixelFormatName(hardwareSoftwareFormat), encoderPixelFormat, StringComparison.Ordinal)) &&
                avfilter_get_by_name(scaler) != null)
            {
                chain.Add($"{scaler}={ScalerArguments(encoderInterface!, width, height, encoderPixelFormat)}");
                usesHardwareScaler = true;
            }
            else
            {
                chain.AddRange(BuildCopyBackScaleChain(
                    PixelFormatName(hardwareSoftwareFormat),
                    encoderPixelFormat,
                    width,
                    height));
            }
        }
        else
        {
            chain.Add($"scale={width}:{height}:flags=bicubic");
            chain.Add($"format={encoderPixelFormat}");
            if (choice.IsHardware)
            {
                chain.Add("hwupload");
            }
        }

        return (chain, usesHardwareScaler);
    }

    private string SelectEncoderPixelFormat(
        AVPixelFormat decodedPixelFormat,
        AVPixelFormat hardwareSoftwareFormat)
    {
        if (!choice.IsHardware)
        {
            return PixelFormatName(AVPixelFormat.AV_PIX_FMT_YUV420P);
        }

        AVPixelFormat softwareFormat = hardwareSoftwareFormat != AVPixelFormat.AV_PIX_FMT_NONE
            ? hardwareSoftwareFormat
            : decodedPixelFormat;
        return choice.Supports10Bit && BitDepthOf(softwareFormat) == 10
            ? PixelFormatName(AVPixelFormat.AV_PIX_FMT_P010LE)
            : PixelFormatName(AVPixelFormat.AV_PIX_FMT_NV12);
    }

    /// <summary>
    /// Downloads a hardware frame in the software layout its pool actually exposes, then lets the
    /// software scaler resize and convert it to the encoder layout before uploading it again.
    /// D3D11 rejects a download straight to a different layout (for example P010 to NV12).
    /// </summary>
    internal static IReadOnlyList<string> BuildCopyBackScaleChain(
        string hardwareSoftwarePixelFormat,
        string encoderPixelFormat,
        int width,
        int height)
    {
        if (string.IsNullOrWhiteSpace(hardwareSoftwarePixelFormat) ||
            string.Equals(hardwareSoftwarePixelFormat, "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new FfmpegException("The hardware frame pool states no software pixel format.", 0);
        }

        return
        [
            "hwdownload",
            $"format={hardwareSoftwarePixelFormat}",
            $"scale={width}:{height}:flags=bicubic",
            $"format={encoderPixelFormat}",
            "hwupload",
        ];
    }

    /// <summary>Software layout exposed when a hardware surface is copied back to the processor.</summary>
    private static AVPixelFormat HardwareSoftwareFormat(AVFrame* frame)
    {
        if (frame->hw_frames_ctx == null || frame->hw_frames_ctx->data == null)
        {
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }

        return ((AVHWFramesContext*)frame->hw_frames_ctx->data)->sw_format;
    }

    private string ScalerArguments(
        TranscodeEncoderInterface encoderInterface,
        int width,
        int height,
        string pixelFormat)
    {
        var arguments = new List<string>
        {
            $"{encoderInterface.ScalerWidthOption}={width}",
            $"{encoderInterface.ScalerHeightOption}={height}",
        };
        if (encoderInterface.ScalerConvertsPixelFormat)
        {
            arguments.Add($"format={pixelFormat}");
        }

        foreach (KeyValuePair<string, string> option in hardwareSettings.QueryOptions(
            choice.DeviceType,
            HardwareEncoderApis.Scaler,
            HardwareEncoderParameters.Arguments))
        {
            arguments.Add($"{option.Key}={option.Value}");
        }

        return string.Join(':', arguments);
    }

    private void OpenVideoEncoder(AVFrame* sourceFrame)
    {
        AVCodecContext* context = avcodec_alloc_context3(videoEncoderCodec);
        if (context == null)
        {
            throw new FfmpegException("avcodec_alloc_context3 returned null", 0);
        }

        encoder = context;
        graphTimeBase = av_buffersink_get_time_base(graphSink);
        encoderTimeBase = graphTimeBase;

        context->width = av_buffersink_get_w(graphSink);
        context->height = av_buffersink_get_h(graphSink);
        context->pix_fmt = (AVPixelFormat)av_buffersink_get_format(graphSink);
        context->sample_aspect_ratio = av_buffersink_get_sample_aspect_ratio(graphSink);
        context->time_base = encoderTimeBase;
        context->framerate = new AVRational { num = sourceFrameRateNum, den = sourceFrameRateDen };
        // The signal remains PQ/HLG even when an 8-bit-only encoder reduces its precision rather
        // than being silently relabeled as SDR. The first decoded frame is authoritative because
        // its tags include anything the decoder learned from bitstream VUI/SEI rather than the
        // container alone.
        context->color_range = sourceFrame->color_range;
        context->color_primaries = sourceFrame->color_primaries;
        context->color_trc = sourceFrame->color_trc;
        context->colorspace = sourceFrame->colorspace;
        context->chroma_sample_location = sourceFrame->chroma_location;

        long bitRate = videoTarget!.BitRate > 0
            ? videoTarget.BitRate
            : EstimateBitRate(context->width, context->height, SourceFrameRate);
        context->bit_rate = bitRate;
        context->rc_max_rate = bitRate;
        int bufferSeconds = TranscodeEncoderRateControl.UsesVariableBitrate(
            hardwareSettings,
            choice.EncoderInterface) ? 1 : 2;
        context->rc_buffer_size = (int)(Math.Clamp(bitRate, 0, int.MaxValue / bufferSeconds) * bufferSeconds);
        context->gop_size = GopSize(SourceFrameRate);

        // A client plays what arrives in the order it arrives; reordering would make it hold packets
        // for a picture it has not been given yet, and buys nothing at these bitrates.
        context->max_b_frames = 0;
        context->thread_count = ThreadCount();

        // The parameter sets travel in the track's own description rather than in front of every
        // keyframe, which is how the client configures its decoder before a single packet arrives.
        context->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
        encoderCopiesOpaque = (videoEncoderCodec->capabilities &
            AV_CODEC_CAP_ENCODER_REORDERED_OPAQUE) != 0;
        if (encoderCopiesOpaque)
        {
            context->flags |= AV_CODEC_FLAG_COPY_OPAQUE;
        }

        AVBufferRef* frames = av_buffersink_get_hw_frames_ctx(graphSink);
        if (frames != null)
        {
            context->hw_frames_ctx = av_buffer_ref(frames);
        }

        AppLog.Information(
            "FFmpeg.Transcode",
            $"#{id} opening video encoder '{choice.EncoderName}': size={context->width}x{context->height}; " +
            $"pixelFormat={PixelFormatName(context->pix_fmt)}; bitRate={context->bit_rate}; " +
            $"gop={context->gop_size}; hardwareFrames={context->hw_frames_ctx != null}.");
        OpenEncoder(context, videoEncoderCodec);
    }

    private void FillMissingColorMetadata(AVFrame* frame)
    {
        if (frame->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED)
        {
            frame->color_range = decoder->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED
                ? sourceParameters->color_range
                : decoder->color_range;
        }

        if (frame->color_primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED)
        {
            frame->color_primaries = decoder->color_primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED
                ? sourceParameters->color_primaries
                : decoder->color_primaries;
        }

        if (frame->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED)
        {
            frame->color_trc = decoder->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED
                ? sourceParameters->color_trc
                : decoder->color_trc;
        }

        if (frame->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED)
        {
            frame->colorspace = decoder->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED
                ? sourceParameters->color_space
                : decoder->colorspace;
        }

        if (frame->chroma_location == AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED)
        {
            frame->chroma_location = decoder->chroma_sample_location == AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED
                ? sourceParameters->chroma_location
                : decoder->chroma_sample_location;
        }
    }

    private void OpenEncoder(AVCodecContext* context, AVCodec* encoderCodec)
    {
        AVDictionary* opts = null;
        foreach (KeyValuePair<string, string> option in hardwareSettings.QueryOptions(
            choice.EncoderInterface,
            HardwareEncoderApis.EncoderOpen,
            HardwareEncoderParameters.Options))
        {
            av_dict_set(&opts, option.Key, option.Value, 0);
        }

        int opened = avcodec_open2(context, encoderCodec, &opts);
        av_dict_free(&opts);
        if (opened < 0)
        {
            AppLog.Error(
                "FFmpeg.Transcode",
                $"#{id} encoder '{FfmpegTranscodeCapabilities.NameOf(encoderCodec)}' failed to open: " +
                FfmpegError.Describe(opened));
        }

        FfmpegError.ThrowIfError(opened, "avcodec_open2");
        AppLog.Information(
            "FFmpeg.Transcode",
            $"#{id} opened encoder '{FfmpegTranscodeCapabilities.NameOf(encoderCodec)}'.");
    }

    private void OpenAudio(CodecParameters source, TranscodeAudioTarget target)
    {
        AVCodec* encoderCodec = avcodec_find_encoder_by_name(choice.EncoderName);
        if (encoderCodec == null)
        {
            throw new FfmpegException($"This build has no encoder named '{choice.EncoderName}'.", 0);
        }

        AVCodec* decoderCodec = avcodec_find_decoder(sourceParameters->codec_id);
        if (decoderCodec == null)
        {
            throw new FfmpegException("This build cannot decode the source audio.", 0);
        }

        decoder = CreateDecoderContext(decoderCodec);
        FfmpegError.ThrowIfError(avcodec_open2(decoder, decoderCodec, null), "avcodec_open2");
        AppLog.Information(
            "FFmpeg.Transcode",
            $"#{id} opened audio decoder '{FfmpegTranscodeCapabilities.NameOf(decoderCodec)}'.");

        OpenAudioEncoder(encoderCodec, source, target);
        PublishOutput();

        Description = string.Create(
            CultureInfo.InvariantCulture,
            $"{choice.EncoderName} {encoder->sample_rate} Hz, {encoder->ch_layout.nb_channels} ch @ " +
            $"{encoder->bit_rate / 1000} kb/s");
        AppLog.Information("FFmpeg.Transcode", $"#{id} audio encoder configured: {Description}.");
    }

    /// <summary>
    /// Opens the audio encoder on the source's own sample rate and channel layout where it will take
    /// them, so a surround track is not flattened to stereo just to be made smaller.
    /// </summary>
    /// <remarks>
    /// Advertised sample formats are preferred when FFmpeg exposes them. The portable candidate
    /// list remains a fallback for encoders that do not, and a failed open costs little once per track.
    /// </remarks>
    private void OpenAudioEncoder(AVCodec* encoderCodec, CodecParameters source, TranscodeAudioTarget target)
    {
        int sourceRate = source.SampleRate > 0 ? source.SampleRate : FallbackSampleRates[0];
        var rates = new List<int> { sourceRate };
        IReadOnlyList<AVSampleFormat> sampleFormats = GetSampleFormats(encoderCodec);
        foreach (int rate in FallbackSampleRates)
        {
            if (!rates.Contains(rate))
            {
                rates.Add(rate);
            }
        }

        long bitRate = target.BitRate > 0
            ? target.BitRate
            : Math.Max(64_000, Math.Min(sourceParameters->ch_layout.nb_channels, 8) * 48_000L);

        foreach (int rate in rates)
        {
            foreach (AudioLayoutChoice layout in Enum.GetValues<AudioLayoutChoice>())
            {
                foreach (AVSampleFormat format in sampleFormats)
                {
                    if (TryOpenAudioEncoder(encoderCodec, rate, layout, format, bitRate))
                    {
                        return;
                    }
                }
            }
        }

        throw new FfmpegException($"'{choice.EncoderName}' would not take this audio track.", 0);
    }

    private static IReadOnlyList<AVSampleFormat> GetSampleFormats(AVCodec* encoderCodec)
    {
        void* configurations = null;
        int configurationCount = 0;
        int result = avcodec_get_supported_config(
            null,
            encoderCodec,
            AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_FORMAT,
            0,
            &configurations,
            &configurationCount);
        if (result < 0 || configurations == null || configurationCount <= 0)
        {
            return CandidateSampleFormats;
        }

        var supported = (AVSampleFormat*)configurations;
        var formats = new List<AVSampleFormat>(configurationCount);
        foreach (AVSampleFormat candidate in CandidateSampleFormats)
        {
            for (int index = 0; index < configurationCount; index++)
            {
                if (supported[index] == candidate)
                {
                    formats.Add(candidate);
                    break;
                }
            }
        }

        for (int index = 0; index < configurationCount; index++)
        {
            AVSampleFormat format = supported[index];
            if (format != AVSampleFormat.AV_SAMPLE_FMT_NONE && !formats.Contains(format))
            {
                formats.Add(format);
            }
        }

        return formats.Count > 0 ? formats : CandidateSampleFormats;
    }

    /// <summary>
    /// Layouts tried in turn. The source's own comes first; the canonical layout for the same
    /// channel count is what saves a surround track, because a container routinely labels it in a
    /// way no encoder has a table for -- AC-3 gives 5.1(side) where Opus knows only 5.1.
    /// </summary>
    private enum AudioLayoutChoice
    {
        Source,
        Canonical,
        Stereo,
    }

    private bool TryOpenAudioEncoder(
        AVCodec* encoderCodec,
        int sampleRate,
        AudioLayoutChoice layout,
        AVSampleFormat format,
        long bitRate)
    {
        AVCodecContext* context = avcodec_alloc_context3(encoderCodec);
        if (context == null)
        {
            throw new FfmpegException("avcodec_alloc_context3 returned null", 0);
        }

        try
        {
            int channels = sourceParameters->ch_layout.nb_channels;
            if (layout != AudioLayoutChoice.Stereo && channels is > 0 and <= 8)
            {
                if (layout == AudioLayoutChoice.Source)
                {
                    if (av_channel_layout_copy(&context->ch_layout, &sourceParameters->ch_layout) < 0)
                    {
                        return false;
                    }
                }
                else
                {
                    av_channel_layout_default(&context->ch_layout, channels);
                }
            }
            else
            {
                av_channel_layout_default(&context->ch_layout, 2);
            }

            context->sample_rate = sampleRate;
            context->sample_fmt = format;
            context->bit_rate = bitRate;
            context->time_base = new AVRational { num = 1, den = sampleRate };
            context->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
            if (FfmpegTranscodeCapabilities.IsExperimental(encoderCodec))
            {
                // The native Opus encoder is flagged experimental and refuses to open otherwise.
                context->strict_std_compliance = FF_COMPLIANCE_EXPERIMENTAL;
            }

            AVDictionary* opts = null;
            if (context->ch_layout.nb_channels > 2)
            {
                // Opus carries more than two channels only under a declared mapping, and libopus
                // refuses a surround layout outright without one. An encoder that has no such
                // option leaves this in the dictionary and opens regardless.
                av_dict_set(&opts, "mapping_family", "1", 0);
            }

            int opened = avcodec_open2(context, encoderCodec, &opts);
            av_dict_free(&opts);
            if (opened < 0)
            {
                return false;
            }

            encoder = context;
            encoderTimeBase = context->time_base;
            context = null;
            AppLog.Information(
                "FFmpeg.Transcode",
                $"#{id} selected audio encoder format: rate={sampleRate}; channels={encoder->ch_layout.nb_channels}; " +
                $"layout={layout}; sampleFormat={av_get_sample_fmt_name(format)}; bitRate={bitRate}.");
            return true;
        }
        finally
        {
            if (context != null)
            {
                avcodec_free_context(&context);
            }
        }
    }

    private double SourceFrameRate =>
        sourceFrameRateNum > 0 && sourceFrameRateDen > 0 ? sourceFrameRateNum / (double)sourceFrameRateDen : 0;

    /// <summary>
    /// Builds the chain on the first frame: for video because nothing about the picture is settled
    /// until one exists, and for audio because a container does not always state the sample format.
    /// </summary>
    private void EnsureGraph(AVFrame* frame)
    {
        if (graphReady)
        {
            return;
        }

        try
        {
            if (sourceParameters->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                BuildVideoGraph(frame);
            }
            else
            {
                BuildAudioGraph(frame);
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("FFmpeg.Transcode", $"#{id} filter graph setup failed.", exception);
            throw;
        }
    }

    private void BuildAudioGraph(AVFrame* frame)
    {
        string layoutName = Describe(&frame->ch_layout);
        string encoderLayoutName = Describe(&encoder->ch_layout);
        string sampleFormat = av_get_sample_fmt_name((AVSampleFormat)frame->format)
            ?? throw new FfmpegException($"Unknown source audio sample format: {frame->format}.", 0);
        string encoderFormat = av_get_sample_fmt_name(encoder->sample_fmt)
            ?? throw new FfmpegException($"Unknown encoder audio sample format: {encoder->sample_fmt}.", 0);

        AppLog.Information(
            "FFmpeg.Transcode",
            $"#{id} first audio frame: rate={frame->sample_rate}; layout={layoutName}; format={sampleFormat}; " +
            $"targetRate={encoder->sample_rate}; targetLayout={encoderLayoutName}; targetFormat={encoderFormat}.");

        AllocateGraph();
        CreateFilter(
            ref graphSource,
            "abuffer",
            "in",
            string.Create(
                CultureInfo.InvariantCulture,
                $"time_base={streamTimeBase.num}/{streamTimeBase.den}:sample_rate={frame->sample_rate}:" +
                $"sample_fmt={sampleFormat}:channel_layout={layoutName}"));
        CreateFilter(ref graphSink, "abuffersink", "out", null);
        BuildChain(
        [
            $"aformat=sample_fmts={encoderFormat}:sample_rates={encoder->sample_rate}:" +
            $"channel_layouts={encoderLayoutName}",
        ]);
        ConfigureGraph();

        graphTimeBase = av_buffersink_get_time_base(graphSink);
        if (encoder->frame_size > 0)
        {
            // Everything but the last block reaches the encoder at exactly the size it wants, so it
            // never has to buffer a partial one of its own.
            av_buffersink_set_frame_size(graphSink, (uint)encoder->frame_size);
        }

        graphReady = true;
    }

    private static string Describe(AVChannelLayout* layout)
    {
        var buffer = new byte[256];
        int written;
        fixed (byte* target = buffer)
        {
            written = av_channel_layout_describe(layout, target, (nuint)buffer.Length);
        }

        if (written <= 0)
        {
            throw new FfmpegException("av_channel_layout_describe failed", written);
        }

        // The count includes the terminator, and is what the name would have taken had it fitted.
        return System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Min(written, buffer.Length) - 1);
    }

    private void AllocateGraph()
    {
        graph = avfilter_graph_alloc();
        if (graph == null)
        {
            throw new FfmpegException("avfilter_graph_alloc returned null", 0);
        }
    }

    private AVFilterContext* AllocateFilter(string filterName, string label)
    {
        AVFilter* filter = avfilter_get_by_name(filterName);
        if (filter == null)
        {
            throw new FfmpegException($"This build has no '{filterName}' filter.", 0);
        }

        AVFilterContext* context = avfilter_graph_alloc_filter(graph, filter, label);
        if (context == null)
        {
            throw new FfmpegException($"avfilter_graph_alloc_filter({filterName}) returned null", 0);
        }

        return context;
    }

    private static void InitializeFilter(AVFilterContext* context, string filterName, string? arguments) =>
        FfmpegError.ThrowIfError(
            avfilter_init_str(context, arguments),
            $"avfilter_init_str({filterName}, '{arguments}')");

    private void CreateFilter(ref AVFilterContext* target, string filterName, string label, string? arguments)
    {
        target = AllocateFilter(filterName, label);
        InitializeFilter(target, filterName, arguments);
    }

    /// <summary>
    /// Builds the chain one filter at a time rather than by parsing it whole.
    /// </summary>
    /// <remarks>
    /// A filter that allocates on a device -- an uploader, an on-device scaler -- has to be told
    /// which device before it is initialized, and parsing a chain initializes every filter it
    /// creates as it goes, which leaves no moment to say so.
    /// </remarks>
    private void BuildChain(IReadOnlyList<string> filters)
    {
        AVFilterContext* previous = graphSource;
        for (int index = 0; index < filters.Count; index++)
        {
            string specification = filters[index];
            int separator = specification.IndexOf('=', StringComparison.Ordinal);
            string name = separator < 0 ? specification : specification[..separator];
            string? arguments = separator < 0 ? null : specification[(separator + 1)..];

            AVFilterContext* filter = AllocateFilter(name, $"stage{index}");
            if (hardwareDevice != null)
            {
                filter->hw_device_ctx = av_buffer_ref(hardwareDevice);
            }

            InitializeFilter(filter, name, arguments);
            FfmpegError.ThrowIfError(avfilter_link(previous, 0, filter, 0), $"avfilter_link({name})");
            previous = filter;
        }

        FfmpegError.ThrowIfError(avfilter_link(previous, 0, graphSink, 0), "avfilter_link(buffersink)");
    }

    private void ConfigureGraph() =>
        FfmpegError.ThrowIfError(avfilter_graph_config(graph, null), "avfilter_graph_config");

    private void PublishOutput()
    {
        AVCodecParameters* parameters = avcodec_parameters_alloc();
        if (parameters == null)
        {
            throw new FfmpegException("avcodec_parameters_alloc returned null", 0);
        }

        try
        {
            FfmpegError.ThrowIfError(
                avcodec_parameters_from_context(parameters, encoder),
                "avcodec_parameters_from_context");

            // A hardware encoder is configured with the surface format it is fed, but what travels
            // is what the software format behind those surfaces describes.
            if (encoder->hw_frames_ctx != null)
            {
                parameters->format = (int)((AVHWFramesContext*)encoder->hw_frames_ctx->data)->sw_format;
            }

            OutputParameters = CodecParameters.FromNative(parameters, streamTimeBase, encoder->framerate) with
            {
                HdrPeakLuminance = sourceHdrPeakLuminance,
                HdrMasteringLuminance = sourceHdrMasteringLuminance,
                // The chain scales the picture but never turns it, so the source's display matrix
                // still applies and would otherwise be lost with the container it came from.
                Rotation = sourceRotation,
                MirrorHorizontally = sourceMirrorHorizontally,
            };
            IsReady = true;
            AppLog.Information(
                "FFmpeg.Transcode",
                $"#{id} published output: codec={OutputParameters.CodecName}; size={OutputParameters.Width}x{OutputParameters.Height}; " +
                $"sampleRate={OutputParameters.SampleRate}; channels={OutputParameters.ChannelCount}; " +
                $"bitRate={OutputParameters.BitRate}; extradata={OutputParameters.Extradata.Length} bytes; " +
                $"timeBase={OutputParameters.TimeBaseNum}/{OutputParameters.TimeBaseDen}.");
        }
        finally
        {
            avcodec_parameters_free(&parameters);
        }
    }

    /// <summary>
    /// The largest size within the requested bounds that keeps the source's shape, rounded to even
    /// numbers because chroma is subsampled in both directions. Never larger than the source: a
    /// client asking for more than it was given is served what there is.
    /// </summary>
    private static (int Width, int Height) FitWithin(int width, int height, int maxWidth, int maxHeight)
    {
        if (width <= 0 || height <= 0)
        {
            throw new FfmpegException("The source states no picture size, so it cannot be re-encoded.", 0);
        }

        double scale = 1;
        if (maxWidth > 0 && width > maxWidth)
        {
            scale = maxWidth / (double)width;
        }

        if (maxHeight > 0 && height > maxHeight)
        {
            scale = Math.Min(scale, maxHeight / (double)height);
        }

        return (
            EvenWithin(width * scale, maxWidth > 0 ? Math.Min(width, maxWidth) : width),
            EvenWithin(height * scale, maxHeight > 0 ? Math.Min(height, maxHeight) : height));
    }

    /// <summary>The nearest even size to <paramref name="value"/> that stays within <paramref name="limit"/>.</summary>
    private static int EvenWithin(double value, int limit)
    {
        int size = (int)Math.Round(value / 2) * 2;

        // Rounding an odd dimension up would enlarge the picture rather than leave it as it was.
        if (size > limit)
        {
            size -= 2;
        }

        return Math.Max(2, size);
    }

    private static long EstimateBitRate(int width, int height, double frameRate) =>
        (long)(width * height * (frameRate > 0 ? Math.Min(frameRate, 60) : 30) * DefaultBitsPerPixel);

    private static string PixelFormatName(AVPixelFormat format) =>
        format == AVPixelFormat.AV_PIX_FMT_NONE ? "none" : av_get_pix_fmt_name(format) ?? format.ToString();

    private static int BitDepthOf(AVPixelFormat format)
    {
        AVPixFmtDescriptor* descriptor = av_pix_fmt_desc_get(format);
        return descriptor != null && descriptor->nb_components > 0 ? descriptor->comp[0].depth : 0;
    }

    private int GopSize(double frameRate)
    {
        double rate = frameRate > 0 ? frameRate : 30;
        return Math.Max(1, (int)Math.Round(rate * options.KeyFrameIntervalSeconds));
    }

    private int ThreadCount() =>
        options.ThreadCount > 0 ? options.ThreadCount : Math.Min(Environment.ProcessorCount, 8);

    private static AVCodecHWConfig* FindHardwareConfig(AVCodec* codec, AVHWDeviceType deviceType)
    {
        for (int index = 0; ; index++)
        {
            AVCodecHWConfig* config = avcodec_get_hw_config(codec, index);
            if (config == null)
            {
                return null;
            }

            // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX.
            if (config->device_type == deviceType && ((int)config->methods & 1) != 0)
            {
                return config;
            }
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static AVPixelFormat SelectHardwareFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        AVPixelFormat wanted = AVPixelFormat.AV_PIX_FMT_NONE;
        if (context != null && context->opaque != null &&
            GCHandle.FromIntPtr((IntPtr)context->opaque).Target is FfmpegTranscoder transcoder)
        {
            wanted = transcoder.hardwarePixelFormat;
        }

        for (AVPixelFormat* format = formats; format != null && *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
        {
            if (*format == wanted)
            {
                return wanted;
            }
        }

        return formats != null ? *formats : AVPixelFormat.AV_PIX_FMT_NONE;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        while (ready.Count > 0)
        {
            var packet = (AVPacket*)ready.Dequeue();
            av_packet_free(&packet);
        }

        if (graph != null)
        {
            AVFilterGraph* handle = graph;
            avfilter_graph_free(&handle);
            graph = null;
            graphSource = null;
            graphSink = null;
        }

        if (encoder != null)
        {
            AVCodecContext* handle = encoder;
            avcodec_free_context(&handle);
            encoder = null;
        }

        if (decoder != null)
        {
            AVCodecContext* handle = decoder;
            avcodec_free_context(&handle);
            decoder = null;
        }

        if (hardwareDevice != null)
        {
            AVBufferRef* handle = hardwareDevice;
            av_buffer_unref(&handle);
            hardwareDevice = null;
        }

        if (decoded != null)
        {
            AVFrame* handle = decoded;
            av_frame_free(&handle);
            decoded = null;
        }

        if (filtered != null)
        {
            AVFrame* handle = filtered;
            av_frame_free(&handle);
            filtered = null;
        }

        if (sourceParameters != null)
        {
            AVCodecParameters* handle = sourceParameters;
            sourceParameters = null;
            CodecParameters.FreeNative(&handle);
        }

        // Freed only after the decoder is gone: the callback it points at runs until then.
        if (owner.IsAllocated)
        {
            owner.Free();
        }
    }
}
