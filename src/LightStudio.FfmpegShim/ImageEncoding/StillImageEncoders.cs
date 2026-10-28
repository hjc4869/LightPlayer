#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Transcoding;
using LightStudio.Logging;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.ImageEncoding;

public enum StillImageCodec
{
    Hevc,
    Av1,
    JpegXl,
}

/// <summary>One encoder the loaded FFmpeg can produce still pictures with.</summary>
public sealed class StillImageEncoderInfo
{
    internal StillImageEncoderInfo(string name, StillImageCodec codec, string displayName,
        IReadOnlyList<AVPixelFormat> pixelFormats, TranscodeEncoderInterface? hardware = null)
    {
        Name = name;
        Codec = codec;
        DisplayName = displayName;
        PixelFormats = pixelFormats;
        Hardware = hardware;
    }

    /// <summary>The FFmpeg encoder name, such as <c>libx265</c>.</summary>
    public string Name { get; }

    public StillImageCodec Codec { get; }

    /// <summary>The encoder library or hardware interface as users know it.</summary>
    public string DisplayName { get; }

    public bool IsHardware => Hardware is not null;

    /// <summary>Picture layouts the encoder accepts, in FFmpeg's terms.</summary>
    public IReadOnlyList<AVPixelFormat> PixelFormats { get; }

    /// <summary>The largest width or height coded as one picture; larger images become grids of tiles.</summary>
    public int MaximumDimension => IsHardware ? 4096 : Codec == StillImageCodec.Hevc ? 8192 : 16384;

    internal TranscodeEncoderInterface? Hardware { get; }

    public bool Supports(AVPixelFormat format) => PixelFormats.Contains(format);

    public WireStillImageEncoder ToWire() => new(Name, Codec, DisplayName,
        PixelFormats.Select(format => (int)format).ToArray(), Hardware?.Name);

    /// <summary>Describes an encoder another runtime found, such as a browser's media worker.</summary>
    public static StillImageEncoderInfo FromWire(WireStillImageEncoder encoder) => new(encoder.Name, encoder.Codec,
        encoder.DisplayName, encoder.PixelFormats.Select(format => (AVPixelFormat)format).ToArray(),
        TranscodeEncoderInterfaces.Find(encoder.Hardware));
}

/// <summary>
/// Finds the still-picture encoders the loaded FFmpeg provides. Hardware encoders are probed only for
/// codecs without a software encoder, by opening them on the platform's device.
/// </summary>
public static unsafe class StillImageEncoders
{
    private static readonly (string Name, StillImageCodec Codec, string DisplayName)[] SoftwareEncoders =
    [
        ("libx265", StillImageCodec.Hevc, "x265"),
        ("libkvazaar", StillImageCodec.Hevc, "Kvazaar"),
        ("libaom-av1", StillImageCodec.Av1, "libaom"),
        ("libsvtav1", StillImageCodec.Av1, "SVT-AV1"),
        ("librav1e", StillImageCodec.Av1, "rav1e"),
        ("libjxl", StillImageCodec.JpegXl, "libjxl"),
    ];

    private static readonly AVPixelFormat[] KnownFormats =
    [
        AVPixelFormat.AV_PIX_FMT_YUV420P,
        AVPixelFormat.AV_PIX_FMT_YUV422P,
        AVPixelFormat.AV_PIX_FMT_YUV444P,
        AVPixelFormat.AV_PIX_FMT_YUV420P10LE,
        AVPixelFormat.AV_PIX_FMT_YUV422P10LE,
        AVPixelFormat.AV_PIX_FMT_YUV444P10LE,
        AVPixelFormat.AV_PIX_FMT_YUV420P12LE,
        AVPixelFormat.AV_PIX_FMT_YUV422P12LE,
        AVPixelFormat.AV_PIX_FMT_YUV444P12LE,
        AVPixelFormat.AV_PIX_FMT_GBRP,
        AVPixelFormat.AV_PIX_FMT_GRAY8,
        AVPixelFormat.AV_PIX_FMT_GRAY10LE,
        AVPixelFormat.AV_PIX_FMT_GRAY12LE,
        AVPixelFormat.AV_PIX_FMT_GRAY16LE,
        AVPixelFormat.AV_PIX_FMT_RGB24,
        AVPixelFormat.AV_PIX_FMT_RGBA,
        AVPixelFormat.AV_PIX_FMT_RGB48LE,
        AVPixelFormat.AV_PIX_FMT_RGBA64LE,
    ];

    private static readonly object Gate = new();
    private static Task<IReadOnlyList<StillImageEncoderInfo>>? probe;

    /// <summary>Probes once per process on a worker thread; later calls share the result.</summary>
    public static Task<IReadOnlyList<StillImageEncoderInfo>> ProbeAsync()
    {
        lock (Gate)
            return probe ??= Task.Run(Probe);
    }

    private static IReadOnlyList<StillImageEncoderInfo> Probe()
    {
        var encoders = new List<StillImageEncoderInfo>();
        try
        {
            foreach ((string name, StillImageCodec codec, string displayName) in SoftwareEncoders)
            {
                AVCodec* encoder = avcodec_find_encoder_by_name(name);
                if (encoder == null)
                    continue;
                AVPixelFormat[] formats = SupportedFormats(encoder);
                if (formats.Length > 0)
                    encoders.Add(new StillImageEncoderInfo(name, codec, displayName, formats));
            }

            foreach (StillImageCodec codec in new[] { StillImageCodec.Hevc, StillImageCodec.Av1 })
            {
                if (!encoders.Any(encoder => encoder.Codec == codec) && ProbeHardware(codec) is { } hardware)
                    encoders.Add(hardware);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
            NotSupportedException or TypeInitializationException)
        {
            AppLog.Error("FFmpeg.StillEncode", "Could not enumerate FFmpeg still-image encoders.", exception);
        }

        AppLog.Information("FFmpeg.StillEncode",
            $"Still-image encoders: [{string.Join(", ", encoders.Select(encoder => encoder.Name))}].");
        return encoders;
    }

    /// <summary>The first hardware encoder for the codec that opens on this machine, or null.</summary>
    internal static StillImageEncoderInfo? ProbeHardware(StillImageCodec codec)
    {
        string codecName = codec == StillImageCodec.Hevc ? "hevc" : "av1";
        foreach (TranscodeEncoderInterface encoderInterface in FfmpegTranscodeCapabilities.GetAvailableEncoderInterfaces())
        {
            AVCodec* encoder = FfmpegTranscodeCapabilities.FindHardwareEncoder(codecName, encoderInterface);
            if (encoder == null)
                continue;
            AVBufferRef* device = FfmpegTranscodeCapabilities.TryCreateDevice(encoderInterface.DeviceType, null);
            if (device == null)
                continue;
            try
            {
                var formats = new List<AVPixelFormat>();
                foreach (AVPixelFormat format in new[] { AVPixelFormat.AV_PIX_FMT_NV12, AVPixelFormat.AV_PIX_FMT_P010LE })
                {
                    if (FfmpegTranscodeCapabilities.TryOpenHardwareEncoder(encoder, encoderInterface, device, null, format,
                            logUnavailable: false))
                        formats.Add(format);
                }

                if (formats.Count > 0)
                    return new StillImageEncoderInfo(FfmpegTranscodeCapabilities.NameOf(encoder), codec,
                        encoderInterface.DisplayName, formats, encoderInterface);
            }
            finally
            {
                av_buffer_unref(&device);
            }
        }

        return null;
    }

    private static AVPixelFormat[] SupportedFormats(AVCodec* encoder)
    {
        void* configurations = null;
        int count = 0;
        int result = avcodec_get_supported_config(null, encoder, AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT, 0,
            &configurations, &count);
        if (result < 0 || configurations == null || count <= 0)
            return [];
        AVPixelFormat[] supported = new ReadOnlySpan<AVPixelFormat>(configurations, count).ToArray();
        return KnownFormats.Where(format => Array.IndexOf(supported, format) >= 0).ToArray();
    }
}
