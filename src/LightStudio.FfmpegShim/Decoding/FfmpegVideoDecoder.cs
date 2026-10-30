using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Interop;
using LightStudio.FfmpegShim.Media;
using LightStudio.NativeBindings.Types;
using LightStudio.Logging;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>
/// Software and hardware video decoding, handing frames over in the layout they were decoded in.
/// </summary>
/// <remarks>
/// Common YUV layouts reach the renderer untouched and are converted there, on the GPU. swscale is
/// kept only for the layouts the renderer cannot sample -- RGB-coded video, monochrome, alpha and
/// the rarer subsamplings -- which it flattens to BGRA here.
/// </remarks>
public sealed unsafe partial class FfmpegVideoDecoder : IVideoDecoder
{
    private static long nextId;

    // Never reused, so a surface identifier a new pool hands out again cannot be mistaken for the
    // surface of the same name in the pool before it.
    private static long nextSurfacePool;

    private const int DecodeErrorThreshold = 3;
    private const double PerformanceSmoothingFactor = 0.1;

    // Surfaces held on top of whatever the frame budget itself admits: the frame the queue was
    // allowed to overshoot its byte ceiling by, the one being shown, the one being drawn, the one
    // the decoder is waiting to hand over, and a little slack so a burst does not tip the pipeline
    // into reading frames back.
    private const int SurfaceHeadroom = 8;

    // Fewer than this and the renderer runs dry between decodes; more and nothing is gained, while
    // every entry is another whole frame of video memory reserved for the life of the stream.
    private const int MinimumSharedSurfaces = 6;
    private const int MaximumSharedSurfaces = 64;

    // A display cannot be larger than this, so a size beyond it is a layout that has gone wrong
    // rather than a request, and reserving surfaces for it would exhaust video memory.
    private const int MaximumDeviceScale = 16384;

    // Set in the requested size to ask the device for an already converted picture. Above every
    // size bit, so one write still carries both halves of the request.
    private const long PackedRgbRequest = 1L << 62;

    // D3D11_BIND_FLAG, which no binding exposes because it belongs to Direct3D rather than FFmpeg.
    private const uint D3D11BindShaderResource = 0x8;
    private const uint D3D11BindDecoder = 0x200;

    // Source and destination geometry always match, so the resampling filter is inert and only the chroma handling counts:
    // SWS_ACCURATE_RND and SWS_FULL_CHROMA_INT together match what the shader path does.
    private const int ScalerFlags = 2 /* SWS_BILINEAR */ | 0x40000 /* SWS_ACCURATE_RND */ | 0x2000 /* SWS_FULL_CHROMA_INT */;

    private readonly IPacketSource source;
    private readonly VideoFramePool pool;
    private readonly VideoDecoderOptions options;
    private readonly int streamIndex;
    private readonly double timeBase;
    private readonly double nominalFrameDuration;
    private readonly AVCodec* defaultDecoder;
    private readonly AVRational packetTimeBase;
    private readonly bool hasDeclaredPixelAspectRatio;
    private readonly double declaredHdrPeakLuminance;
    private readonly VideoLuminance? declaredHdrMasteringLuminance;
    private readonly VideoFieldOrder declaredFieldOrder;
    private readonly int dolbyVisionProfile;
    private readonly int dolbyVisionLevel;
    private readonly int dolbyVisionCompatibilityId;
    private readonly long id;

    // Built from the portable description rather than borrowed from a container, so this decoder
    // owns it and has to free it.
    private AVCodecParameters* codecParameters;

    // The pool owns the pixel buffer, so the AVBufferRef wrapped around it must free nothing.
    private static readonly delegate* unmanaged[Cdecl]<void*, byte*, void> keepPooledBuffer = &KeepPooledBuffer;

    private AVCodecContext* codec;
    private AVFrame* frame;
    private AVFrame* transferredFrame;
    private AVFrame* scaled;
    private SwsContext* scaler;
    private DolbyVisionEnhancementDecoder enhancementDecoder;
    private AlphaBlockAdditionDecoder alphaAdditionDecoder;
    private AVFrame* alphaComposite;
    private AVPixelFormat scalerInputFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private AVPixelFormat scalerOutputFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private int scalerWidth;
    private int scalerHeight;
    private int scalerColorSpace = -1;
    private int scalerColorRange = -1;
    private GCHandle hardwareOwner;
    private AVPixelFormat hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private VideoSurfaceKind surfaceKind;
    private IntPtr surfaceDevice;
    private IntPtr surfacePool;

    // The window and codec stay alive while the decoder or any exported picture still uses them.
    private IVideoDecoderOutputSurface outputWindow;
    private WindowCodecReferences windowCodecReferences;

    // Owned by the decode thread, but the size it works to is written by the renderer's.
    private IHardwareVideoScaler deviceScaler;
    private VideoOutputRequest requestedOutput;
    private bool deviceScalingRequested;
    private bool deviceOutputCoversControl;
    private int decodedWidth;
    private int decodedHeight;
    private SurfacePoolReferences surfacePoolReferences;
    private int sharedSurfaceBudget;
    private int sharedSurfaces;
    private bool sharedSurfaceBudgetReported;
    private int surfaceDeliveryRejected;
    private bool usingHardwareDecoder;

    // A codec that is itself the hardware, rather than a software decoder driving an hwaccel. Such
    // a decoder hands its pixels back in a software layout unless it was given a surface to draw
    // into, and those frames are still hardware-decoded.
    private bool hardwareBackedCodec;
    private bool hardwareFrameFormatNegotiated;
    private int consecutiveDecodeErrors;
    private double lastPts = MediaTime.NoTimestamp;
    private volatile bool disposed;

    // Published rather than measured on demand, so the diagnostics overlay never reads native
    // decoder state from its own thread.
    private volatile VideoDecoderDiagnostics diagnostics = VideoDecoderDiagnostics.None;
    private IReadOnlyList<string> hardwareCandidates = Array.Empty<string>();
    private readonly List<string> hardwareAttempts = new();
    private readonly List<string> hardwareFailures = new();
    private string hardwareDeviceType = string.Empty;
    private bool softwareFallbackOccurred;
    /// <summary><c>AV_FRAME_CROP_UNALIGNED</c>.</summary>
    private const int FrameCropUnaligned = 1;

    private readonly bool alphaMode;
    private VideoFrameDeliveryPath deliveryPath;
    private AVPixelFormat deliveredSampleFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private VideoPixelFormat? deliveredFormat;
    private VideoColorSpace deliveredColorSpace;
    private VideoColorPrimaries deliveredColorPrimaries;
    private VideoColorTransfer deliveredColorTransfer;
    private double deliveredHdrPeakLuminance;
    private VideoColorRange deliveredColorRange;
    private VideoChromaLocation deliveredChromaLocation;
    private bool? deliveredDolbyVisionActive;
    private bool? deliveredDolbyVisionEnhancementLayerActive;
    private bool? deliveredHdr10PlusActive;
    private int deliveredHdr10PlusApplicationVersion;
    private int deliveredHdr10PlusWindowCount;
    private double deliveredHdr10PlusTargetLuminance;
    private double deliveredHdr10PlusSceneAverageLuminance;
    private int deliveredHdr10PlusAnchorCount;
    private bool deliveredHdr10PlusToneMapping;
    private readonly Queue<double> decodeSubmissions = new();
    private double pendingDecodeWorkSeconds;
    private double decodeFrameTimeMilliseconds;
    private double estimatedDecodeLatencyMilliseconds;

    public FfmpegVideoDecoder(IPacketSource packetSource, VideoDecoderOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(packetSource);
        options ??= VideoDecoderOptions.Default;

        id = Interlocked.Increment(ref nextId);
        source = packetSource;
        this.options = options;
        streamIndex = packetSource.VideoStreamIndex;
        if (streamIndex < 0)
            throw new InvalidOperationException("The media has no video stream.");

        pool = new VideoFramePool(options.FramePoolSize, options.FramePoolByteCapacity);

        CodecParameters description = packetSource.GetCodecParameters(streamIndex);
        codecParameters = description.ToNative();
        packetTimeBase = description.NativeTimeBase();
        timeBase = description.TimeBase;
        hasDeclaredPixelAspectRatio =
            description.SampleAspectRatioNum > 0 && description.SampleAspectRatioDen > 0;
        declaredHdrPeakLuminance = description.HdrPeakLuminance;
        declaredHdrMasteringLuminance = description.HdrMasteringLuminance;
        declaredFieldOrder = VideoFieldOrders.FromFfmpeg(description.FieldOrder);
        dolbyVisionProfile = description.DolbyVisionProfile;
        dolbyVisionLevel = description.DolbyVisionLevel;
        dolbyVisionCompatibilityId = description.DolbyVisionCompatibilityId;

        defaultDecoder = avcodec_find_decoder(codecParameters->codec_id);
        alphaMode = description.AlphaMode;
        AVCodec* alphaDecoder = alphaMode ? FindAlphaDecoder(codecParameters->codec_id) : null;
        if (alphaDecoder != null)
            defaultDecoder = alphaDecoder;
        if (defaultDecoder == null)
        {
            AVCodecParameters* owned = codecParameters;
            codecParameters = null;
            CodecParameters.FreeNative(&owned);
            throw new FfmpegException($"No decoder for {description.CodecName}.", 0);
        }

        CodecName = description.CodecName;
        Width = description.Width;
        Height = description.Height;
        PixelAspectRatio = description.PixelAspectRatio;
        Rotation = description.Rotation;
        MirrorHorizontally = description.MirrorHorizontally;
        nominalFrameDuration = description.FrameRate > 0 ? 1.0 / description.FrameRate : 0;

        AppLog.Information(
            "FFmpeg.Decode",
            $"#{id} creating video decoder: stream={streamIndex}; codec={CodecName}; size={Width}x{Height}; " +
            $"frameRate={description.FrameRate:F3}; timeBase={description.TimeBaseNum}/{description.TimeBaseDen}; " +
            $"dolbyVision={description.DolbyVisionProfile}; doviConfig={description.DolbyVisionConfiguration.Length}; " +
            $"enhancementConfig={description.DolbyVisionEnhancementConfiguration.Length}; " +
            $"hardware={options.EnableHardwareDecoding}; requestedDeviceType={options.HardwareDeviceType ?? "auto"}; " +
            $"framePool={options.FramePoolSize}; framePoolBytes={options.FramePoolByteCapacity}.");

        try
        {
            codec = OpenCodec();

            frame = av_frame_alloc();
            if (frame == null)
                throw new FfmpegException("av_frame_alloc returned null", 0);

            scaled = av_frame_alloc();
            if (scaled == null)
                throw new FfmpegException("av_frame_alloc returned null", 0);

            if (description.DolbyVisionProfile == 7 &&
                description.DolbyVisionConfiguration.Length > 5 &&
                description.DolbyVisionConfiguration[5] != 0)
            {
                try
                {
                    enhancementDecoder = new DolbyVisionEnhancementDecoder(description, options);
                    AppLog.Information("FFmpeg.Decode", $"#{id} opened Dolby Vision enhancement-layer decoder.");
                }
                catch (Exception exception)
                {
                    AppLog.Warning(
                        "FFmpeg.Decode",
                        $"#{id} could not open the Dolby Vision enhancement layer; using the base layer only.",
                        exception);
                }
            }

            // Without libvpx, the native VP8 and VP9 decoders leave the alpha addition in the packet.
            if (alphaMode && alphaDecoder == null)
            {
                try
                {
                    alphaAdditionDecoder = new AlphaBlockAdditionDecoder(description, defaultDecoder, options);
                    AppLog.Information("FFmpeg.Decode", $"#{id} opened alpha block addition decoder.");
                }
                catch (Exception exception)
                {
                    AppLog.Warning(
                        "FFmpeg.Decode",
                        $"#{id} could not open the alpha block addition decoder; decoding without alpha.",
                        exception);
                }
            }

            PublishDiagnostics();
        }
        catch (Exception exception)
        {
            AppLog.Error("FFmpeg.Decode", $"#{id} video decoder setup failed.", exception);
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// The decoder that composes a Matroska alpha block addition: libvpx reads it beside the
    /// picture, while the native VP8 and VP9 decoders leave it in the packet.
    /// </summary>
    private static AVCodec* FindAlphaDecoder(AVCodecID codec) => codec switch
    {
        AVCodecID.AV_CODEC_ID_VP9 => avcodec_find_decoder_by_name("libvpx-vp9"),
        AVCodecID.AV_CODEC_ID_VP8 => avcodec_find_decoder_by_name("libvpx"),
        _ => null,
    };

    private AVCodecContext* OpenCodec()
    {
        // A hardware decoder has no alpha plane to give back.
        hardwareCandidates = alphaMode ? Array.Empty<string>() : GetHardwareCandidates(options);
        hardwareAttempts.Clear();
        hardwareFailures.Clear();
        if (!string.IsNullOrEmpty(options.SoftwareFallbackReason))
        {
            hardwareFailures.Add(options.SoftwareFallbackReason);
            softwareFallbackOccurred = true;
        }

        AppLog.Information(
            "FFmpeg.Decode",
            $"#{id} hardware candidates=[{string.Join(", ", hardwareCandidates)}].");

        foreach (string deviceName in hardwareCandidates)
        {
            hardwareAttempts.Add(deviceName);
            AVCodec* decoder = GetDecoderForHardwareDevice(deviceName);
            if (decoder == null)
            {
                hardwareFailures.Add($"{deviceName}: no decoder for {CodecName} advertises it");
                AppLog.Warning(
                    "FFmpeg.Decode",
                    $"#{id} no decoder for {CodecName} advertises hardware device type '{deviceName}'.");
                continue;
            }

            AVCodecContext* candidate = CreateCodecContext(decoder);
            codec = candidate;
            bool configured = TryConfigureHardware(decoder, candidate, deviceName);
            int hardwareOpened = configured ? avcodec_open2(candidate, decoder, null) : -1;
            if (configured && hardwareOpened >= 0)
            {
                transferredFrame = av_frame_alloc();
                if (transferredFrame == null)
                {
                    ReleaseCodec(candidate);
                    throw new FfmpegException("av_frame_alloc returned null", 0);
                }

                usingHardwareDecoder = true;
                hardwareBackedCodec = (decoder->capabilities & AV_CODEC_CAP_HARDWARE) != 0;
                hardwareDeviceType = deviceName;
                AppLog.Information(
                    "FFmpeg.Decode",
                    $"#{id} opened hardware decoder '{CodecNameOf(decoder)}' on {deviceName}; " +
                    $"pixelFormat={DescribePixelFormat(hardwarePixelFormat)}; surfaceKind={surfaceKind}; " +
                    $"hardwareCodec={hardwareBackedCodec}; threads={candidate->thread_count}.");
                return candidate;
            }

            string failure = configured
                ? $"codec would not open: {FfmpegError.Describe(hardwareOpened)}"
                : "device configuration failed";
            hardwareFailures.Add($"{deviceName}: {failure}");
            AppLog.Warning(
                "FFmpeg.Decode",
                $"#{id} hardware decoder '{CodecNameOf(decoder)}' on {deviceName} failed: {failure}.");
            ReleaseCodec(candidate);
        }

        AVCodecContext* software = CreateCodecContext(defaultDecoder);
        int opened = avcodec_open2(software, defaultDecoder, null);
        if (opened < 0)
        {
            ReleaseCodec(software);
            FfmpegError.ThrowIfError(opened, "avcodec_open2");
        }

        AppLog.Information(
            "FFmpeg.Decode",
            $"#{id} opened software decoder '{CodecNameOf(defaultDecoder)}' with {software->thread_count} threads; " +
            $"hardwareStatus={DescribeHardwareStatus()}.");
        return software;
    }

    private AVCodecContext* CreateCodecContext(AVCodec* decoder)
    {
        AVCodecContext* context = avcodec_alloc_context3(decoder);
        if (context == null)
            throw new FfmpegException("avcodec_alloc_context3 returned null", 0);

        int copied = avcodec_parameters_to_context(context, codecParameters);
        if (copied < 0)
        {
            avcodec_free_context(&context);
            FfmpegError.ThrowIfError(copied, "avcodec_parameters_to_context");
        }

        context->pkt_timebase = packetTimeBase;

        // Cropping is applied exactly: a left or top offset libavcodec would otherwise round down
        // to keep the plane pointers aligned, leaving part of the conformance window in view.
        context->flags |= AV_CODEC_FLAG_UNALIGNED;

        // Frame + slice threading is what makes 1080p/4K software decoding viable. 0 lets
        // libavcodec size the pool from the machine; the cap keeps very wide machines from
        // spending more memory on reference frames than the decode actually needs.
        context->thread_count = options.ThreadCount > 0
            ? options.ThreadCount
            : Math.Min(Environment.ProcessorCount, 16);
        context->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
        return context;
    }

    private AVCodec* GetDecoderForHardwareDevice(string deviceName)
    {
        AVHWDeviceType deviceType = FfmpegHardwareDevices.FindType(deviceName);
        if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            return null;
        if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC && !OperatingSystem.IsAndroid())
            return null;

        if (FindHardwareConfig(defaultDecoder, deviceType) != null)
            return defaultDecoder;

        // avcodec_find_decoder may prefer a software wrapper such as libdav1d. Match the CLI's
        // hardware selection by finding a decoder for the same codec ID that advertises the
        // requested device type.
        void* iterator = null;
        AVCodec* candidate;
        while ((candidate = av_codec_iterate(&iterator)) != null)
        {
            if (candidate == defaultDecoder ||
                candidate->id != codecParameters->codec_id ||
                candidate->type != AVMediaType.AVMEDIA_TYPE_VIDEO ||
                av_codec_is_decoder(candidate) == 0)
            {
                continue;
            }

            if (FindHardwareConfig(candidate, deviceType) != null)
                return candidate;
        }

        return null;
    }

    private static IReadOnlyList<string> GetHardwareCandidates(VideoDecoderOptions options)
    {
        if (!options.EnableHardwareDecoding)
            return Array.Empty<string>();

        IReadOnlyList<string> preferred = FfmpegHardwareDevices.GetPreferredTypesForCurrentPlatform();
        if (string.IsNullOrWhiteSpace(options.HardwareDeviceType))
            return preferred;

        var candidates = new List<string>(preferred.Count);
        foreach (string type in preferred)
        {
            if (string.Equals(type, options.HardwareDeviceType, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(type);
                break;
            }
        }

        foreach (string type in preferred)
        {
            if (!candidates.Exists(candidate => string.Equals(candidate, type, StringComparison.OrdinalIgnoreCase)))
                candidates.Add(type);
        }

        return candidates;
    }

    private bool TryConfigureHardware(AVCodec* decoder, AVCodecContext* context, string deviceName)
    {
        AVHWDeviceType deviceType = FfmpegHardwareDevices.FindType(deviceName);
        if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            return false;

        AVCodecHWConfig* match = FindHardwareConfig(decoder, deviceType);
        if (match == null)
        {
            AppLog.Warning(
                "FFmpeg.Decode",
                $"#{id} decoder '{CodecNameOf(decoder)}' has no {deviceName} hardware configuration.");
            return false;
        }

        AVBufferRef* device = CreateHardwareDevice(deviceType, deviceName);
        if (device == null)
            return false;

        context->hw_device_ctx = device;
        hardwarePixelFormat = match->pix_fmt;
        hardwareOwner = GCHandle.Alloc(this);
        context->opaque = (void*)GCHandle.ToIntPtr(hardwareOwner);
        context->get_format = &SelectHardwareFormat;
        ConfigureSurfaceSharing(context, device, deviceType);
        return true;
    }

    /// <summary>
    /// Opens the hardware device to decode with, which is the renderer's own where that is what it
    /// takes for it to be able to draw the result without a copy.
    /// </summary>
    private AVBufferRef* CreateHardwareDevice(AVHWDeviceType deviceType, string deviceName)
    {
        HardwareDecoderSettings settings = options.HardwareSettings ?? HardwareDecoderSettings.Empty;
        string selected = settings.QueryDevice(deviceName);

        // An explicitly chosen device is the user overriding which adapter decodes, so the
        // renderer's own is only adopted when the choice was left open.
        if (selected.Length == 0)
        {
            AVBufferRef* adopted = TryAdoptRendererDevice(deviceType);
            if (adopted != null)
            {
                AppLog.Information(
                    "FFmpeg.Decode",
                    $"#{id} adopted the renderer's {deviceName} device for zero-copy decoding.");
                return adopted;
            }

            AVBufferRef* window = TryOpenDecoderWindow(deviceType, deviceName);
            if (window != null)
                return window;
        }

        AVDictionary* opts = null;
        foreach (KeyValuePair<string, string> option in settings.QueryOptions(
            deviceName,
            HardwareDecoderApis.DeviceCreate,
            HardwareDecoderParameters.Options))
        {
            av_dict_set(&opts, option.Key, option.Value, 0);
        }

        int flags = settings.QueryFlags(
            deviceName,
            HardwareDecoderApis.DeviceCreate,
            HardwareDecoderParameters.Flags);

        AppLog.Information(
            "FFmpeg.Decode",
            $"#{id} opening hardware device type={deviceName}; " +
            $"device={(selected.Length == 0 ? "default" : selected)}; " +
            $"options={settings.QueryOptions(deviceName, HardwareDecoderApis.DeviceCreate, HardwareDecoderParameters.Options).Count}; " +
            $"flags={flags}.");

        AVBufferRef* device = null;
        int created = D3D11HardwareContext.CreateDevice(
            &device,
            deviceType,
            selected.Length == 0 ? null : selected,
            opts,
            flags);
        av_dict_free(&opts);

        if (created >= 0)
        {
            AppLog.Information(
                "FFmpeg.Decode",
                $"#{id} opened hardware device type={deviceName}; device={(selected.Length == 0 ? "default" : selected)}.");
            return device;
        }

        AppLog.Warning(
            "FFmpeg.Decode",
            $"#{id} hardware device type={deviceName}; device={(selected.Length == 0 ? "default" : selected)} " +
            $"failed: {FfmpegError.Describe(created)}.");

        if (device != null)
            av_buffer_unref(&device);
        return null;
    }

    /// <summary>
    /// Wraps the Direct3D device the renderer draws with, so that decoded textures are already the
    /// graphics context's own and can be sampled where they lie.
    /// </summary>
    /// <remarks>
    /// Nothing can be shared between two Direct3D devices without a copy through a shared handle,
    /// which is most of what sharing the surface was meant to avoid, so decoding on the renderer's
    /// device is the whole of what makes this worthwhile. Null when the renderer has not published
    /// one -- it does so as it first draws -- and the decoder then opens a device of its own and
    /// reads its frames back, as it would on a backend that cannot import surfaces at all.
    /// </remarks>
    private AVBufferRef* TryAdoptRendererDevice(AVHWDeviceType deviceType)
    {
        if (deviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA ||
            VideoFrameCapabilities.SupportedSurfaceKind != VideoSurfaceKind.Direct3D11)
        {
            return null;
        }

        IntPtr rendererDevice = VideoFrameCapabilities.SupportedSurfaceDevice;
        if (rendererDevice == IntPtr.Zero)
            return null;

        return D3D11HardwareContext.Adopt(rendererDevice, D3D11BindDecoder | D3D11BindShaderResource);
    }

    /// <summary>
    /// Opens a platform window for the codec to draw into, when the head offers one.
    /// </summary>
    /// <remarks>
    /// MediaCodec's own memory output is 4:2:0 8-bit whatever the stream was, so a 10-bit HDR source
    /// decoded that way arrives quantized to a quarter of its steps and contours everywhere it is
    /// smooth; a window is the only route to the rest of the depth. It is worth having for an 8-bit
    /// source too, where the depth is not in question but the two full-frame copies the memory path
    /// costs -- out of the codec's buffer and then into the pool -- are.
    /// </remarks>
    private AVBufferRef* TryOpenDecoderWindow(AVHWDeviceType deviceType, string deviceName)
    {
        if (deviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC || !options.AllowDecoderOutputWindow)
            return null;

        int bitDepth = SourceBitDepth();
        IVideoDecoderOutputSurface window = VideoDecoderOutputSurfaces.TryCreate(
            Width,
            Height,
            bitDepth,
            out string declined);
        if (window is null)
        {
            AppLog.Information(
                "FFmpeg.Decode",
                $"#{id} decoding into a {deviceName} window is unavailable: {declined}.");
            return null;
        }

        AVBufferRef* device = av_hwdevice_ctx_alloc(deviceType);
        if (device == null)
        {
            window.Dispose();
            return null;
        }

        var hardwareContext = (AVMediaCodecDeviceContext*)((AVHWDeviceContext*)device->data)->hwctx;
        hardwareContext->surface = (void*)window.SurfaceObject;
        hardwareContext->native_window = (void*)window.NativeWindow;

        int initialized = av_hwdevice_ctx_init(device);
        if (initialized < 0)
        {
            AppLog.Warning(
                "FFmpeg.Decode",
                $"#{id} could not open a {deviceName} device on the platform window: " +
                $"{FfmpegError.Describe(initialized)}.");
            av_buffer_unref(&device);
            window.Dispose();
            return null;
        }

        outputWindow = window;
        AppLog.Information(
            "FFmpeg.Decode",
            $"#{id} decoding into a {deviceName} window; " +
            $"texture={window.TextureHandle} target=0x{window.TextureTarget:X}; " +
            $"format={window.TextureFormat}; colorModel={window.TextureColorModel}; " +
            $"bitDepth={window.TextureBitDepth}.");
        return device;
    }

    /// <summary>
    /// Wraps one picture the codec left in the platform window. The pixels stay where the decoder
    /// put them; what the frame carries is the token that shows them and the texture they show up in.
    /// </summary>
    private VideoFrame WrapWindowPicture(AVFrame* input, int width, int height)
    {
        IVideoDecoderOutputSurface window = outputWindow;
        VideoFrame target = pool.Rent(width, height, VideoPixelFormat.ExternalTexture);

        // The codec's buffer is only in the window's queue for as long as something references it,
        // and the decoder unreferences its own copy as soon as this returns.
        AVFrame* held = av_frame_alloc();
        if (held == null)
        {
            target.Release();
            throw new FfmpegException("av_frame_alloc returned null", 0);
        }

        int referenced = av_frame_ref(held, input);
        if (referenced < 0)
        {
            av_frame_free(&held);
            target.Release();
            FfmpegError.ThrowIfError(referenced, "av_frame_ref");
        }

        WindowCodecReferences owner = windowCodecReferences ??=
            new WindowCodecReferences(codec, window, hardwareOwner);
        owner.AddRef();

        // Pointers cannot be captured, so both the picture and the reference keeping it alive
        // travel as addresses.
        IntPtr picture = (IntPtr)held->data[3];
        IntPtr reference = (IntPtr)held;

        target.ExternalHandle = window.TextureHandle;
        target.ExternalHandleGeneration = window.TextureGeneration;
        target.ExternalTextureTarget = window.TextureTarget;
        target.ExternalTextureFormat = window.TextureFormat;
        target.ExternalTextureColorModel = window.TextureColorModel;
        target.ExternalTextureBitDepth = window.TextureBitDepth;
        target.AcquireExternalTexture = frame =>
        {
            if (!window.Acquire(picture, out VideoTextureRect rect))
                return false;

            // A window makes its texture the first time it is drawn into, which is after the frame
            // carrying this was built.
            frame.ExternalHandle = window.TextureHandle;
            frame.ExternalTextureRect = rect;
            return true;
        };
        target.Recycled = _ =>
        {
            AVFrame* released = (AVFrame*)reference;
            av_frame_free(&released);
            owner.Release();
        };

        return target;
    }

    /// <summary>
    /// Works out whether this device's frames can be handed to a renderer as they are, and if so
    /// enlarges the decoder's surface pool to cover the ones it will be holding.
    /// </summary>
    private void ConfigureSurfaceSharing(AVCodecContext* context, AVBufferRef* device, AVHWDeviceType deviceType)
    {
        // A window is not a surface the renderer imports: the codec draws into it and the picture
        // comes back as one texture, so there are no planes to share.
        if (outputWindow is not null)
            return;

        void* hardwareContext = ((AVHWDeviceContext*)device->data)->hwctx;
        IntPtr handle = hardwareContext == null ? IntPtr.Zero : *(IntPtr*)hardwareContext;

        switch (deviceType)
        {
            // AVVAAPIDeviceContext opens with the VADisplay, which is all the renderer needs to
            // export a surface; binding libva here just to name the field it already has would gain
            // nothing.
            case AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI when handle != IntPtr.Zero:
                surfaceKind = VideoSurfaceKind.Vaapi;
                break;

            // AVD3D11VADeviceContext opens with the ID3D11Device. Only the renderer's own device
            // produces textures it can sample, so anything else has to be read back.
            case AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA
                when handle != IntPtr.Zero && handle == VideoFrameCapabilities.SupportedSurfaceDevice:
                surfaceKind = VideoSurfaceKind.Direct3D11;
                break;

            case AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX:
                surfaceKind = VideoSurfaceKind.VideoToolbox;
                break;

            default:
                return;
        }

        surfaceDevice = handle;

        // FFmpeg reserves its decoder-reference and frame-threading surfaces separately, but the
        // pool it then allocates is static: it is sized once, here, and a frame that finds it empty
        // fails to decode outright rather than waiting for one to come back. So this has to cover
        // every surface the pipeline can be holding at once -- and the pipeline must never hold more
        // than it, which is what the same number enforces on the way out.
        sharedSurfaceBudget = ReserveSurfaces();
        context->extra_hw_frames = sharedSurfaceBudget;
        AppLog.Information(
            "FFmpeg.Decode",
            $"#{id} reserving {context->extra_hw_frames} hardware surfaces of " +
            $"{SurfaceBytes() / (1024 * 1024)} MiB for zero-copy delivery.");
    }

    /// <summary>
    /// How many decoded surfaces the pipeline may hold at once, which is what the hardware pool is
    /// then sized to reserve.
    /// </summary>
    /// <remarks>
    /// The frame budget's byte ceiling bounds this as well as its frame count does, because these
    /// are graphics allocations rather than pooled buffers: a second of 4K HDR is around sixty
    /// surfaces, which is close to two gigabytes of video memory -- more than most machines have to
    /// spare, and more than any of them gain anything from holding.
    /// </remarks>
    private int ReserveSurfaces()
    {
        long bytes = SurfaceBytes();
        long affordable = bytes > 0 && options.FramePoolByteCapacity > 0
            ? (options.FramePoolByteCapacity / bytes) + SurfaceHeadroom
            : options.FramePoolSize;

        return (int)Math.Clamp(
            Math.Min(options.FramePoolSize, affordable),
            MinimumSharedSurfaces,
            MaximumSharedSurfaces);
    }

    /// <summary>
    /// Video memory one decoded surface occupies. Hardware decoders deliver 4:2:0, so a surface is
    /// a luma plane and half of one again, at one or two bytes a sample.
    /// </summary>
    private long SurfaceBytes()
    {
        int depth = SourceBitDepth();
        return (long)Width * Height * (depth > 8 ? 2 : 1) * 3 / 2;
    }

    /// <summary>
    /// Best available depth before the decoder has produced a frame. Some containers omit the
    /// pixel layout but retain the raw depth; PQ and HLG are ten-bit signals when both are absent.
    /// </summary>
    private int SourceBitDepth()
    {
        if (codecParameters == null)
            return 8;

        int depth = BitDepthOf((AVPixelFormat)codecParameters->format);
        if (depth <= 0)
            depth = codecParameters->bits_per_raw_sample;

        if (depth <= 8 &&
            VideoFrameMetadata.MapColorTransfer(codecParameters->color_trc) is
                VideoColorTransfer.Pq or VideoColorTransfer.Hlg)
        {
            depth = 10;
        }

        return Math.Max(depth, 8);
    }

    private static AVCodecHWConfig* FindHardwareConfig(AVCodec* decoder, AVHWDeviceType deviceType)
    {
        for (int index = 0; ; index++)
        {
            AVCodecHWConfig* config = avcodec_get_hw_config(decoder, index);
            if (config == null)
                return null;

            int methods = (int)config->methods;
            if (config->device_type == deviceType &&
                (methods & 1 /* (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX*/ ) != 0)
            {
                return config;
            }
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static AVPixelFormat SelectHardwareFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        if (context != null && context->opaque != null)
        {
            var owner = GCHandle.FromIntPtr((IntPtr)context->opaque).Target as FfmpegVideoDecoder;
            if (owner is not null)
            {
                for (AVPixelFormat* format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
                {
                    if (*format == owner.hardwarePixelFormat)
                        return *format;
                }
            }
        }

        return avcodec_default_get_format(context, formats);
    }

    private void ReleaseCodec(AVCodecContext* context)
    {
        // Before the codec context goes: the graph holds a reference to the device it opened.
        deviceScaler?.Dispose();
        deviceScaler = null;
        deviceScalingRequested = false;

        if (windowCodecReferences is { } references)
        {
            windowCodecReferences = null;
            hardwareOwner = default;
            references.Release();
        }
        else
        {
            if (context != null)
                avcodec_free_context(&context);

            outputWindow?.Dispose();
        }
        outputWindow = null;

        if (hardwareOwner.IsAllocated)
            hardwareOwner.Free();
        hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        surfaceKind = VideoSurfaceKind.None;
        surfaceDevice = IntPtr.Zero;
        EndSurfacePool();
        hardwareDeviceType = string.Empty;
        usingHardwareDecoder = false;
        hardwareBackedCodec = false;
        hardwareFrameFormatNegotiated = false;
        consecutiveDecodeErrors = 0;
        codec = null;
    }

    /// <summary>
    /// Reopens this stream with its software decoder after a hardware device fails. The caller must
    /// seek or otherwise feed a new keyframe before expecting output from the replacement context.
    /// </summary>
    public bool TryFallbackToSoftware()
    {
        if (disposed || !usingHardwareDecoder)
            return false;

        AppLog.Warning(
            "FFmpeg.Decode",
            $"#{id} hardware decoder on {hardwareDeviceType} failed during playback; opening software fallback.");
        AVCodecContext* replacement = CreateCodecContext(defaultDecoder);
        int opened = avcodec_open2(replacement, defaultDecoder, null);
        if (opened < 0)
        {
            AppLog.Error(
                "FFmpeg.Decode",
                $"#{id} software fallback decoder failed to open: {FfmpegError.Describe(opened)}.");
            avcodec_free_context(&replacement);
            return false;
        }

        AVCodecContext* failed = codec;
        ReleaseCodec(failed);

        if (transferredFrame != null)
        {
            AVFrame* handle = transferredFrame;
            av_frame_free(&handle);
            transferredFrame = null;
        }

        codec = replacement;
        lastPts = MediaTime.NoTimestamp;
        softwareFallbackOccurred = true;
        deliveryPath = VideoFrameDeliveryPath.Unknown;
        deliveredSampleFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        deliveredFormat = null;
        deliveredDolbyVisionActive = null;
        deliveredDolbyVisionEnhancementLayerActive = null;
        deliveredHdr10PlusActive = null;
        hardwareFailures.Add($"{diagnostics.HardwareDeviceType}: failed while decoding");
        PublishDiagnostics();
        AppLog.Information(
            "FFmpeg.Decode",
            $"#{id} switched to software decoder '{CodecNameOf(defaultDecoder)}'.");
        return true;
    }

    /// <summary>
    /// Set once the renderer has given up on decoder windows while this decoder is using one: its
    /// pictures are in a texture nothing here can draw, so the stream needs a different decoder
    /// rather than another attempt at this one.
    /// </summary>
    public string RequiredSoftwareFallbackReason =>
        outputWindow is not null ? VideoDecoderOutputSurfaces.Rejection : string.Empty;

    public VideoDecoderDiagnostics Diagnostics => diagnostics with
    {
        DecodeFrameTimeMilliseconds = Volatile.Read(ref decodeFrameTimeMilliseconds),
        EstimatedDecodeLatencyMilliseconds = Volatile.Read(ref estimatedDecodeLatencyMilliseconds),
    };

    /// <summary>
    /// Rebuilds the published snapshot from the codec context and the last frame handed over. Runs
    /// on the decode thread only, and only when something it reports has actually changed.
    /// </summary>
    private void PublishDiagnostics()
    {
        AVCodec* implementation = codec != null ? codec->codec : defaultDecoder;
        bool hardwareFramesDelivered = deliveryPath is
            VideoFrameDeliveryPath.HardwareSurface or
            VideoFrameDeliveryPath.HardwareReadback or
            VideoFrameDeliveryPath.HardwareConverted or
            VideoFrameDeliveryPath.ExternalTexture;
        bool hardwareActive = usingHardwareDecoder &&
            (deliveryPath == VideoFrameDeliveryPath.Unknown || hardwareFramesDelivered);

        diagnostics = new VideoDecoderDiagnostics
        {
            Implementation = "libavcodec",
            CodecName = CodecName,
            DecoderName = implementation != null && implementation->name != null
                ? new string((sbyte*)implementation->name)
                : CodecName,
            DecoderDescription = implementation != null && implementation->long_name != null
                ? new string((sbyte*)implementation->long_name)
                : string.Empty,
            Profile = codec != null ? av_get_profile_name(implementation, codec->profile) ?? string.Empty : string.Empty,
            Level = codec != null ? codec->level : -1,
            Width = Width,
            Height = Height,
            PixelAspectRatio = PixelAspectRatio,
            Rotation = Rotation,
            MirrorHorizontally = MirrorHorizontally,
            BitDepth = DescribeBitDepth(),
            IsHardwareAccelerated = hardwareActive,
            RequestedHardwareDeviceType = options.HardwareDeviceType ?? string.Empty,
            HardwareDeviceType = hardwareActive ? hardwareDeviceType : string.Empty,
            HardwareCandidates = hardwareAttempts.ToArray(),
            HardwareStatus = usingHardwareDecoder && !hardwareActive
                ? $"{hardwareDeviceType} opened, but the stream negotiated software frames"
                : DescribeHardwareStatus(),
            HardwarePixelFormat = hardwareActive &&
                (deliveryPath == VideoFrameDeliveryPath.Unknown || hardwareFrameFormatNegotiated)
                ? DescribePixelFormat(hardwarePixelFormat)
                : string.Empty,
            SamplePixelFormat = DescribePixelFormat(deliveredSampleFormat),
            OutputFormat = deliveredFormat,
            SurfaceKind = hardwareActive ? surfaceKind : VideoSurfaceKind.None,
            RendererSurfaceKind = VideoFrameCapabilities.SupportedSurfaceKind,
            RendererMaxPlanarBytesPerSample = VideoFrameCapabilities.MaxPlanarBytesPerSample,
            ZeroCopyActive = deliveryPath is VideoFrameDeliveryPath.HardwareSurface
                or VideoFrameDeliveryPath.ExternalTexture,
            DeviceScaledSize = DeviceScaledSize(),
            DeviceScalingStatus = deviceScaler?.Status ?? string.Empty,
            DeliveryPath = deliveryPath,
            ThreadCount = codec != null ? codec->thread_count : 0,
            ThreadType = "frame+slice",
            ColorSpace = deliveredColorSpace,
            ColorPrimaries = deliveredColorPrimaries,
            ColorTransfer = deliveredColorTransfer,
            HdrPeakLuminance = deliveredHdrPeakLuminance,
            ColorRange = deliveredColorRange,
            ChromaLocation = deliveredChromaLocation,
            DolbyVisionProfile = dolbyVisionProfile,
            DolbyVisionLevel = dolbyVisionLevel,
            DolbyVisionCompatibilityId = dolbyVisionCompatibilityId,
            DolbyVisionActive = deliveredDolbyVisionActive,
            DolbyVisionEnhancementLayerActive = deliveredDolbyVisionEnhancementLayerActive,
            Hdr10PlusApplicationVersion = deliveredHdr10PlusApplicationVersion,
            Hdr10PlusWindowCount = deliveredHdr10PlusWindowCount,
            Hdr10PlusTargetLuminance = deliveredHdr10PlusTargetLuminance,
            Hdr10PlusSceneAverageLuminance = deliveredHdr10PlusSceneAverageLuminance,
            Hdr10PlusAnchorCount = deliveredHdr10PlusAnchorCount,
            Hdr10PlusToneMapping = deliveredHdr10PlusToneMapping,
            Hdr10PlusActive = deliveredHdr10PlusActive,
            SoftwareFallbackOccurred = softwareFallbackOccurred,
        };
    }

    private string DescribeHardwareStatus()
    {
        if (usingHardwareDecoder)
            return string.Empty;
        if (hardwareFailures.Count > 0)
            return string.Join("; ", hardwareFailures);
        if (!options.EnableHardwareDecoding)
            return "turned off in settings";
        return hardwareCandidates.Count == 0
            ? "no hardware device type is available here"
            : "no device was tried";
    }

    /// <summary>
    /// Bits per luma sample of the decoded picture, which is what the stream is really in: the
    /// delivered format can be BGRA when swscale had to flatten it, and that is not 8-bit source.
    /// </summary>
    private int DescribeBitDepth()
    {
        int depth = BitDepthOf(deliveredSampleFormat);
        if (depth == 0 && codec != null)
            depth = BitDepthOf(codec->pix_fmt);
        if (depth == 0 && codecParameters != null)
            depth = BitDepthOf((AVPixelFormat)codecParameters->format);
        return depth;
    }

    private static int BitDepthOf(AVPixelFormat format)
    {
        if (format == AVPixelFormat.AV_PIX_FMT_NONE)
            return 0;

        AVPixFmtDescriptor* descriptor = av_pix_fmt_desc_get(format);
        return descriptor != null && descriptor->nb_components > 0 ? descriptor->comp[0].depth : 0;
    }

    private static string DescribePixelFormat(AVPixelFormat format) =>
        format == AVPixelFormat.AV_PIX_FMT_NONE ? string.Empty : av_get_pix_fmt_name(format) ?? string.Empty;

    private (int Width, int Height) DeviceScaledSize() =>
        deviceScalingRequested && deviceScaler is not null ? deviceScaler.ActiveSize : default;

    /// <summary>Republishes the snapshot when a frame arrives configured differently from the last.</summary>
    private void NoteDelivery(VideoFrame target, VideoFrameDeliveryPath path, AVPixelFormat sampleFormat)
    {
        VideoDecoderDiagnostics current = diagnostics;
        bool configurationChanged =
            deliveryPath != path ||
            deliveredFormat != target.Format ||
            deliveredSampleFormat != sampleFormat ||
            deliveredColorSpace != target.ColorSpace ||
            deliveredColorPrimaries != target.ColorPrimaries ||
            deliveredColorTransfer != target.ColorTransfer ||
            deliveredColorRange != target.ColorRange ||
            deliveredChromaLocation != target.ChromaLocation ||
            deliveredDolbyVisionActive != target.DolbyVision.IsPresent ||
            deliveredDolbyVisionEnhancementLayerActive != (target.EnhancementLayer is not null) ||
            deliveredHdr10PlusActive != target.Hdr10Plus.IsPresent ||
            deliveredHdr10PlusApplicationVersion != target.Hdr10Plus.ApplicationVersion ||
            deliveredHdr10PlusWindowCount != target.Hdr10Plus.WindowCount ||
            current.Width != Width ||
            current.Height != Height ||
            current.DeviceScaledSize != DeviceScaledSize() ||
            current.RendererSurfaceKind != VideoFrameCapabilities.SupportedSurfaceKind ||
            current.RendererMaxPlanarBytesPerSample != VideoFrameCapabilities.MaxPlanarBytesPerSample;
        if (deliveryPath == path &&
            deliveredFormat == target.Format &&
            deliveredSampleFormat == sampleFormat &&
            deliveredColorSpace == target.ColorSpace &&
            deliveredColorPrimaries == target.ColorPrimaries &&
            deliveredColorTransfer == target.ColorTransfer &&
            deliveredHdrPeakLuminance == target.HdrPeakLuminance &&
            deliveredColorRange == target.ColorRange &&
            deliveredChromaLocation == target.ChromaLocation &&
            deliveredDolbyVisionActive == target.DolbyVision.IsPresent &&
            deliveredDolbyVisionEnhancementLayerActive == (target.EnhancementLayer is not null) &&
            deliveredHdr10PlusActive == target.Hdr10Plus.IsPresent &&
            deliveredHdr10PlusApplicationVersion == target.Hdr10Plus.ApplicationVersion &&
            deliveredHdr10PlusWindowCount == target.Hdr10Plus.WindowCount &&
            deliveredHdr10PlusTargetLuminance == target.Hdr10Plus.TargetedSystemDisplayMaximumLuminance &&
            deliveredHdr10PlusSceneAverageLuminance == target.Hdr10Plus.SceneAverageLuminance &&
            deliveredHdr10PlusAnchorCount == target.Hdr10Plus.Windows[0].AnchorCount &&
            deliveredHdr10PlusToneMapping == target.Hdr10Plus.Windows[0].ToneMapping &&
            current.Width == Width &&
            current.Height == Height &&
            current.DeviceScaledSize == DeviceScaledSize() &&
            current.RendererSurfaceKind == VideoFrameCapabilities.SupportedSurfaceKind &&
            current.RendererMaxPlanarBytesPerSample == VideoFrameCapabilities.MaxPlanarBytesPerSample)
        {
            return;
        }

        deliveryPath = path;
        deliveredFormat = target.Format;
        deliveredSampleFormat = sampleFormat;
        deliveredColorSpace = target.ColorSpace;
        deliveredColorPrimaries = target.ColorPrimaries;
        deliveredColorTransfer = target.ColorTransfer;
        deliveredHdrPeakLuminance = target.HdrPeakLuminance;
        deliveredColorRange = target.ColorRange;
        deliveredChromaLocation = target.ChromaLocation;
        deliveredDolbyVisionActive = target.DolbyVision.IsPresent;
        deliveredDolbyVisionEnhancementLayerActive = target.EnhancementLayer is not null;
        deliveredHdr10PlusActive = target.Hdr10Plus.IsPresent;
        deliveredHdr10PlusApplicationVersion = target.Hdr10Plus.ApplicationVersion;
        deliveredHdr10PlusWindowCount = target.Hdr10Plus.WindowCount;
        deliveredHdr10PlusTargetLuminance = target.Hdr10Plus.TargetedSystemDisplayMaximumLuminance;
        deliveredHdr10PlusSceneAverageLuminance = target.Hdr10Plus.SceneAverageLuminance;
        deliveredHdr10PlusAnchorCount = target.Hdr10Plus.Windows[0].AnchorCount;
        deliveredHdr10PlusToneMapping = target.Hdr10Plus.Windows[0].ToneMapping;
        PublishDiagnostics();
        if (!configurationChanged)
            return;

        AppLog.Information(
            "FFmpeg.Decode",
            $"#{id} delivery configured: path={path}; size={target.Width}x{target.Height}; " +
            $"sampleFormat={DescribePixelFormat(sampleFormat)}; outputFormat={target.Format}; " +
            $"surfaceKind={target.Surface.Kind}; colorSpace={target.ColorSpace}; " +
            $"primaries={target.ColorPrimaries}; transfer={target.ColorTransfer}; " +
            $"hdrPeak={target.HdrPeakLuminance:F0}; colorRange={target.ColorRange}; " +
            $"dolbyVisionResidual={target.DolbyVision.EnhancementLayerResidual}; " +
            $"dolbyVisionFel={target.EnhancementLayer is not null}; " +
            $"hdr10Plus={target.Hdr10Plus.IsPresent}; " +
            $"chromaLocation={target.ChromaLocation}; fieldOrder={target.FieldOrder}.");
    }

    public int StreamIndex => streamIndex;

    public string CodecName { get; } = string.Empty;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public double PixelAspectRatio { get; private set; }

    public int Rotation { get; private set; }

    public bool MirrorHorizontally { get; private set; }

    /// <summary>Frames decoded since the decoder was created, for the diagnostics overlay.</summary>
    public long DecodedFrameCount => Interlocked.Read(ref decodedFrames);

    private long decodedFrames;

    public void Flush()
    {
        if (disposed)
            return;

        avcodec_flush_buffers(codec);
        enhancementDecoder?.Flush();
        alphaAdditionDecoder?.Flush();
        lastPts = MediaTime.NoTimestamp;
        decodeSubmissions.Clear();
        pendingDecodeWorkSeconds = 0;
    }

    public DecoderStatus Send(MediaPacket packet)
    {
        if (disposed)
            return DecoderStatus.Fatal;

        double started = MediaTime.Now;
        DecoderStatus status = SendCore(packet);
        pendingDecodeWorkSeconds += MediaTime.Now - started;
        if (!packet.IsEndOfStream && status == DecoderStatus.Ok)
            decodeSubmissions.Enqueue(started);
        return status;
    }

    private DecoderStatus SendCore(MediaPacket packet)
    {
        AVCodecContext* context = codec;
        if (context == null)
            return DecoderStatus.Fatal;

        if (packet.IsEndOfStream)
        {
            int drain = avcodec_send_packet(context, null);
            if (drain >= 0 || FfmpegError.IsEndOfFile(drain))
            {
                SendAlphaAddition(null);
                try
                {
                    enhancementDecoder?.Send(null);
                }
                catch (FfmpegException exception)
                {
                    AppLog.Warning(
                        "FFmpeg.Decode",
                        $"#{id} Dolby Vision enhancement-layer drain failed.",
                        exception);
                    enhancementDecoder?.Dispose();
                    enhancementDecoder = null;
                }
            }
            return drain < 0 && !FfmpegError.IsEndOfFile(drain)
                ? ClassifyDecodeError(drain)
                : DecoderStatus.Ok;
        }

        int ret = avcodec_send_packet(context, packet.Handle);
        if (ret >= 0)
        {
            SendAlphaAddition(packet.Handle);
            try
            {
                enhancementDecoder?.Send(packet.Handle);
            }
            catch (FfmpegException exception)
            {
                AppLog.Warning(
                    "FFmpeg.Decode",
                    $"#{id} Dolby Vision enhancement-layer decode failed; using the base layer only.",
                    exception);
                enhancementDecoder?.Dispose();
                enhancementDecoder = null;
            }
            return DecoderStatus.Ok;
        }

        if (FfmpegError.IsTryAgain(ret))
            return DecoderStatus.NeedMoreInput;

        // Corrupt packets and mid-stream parameter changes surface here; the decoder resynchronizes
        // at the next keyframe, so playback continues rather than failing.
        return ClassifyDecodeError(ret);
    }

    private void SendAlphaAddition(AVPacket* value)
    {
        try
        {
            alphaAdditionDecoder?.Send(value);
        }
        catch (FfmpegException exception)
        {
            AppLog.Warning("FFmpeg.Decode", $"#{id} alpha block addition decode failed; continuing without alpha.", exception);
            alphaAdditionDecoder?.Dispose();
            alphaAdditionDecoder = null;
        }
    }

    public DecoderStatus Receive(out VideoFrame decoded)
    {
        double started = MediaTime.Now;
        DecoderStatus status = ReceiveCore(out decoded);
        pendingDecodeWorkSeconds += MediaTime.Now - started;
        if (status == DecoderStatus.Ok)
            RecordDecodePerformance();
        return status;
    }

    private DecoderStatus ReceiveCore(out VideoFrame decoded)
    {
        decoded = null;

        // Read once: a dispose racing this call clears the fields, and half a call made against
        // handles that changed underneath it is what a null dereference here would be.
        AVCodecContext* context = codec;
        AVFrame* target = frame;
        if (disposed || context == null || target == null)
            return DecoderStatus.Fatal;

        int ret = avcodec_receive_frame(context, target);
        if (FfmpegError.IsTryAgain(ret))
            return DecoderStatus.NeedMoreInput;

        if (FfmpegError.IsEndOfFile(ret))
        {
            return DecoderStatus.EndOfStream;
        }

        if (ret < 0)
        {
            av_frame_unref(target);
            return ClassifyDecodeError(ret);
        }

        try
        {
            // The stream's own geometry, which a device-scaled picture no longer carries and the
            // layout above must not be told has changed.
            decodedWidth = target->width;
            decodedHeight = target->height;
            decoded = Convert(ScaleOnDevice(target));
            consecutiveDecodeErrors = 0;
            Interlocked.Increment(ref decodedFrames);
            return DecoderStatus.Ok;
        }
        catch (FfmpegException exception)
        {
            AppLog.Error("FFmpeg.Decode", $"#{id} could not convert a decoded video frame.", exception);
            return DecoderStatus.Fatal;
        }
        finally
        {
            AVFrame* transferred = transferredFrame;
            if (transferred != null)
                av_frame_unref(transferred);
            av_frame_unref(target);
        }
    }

    private void RecordDecodePerformance()
    {
        double frameTime = pendingDecodeWorkSeconds * 1000.0;
        pendingDecodeWorkSeconds = 0;
        if (frameTime > 0)
        {
            Volatile.Write(
                ref decodeFrameTimeMilliseconds,
                Smooth(Volatile.Read(ref decodeFrameTimeMilliseconds), frameTime));
        }

        if (decodeSubmissions.Count == 0)
            return;

        double latency = (MediaTime.Now - decodeSubmissions.Dequeue()) * 1000.0;
        if (latency > 0)
        {
            Volatile.Write(
                ref estimatedDecodeLatencyMilliseconds,
                Smooth(Volatile.Read(ref estimatedDecodeLatencyMilliseconds), latency));
        }
    }

    private static double Smooth(double current, double sample) =>
        current > 0 ? current + ((sample - current) * PerformanceSmoothingFactor) : sample;

    private DecoderStatus ClassifyDecodeError(int error)
    {
        consecutiveDecodeErrors++;
        bool fatal = (usingHardwareDecoder && error == AVERROR_EXTERNAL) ||
                     consecutiveDecodeErrors >= DecodeErrorThreshold;
        AppLog.Warning(
            "FFmpeg.Decode",
            $"#{id} decode error {consecutiveDecodeErrors}/{DecodeErrorThreshold}: " +
            $"{FfmpegError.Describe(error)}; hardware={usingHardwareDecoder}; fatal={fatal}.");
        return fatal
            ? DecoderStatus.Fatal
            : DecoderStatus.Recovered;
    }

    private static string CodecNameOf(AVCodec* decoder) =>
        decoder == null || decoder->name == null ? "unknown" : new string((sbyte*)decoder->name);

    /// <summary>
    /// States what the picture is about to be shown as, so that the device that decoded it can
    /// resize and letterbox it instead of the display filter. Null withdraws the request.
    /// </summary>
    /// <remarks>
    /// Called from the renderer's thread as it lays the picture out, and read by the decode thread
    /// on its next frame, so a request that arrives mid-frame simply applies to the one after.
    /// </remarks>
    public void RequestOutput(VideoOutputRequest request)
    {
        if (request is not null &&
            (request.PictureWidth <= 0 || request.PictureHeight <= 0 ||
             request.SurfaceWidth > MaximumDeviceScale || request.SurfaceHeight > MaximumDeviceScale))
        {
            request = null;
        }

        Volatile.Write(ref requestedOutput, request);
    }

    /// <summary>
    /// Resizes the decoded surface to the size it will be drawn at, where the renderer has asked
    /// for that and the device can do it. Otherwise the picture is handed on exactly as decoded.
    /// </summary>
    private AVFrame* ScaleOnDevice(AVFrame* input)
    {
        VideoOutputRequest request = Volatile.Read(ref requestedOutput);
        if (request is null || !VideoFrameCapabilities.SupportsSurfaces(surfaceKind) ||
            codec->hw_device_ctx == null)
        {
            deviceScalingRequested = false;
            deviceOutputCoversControl = false;
            return input;
        }

        if (deviceScaler is null)
        {
            deviceScaler = HardwareVideoScalerFactory.Create(
                id,
                surfaceKind,
                codec->hw_device_ctx,
                packetTimeBase,
                sharedSurfaceBudget);

            if (!deviceScaler.IsAvailable)
                AppLog.Information("FFmpeg.Decode", $"#{id} cannot resize on the device: {deviceScaler.Status}.");
        }

        if (!deviceScaler.IsAvailable)
        {
            deviceScalingRequested = false;
            deviceOutputCoversControl = false;
            return input;
        }

        // Chroma is subsampled, so an odd side has no plane to hold it.
        request = request with
        {
            PictureWidth = request.PictureWidth & ~1,
            PictureHeight = request.PictureHeight & ~1,
            SurfaceWidth = request.SurfaceWidth & ~1,
            SurfaceHeight = request.SurfaceHeight & ~1,
            OffsetX = request.OffsetX & ~1,
            OffsetY = request.OffsetY & ~1,
        };

        deviceScalingRequested = deviceScaler.TryScale(input, request, out AVFrame* scaled);
        deviceOutputCoversControl = deviceScalingRequested && deviceScaler.CoversOutput;
        return scaled;
    }

    /// <summary>Software layout a hardware surface reads back as.</summary>
    private static AVPixelFormat HardwareSampleFormat(AVFrame* input)
    {
        if (input->hw_frames_ctx == null)
            return AVPixelFormat.AV_PIX_FMT_NONE;

        return ((AVHWFramesContext*)input->hw_frames_ctx->data)->sw_format;
    }

    /// <summary>
    /// Reads a hardware surface straight into the pooled buffer. Landing it in a staging frame and
    /// copying from there would double the per-frame memory traffic, which at 4K120 is a quarter of
    /// the time available for the whole frame.
    /// </summary>
    private VideoFrame TransferPlanes(
        AVFrame* input,
        VideoPixelFormat format,
        AVPixelFormat sampleFormat,
        int width,
        int height)
    {
        if (input->crop_left > 0 || input->crop_top > 0)
            return TransferCroppedPlanes(input, format, sampleFormat, width, height);

        VideoFrame target = pool.Rent(width, height, format);

        // The transfer allocates its own storage unless buf[0] says the frame already has some, so
        // the pooled buffer is lent to it behind an AVBufferRef that frees nothing.
        AVBufferRef* wrapper = av_buffer_create(
            (byte*)target.Data,
            (nuint)target.SizeInBytes,
            keepPooledBuffer,
            null,
            0);

        if (wrapper == null)
        {
            target.Release();
            throw new FfmpegException("av_buffer_create returned null", 0);
        }

        transferredFrame->format = (int)sampleFormat;
        transferredFrame->width = width;
        transferredFrame->height = height;
        transferredFrame->buf[0] = wrapper;
        for (int plane = 0; plane < target.PlaneCount; plane++)
        {
            transferredFrame->data[(uint)plane] = (byte*)target.GetPlane(plane);
            transferredFrame->linesize[(uint)plane] = target.GetStride(plane);
        }

        int ret = D3D11HardwareContext.TransferFrame(transferredFrame, input);
        av_frame_unref(transferredFrame);
        if (ret < 0)
        {
            target.Release();
            FfmpegError.ThrowIfError(ret, "av_hwframe_transfer_data");
        }

        return target;
    }

    /// <summary>
    /// Reads a frame back through the transfer's own storage so the left and top of its cropping
    /// can be applied before the planes are copied into the pool.
    /// </summary>
    private VideoFrame TransferCroppedPlanes(
        AVFrame* input,
        VideoPixelFormat format,
        AVPixelFormat sampleFormat,
        int width,
        int height)
    {
        transferredFrame->format = (int)sampleFormat;
        transferredFrame->width = width;
        transferredFrame->height = height;
        try
        {
            FfmpegError.ThrowIfError(D3D11HardwareContext.TransferFrame(transferredFrame, input), "av_hwframe_transfer_data");
            transferredFrame->crop_left = input->crop_left;
            transferredFrame->crop_top = input->crop_top;
            FfmpegError.ThrowIfError(av_frame_apply_cropping(transferredFrame, FrameCropUnaligned), "av_frame_apply_cropping");
            return CopyPlanes(transferredFrame, format, transferredFrame->width, transferredFrame->height);
        }
        finally
        {
            av_frame_unref(transferredFrame);
        }
    }

    private VideoFrame Convert(AVFrame* input)
    {        int width = input->width;
        int height = input->height;
        var inputFormat = (AVPixelFormat)input->format;

        VideoFrame target;
        VideoFrameDeliveryPath path;
        AVPixelFormat sampleFormat = inputFormat;
        hardwareFrameFormatNegotiated = transferredFrame != null && inputFormat == hardwarePixelFormat;

        if (outputWindow is not null && inputFormat == AVPixelFormat.AV_PIX_FMT_MEDIACODEC)
        {
            target = WrapWindowPicture(input, width, height);
            path = VideoFrameDeliveryPath.ExternalTexture;
        }
        else if (hardwareFrameFormatNegotiated)
        {
            sampleFormat = HardwareSampleFormat(input);
            (VideoPixelFormat? surfaceFormat, VideoPixelFormat? readbackFormat) =
                MapHardwareDeliveryFormats(
                    sampleFormat,
                    VideoFrameCapabilities.MaxPlanarBytesPerSample);
            // libavcodec applies only the right and bottom of a hardware frame's cropping; the
            // rest is applied on readback, which a shared surface cannot have.
            bool cropped = input->crop_left > 0 || input->crop_top > 0;
            VideoFrame shared = !cropped && surfaceFormat is { } directSurface
                ? TryShareSurface(input, directSurface, width, height)
                : null;
            if (shared is not null)
            {
                // Sharing the surface skips the readback entirely; it is only possible while the
                // renderer's graphics backend can import one, which it establishes as it draws.
                target = shared;
                path = VideoFrameDeliveryPath.HardwareSurface;
            }
            else if (readbackFormat is { } direct)
            {
                target = TransferPlanes(input, direct, sampleFormat, width, height);
                path = VideoFrameDeliveryPath.HardwareReadback;
            }
            else
            {
                // Nothing the renderer can sample, so the readback needs a frame of its own for
                // swscale to read from.
                FfmpegError.ThrowIfError(
                    D3D11HardwareContext.TransferFrame(transferredFrame, input),
                    "av_hwframe_transfer_data");
                FfmpegError.ThrowIfError(
                    av_frame_copy_props(transferredFrame, input),
                    "av_frame_copy_props");
                if (cropped)
                    FfmpegError.ThrowIfError(av_frame_apply_cropping(transferredFrame, FrameCropUnaligned), "av_frame_apply_cropping");

                sampleFormat = (AVPixelFormat)transferredFrame->format;
                AVPixelFormat planarFormat = NearestPlanarFormat(sampleFormat);
                target = planarFormat != AVPixelFormat.AV_PIX_FMT_NONE && MapPlanarFormat(planarFormat) is { } planarLayout
                    ? ConvertToPlanar(transferredFrame, transferredFrame->width, transferredFrame->height, sampleFormat, planarFormat, planarLayout)
                    : ConvertToBgra(transferredFrame, transferredFrame->width, transferredFrame->height, sampleFormat);
                path = VideoFrameDeliveryPath.HardwareConverted;
            }
        }
        else if (alphaAdditionDecoder is not null &&
                 ConvertWithAlpha(input, inputFormat, width, height, out AVPixelFormat alphaFormat) is { } withAlpha)
        {
            sampleFormat = alphaFormat;
            target = withAlpha;
            path = VideoFrameDeliveryPath.SoftwareConverted;
        }
        else if (MapPlanarFormat(inputFormat) is { } planar)
        {
            target = CopyPlanes(input, planar, width, height);

            // MediaCodec and its like decode in hardware and copy the pixels out themselves unless
            // they were handed a surface to decode into, so software layouts from one of those are
            // still hardware-decoded frames that came back through memory.
            path = hardwareBackedCodec
                ? VideoFrameDeliveryPath.HardwareReadback
                : VideoFrameDeliveryPath.SoftwarePlanar;
        }
        else
        {
            target = ConvertToBgra(input, width, height, inputFormat);
            path = hardwareBackedCodec
                ? VideoFrameDeliveryPath.HardwareConverted
                : VideoFrameDeliveryPath.SoftwareConverted;
        }

        if (target.Format == VideoPixelFormat.Bgra32)
            target.HasAlpha = HasAlphaPlane(sampleFormat);
        ApplyColorMetadata(target, input, sampleFormat);

        long timestamp = input->best_effort_timestamp != AV_NOPTS_VALUE
            ? input->best_effort_timestamp
            : input->pts;

        double pts = source.ToSeconds(streamIndex, timestamp);
        if (!MediaTime.IsValid(pts))
        {
            // Timestamp discontinuity: keep the stream moving by extrapolating from the previous
            // frame instead of dropping everything that follows.
            pts = MediaTime.IsValid(lastPts) ? lastPts + nominalFrameDuration : 0;
        }

        // AVFrame.duration is in stream time-base units and is a plain interval, so it must not go
        // through ToSeconds (which also removes the container start offset).
        long frameDuration = input->duration;
        double duration = frameDuration > 0 ? frameDuration * timeBase : nominalFrameDuration;
        if (!(duration > 0))
            duration = nominalFrameDuration;

        target.Pts = pts;
        target.Duration = duration;
        target.IsKeyFrame = input->pict_type == AVPictureType.AV_PICTURE_TYPE_I;
        target.PixelAspectRatio = !hasDeclaredPixelAspectRatio &&
            input->sample_aspect_ratio.num > 0 && input->sample_aspect_ratio.den > 0
            ? Utils.RationalToDouble(input->sample_aspect_ratio)
            : PixelAspectRatio;

        // A frame carries the matrix only where a packet did; otherwise the container's own stands.
        if (VideoDisplayMatrix.TryRead(input, out int rotation, out bool mirrorHorizontally))
        {
            Rotation = rotation;
            MirrorHorizontally = mirrorHorizontally;
        }

        target.Rotation = Rotation;
        target.MirrorHorizontally = MirrorHorizontally;
        target.FieldOrder = ReadFieldOrder(input);
        target.IsHardwareScaled = deviceScalingRequested;
        target.CoversOutput = deviceOutputCoversControl;
        target.SourceWidth = decodedWidth;
        target.SourceHeight = decodedHeight;

        VideoFrame enhancementLayer = enhancementDecoder?.Take(timestamp);
        if (target.DolbyVision.EnhancementLayerResidual)
            target.AttachEnhancementLayer(enhancementLayer);
        else
            enhancementLayer?.Release();

        Width = decodedWidth > 0 ? decodedWidth : width;
        Height = decodedHeight > 0 ? decodedHeight : height;
        lastPts = pts;
        NoteDelivery(target, path, sampleFormat);
        return target;
    }

    /// <summary>
    /// Resolves a hardware frame's surface-import and readback layouts independently.
    /// </summary>
    /// <remarks>
    /// An imported surface becomes textures of its native format, so a failed CPU-plane probe must
    /// not veto it. A readback still obeys that probe and converts when its planes cannot be sampled.
    /// BGRA is admitted only as a surface because a failed import is cheaper to convert directly.
    /// </remarks>
    internal static (VideoPixelFormat? Surface, VideoPixelFormat? Readback) MapHardwareDeliveryFormats(
        AVPixelFormat format,
        int maxPlanarBytesPerSample)
    {
        VideoPixelFormat? planar = VideoFrameMetadata.MapLayout(format);
        VideoPixelFormat? surface = format == AVPixelFormat.AV_PIX_FMT_BGRA
            ? VideoPixelFormat.Bgra32
            : planar;
        VideoPixelFormat? readback = planar is { } candidate &&
                                     VideoPlanarLayout.For(candidate).BytesPerSample <=
                                     maxPlanarBytesPerSample
            ? candidate
            : null;
        return (surface, readback);
    }

    /// <summary>
    /// Layouts the renderer can sample directly. Everything else still goes through swscale, so an
    /// exotic source degrades in performance rather than failing. Only little-endian sample orders
    /// are listed because that is what a decoder produces natively on every platform in use.
    /// </summary>
    private static VideoPixelFormat? MapPlanarFormat(AVPixelFormat format)
    {
        VideoPixelFormat? mapped = VideoFrameMetadata.MapLayout(format);

        // A backend that turned out not to be able to sample this width of sample takes the frame
        // converted instead, for the rest of the session.
        return mapped is { } candidate &&
               !VideoFrameCapabilities.SupportsPlanarSamples(VideoPlanarLayout.For(candidate).BytesPerSample)
            ? null
            : mapped;
    }

    /// <summary>
    /// Color description comes from the decoded frame even when the pixels came from a readback,
    /// because a hardware surface carries the stream's tags while the transfer target does not.
    /// When a frame omits one, prefer what the decoder inferred from the bitstream over the
    /// container's original parameters.
    /// </summary>
    private void ApplyColorMetadata(VideoFrame target, AVFrame* input, AVPixelFormat sampleFormat)
    {
        AVColorSpace colorSpace = input->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED
            ? codec->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED
                ? codecParameters->color_space
                : codec->colorspace
            : input->colorspace;
        AVColorPrimaries primaries = input->color_primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED
            ? codec->color_primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED
                ? codecParameters->color_primaries
                : codec->color_primaries
            : input->color_primaries;
        AVColorTransferCharacteristic transfer =
            input->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED
                ? codec->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED
                    ? codecParameters->color_trc
                    : codec->color_trc
                : input->color_trc;
        AVColorRange range = input->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED
            ? codec->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED
                ? codecParameters->color_range
                : codec->color_range
            : input->color_range;
        AVChromaLocation chromaLocation = input->chroma_location == AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED
            ? codec->chroma_sample_location == AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED
                ? codecParameters->chroma_location
                : codec->chroma_sample_location
            : input->chroma_location;

        target.ColorSpace = VideoFrameMetadata.MapColorSpace(colorSpace, decodedHeight > 0 ? decodedHeight : input->height);
        target.ColorPrimaries = VideoFrameMetadata.MapColorPrimaries(primaries, target.ColorSpace);
        target.ColorTransfer = VideoFrameMetadata.MapColorTransfer(transfer);
        target.HdrPeakLuminance = ReadHdrPeakLuminance(input);
        target.HdrMasteringLuminance = VideoFrameMetadata.ReadHdrMasteringLuminance(input) ?? declaredHdrMasteringLuminance;
        if (Hdr10PlusMetadataReader.TryRead(input, target.Hdr10Plus) &&
            target.Hdr10Plus.EffectivePeakLuminance > 0)
        {
            target.HdrPeakLuminance = target.Hdr10Plus.EffectivePeakLuminance;
        }
        target.ColorRange = range == AVColorRange.AVCOL_RANGE_JPEG ||
                    (target.Format == VideoPixelFormat.ExternalTexture &&
                     target.ExternalTextureColorModel == VideoExternalTextureColorModel.Rgb) ||
                            VideoFrameMetadata.IsFullRangeFormat(sampleFormat)
            ? VideoColorRange.Full
            : VideoColorRange.Limited;
        target.ChromaLocation = VideoFrameMetadata.MapChromaLocation(chromaLocation);

        if (VideoPlanarLayout.IsPlanar(target.Format) &&
            DolbyVisionMetadataReader.TryRead(
                input,
                dolbyVisionProfile,
                dolbyVisionLevel,
                dolbyVisionCompatibilityId,
                target.DolbyVision))
        {
            // The RPU matrices produce BT.2020-referred PQ RGB regardless of how the base layer
            // itself is tagged. Profile 5 in particular commonly carries no ordinary CICP tags.
            target.ColorSpace = VideoColorSpace.Bt2020;
            target.ColorPrimaries = VideoColorPrimaries.Bt2020;
            target.ColorTransfer = VideoColorTransfer.Pq;
            target.ColorRange = target.DolbyVision.BaseLayerFullRange
                ? VideoColorRange.Full
                : VideoColorRange.Limited;
            target.HdrPeakLuminance = target.DolbyVision.EffectivePeakLuminance;
        }
    }

    /// <summary>
    /// How the rows of one decoded picture were sampled.
    /// </summary>
    /// <remarks>
    /// A frame's own flags are the only ones that can be right for mixed content -- soft telecine
    /// interleaves progressive pictures into a stream the container still declares interlaced -- but
    /// a hardware decoder handing back a surface often sets none, so the container's declaration
    /// stands in. Where that turns out to overstate it, the renderer's own field test is what
    /// stops the picture from being touched.
    /// </remarks>
    private VideoFieldOrder ReadFieldOrder(AVFrame* input)
    {
        if (declaredFieldOrder == VideoFieldOrder.Progressive)
            return VideoFieldOrder.Progressive;

        if ((input->flags & AV_FRAME_FLAG_INTERLACED) != 0)
        {
            return (input->flags & AV_FRAME_FLAG_TOP_FIELD_FIRST) != 0
                ? VideoFieldOrder.TopFieldFirst
                : VideoFieldOrder.BottomFieldFirst;
        }

        return declaredFieldOrder;
    }

    /// <summary>
    /// Hands the decoded surface over instead of its pixels, holding a reference to the decoder's
    /// frame so the surface is not recycled underneath the renderer. Null when the renderer has not
    /// been found able to import surfaces of this kind, which is the case until it has drawn once
    /// and permanently again if importing ever fails.
    /// </summary>
    private VideoFrame TryShareSurface(AVFrame* input, VideoPixelFormat format, int width, int height)
    {
        if (!VideoFrameCapabilities.SupportsSurfaces(surfaceKind) ||
            Volatile.Read(ref surfaceDeliveryRejected) != 0)
            return null;

        // The pool is static, so handing out more than was reserved does not slow the decoder down:
        // it fails the next frame outright, and what comes out of the decoder after that is a
        // surface nothing ever wrote. Reading this one back instead costs bandwidth and leaves the
        // stream whole.
        if (Interlocked.Increment(ref sharedSurfaces) > sharedSurfaceBudget)
        {
            ReleaseSharedSurface();
            if (!sharedSurfaceBudgetReported)
            {
                sharedSurfaceBudgetReported = true;
                AppLog.Warning(
                    "FFmpeg.Decode",
                    $"#{id} all {sharedSurfaceBudget} reserved hardware surfaces are held downstream; " +
                    "frames are being read back until one comes back.");
            }

            return null;
        }

        VideoSurfaceHandle handle = DescribeSurface(input, PoolGenerationOf(input));
        if (!handle.IsValid)
        {
            ReleaseSharedSurface();
            return null;
        }

        AVFrame* held = av_frame_alloc();
        if (held == null)
        {
            ReleaseSharedSurface();
            return null;
        }

        if (av_frame_ref(held, input) < 0)
        {
            av_frame_free(&held);
            ReleaseSharedSurface();
            return null;
        }

        VideoFrame target = pool.RentSurface(width, height, format);
        target.Surface = handle;
        target.RejectSurfaceDelivery = RejectSurfaceDelivery;
        SurfacePoolReferences poolReferences = surfacePoolReferences;
        poolReferences.AddRef();

        // The pool the surface came from was allocated at the coded size, which the renderer has to
        // sample the picture out of rather than assume away.
        if (input->hw_frames_ctx != null)
        {
            var frames = (AVHWFramesContext*)input->hw_frames_ctx->data;
            target.SurfaceWidth = frames->width;
            target.SurfaceHeight = frames->height;
        }

        // The reference has to outlive every draw the frame takes part in, and the frame is the only
        // thing that knows when that is.
        var reference = (IntPtr)held;
        target.Recycled = _ =>
        {
            ReleaseSharedFrame(reference);
            ReleaseSharedSurface();
            poolReferences.Release();
        };
        target.DownloadSamples = () => DownloadSharedSurface(reference, target);

        return target;
    }

    private bool RejectSurfaceDelivery(VideoFrame rejected)
    {
        if (Interlocked.Exchange(ref surfaceDeliveryRejected, 1) != 0)
            return false;

        AppLog.Warning(
            "FFmpeg.Decode",
            $"#{id} renderer rejected {rejected.Surface.Kind} surface delivery; " +
            "this decoder will read back subsequent frames.");
        return true;
    }

    private void ReleaseSharedSurface() => Interlocked.Decrement(ref sharedSurfaces);

    /// <summary>
    /// Names where the decoded frame sits, in the terms the API it was decoded by uses. Where the
    /// planes are is a property of the layout rather than of the frame, so only the surface itself
    /// is described.
    /// </summary>
    private VideoSurfaceHandle DescribeSurface(AVFrame* input, long pool) => surfaceKind switch
    {
        // data[3] is the VASurfaceID.
        VideoSurfaceKind.Vaapi when input->data[3] != null =>
            new VideoSurfaceHandle(surfaceKind, surfaceDevice, (long)(nint)input->data[3], 0, pool),

        VideoSurfaceKind.VideoToolbox when input->data[3] != null =>
            new VideoSurfaceHandle(surfaceKind, IntPtr.Zero, (long)(nint)input->data[3], 0, pool),

        // data[0] is the ID3D11Texture2D the whole pool shares and data[1] the frame's slice of it.
        VideoSurfaceKind.Direct3D11 when input->data[0] != null =>
            new VideoSurfaceHandle(
                surfaceKind,
                surfaceDevice,
                (long)(nint)input->data[0],
                (long)(nint)input->data[1],
                pool),

        _ => VideoSurfaceHandle.None,
    };

    /// <summary>
    /// Numbers the pool the frame's surface came from. A pool is freed whole and the next one
    /// starts its identifiers over, so this is what tells a renderer holding on to what it made of
    /// a surface that the name now belongs to something else.
    /// </summary>
    private long PoolGenerationOf(AVFrame* input)
    {
        IntPtr current = input->hw_frames_ctx != null ? (IntPtr)input->hw_frames_ctx->data : IntPtr.Zero;
        if (current != surfacePool || surfacePoolReferences is null)
        {
            EndSurfacePool();
            surfacePool = current;
            surfacePoolReferences = new SurfacePoolReferences(Interlocked.Increment(ref nextSurfacePool));
        }

        return surfacePoolReferences.Pool;
    }

    /// <summary>
    /// Stops numbering frames with the current pool, which retires it once the last frame shared
    /// from it is recycled.
    /// </summary>
    private void EndSurfacePool()
    {
        surfacePool = IntPtr.Zero;
        surfacePoolReferences?.Release();
        surfacePoolReferences = null;
    }

    private static void ReleaseSharedFrame(IntPtr reference)
    {
        AVFrame* held = (AVFrame*)reference;
        av_frame_free(&held);
    }

    /// <summary>
    /// Reads a shared surface back into memory for a holder of <paramref name="source"/>, leaving the
    /// surface in place. A frame of its own receives the samples, so the decoder's staging frame is
    /// never touched from another thread.
    /// </summary>
    private static VideoFrame DownloadSharedSurface(IntPtr reference, VideoFrame source)
    {
        AVFrame* held = (AVFrame*)reference;
        AVFrame* downloaded = av_frame_alloc();
        if (downloaded == null)
            throw new OutOfMemoryException();
        try
        {
            if (held->hw_frames_ctx != null)
                downloaded->format = (int)((AVHWFramesContext*)held->hw_frames_ctx->data)->sw_format;
            FfmpegError.ThrowIfError(D3D11HardwareContext.TransferFrame(downloaded, held), "av_hwframe_transfer_data");
            VideoPixelFormat format = VideoFrameMetadata.MapLayout((AVPixelFormat)downloaded->format) ??
                throw new NotSupportedException($"Surfaces downloaded as {(AVPixelFormat)downloaded->format} cannot be read.");
            var address = (IntPtr)downloaded;
            VideoFrame frame = VideoFrame.CreateBorrowed(downloaded->width, downloaded->height, format,
                [(IntPtr)downloaded->data[0], (IntPtr)downloaded->data[1], (IntPtr)downloaded->data[2]],
                [downloaded->linesize[0], downloaded->linesize[1], downloaded->linesize[2]],
                _ => ReleaseSharedFrame(address));
            downloaded = null;
            try
            {
                CopyDescription(source, frame);
                return frame;
            }
            catch
            {
                frame.Release();
                throw;
            }
        }
        finally
        {
            if (downloaded != null)
                av_frame_free(&downloaded);
        }
    }

    private static void CopyDescription(VideoFrame source, VideoFrame target)
    {
        target.ColorSpace = source.ColorSpace;
        target.ColorPrimaries = source.ColorPrimaries;
        target.ColorTransfer = source.ColorTransfer;
        target.ColorRange = source.ColorRange;
        target.ChromaLocation = source.ChromaLocation;
        target.FieldOrder = source.FieldOrder;
        target.HdrPeakLuminance = source.HdrPeakLuminance;
        target.HdrMasteringLuminance = source.HdrMasteringLuminance;
        target.Pts = source.Pts;
        target.Duration = source.Duration;
        target.PixelAspectRatio = source.PixelAspectRatio;
        target.Rotation = source.Rotation;
        target.MirrorHorizontally = source.MirrorHorizontally;
        target.SourceWidth = source.SourceWidth;
        target.SourceHeight = source.SourceHeight;
        if (source.DolbyVision.IsPresent &&
            !VideoFrameMetadataCodec.TryRead(VideoFrameMetadataCodec.Write(source.DolbyVision), target.DolbyVision))
            throw new InvalidDataException("The frame's Dolby Vision metadata could not be copied.");
        if (source.Hdr10Plus.IsPresent &&
            !VideoFrameMetadataCodec.TryRead(VideoFrameMetadataCodec.Write(source.Hdr10Plus), target.Hdr10Plus))
            throw new InvalidDataException("The frame's HDR10+ metadata could not be copied.");
        if (source.EnhancementLayer is { } enhancement)
            target.AttachEnhancementLayer(enhancement.AddRef());
    }

    /// <summary>Retains decoder planes on browsers, or copies them into a pooled buffer.</summary>
    private VideoFrame CopyPlanes(AVFrame* input, VideoPixelFormat format, int width, int height)
    {
        if (OperatingSystem.IsBrowser() &&
            input->linesize[0] > 0 && input->linesize[1] > 0 && input->linesize[2] >= 0)
        {
            AVFrame* retained = av_frame_clone(input);
            if (retained != null)
            {
                IntPtr address = (IntPtr)retained;
                try
                {
                    return VideoFrame.CreateBorrowed(width, height, format,
                        [(IntPtr)retained->data[0], (IntPtr)retained->data[1], (IntPtr)retained->data[2]],
                        [retained->linesize[0], retained->linesize[1], retained->linesize[2]],
                        _ => ReleaseSharedFrame(address), pool);
                }
                catch
                {
                    ReleaseSharedFrame(address);
                    throw;
                }
            }
        }

        VideoFrame target = pool.Rent(width, height, format);
        for (int plane = 0; plane < target.PlaneCount; plane++)
        {
            av_image_copy_plane(
                (byte*)target.GetPlane(plane),
                target.GetStride(plane),
                input->data[(uint)plane],
                input->linesize[(uint)plane],
                target.GetRowBytes(plane),
                target.GetPlaneHeight(plane));
        }

        return target;
    }

    private double ReadHdrPeakLuminance(AVFrame* input)
    {
        double peak = VideoFrameMetadata.ReadHdrPeakLuminance(input);

        // Hardware decoders do not consistently copy static HDR10 side data onto output frames.
        // The portable stream description retained it before AVCodecParameters was rebuilt.
        return peak > 0 ? peak : declaredHdrPeakLuminance;
    }

    /// <summary>Whether swscale carried an alpha plane into the BGRA conversion.</summary>
    private static bool HasAlphaPlane(AVPixelFormat format)
    {
        AVPixFmtDescriptor* descriptor = av_pix_fmt_desc_get(format);
        return descriptor != null && (descriptor->flags & AV_PIX_FMT_FLAG_ALPHA) != 0;
    }

    /// <summary>
    /// The planar layout a semi-planar sample format repacks into with its depth and chroma
    /// resolution intact, where a hardware surface reads back as one the renderer cannot sample.
    /// </summary>
    private static AVPixelFormat NearestPlanarFormat(AVPixelFormat format) => format switch
    {
        AVPixelFormat.AV_PIX_FMT_NV16 => AVPixelFormat.AV_PIX_FMT_YUV422P,
        AVPixelFormat.AV_PIX_FMT_NV24 => AVPixelFormat.AV_PIX_FMT_YUV444P,
        AVPixelFormat.AV_PIX_FMT_P210LE => AVPixelFormat.AV_PIX_FMT_YUV422P10LE,
        AVPixelFormat.AV_PIX_FMT_P410LE => AVPixelFormat.AV_PIX_FMT_YUV444P10LE,
        AVPixelFormat.AV_PIX_FMT_P212LE => AVPixelFormat.AV_PIX_FMT_YUV422P12LE,
        AVPixelFormat.AV_PIX_FMT_P412LE => AVPixelFormat.AV_PIX_FMT_YUV444P12LE,
        AVPixelFormat.AV_PIX_FMT_P216LE => AVPixelFormat.AV_PIX_FMT_YUV422P16LE,
        AVPixelFormat.AV_PIX_FMT_P416LE => AVPixelFormat.AV_PIX_FMT_YUV444P16LE,
        _ => AVPixelFormat.AV_PIX_FMT_NONE,
    };

    private VideoFrame ConvertToPlanar(
        AVFrame* input,
        int width,
        int height,
        AVPixelFormat inputFormat,
        AVPixelFormat planarFormat,
        VideoPixelFormat layout)
    {
        EnsureScaler(width, height, inputFormat, planarFormat, input->colorspace, input->color_range);

        VideoFrame target = pool.Rent(width, height, layout);

        int converted = Scale(input, target);
        if (converted < 0)
        {
            target.Release();
            throw new FfmpegException($"sws_scale_frame failed: {FfmpegError.Describe(converted)}", converted);
        }

        return target;
    }

    /// <summary>
    /// Converts <paramref name="input"/> together with the alpha picture decoded beside it, viewed
    /// as one frame with an alpha plane, or returns null when no alpha picture accompanies it.
    /// </summary>
    private VideoFrame ConvertWithAlpha(AVFrame* input, AVPixelFormat format, int width, int height, out AVPixelFormat alphaFormat)
    {
        alphaFormat = format switch
        {
            AVPixelFormat.AV_PIX_FMT_YUV420P => AVPixelFormat.AV_PIX_FMT_YUVA420P,
            AVPixelFormat.AV_PIX_FMT_YUV422P => AVPixelFormat.AV_PIX_FMT_YUVA422P,
            AVPixelFormat.AV_PIX_FMT_YUV444P => AVPixelFormat.AV_PIX_FMT_YUVA444P,
            AVPixelFormat.AV_PIX_FMT_YUV420P10LE => AVPixelFormat.AV_PIX_FMT_YUVA420P10LE,
            AVPixelFormat.AV_PIX_FMT_YUV422P10LE => AVPixelFormat.AV_PIX_FMT_YUVA422P10LE,
            AVPixelFormat.AV_PIX_FMT_YUV444P10LE => AVPixelFormat.AV_PIX_FMT_YUVA444P10LE,
            _ => AVPixelFormat.AV_PIX_FMT_NONE,
        };
        if (alphaFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            return null;

        long timestamp = input->best_effort_timestamp != AV_NOPTS_VALUE ? input->best_effort_timestamp : input->pts;
        AVFrame* alpha = alphaAdditionDecoder.Take(timestamp);
        if (alpha == null)
            return null;

        try
        {
            if (alpha->width != width || alpha->height != height || alpha->buf[0] == null || input->buf[0] == null)
                return null;

            if (alphaComposite == null)
                alphaComposite = av_frame_alloc();
            if (alphaComposite == null)
                throw new FfmpegException("av_frame_alloc returned null", 0);

            // A view over both pictures: the color planes and buffers of the main frame, the alpha
            // picture's luma as the fourth plane, each buffer referenced so the view keeps them alive.
            av_frame_unref(alphaComposite);
            FfmpegError.ThrowIfError(av_frame_copy_props(alphaComposite, input), "av_frame_copy_props");
            alphaComposite->format = (int)alphaFormat;
            alphaComposite->width = width;
            alphaComposite->height = height;
            uint slot = 0;
            for (uint plane = 0; plane < 3; plane++)
            {
                alphaComposite->data[plane] = input->data[plane];
                alphaComposite->linesize[plane] = input->linesize[plane];
            }
            alphaComposite->data[3] = alpha->data[0];
            alphaComposite->linesize[3] = alpha->linesize[0];
            for (uint buffer = 0; buffer < 7; buffer++)
            {
                if (input->buf[buffer] != null)
                    alphaComposite->buf[slot++] = av_buffer_ref(input->buf[buffer]);
            }
            alphaComposite->buf[slot] = av_buffer_ref(alpha->buf[0]);
            try
            {
                return ConvertToBgra(alphaComposite, width, height, alphaFormat);
            }
            finally
            {
                av_frame_unref(alphaComposite);
            }
        }
        finally
        {
            av_frame_free(&alpha);
        }
    }

    private VideoFrame ConvertToBgra(AVFrame* input, int width, int height, AVPixelFormat inputFormat)
    {
        EnsureScaler(width, height, inputFormat, AVPixelFormat.AV_PIX_FMT_BGRA, input->colorspace, input->color_range);

        VideoFrame target = pool.Rent(width, height, VideoPixelFormat.Bgra32);

        int converted = Scale(input, target);
        if (converted < 0)
        {
            target.Release();
            throw new FfmpegException($"sws_scale_frame failed: {FfmpegError.Describe(converted)}", converted);
        }

        return target;
    }

    /// <summary>
    /// Converts into the pooled buffer through the frame API: plain <c>sws_scale</c> always runs on
    /// the calling thread, only this entry point dispatches to the scaler's slice threads.
    /// </summary>
    private int Scale(AVFrame* input, VideoFrame target)
    {
        AVBufferRef* wrapper = av_buffer_create(
            (byte*)target.Data,
            (nuint)target.SizeInBytes,
            keepPooledBuffer,
            null,
            0);

        if (wrapper == null)
            return -ENOMEM;

        scaled->width = target.Width;
        scaled->height = target.Height;
        scaled->format = (int)scalerOutputFormat;
        for (int plane = 0; plane < target.PlaneCount; plane++)
        {
            scaled->data[(uint)plane] = (byte*)target.GetPlane(plane);
            scaled->linesize[(uint)plane] = target.GetStride(plane);
        }

        scaled->buf[0] = wrapper;

        int ret = sws_scale_frame(scaler, scaled, input);
        av_frame_unref(scaled);
        return ret;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void KeepPooledBuffer(void* opaque, byte* data)
    {
    }

    private void EnsureScaler(int width, int height, AVPixelFormat format, AVPixelFormat outputFormat, AVColorSpace colorSpace, AVColorRange colorRange)
    {
        if (scaler != null &&
            scalerWidth == width &&
            scalerHeight == height &&
            scalerInputFormat == format &&
            scalerOutputFormat == outputFormat &&
            scalerColorSpace == (int)colorSpace &&
            scalerColorRange == (int)colorRange)
        {
            return;
        }

        if (scaler != null)
        {
            sws_freeContext(scaler);
            scaler = null;
        }

        // The context is built by hand rather than with sws_getCachedContext because the thread
        // count is only settable before sws_init_context.
        SwsContext* created = sws_alloc_context();
        if (created == null)
            throw new FfmpegException("sws_alloc_context returned null", 0);

        int ret = ConfigureScaler(created, width, height, format, outputFormat, ScalerThreadCount(height));
        if (ret >= 0)
            ret = sws_init_context(created, null, null);

        if (ret < 0)
        {
            sws_freeContext(created);
            throw new FfmpegException($"swscale setup failed for {av_get_pix_fmt_name(format)}", ret);
        }

        scaler = created;
        scalerWidth = width;
        scalerHeight = height;
        scalerInputFormat = format;
        scalerOutputFormat = outputFormat;
        scalerColorSpace = (int)colorSpace;
        scalerColorRange = (int)colorRange;

        // A planar repack keeps the samples' own range; only the RGB conversion decodes the matrix.
        if (outputFormat == AVPixelFormat.AV_PIX_FMT_BGRA)
            ApplyColorSpace(colorSpace, colorRange);
    }

    private static int ConfigureScaler(SwsContext* context, int width, int height, AVPixelFormat format, AVPixelFormat outputFormat, int threads)
    {
        int ret;
        if ((ret = av_opt_set_int(context, "srcw", width, 0)) < 0 ||
            (ret = av_opt_set_int(context, "srch", height, 0)) < 0 ||
            (ret = av_opt_set_pixel_fmt(context, "src_format", format, 0)) < 0 ||
            (ret = av_opt_set_int(context, "dstw", width, 0)) < 0 ||
            (ret = av_opt_set_int(context, "dsth", height, 0)) < 0 ||
            (ret = av_opt_set_pixel_fmt(context, "dst_format", outputFormat, 0)) < 0 ||
            (ret = av_opt_set_int(context, "sws_flags", ScalerFlags, 0)) < 0 ||
            (ret = av_opt_set_int(context, "threads", threads, 0)) < 0)
        {
            return ret;
        }

        return 0;
    }

    /// <summary>
    /// swscale converts on a single thread unless told otherwise, which a large RGB-coded source
    /// can outrun. Threads slice the output, so tie the count to the frame height and leave small
    /// frames alone, where the split would cost more than it saves.
    /// </summary>
    private static int ScalerThreadCount(int height) =>
        OperatingSystem.IsBrowser() ? 1 : Math.Clamp(height / 256, 1, Math.Min(Environment.ProcessorCount, 16));

    private void ApplyColorSpace(AVColorSpace colorSpace, AVColorRange colorRange)
    {
        // Without this, BT.709 content (anything HD) is converted with BT.601 coefficients and
        // comes out visibly over-saturated.
        int space = colorSpace == AVColorSpace.AVCOL_SPC_UNSPECIFIED
            ? (scalerHeight >= 720 ? (int)AVColorSpace.AVCOL_SPC_BT709 : (int)AVColorSpace.AVCOL_SPC_BT470BG)
            : (int)colorSpace;

        int* coefficients = sws_getCoefficients(space);
        if (coefficients == null)
            return;

        int sourceFullRange = colorRange == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0;
        sws_setColorspaceDetails(
            scaler,
            coefficients,
            sourceFullRange,
            coefficients,
            1,
            0,
            1 << 16,
            1 << 16);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        // Before the codec context, whose hardware device the graph holds a reference to.
        deviceScaler?.Dispose();
        deviceScaler = null;

        if (frame != null)
        {
            AVFrame* handle = frame;
            av_frame_free(&handle);
            frame = null;
        }

        if (scaled != null)
        {
            AVFrame* handle = scaled;
            av_frame_free(&handle);
            scaled = null;
        }

        if (transferredFrame != null)
        {
            AVFrame* handle = transferredFrame;
            av_frame_free(&handle);
            transferredFrame = null;
        }

        ReleaseCodec(codec);
        enhancementDecoder?.Dispose();
        enhancementDecoder = null;
        alphaAdditionDecoder?.Dispose();
        alphaAdditionDecoder = null;
        if (alphaComposite != null)
        {
            AVFrame* handle = alphaComposite;
            av_frame_free(&handle);
            alphaComposite = null;
        }

        if (codecParameters != null)
        {
            AVCodecParameters* handle = codecParameters;
            codecParameters = null;
            CodecParameters.FreeNative(&handle);
        }

        if (scaler != null)
        {
            sws_freeContext(scaler);
            scaler = null;
        }

        pool.Clear();
    }

    private sealed class WindowCodecReferences(
        AVCodecContext* context,
        IVideoDecoderOutputSurface window,
        GCHandle hardwareOwner)
    {
        private int references = 1;

        public void AddRef() => Interlocked.Increment(ref references);

        public void Release()
        {
            if (Interlocked.Decrement(ref references) != 0)
                return;

            AVCodecContext* handle = context;
            avcodec_free_context(&handle);
            window.Dispose();
            if (hardwareOwner.IsAllocated)
                hardwareOwner.Free();
        }
    }

    /// <summary>
    /// Counts the decoder and the shared frames still using one surface pool, retiring the pool
    /// once none of them is.
    /// </summary>
    private sealed class SurfacePoolReferences(long pool)
    {
        private int references = 1;

        public long Pool => pool;

        public void AddRef() => Interlocked.Increment(ref references);

        public void Release()
        {
            if (Interlocked.Decrement(ref references) == 0)
                VideoFrameCapabilities.RetireSurfacePool(pool);
        }
    }
}

public sealed record VideoDecoderOptions
{
    public static VideoDecoderOptions Default { get; } = new();

    /// <summary>0 lets the decoder pick based on the machine.</summary>
    public int ThreadCount { get; init; }

    /// <summary>Uses a supported hardware decoder when one can be initialized.</summary>
    public bool EnableHardwareDecoding { get; init; }

    /// <summary>
    /// Why hardware decoding was switched off for this decoder, when it was switched off by a
    /// failure rather than by the user. Empty when the setting is the whole story.
    /// </summary>
    public string SoftwareFallbackReason { get; init; } = string.Empty;

    /// <summary>
    /// FFmpeg hardware device type to prefer, or <c>null</c> to try the current platform's known
    /// types in preference order.
    /// </summary>
    public string HardwareDeviceType { get; init; }

    /// <summary>
    /// Per-device-type arguments the user chose for the calls the hardware path makes. Null leaves
    /// every one of them at its FFmpeg default.
    /// </summary>
    public HardwareDecoderSettings HardwareSettings { get; init; }

    /// <summary>
    /// Lets a hardware codec draw into a platform window where the head registered one, instead of
    /// having it copy pixels out.
    /// </summary>
    public bool AllowDecoderOutputWindow { get; init; } = true;

    /// <summary>
    /// Decoded frames kept alive at once. Must cover the frame queue plus the frame the renderer
    /// is holding; larger values only add latency and memory.
    /// </summary>
    public int FramePoolSize { get; init; } = 12;

    /// <summary>
    /// Ceiling on the pooled buffers kept for reuse. Anything beyond it is freed on return and
    /// allocated again on the next frame, so it wants to match the queue's own byte ceiling.
    /// </summary>
    public long FramePoolByteCapacity { get; init; } = 256L * 1024 * 1024;
}
