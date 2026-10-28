using System;
using System.Collections.Generic;
using System.Linq;
using LightStudio.FfmpegShim.ImageEncoding;
using LightStudio.NativeBindings.Types;

namespace LightStudio.LightPlayer.Services.PhotoExport;

public enum PhotoExportFormat
{
    Jpeg,
    Png,
    WebP,
    Avif,
    Heic,
    JpegXl,
}

/// <summary>Sample layouts offered for saving, independent of the encoder implementing them.</summary>
public enum PhotoExportPixelFormat
{
    Yuv420,
    Yuv422,
    Yuv444,
    Yuv420P10,
    Yuv422P10,
    Yuv444P10,
    Yuv420P12,
    Yuv422P12,
    Yuv444P12,
    Gray8,
    Gray10,
    Gray12,
    Gray16,
    Rgb8,
    Rgba8,
    Rgb16,
    Rgba16,
}

/// <summary>How a picture with HDR content keeps it in the saved file.</summary>
public enum PhotoExportHdrEncoding
{
    /// <summary>Standard dynamic range only.</summary>
    None,

    /// <summary>An SDR base image with an ISO 21496-1 gain map recovering the HDR rendition.</summary>
    GainMap,

    /// <summary>Absolute luminance with the SMPTE ST 2084 transfer function on BT.2020 primaries.</summary>
    Pq,
}

/// <summary>How the picture fills a size whose aspect ratio differs from its own.</summary>
public enum PhotoExportResize
{
    Stretch,
    Fit,
    Fill,
}

/// <summary>What the source picture stores, which decides the HDR conversions on offer.</summary>
public enum PhotoExportSourceKind
{
    Sdr,
    GainMap,
    Absolute,
}

/// <summary>The primaries of pictures saved without absolute luminance; PQ pictures always use BT.2020.</summary>
public enum PhotoExportColorSpace
{
    /// <summary>sRGB, or Display P3 when enough of the picture lies outside sRGB.</summary>
    Automatic,
    Srgb,
    DisplayP3,
    Rec2020,
}

/// <summary>How colors outside the saved color space are brought into it.</summary>
public enum PhotoExportGamutMapping
{
    /// <summary>Compresses the source gamut smoothly into the output's, also easing nearby saturated colors.</summary>
    Compress,

    /// <summary>Desaturates only the colors outside, toward their own luminance.</summary>
    PreserveHue,

    /// <summary>Clips each component, which keeps colors inside exact and can shift the hue of those outside.</summary>
    Clip,
}

/// <summary>Which of the source's EXIF and XMP metadata the saved file carries.</summary>
public enum PhotoExportMetadataMode
{
    /// <summary>Everything that still describes the saved picture.</summary>
    All,

    /// <summary>Only what affects how the picture is shown: orientation, color space and resolution.</summary>
    Rendering,
}

internal static class PhotoExportFormats
{
    public static IReadOnlyList<PhotoExportFormat> Builtin { get; } =
        [PhotoExportFormat.Jpeg, PhotoExportFormat.Png, PhotoExportFormat.WebP];

    public static string Extension(PhotoExportFormat format) => format switch
    {
        PhotoExportFormat.Jpeg => ".jpg",
        PhotoExportFormat.Png => ".png",
        PhotoExportFormat.WebP => ".webp",
        PhotoExportFormat.Avif => ".avif",
        PhotoExportFormat.Heic => ".heic",
        _ => ".jxl",
    };

    public static string MimeType(PhotoExportFormat format) => format switch
    {
        PhotoExportFormat.Jpeg => "image/jpeg",
        PhotoExportFormat.Png => "image/png",
        PhotoExportFormat.WebP => "image/webp",
        PhotoExportFormat.Avif => "image/avif",
        PhotoExportFormat.Heic => "image/heic",
        _ => "image/jxl",
    };

    public static string DisplayName(PhotoExportFormat format) => format switch
    {
        PhotoExportFormat.Jpeg => "JPEG",
        PhotoExportFormat.Png => "PNG",
        PhotoExportFormat.WebP => "WebP",
        PhotoExportFormat.Avif => "AVIF",
        PhotoExportFormat.Heic => "HEIC",
        _ => "JPEG XL",
    };

    public static string AppleTypeIdentifier(PhotoExportFormat format) => format switch
    {
        PhotoExportFormat.Jpeg => "public.jpeg",
        PhotoExportFormat.Png => "public.png",
        PhotoExportFormat.WebP => "org.webmproject.webp",
        PhotoExportFormat.Avif => "public.avif",
        PhotoExportFormat.Heic => "public.heic",
        _ => "public.jpeg-xl",
    };

    public static StillImageCodec? Codec(PhotoExportFormat format) => format switch
    {
        PhotoExportFormat.Avif => StillImageCodec.Av1,
        PhotoExportFormat.Heic => StillImageCodec.Hevc,
        PhotoExportFormat.JpegXl => StillImageCodec.JpegXl,
        _ => null,
    };

    public static bool SupportsGainMap(PhotoExportFormat format) =>
        format is PhotoExportFormat.Jpeg or PhotoExportFormat.Avif or PhotoExportFormat.Heic;

    public static bool SupportsPq(PhotoExportFormat format) =>
        format is PhotoExportFormat.Png or PhotoExportFormat.Avif or PhotoExportFormat.Heic or PhotoExportFormat.JpegXl;

    public static bool SupportsAlpha(PhotoExportFormat format, PhotoExportPixelFormat pixelFormat) =>
        format == PhotoExportFormat.WebP || pixelFormat is PhotoExportPixelFormat.Rgba8 or PhotoExportPixelFormat.Rgba16;

    /// <summary>Whether a quality setting trades file size for fidelity.</summary>
    public static bool HasQuality(PhotoExportFormat format, PhotoExportPixelFormat pixelFormat) => format switch
    {
        PhotoExportFormat.Png => false,
        PhotoExportFormat.WebP => !IsLosslessWebP(pixelFormat),
        _ => true,
    };

    /// <summary>Whether encoding speed trades time for file size.</summary>
    public static bool HasSpeed(PhotoExportFormat format) => format is not (PhotoExportFormat.Jpeg or PhotoExportFormat.WebP);

    public static bool IsLosslessWebP(PhotoExportPixelFormat pixelFormat) => pixelFormat is PhotoExportPixelFormat.Rgb8;

    public static int DefaultQuality(PhotoExportFormat format) => format switch
    {
        PhotoExportFormat.Jpeg => 90,
        PhotoExportFormat.JpegXl => 90,
        _ => 85,
    };

    /// <summary>Layouts the format supports for an output, in display order.</summary>
    public static IReadOnlyList<PhotoExportPixelFormat> PixelFormats(PhotoExportFormat format, StillImageEncoderInfo? encoder,
        PhotoExportHdrEncoding hdr)
    {
        IEnumerable<PhotoExportPixelFormat> candidates = format switch
        {
            PhotoExportFormat.Jpeg => [PhotoExportPixelFormat.Yuv420, PhotoExportPixelFormat.Yuv422,
                PhotoExportPixelFormat.Yuv444, PhotoExportPixelFormat.Gray8],
            PhotoExportFormat.Png => hdr == PhotoExportHdrEncoding.Pq
                ? [PhotoExportPixelFormat.Rgb16, PhotoExportPixelFormat.Rgba16]
                : [PhotoExportPixelFormat.Rgb8, PhotoExportPixelFormat.Rgba8, PhotoExportPixelFormat.Rgb16,
                    PhotoExportPixelFormat.Rgba16, PhotoExportPixelFormat.Gray8],
            PhotoExportFormat.WebP => [PhotoExportPixelFormat.Yuv420, PhotoExportPixelFormat.Rgb8],
            PhotoExportFormat.JpegXl => hdr == PhotoExportHdrEncoding.Pq
                ? [PhotoExportPixelFormat.Rgb16, PhotoExportPixelFormat.Rgba16]
                : [PhotoExportPixelFormat.Rgb8, PhotoExportPixelFormat.Rgba8, PhotoExportPixelFormat.Rgb16,
                    PhotoExportPixelFormat.Rgba16, PhotoExportPixelFormat.Gray8, PhotoExportPixelFormat.Gray16],
            _ => Enum.GetValues<PhotoExportPixelFormat>().Where(candidate => IsYuvOrGray(candidate) &&
                (hdr != PhotoExportHdrEncoding.Pq || BitDepth(candidate) >= 10)),
        };
        return encoder is null ? candidates.ToArray()
            : candidates.Where(candidate => EncoderFormat(candidate, encoder) is not null).ToArray();
    }

    /// <summary>The FFmpeg layout an encoder takes for a pixel format, or null when it takes none.</summary>
    public static AVPixelFormat? EncoderFormat(PhotoExportPixelFormat pixelFormat, StillImageEncoderInfo encoder)
    {
        foreach (AVPixelFormat candidate in FfmpegFormats(pixelFormat))
        {
            if (encoder.Supports(candidate))
                return candidate;
        }

        return null;
    }

    public static int BitDepth(PhotoExportPixelFormat pixelFormat) => pixelFormat switch
    {
        PhotoExportPixelFormat.Yuv420P10 or PhotoExportPixelFormat.Yuv422P10 or PhotoExportPixelFormat.Yuv444P10 or
            PhotoExportPixelFormat.Gray10 => 10,
        PhotoExportPixelFormat.Yuv420P12 or PhotoExportPixelFormat.Yuv422P12 or PhotoExportPixelFormat.Yuv444P12 or
            PhotoExportPixelFormat.Gray12 => 12,
        PhotoExportPixelFormat.Gray16 or PhotoExportPixelFormat.Rgb16 or PhotoExportPixelFormat.Rgba16 => 16,
        _ => 8,
    };

    public static bool IsGray(PhotoExportPixelFormat pixelFormat) => pixelFormat is PhotoExportPixelFormat.Gray8 or
        PhotoExportPixelFormat.Gray10 or PhotoExportPixelFormat.Gray12 or PhotoExportPixelFormat.Gray16;

    private static bool IsYuvOrGray(PhotoExportPixelFormat pixelFormat) =>
        pixelFormat is not (PhotoExportPixelFormat.Rgb8 or PhotoExportPixelFormat.Rgba8 or PhotoExportPixelFormat.Rgb16 or
            PhotoExportPixelFormat.Rgba16 or PhotoExportPixelFormat.Gray16);

    private static AVPixelFormat[] FfmpegFormats(PhotoExportPixelFormat pixelFormat) => pixelFormat switch
    {
        PhotoExportPixelFormat.Yuv420 => [AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_NV12],
        PhotoExportPixelFormat.Yuv422 => [AVPixelFormat.AV_PIX_FMT_YUV422P],
        PhotoExportPixelFormat.Yuv444 => [AVPixelFormat.AV_PIX_FMT_YUV444P],
        PhotoExportPixelFormat.Yuv420P10 => [AVPixelFormat.AV_PIX_FMT_YUV420P10LE, AVPixelFormat.AV_PIX_FMT_P010LE],
        PhotoExportPixelFormat.Yuv422P10 => [AVPixelFormat.AV_PIX_FMT_YUV422P10LE],
        PhotoExportPixelFormat.Yuv444P10 => [AVPixelFormat.AV_PIX_FMT_YUV444P10LE],
        PhotoExportPixelFormat.Yuv420P12 => [AVPixelFormat.AV_PIX_FMT_YUV420P12LE],
        PhotoExportPixelFormat.Yuv422P12 => [AVPixelFormat.AV_PIX_FMT_YUV422P12LE],
        PhotoExportPixelFormat.Yuv444P12 => [AVPixelFormat.AV_PIX_FMT_YUV444P12LE],
        PhotoExportPixelFormat.Gray8 => [AVPixelFormat.AV_PIX_FMT_GRAY8],
        PhotoExportPixelFormat.Gray10 => [AVPixelFormat.AV_PIX_FMT_GRAY10LE],
        PhotoExportPixelFormat.Gray12 => [AVPixelFormat.AV_PIX_FMT_GRAY12LE],
        PhotoExportPixelFormat.Gray16 => [AVPixelFormat.AV_PIX_FMT_GRAY16LE],
        PhotoExportPixelFormat.Rgb8 => [AVPixelFormat.AV_PIX_FMT_RGB24],
        PhotoExportPixelFormat.Rgba8 => [AVPixelFormat.AV_PIX_FMT_RGBA],
        PhotoExportPixelFormat.Rgb16 => [AVPixelFormat.AV_PIX_FMT_RGB48LE],
        _ => [AVPixelFormat.AV_PIX_FMT_RGBA64LE],
    };
}
