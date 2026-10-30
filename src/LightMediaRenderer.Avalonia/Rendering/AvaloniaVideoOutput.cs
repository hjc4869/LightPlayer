using Avalonia.Platform;
using Avalonia.Skia;
using LightMediaRenderer.Contracts;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Rendering;

internal static class AvaloniaVideoOutput
{
    public static VideoOutputColorSpace GetOutputColorSpace(this ISkiaSharpApiLease lease) =>
#if LMR_AVALONIA_FORK
        lease.ColorFormat.ColorSpace switch
        {
            PlatformColorSpace.ScRgbLinear => VideoOutputColorSpace.ScRgbLinear,
            PlatformColorSpace.DisplayP3 => VideoOutputColorSpace.DisplayP3,
            PlatformColorSpace.Rec2020 => VideoOutputColorSpace.Bt2020,
            _ => VideoOutputColorSpace.Srgb,
        };
#else
        VideoOutputColorSpace.Srgb;
#endif

    public static SKColorSpace? GetOutputSkColorSpace(this ISkiaSharpApiLease lease) =>
#if LMR_AVALONIA_FORK
        lease.SkColorSpace;
#else
        Srgb;

    private static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();
#endif

    public static string GetSurfaceEncoding(this ISkiaSharpApiLease lease) =>
#if LMR_AVALONIA_FORK
        lease.ColorFormat.Encoding.ToString();
#else
        "8-bit";
#endif

    public static string GetSurfaceColorSpaceName(this ISkiaSharpApiLease lease) =>
#if LMR_AVALONIA_FORK
        lease.ColorFormat.ColorSpace.ToString();
#else
        "Srgb";
#endif

    public static VideoOutputColorVolume? GetOutputColorVolume(this ISkiaSharpApiLease lease) =>
#if LMR_AVALONIA_FORK
        ToRendererColorVolume(lease.PreferredColorVolume);

    internal static VideoOutputColorVolume? ToRendererColorVolume(PlatformSurfaceColorVolume? volume) =>
        volume is { } value
            ? new VideoOutputColorVolume(
                value.Transfer switch
                {
                    PlatformTransferFunction.Srgb => VideoOutputTransferFunction.Srgb,
                    PlatformTransferFunction.Power => VideoOutputTransferFunction.Power,
                    PlatformTransferFunction.Bt1886 => VideoOutputTransferFunction.Bt1886,
                    _ => VideoOutputTransferFunction.Unknown,
                },
                value.TransferExponent,
                value.ReferenceWhiteNits,
                value.SurfaceNitsPerUnit,
                value.TargetLuminance is { } luminance
                    ? new VideoLuminance(luminance.MinimumNits, luminance.MaximumNits)
                    : null)
            {
                ReferenceWhiteScale = value.ReferenceWhiteScale,
                HeadroomRatio = value.HeadroomRatio,
                MaximumHeadroomRatio = value.MaximumHeadroomRatio,
                ToneMapping = value.ToneMapping switch
                {
                    PlatformToneMappingMode.Client => VideoToneMappingMode.Client,
                    PlatformToneMappingMode.Platform => VideoToneMappingMode.Platform,
                    _ => VideoToneMappingMode.Unknown,
                },
                LuminanceBasis = value.LuminanceBasis switch
                {
                    PlatformLuminanceBasis.DisplayReported => VideoLuminanceBasis.DisplayReported,
                    PlatformLuminanceBasis.Nominal => VideoLuminanceBasis.Nominal,
                    _ => VideoLuminanceBasis.Unknown,
                },
            }
            : null;
        #else
            null;
        #endif
}