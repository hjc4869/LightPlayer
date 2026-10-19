#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Interop;
using LightStudio.Logging;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Transcoding;

/// <summary>One encoder this machine can actually produce a track with.</summary>
/// <param name="CodecName">Codec short name a client asks for, e.g. <c>h264</c>.</param>
/// <param name="EncoderName">Implementation behind it, e.g. <c>h264_vaapi</c>.</param>
/// <param name="EncoderInterface">Encoder family, e.g. <c>mf</c> or <c>nvenc</c>.</param>
/// <param name="DeviceType">Hardware device it runs on, empty when it runs on the processor.</param>
/// <param name="Supports10Bit">Whether the hardware encoder accepts P010 input.</param>
public sealed partial record TranscodeEncoder(
    string CodecName,
    string EncoderName,
    string EncoderInterface,
    string DeviceType,
    bool Supports10Bit = false)
{
    public bool IsHardware => DeviceType.Length > 0;
}

/// <summary>Encoder choices resolved for one request.</summary>
public sealed record TranscodeCapabilities(
    string EncoderInterface,
    string DeviceType,
    IReadOnlyList<TranscodeEncoder> VideoEncoders,
    IReadOnlyList<TranscodeEncoder> AudioEncoders)
{
    public static TranscodeCapabilities None { get; } = new(string.Empty, string.Empty, [], []);

    public bool IsAvailable => VideoEncoders.Count > 0 || AudioEncoders.Count > 0;

    public bool IsHardware => DeviceType.Length > 0;

    public TranscodeEncoder? FindVideo(string? codecName) => Find(VideoEncoders, codecName);

    public TranscodeEncoder? FindAudio(string? codecName) => Find(AudioEncoders, codecName);

    private static TranscodeEncoder? Find(IReadOnlyList<TranscodeEncoder> encoders, string? codecName)
    {
        if (encoders.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(codecName))
        {
            return encoders[0];
        }

        foreach (TranscodeEncoder encoder in encoders)
        {
            if (string.Equals(encoder.CodecName, codecName, StringComparison.OrdinalIgnoreCase))
            {
                return encoder;
            }
        }

        return null;
    }
}

/// <summary>
/// Works out what the loaded FFmpeg and the machine under it can encode, by asking rather than by
/// assuming: exact backend names establish what the build contains, and opening a small encoder
/// context establishes which codecs the installed driver can actually produce.
/// </summary>
public static unsafe partial class FfmpegTranscodeCapabilities
{
    /// <summary>
    /// Output codecs offered, best first. Everything here decodes in a browser as well as it does
    /// natively, which is the whole point of re-encoding to it.
    /// </summary>
    private static readonly string[] VideoCodecNames = ["av1", "hevc", "h264"];

    private static readonly string[] AudioCodecNames = ["opus"];

    private static readonly string[] VideoFilterNames = ["buffer", "buffersink", "scale", "format"];

    private static readonly string[] HardwareTransferFilterNames = ["hwdownload", "hwupload"];

    private static readonly string[] AudioFilterNames = ["abuffer", "abuffersink", "aformat", "aresample"];

    private static readonly string[] HardwareEncoderSuffixes =
    [
        "_mf",
        "_qsv",
        "_amf",
        "_nvenc",
        "_vaapi",
        "_videotoolbox",
        "_mediacodec",
        "_d3d12va",
        "_vulkan",
    ];

    public static TranscodeCapabilities Describe(TranscodeOptions options)
    {
        if (!options.Enabled)
        {
            return TranscodeCapabilities.None;
        }

        bool hasVideoFilters = HasRequiredFilters(VideoFilterNames);
        bool allowSoftwareEncoding = hasVideoFilters && options.AllowSoftwareEncoding && HasSoftwareVideoEncoders();
        IReadOnlyList<TranscodeEncoderInterface> interfaces = hasVideoFilters && options.EnableHardware &&
            HasRequiredFilters(HardwareTransferFilterNames)
            ? CandidateEncoderInterfaces(options)
            : [];
        var video = new List<TranscodeEncoder>();
        foreach (string codecName in VideoCodecNames)
        {
            TranscodeEncoderInterface? hardware = interfaces.FirstOrDefault(
                candidate => FindHardwareEncoder(codecName, candidate) != null);
            if (hardware is not null)
            {
                video.Add(new TranscodeEncoder(codecName, hardware.EncoderName(codecName), hardware.Name, hardware.DeviceType));
            }
            else if (allowSoftwareEncoding)
            {
                AVCodec* encoder = FindSoftwareEncoder(codecName, AVMediaType.AVMEDIA_TYPE_VIDEO);
                if (encoder != null)
                {
                    video.Add(new TranscodeEncoder(codecName, NameOf(encoder), string.Empty, string.Empty));
                }
            }
        }

        var audio = new List<TranscodeEncoder>();
        if (HasRequiredFilters(AudioFilterNames))
        {
            foreach (string codecName in AudioCodecNames)
            {
                AVCodec* encoder = FindSoftwareEncoder(codecName, AVMediaType.AVMEDIA_TYPE_AUDIO);
                if (encoder != null)
                {
                    audio.Add(new TranscodeEncoder(codecName, NameOf(encoder), string.Empty, string.Empty));
                }
            }
        }

        TranscodeEncoder? first = video.FirstOrDefault();
        return new TranscodeCapabilities(first?.EncoderInterface ?? string.Empty, first?.DeviceType ?? string.Empty, video, audio);
    }

    public static TranscodeCapabilities Probe(TranscodeOptions options, IReadOnlyCollection<string>? requestedCodecs = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return TranscodeCapabilities.None;
        }

        try
        {
            options = options with
            {
                AllowSoftwareEncoding = options.AllowSoftwareEncoding && HasSoftwareVideoEncoders(),
            };
            uint codecVersion = avcodec_version();
            uint filterVersion = avfilter_version();
            AppLog.Information(
                "FFmpeg.Transcode",
                $"Probing capabilities with libavcodec {Major(codecVersion)} and libavfilter {Major(filterVersion)}; " +
                $"hardware={options.EnableHardware}; softwareFallback={options.AllowSoftwareEncoding}; " +
                $"requestedEncoder={options.HardwareEncoder ?? "auto"}.");

            TranscodeCapabilities capabilities = ProbeCore(options, requestedCodecs);
            AppLog.Information(
                "FFmpeg.Transcode",
                $"Capability probe completed: encoderInterface={capabilities.EncoderInterface}; " +
                $"deviceType={capabilities.DeviceType}; " +
                $"video=[{string.Join(", ", capabilities.VideoEncoders)}]; " +
                $"audio=[{string.Join(", ", capabilities.AudioEncoders)}].");
            return capabilities;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
            NotSupportedException or TypeInitializationException)
        {
            AppLog.Error(
                "FFmpeg.Transcode",
                "Capability probing could not call the required FFmpeg codec/filter APIs. " +
                "Verify that every FFmpeg shared library matching the bindings is installed.",
                exception);
            return TranscodeCapabilities.None;
        }
    }

    private static TranscodeCapabilities ProbeCore(TranscodeOptions options, IReadOnlyCollection<string>? requestedCodecs)
    {
        string[] videoCodecs = VideoCodecNames.Where(codec =>
            requestedCodecs is null || requestedCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (videoCodecs.Length > 0 && !HasRequiredFilters(VideoFilterNames, logMissing: true))
        {
            videoCodecs = [];
        }

        string[] audioCodecs = AudioCodecNames.Where(codec =>
            requestedCodecs is null || requestedCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (audioCodecs.Length > 0 && !HasRequiredFilters(AudioFilterNames, logMissing: true))
        {
            audioCodecs = [];
        }

        var audio = new List<TranscodeEncoder>();
        foreach (string codecName in audioCodecs)
        {
            AVCodec* encoder = FindSoftwareEncoder(codecName, AVMediaType.AVMEDIA_TYPE_AUDIO);
            if (encoder != null)
            {
                audio.Add(new TranscodeEncoder(codecName, NameOf(encoder), string.Empty, string.Empty));
            }
        }

        var video = new List<TranscodeEncoder>();
        if (options.EnableHardware && videoCodecs.Length > 0 &&
            HasRequiredFilters(HardwareTransferFilterNames, logMissing: true))
        {
            foreach (TranscodeEncoderInterface encoderInterface in CandidateEncoderInterfaces(options))
            {
                AppLog.Information(
                    "FFmpeg.Transcode",
                    $"Trying encoder interface '{encoderInterface.Name}' on device type '{encoderInterface.DeviceType}'.");
                // Asking whether the device opens is the only test that means anything: a build can
                // carry a VA-API encoder on a machine with no render node to run it on.
                AVBufferRef* device = TryCreateDevice(encoderInterface.DeviceType, options.HardwareSettings);
                if (device == null)
                {
                    AppLog.Warning(
                        "FFmpeg.Transcode",
                        $"Device type '{encoderInterface.DeviceType}' for encoder interface " +
                        $"'{encoderInterface.Name}' was unavailable.");
                    continue;
                }

                try
                {
                    foreach (string codecName in videoCodecs)
                    {
                        if (video.Any(choice => choice.CodecName == codecName))
                        {
                            continue;
                        }

                        AVCodec* encoder = FindHardwareEncoder(codecName, encoderInterface);
                        if (encoder == null || !TryOpenHardwareEncoder(
                                encoder,
                                encoderInterface,
                                device,
                                options.HardwareSettings))
                        {
                            continue;
                        }

                        bool supports10Bit = TryOpenHardwareEncoder(
                            encoder,
                            encoderInterface,
                            device,
                            options.HardwareSettings,
                            AVPixelFormat.AV_PIX_FMT_P010LE,
                            logUnavailable: false);
                        video.Add(new TranscodeEncoder(
                            codecName,
                            NameOf(encoder),
                            encoderInterface.Name,
                            encoderInterface.DeviceType,
                            supports10Bit));
                    }
                }
                finally
                {
                    av_buffer_unref(&device);
                }

                if (video.Count == videoCodecs.Length)
                {
                    return new TranscodeCapabilities(
                        encoderInterface.Name,
                        encoderInterface.DeviceType,
                        video,
                        audio);
                }
            }
        }

        foreach (string codecName in videoCodecs)
        {
            if (!options.AllowSoftwareEncoding || video.Any(choice => choice.CodecName == codecName))
            {
                continue;
            }

            AVCodec* encoder = FindSoftwareEncoder(codecName, AVMediaType.AVMEDIA_TYPE_VIDEO);
            if (encoder != null)
            {
                video.Add(new TranscodeEncoder(codecName, NameOf(encoder), string.Empty, string.Empty));
            }
        }

        TranscodeEncoder? selected = video.FirstOrDefault();
        return new TranscodeCapabilities(selected?.EncoderInterface ?? string.Empty, selected?.DeviceType ?? string.Empty, video, audio);
    }

    public static bool HasSoftwareVideoEncoders()
    {
        try
        {
            foreach (string codecName in VideoCodecNames)
            {
                if (FindSoftwareEncoder(codecName, AVMediaType.AVMEDIA_TYPE_VIDEO) != null)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
            NotSupportedException or TypeInitializationException)
        {
            AppLog.Error("FFmpeg.Transcode", "Could not enumerate FFmpeg software video encoders.", exception);
            return false;
        }
    }

    private static bool HasRequiredFilters(IReadOnlyList<string> filterNames, bool logMissing = false)
    {
        string[] missing = filterNames.Where(name => avfilter_get_by_name(name) == null).ToArray();
        if (missing.Length == 0)
        {
            return true;
        }

        if (logMissing)
        {
            AppLog.Warning("FFmpeg.Transcode", $"Required transcoding filters are unavailable: {string.Join(", ", missing)}.");
        }

        return false;
    }

    /// <summary>
    /// Encoder interfaces compiled into the loaded FFmpeg for this platform. This intentionally
    /// tests encoder names rather than hardware-device names: <c>d3d11va</c> is therefore never
    /// presented as an encoder, while <c>h264_mf</c> makes Media Foundation present.
    /// </summary>
    public static IReadOnlyList<TranscodeEncoderInterface> GetAvailableEncoderInterfaces()
    {
        try
        {
            var availableDeviceTypes = new HashSet<string>(
                FfmpegHardwareDevices.GetAvailableTypes(),
                StringComparer.OrdinalIgnoreCase);
            var result = new List<TranscodeEncoderInterface>();
            foreach (TranscodeEncoderInterface encoderInterface in
                TranscodeEncoderInterfaces.GetPreferredForCurrentPlatform())
            {
                if (!availableDeviceTypes.Contains(encoderInterface.DeviceType))
                {
                    continue;
                }

                foreach (string codecName in VideoCodecNames)
                {
                    if (avcodec_find_encoder_by_name(encoderInterface.EncoderName(codecName)) != null)
                    {
                        result.Add(encoderInterface);
                        break;
                    }
                }
            }

            return result;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
            NotSupportedException or TypeInitializationException)
        {
            AppLog.Error("FFmpeg.Transcode", "Could not enumerate FFmpeg encoder interfaces.", exception);
            return [];
        }
    }

    /// <summary>The selected encoder interface, or the platform's available interfaces in preference order.</summary>
    internal static IReadOnlyList<TranscodeEncoderInterface> CandidateEncoderInterfaces(TranscodeOptions options)
    {
        TranscodeEncoderInterface? requested = TranscodeEncoderInterfaces.Find(options.HardwareEncoder);
        return requested is null ? GetAvailableEncoderInterfaces() : [requested];
    }

    /// <summary>Opens a hardware device with the arguments the head configured, or null.</summary>
    internal static AVBufferRef* TryCreateDevice(string deviceType, HardwareDecoderSettings? settings)
    {
        AVHWDeviceType type = FfmpegHardwareDevices.FindType(deviceType);
        if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            return null;
        }

        settings ??= HardwareDecoderSettings.Empty;
        string selected = settings.QueryDevice(deviceType);

        AVDictionary* opts = null;
        foreach (KeyValuePair<string, string> option in settings.QueryOptions(
            deviceType,
            HardwareDecoderApis.DeviceCreate,
            HardwareDecoderParameters.Options))
        {
            av_dict_set(&opts, option.Key, option.Value, 0);
        }

        int flags = settings.QueryFlags(
            deviceType,
            HardwareDecoderApis.DeviceCreate,
            HardwareDecoderParameters.Flags);

        AppLog.Information(
            "FFmpeg.Device",
            $"Opening device type={deviceType}; device={(selected.Length == 0 ? "default" : selected)}; " +
            $"options={settings.QueryOptions(deviceType, HardwareDecoderApis.DeviceCreate, HardwareDecoderParameters.Options).Count}; " +
            $"flags={flags}.");

        AVBufferRef* device = null;
        int created = av_hwdevice_ctx_create(
            &device,
            type,
            selected.Length == 0 ? null : selected,
            opts,
            flags);
        av_dict_free(&opts);

        if (created >= 0)
        {
            AppLog.Information("FFmpeg.Device", $"Opened device type={deviceType}; device={(selected.Length == 0 ? "default" : selected)}.");
            return device;
        }

        AppLog.Warning(
            "FFmpeg.Device",
            $"Could not open device type={deviceType}; device={(selected.Length == 0 ? "default" : selected)}; " +
            $"error={FfmpegError.Describe(created)}.");

        if (device != null)
        {
            av_buffer_unref(&device);
        }

        return null;
    }

    /// <summary>The exact implementation for a codec and encoder interface.</summary>
    internal static AVCodec* FindHardwareEncoder(
        string codecName,
        TranscodeEncoderInterface encoderInterface)
    {
        AVCodecID id = FindCodecId(codecName);
        if (id == AVCodecID.AV_CODEC_ID_NONE)
        {
            return null;
        }

        AVCodec* encoder = avcodec_find_encoder_by_name(encoderInterface.EncoderName(codecName));
        return encoder != null && encoder->id == id && av_codec_is_encoder(encoder) != 0
            ? encoder
            : null;
    }

    /// <summary>
    /// Opens a small encoder context to prove the installed driver can run it. Merely finding
    /// <c>h264_nvenc</c> in a build is not enough on a machine without an NVIDIA driver, just as
    /// finding <c>h264_amf</c> is not enough without the AMF runtime.
    /// </summary>
    internal static bool TryOpenHardwareEncoder(
        AVCodec* encoder,
        TranscodeEncoderInterface encoderInterface,
        AVBufferRef* device,
        HardwareDecoderSettings? settings,
        AVPixelFormat softwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NV12,
        bool logUnavailable = true)
    {
        if (encoder == null || device == null)
        {
            return false;
        }

        settings ??= HardwareDecoderSettings.Empty;
        AVCodecContext* context = avcodec_alloc_context3(encoder);
        if (context == null)
        {
            return false;
        }

        AVDictionary* opts = null;
        try
        {
            context->width = 640;
            context->height = 360;
            context->time_base = new AVRational { num = 1, den = 30 };
            context->framerate = new AVRational { num = 30, den = 1 };
            context->bit_rate = 2_000_000;
            context->rc_max_rate = 2_000_000;
            context->rc_buffer_size = 4_000_000;
            context->gop_size = 60;
            context->max_b_frames = 0;
            context->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
            context->hw_device_ctx = av_buffer_ref(device);

            if (encoderInterface.ProbeWithHardwareFrames)
            {
                AVCodecHWConfig* config = FindHardwareFramesConfig(
                    encoder,
                    FfmpegHardwareDevices.FindType(encoderInterface.DeviceType));
                if (config == null)
                {
                    return false;
                }

                AVBufferRef* frames = av_hwframe_ctx_alloc(device);
                if (frames == null)
                {
                    return false;
                }

                var framesContext = (AVHWFramesContext*)frames->data;
                framesContext->format = config->pix_fmt;
                framesContext->sw_format = softwarePixelFormat;
                framesContext->width = context->width;
                framesContext->height = context->height;
                framesContext->initial_pool_size = 8;
                int initialized = av_hwframe_ctx_init(frames);
                if (initialized < 0)
                {
                    av_buffer_unref(&frames);
                    string framesMessage =
                        $"Encoder '{NameOf(encoder)}' probe could not create {encoderInterface.DeviceType} " +
                        $"frames in {PixelFormatName(softwarePixelFormat)}: {FfmpegError.Describe(initialized)}.";
                    if (logUnavailable)
                    {
                        AppLog.Warning("FFmpeg.Transcode", framesMessage);
                    }
                    else
                    {
                        AppLog.Information("FFmpeg.Transcode", framesMessage);
                    }
                    return false;
                }

                context->pix_fmt = config->pix_fmt;
                context->hw_frames_ctx = frames;
            }
            else
            {
                // These encoders accept software formats as well as their hardware frame format. A
                // small open verifies the vendor runtime and selected device without inventing the
                // API-specific allocation flags that the real filter graph negotiates itself.
                // For Media Foundation, hw_encoding=1 additionally requires a hardware MFT.
                context->pix_fmt = softwarePixelFormat;
            }

            foreach (KeyValuePair<string, string> option in settings.QueryOptions(
                encoderInterface.Name,
                HardwareEncoderApis.EncoderOpen,
                HardwareEncoderParameters.Options))
            {
                av_dict_set(&opts, option.Key, option.Value, 0);
            }

            int opened = avcodec_open2(context, encoder, &opts);
            if (opened >= 0)
            {
                AppLog.Information(
                    "FFmpeg.Transcode",
                    $"Encoder interface '{encoderInterface.Name}' proved usable through '{NameOf(encoder)}' " +
                    $"with {PixelFormatName(softwarePixelFormat)}.");
                return true;
            }

            string encoderMessage =
                $"Encoder '{NameOf(encoder)}' is compiled in but unavailable with " +
                $"{PixelFormatName(softwarePixelFormat)}: {FfmpegError.Describe(opened)}";
            if (logUnavailable)
            {
                AppLog.Warning("FFmpeg.Transcode", encoderMessage);
            }
            else
            {
                AppLog.Information("FFmpeg.Transcode", encoderMessage);
            }
            return false;
        }
        finally
        {
            av_dict_free(&opts);
            avcodec_free_context(&context);
        }
    }

    private static AVCodecHWConfig* FindHardwareFramesConfig(AVCodec* codec, AVHWDeviceType deviceType)
    {
        if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            return null;
        }

        for (int index = 0; ; index++)
        {
            AVCodecHWConfig* config = avcodec_get_hw_config(codec, index);
            if (config == null)
            {
                return null;
            }

            // AV_CODEC_HW_CONFIG_METHOD_HW_FRAMES_CTX.
            if (config->device_type == deviceType && ((int)config->methods & 2) != 0)
            {
                return config;
            }
        }
    }

    /// <summary>
    /// Finds a processor-side encoder, preferring one FFmpeg does not consider experimental: a build
    /// carrying both the native <c>opus</c> encoder and <c>libopus</c> iterates the native one first,
    /// and that one refuses to open unless the compliance bar is lowered for it.
    /// </summary>
    internal static AVCodec* FindSoftwareEncoder(string codecName, AVMediaType mediaType)
    {
        AVCodecID id = FindCodecId(codecName);
        if (id == AVCodecID.AV_CODEC_ID_NONE)
        {
            return null;
        }

        AVCodec* experimental = null;
        void* iterator = null;
        AVCodec* encoder;
        while ((encoder = av_codec_iterate(&iterator)) != null)
        {
            if (encoder->id == id &&
                encoder->type == mediaType &&
                av_codec_is_encoder(encoder) != 0 &&
                (encoder->capabilities & AV_CODEC_CAP_HARDWARE) == 0 &&
                !IsHardwareEncoderName(NameOf(encoder)))
            {
                if (!IsExperimental(encoder))
                {
                    return encoder;
                }

                if (experimental == null)
                {
                    experimental = encoder;
                }
            }
        }

        return experimental;
    }

    internal static bool IsExperimental(AVCodec* codec) =>
        codec != null && (codec->capabilities & AV_CODEC_CAP_EXPERIMENTAL) != 0;

    private static bool IsHardwareEncoderName(string name)
    {
        foreach (string suffix in HardwareEncoderSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static AVCodecID FindCodecId(string codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName))
        {
            return AVCodecID.AV_CODEC_ID_NONE;
        }

        AVCodecDescriptor* descriptor = avcodec_descriptor_get_by_name(codecName);
        return descriptor != null ? descriptor->id : AVCodecID.AV_CODEC_ID_NONE;
    }

    internal static string NameOf(AVCodec* codec) =>
        codec == null ? string.Empty : new string((sbyte*)codec->name);

    private static string PixelFormatName(AVPixelFormat format) =>
        av_get_pix_fmt_name(format) ?? format.ToString();

    private static uint Major(uint version) => version >> 16;
}
