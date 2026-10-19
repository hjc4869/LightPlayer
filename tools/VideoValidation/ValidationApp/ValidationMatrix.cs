using System;
using System.Collections.Generic;
using System.Linq;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.VideoValidation;

public enum ValidationDecodeMode
{
    Software,
    HardwareAuto,
}

public enum ValidationPattern
{
    /// <summary>Chosen from the transfer function: SMPTE bars for SDR, the ladder for HDR.</summary>
    Auto,

    SmpteBars,

    /// <summary>
    /// SMPTE bars tagged with the asset's primaries rather than converted into them, so the
    /// saturated bars really are that gamut's own and leave BT.709 on the way out.
    /// </summary>
    NativeGamutBars,

    HdrLadder,

    /// <summary>Flat patches of known luminance and saturation. See <see cref="HdrGamutProbe"/>.</summary>
    HdrGamutProbe,
}

public sealed record ValidationAsset(
    string Id,
    string Codec,
    string PixelFormat,
    VideoPixelFormat ExpectedFormat,
    int Width,
    int Height,
    VideoColorSpace ColorSpace,
    VideoColorPrimaries ColorPrimaries,
    VideoColorTransfer ColorTransfer,
    VideoColorRange ColorRange,
    VideoChromaLocation ChromaLocation,
    string Purpose,
    ValidationPattern Pattern = ValidationPattern.Auto)
{
    public bool IsHdr => ColorTransfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg;

    /// <summary>Whether the picture has to cross into the display's primaries to be shown.</summary>
    public bool IsWideGamut => ColorPrimaries != VideoColorPrimaries.Bt709;

    public ValidationPattern ResolvedPattern => Pattern switch
    {
        ValidationPattern.Auto when IsHdr => ValidationPattern.HdrLadder,
        ValidationPattern.Auto => ValidationPattern.SmpteBars,
        _ => Pattern,
    };
}

public sealed record ValidationCase(
    string Id,
    string AssetId,
    ValidationDecodeMode DecodeMode,
    VideoScalingQuality Scaling,
    int ViewportWidth,
    int ViewportHeight,
    bool HdrPassthrough,
    IReadOnlyList<VideoFrameDeliveryPath> AllowedDeliveryPaths,
    string Purpose);

public static class ValidationMatrix
{
    public const int SourceWidth = 320;
    public const int SourceHeight = 180;

    private static readonly VideoFrameDeliveryPath[] SoftwarePaths =
    [
        VideoFrameDeliveryPath.SoftwarePlanar,
        VideoFrameDeliveryPath.SoftwareConverted,
    ];

    private static readonly VideoFrameDeliveryPath[] HardwarePaths =
    [
        VideoFrameDeliveryPath.HardwareSurface,
        VideoFrameDeliveryPath.HardwareReadback,
        VideoFrameDeliveryPath.HardwareConverted,
    ];

    private static readonly VideoScalingQuality[] WideKernelScalers =
    [
        VideoScalingQuality.Lanczos3,
        VideoScalingQuality.Jinc,
    ];

    public static IReadOnlyList<ValidationAsset> Assets { get; } = CreateAssets();

    public static IReadOnlyList<ValidationCase> Cases { get; } = CreateCases();

    private static IReadOnlyList<ValidationAsset> CreateAssets()
    {
        var assets = new List<ValidationAsset>();
        foreach ((VideoPixelFormat format, string ffmpegName) in SoftwarePixelFormats())
        {
            var color = ColorProfile(format);
            assets.Add(new ValidationAsset(
                $"raw-{ffmpegName}",
                "rawvideo",
                ffmpegName,
                format,
                SourceWidth,
                SourceHeight,
                color.Space,
                color.Primaries,
                color.Transfer,
                color.Range,
                color.Chroma,
                $"Renderer input coverage for {format}."));
        }

        assets.AddRange(
        [
            CodecAsset("h264-sdr-420p", "h264", "yuv420p", VideoPixelFormat.Yuv420p, VideoColorTransfer.Bt709, "Common H.264 hardware and software decode with 2x upscale."),
            CodecAsset("h264-srgb-420p", "h264", "yuv420p", VideoPixelFormat.Yuv420p, VideoColorTransfer.Srgb, "Explicit sRGB transfer metadata through real codec decode."),
            CodecAsset("hevc-sdr-420p", "hevc", "yuv420p", VideoPixelFormat.Yuv420p, VideoColorTransfer.Bt709, "HEVC Main with non-mod16 coded-surface padding.", 638, 358),
            CodecAsset("hevc-hdr10-420p10", "hevc", "yuv420p10le", VideoPixelFormat.Yuv420p10, VideoColorTransfer.Pq, "HDR10 Main10 decode at a 1:1 viewport.", 640, 360),
            CodecAsset("hevc-hlg-420p10", "hevc", "yuv420p10le", VideoPixelFormat.Yuv420p10, VideoColorTransfer.Hlg, "HLG Main10 decode at a 1:1 viewport.", 640, 360),
            CodecAsset("av1-sdr-420p", "av1", "yuv420p", VideoPixelFormat.Yuv420p, VideoColorTransfer.Bt709, "AV1 Main 8-bit 720p downscale.", 1280, 720),
            CodecAsset("av1-hdr10-420p10", "av1", "yuv420p10le", VideoPixelFormat.Yuv420p10, VideoColorTransfer.Pq, "AV1 Main 10-bit HDR decode.", 640, 360),
            SdrGamutAsset(
                "sdr-displayp3-bars",
                VideoColorPrimaries.DisplayP3,
                VideoColorSpace.Bt709,
                "SDR Display P3, the gamut a phone or a modern camera writes."),
            SdrGamutAsset(
                "sdr-displayp3-primaries",
                VideoColorPrimaries.DisplayP3,
                VideoColorSpace.Bt709,
                "SDR Display P3 bars on P3's own primaries, which leave the BT.709 gamut.",
                ValidationPattern.NativeGamutBars),
            SdrGamutAsset(
                "sdr-bt601-625-bars",
                VideoColorPrimaries.Bt470Bg,
                VideoColorSpace.Bt601,
                "SDR BT.601 625-line, the primaries and matrix PAL material carries."),
            SdrGamutAsset(
                "sdr-dcip3-bars",
                VideoColorPrimaries.DciP3,
                VideoColorSpace.Bt709,
                "SDR DCI-P3, whose theatrical white point has to be adapted to the display's.",
                ValidationPattern.NativeGamutBars),
            new ValidationAsset(
                "h264-displayp3-420p",
                "h264",
                "yuv420p",
                VideoPixelFormat.Yuv420p,
                SourceWidth,
                SourceHeight,
                VideoColorSpace.Bt709,
                VideoColorPrimaries.DisplayP3,
                VideoColorTransfer.Bt709,
                VideoColorRange.Limited,
                VideoChromaLocation.Left,
                "Display P3 carried through a real codec, where the tag has to survive the bitstream."),
        ]);

        assets.Add(new ValidationAsset(
            "hdr-gamut-probe",
            "rawvideo",
            "p010le",
            VideoPixelFormat.P010,
            SourceWidth,
            SourceHeight,
            VideoColorSpace.Bt2020,
            VideoColorPrimaries.Bt2020,
            VideoColorTransfer.Pq,
            VideoColorRange.Limited,
            VideoChromaLocation.Left,
            "Out-of-gamut and peak-luminance patches for the extended-range output path.",
            ValidationPattern.HdrGamutProbe));

        return assets;
    }

    private static IReadOnlyList<ValidationCase> CreateCases()
    {
        var cases = new List<ValidationCase>();
        foreach (ValidationAsset asset in Assets.Where(asset =>
            asset.Id.StartsWith("raw-", StringComparison.Ordinal)))
        {
            cases.Add(new ValidationCase(
                $"format-{asset.PixelFormat}",
                asset.Id,
                ValidationDecodeMode.Software,
                VideoScalingQuality.Bilinear,
                640,
                360,
                HdrPassthrough: false,
                SoftwarePaths,
                asset.Purpose));
        }

        foreach (ValidationAsset asset in Assets.Where(asset => asset.Codec != "rawvideo"))
        {
            cases.Add(CodecCase(asset, ValidationDecodeMode.Software, SoftwarePaths));
            cases.Add(CodecCase(asset, ValidationDecodeMode.HardwareAuto, HardwarePaths));
        }

        foreach (VideoScalingQuality scaling in Enum.GetValues<VideoScalingQuality>())
        {
            cases.Add(new ValidationCase(
                $"scaling-{scaling.ToString().ToLowerInvariant()}",
                "raw-yuv420p",
                ValidationDecodeMode.Software,
                scaling,
                517,
                293,
                HdrPassthrough: false,
                SoftwarePaths,
                "Non-integral upscale through the selected production resampler."));
        }

        // A kernel three lobes wide only shows what it is at a downscale, where the taps outside
        // the destination pixel are the ones deciding the answer.
        foreach (VideoScalingQuality scaling in WideKernelScalers)
        {
            cases.Add(new ValidationCase(
                $"scaling-downscale-{scaling.ToString().ToLowerInvariant()}",
                "raw-yuv420p",
                ValidationDecodeMode.Software,
                scaling,
                213,
                120,
                HdrPassthrough: false,
                SoftwarePaths,
                "Non-integral downscale through a wide-kernel resampler."));
        }

        // An interleaved picture says nothing about its own light, so nothing in the color chain
        // needs it: only the filter does, and a filter carrying a kernel exists in the pipeline.
        foreach (VideoScalingQuality scaling in WideKernelScalers)
        {
            cases.Add(new ValidationCase(
                $"scaling-interleaved-{scaling.ToString().ToLowerInvariant()}",
                "raw-bgra",
                ValidationDecodeMode.Software,
                scaling,
                517,
                293,
                HdrPassthrough: false,
                SoftwarePaths,
                "Upscale of an already interleaved picture, which reaches a kernel only in the pipeline."));
        }

        // The color stage is the same for every filter, so a patch that survives one filter and
        // not another is the renderer's own doing rather than a property of the resampling.
        foreach (VideoScalingQuality scaling in Enum.GetValues<VideoScalingQuality>())
        {
            string slug = scaling.ToString().ToLowerInvariant();
            cases.Add(new ValidationCase(
                $"gamut-native-{slug}",
                "hdr-gamut-probe",
                ValidationDecodeMode.Software,
                scaling,
                SourceWidth,
                SourceHeight,
                HdrPassthrough: true,
                SoftwarePaths,
                "Out-of-gamut and peak HDR patches at native size, where no filter may change a pixel."));
            cases.Add(new ValidationCase(
                $"gamut-scaled-{slug}",
                "hdr-gamut-probe",
                ValidationDecodeMode.Software,
                scaling,
                517,
                293,
                HdrPassthrough: true,
                SoftwarePaths,
                "Out-of-gamut and peak HDR patches through a non-integral resize."));
        }

        cases.Add(new ValidationCase(
            "gamut-adapted-catmullrom",
            "hdr-gamut-probe",
            ValidationDecodeMode.Software,
            VideoScalingQuality.CatmullRom,
            517,
            293,
            HdrPassthrough: false,
            SoftwarePaths,
            "Display-adapted HDR through the cubic path, where the gamut fit still emits negatives."));

        // At native size nothing resamples, so a difference from the reference is the color chain
        // and nothing else; the resize afterwards proves the matrix is applied after the filter.
        foreach (ValidationAsset asset in Assets.Where(asset =>
            asset.Codec == "rawvideo" && asset.IsWideGamut && !asset.IsHdr))
        {
            cases.Add(new ValidationCase(
                $"gamut-sdr-{asset.Id}-native",
                asset.Id,
                ValidationDecodeMode.Software,
                VideoScalingQuality.Bilinear,
                SourceWidth,
                SourceHeight,
                HdrPassthrough: false,
                SoftwarePaths,
                asset.Purpose));
            cases.Add(new ValidationCase(
                $"gamut-sdr-{asset.Id}-scaled",
                asset.Id,
                ValidationDecodeMode.Software,
                VideoScalingQuality.Lanczos3,
                517,
                293,
                HdrPassthrough: false,
                SoftwarePaths,
                $"{asset.Purpose} Resized through a wide kernel, which must not change the colors."));
        }

        return cases;
    }

    private static ValidationAsset CodecAsset(
        string id,
        string codec,
        string pixelFormat,
        VideoPixelFormat expectedFormat,
        VideoColorTransfer transfer,
        string purpose,
        int width = SourceWidth,
        int height = SourceHeight) =>
        new(
            id,
            codec,
            pixelFormat,
            expectedFormat,
            width,
            height,
            transfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg ? VideoColorSpace.Bt2020 : VideoColorSpace.Bt709,
            transfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg ? VideoColorPrimaries.Bt2020 : VideoColorPrimaries.Bt709,
            transfer,
            VideoColorRange.Limited,
            VideoChromaLocation.Left,
            purpose);

    private static ValidationAsset SdrGamutAsset(
        string id,
        VideoColorPrimaries primaries,
        VideoColorSpace colorSpace,
        string purpose,
        ValidationPattern pattern = ValidationPattern.SmpteBars) =>
        new(
            id,
            "rawvideo",
            "yuv420p",
            VideoPixelFormat.Yuv420p,
            SourceWidth,
            SourceHeight,
            colorSpace,
            primaries,
            VideoColorTransfer.Bt709,
            VideoColorRange.Limited,
            VideoChromaLocation.Left,
            purpose,
            pattern);

    private static (
        VideoColorSpace Space,
        VideoColorPrimaries Primaries,
        VideoColorTransfer Transfer,
        VideoColorRange Range,
        VideoChromaLocation Chroma) ColorProfile(VideoPixelFormat format) => format switch
    {
        VideoPixelFormat.Nv12 =>
            (VideoColorSpace.Bt601, VideoColorPrimaries.Bt709, VideoColorTransfer.Bt709, VideoColorRange.Limited, VideoChromaLocation.Left),
        VideoPixelFormat.Nv21 =>
            (VideoColorSpace.Bt709, VideoColorPrimaries.Bt709, VideoColorTransfer.Bt709, VideoColorRange.Full, VideoChromaLocation.Center),
        VideoPixelFormat.P010 =>
            (VideoColorSpace.Bt2020, VideoColorPrimaries.Bt2020, VideoColorTransfer.Pq, VideoColorRange.Limited, VideoChromaLocation.Left),
        VideoPixelFormat.P012 =>
            (VideoColorSpace.Bt2020, VideoColorPrimaries.Bt2020, VideoColorTransfer.Hlg, VideoColorRange.Limited, VideoChromaLocation.TopLeft),
        VideoPixelFormat.P016 =>
            (VideoColorSpace.Bt709, VideoColorPrimaries.Bt709, VideoColorTransfer.Bt709, VideoColorRange.Full, VideoChromaLocation.Bottom),
        VideoPixelFormat.Yuv422p =>
            (VideoColorSpace.Bt601, VideoColorPrimaries.Bt709, VideoColorTransfer.Bt709, VideoColorRange.Limited, VideoChromaLocation.Left),
        VideoPixelFormat.Yuv444p =>
            (VideoColorSpace.Bt709, VideoColorPrimaries.Bt709, VideoColorTransfer.Bt709, VideoColorRange.Full, VideoChromaLocation.Center),
        VideoPixelFormat.Yuv420p10 =>
            (VideoColorSpace.Bt709, VideoColorPrimaries.Bt709, VideoColorTransfer.Bt709, VideoColorRange.Limited, VideoChromaLocation.Top),
        VideoPixelFormat.Yuv420p12 =>
            (VideoColorSpace.Bt709, VideoColorPrimaries.Bt709, VideoColorTransfer.Bt709, VideoColorRange.Limited, VideoChromaLocation.BottomLeft),
        VideoPixelFormat.Yuv420p16 =>
            (VideoColorSpace.Bt709, VideoColorPrimaries.Bt709, VideoColorTransfer.Bt709, VideoColorRange.Limited, VideoChromaLocation.Bottom),
        VideoPixelFormat.Bgra32 =>
            (VideoColorSpace.Bt709, VideoColorPrimaries.Bt709, VideoColorTransfer.Bt709, VideoColorRange.Full, VideoChromaLocation.Left),
        _ =>
            (VideoColorSpace.Bt709, VideoColorPrimaries.Bt709, VideoColorTransfer.Bt709, VideoColorRange.Limited, VideoChromaLocation.Left),
    };

    private static ValidationCase CodecCase(
        ValidationAsset asset,
        ValidationDecodeMode mode,
        IReadOnlyList<VideoFrameDeliveryPath> paths) =>
        new(
            $"codec-{asset.Id}-{mode.ToString().ToLowerInvariant()}",
            asset.Id,
            mode,
            VideoScalingQuality.Bilinear,
            640,
            360,
            HdrPassthrough: asset.ColorTransfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg,
            paths,
            asset.Purpose);

    private static IEnumerable<(VideoPixelFormat Format, string FfmpegName)> SoftwarePixelFormats()
    {
        yield return (VideoPixelFormat.Bgra32, "bgra");
        yield return (VideoPixelFormat.Nv12, "nv12");
        yield return (VideoPixelFormat.Nv21, "nv21");
        yield return (VideoPixelFormat.P010, "p010le");
        yield return (VideoPixelFormat.P012, "p012le");
        yield return (VideoPixelFormat.P016, "p016le");
        yield return (VideoPixelFormat.Yuv420p, "yuv420p");
        yield return (VideoPixelFormat.Yuv422p, "yuv422p");
        yield return (VideoPixelFormat.Yuv444p, "yuv444p");
        yield return (VideoPixelFormat.Yuv420p10, "yuv420p10le");
        yield return (VideoPixelFormat.Yuv422p10, "yuv422p10le");
        yield return (VideoPixelFormat.Yuv444p10, "yuv444p10le");
        yield return (VideoPixelFormat.Yuv420p12, "yuv420p12le");
        yield return (VideoPixelFormat.Yuv422p12, "yuv422p12le");
        yield return (VideoPixelFormat.Yuv444p12, "yuv444p12le");
        yield return (VideoPixelFormat.Yuv420p16, "yuv420p16le");
        yield return (VideoPixelFormat.Yuv422p16, "yuv422p16le");
        yield return (VideoPixelFormat.Yuv444p16, "yuv444p16le");
    }
}