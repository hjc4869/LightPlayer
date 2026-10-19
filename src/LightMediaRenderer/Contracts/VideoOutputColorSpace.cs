using System;
using System.Threading;

namespace LightMediaRenderer.Contracts;

/// <summary>
/// What the surface being drawn into expects a color to mean.
/// </summary>
/// <remarks>
/// The caller supplies the actual output surface's color space and luminance metadata.
/// </remarks>
public enum VideoOutputColorSpace
{
    /// <summary>
    /// Non-linear sRGB confined to <c>[0, 1]</c>: the display shows nothing brighter than white or
    /// outside the sRGB gamut, so highlights have to be tone mapped, wide primaries brought in, and
    /// the result dithered on its way to 8 bits.
    /// </summary>
    Srgb,

    /// <summary>
    /// Linear light on sRGB primaries. Values below 0 and above 1 are meaningful;
    /// the negotiated volume supplies the white scale and highlight-mapping policy.
    /// </summary>
    ScRgbLinear,
}

public readonly record struct VideoHdrOutputMapping(
    double ToneMapPeakNits,
    double LinearNitsPerUnit,
    double? PhysicalPeakNits,
    bool AdaptsToDisplay,
    bool RelativeLuminance = false,
    bool PlatformToneMapping = false,
    double ReferenceWhiteScale = 1);

/// <summary>The shape of the curve a bounded surface's signal is shown through.</summary>
public enum VideoSurfaceTransferKind
{
    /// <summary>The sRGB curve: a linear toe below 0.04045, then a 2.4 power.</summary>
    Srgb,

    /// <summary>A pure power law, with no toe.</summary>
    Power,
}

/// <summary>The curve a bounded surface's signal is shown through.</summary>
public readonly record struct VideoSurfaceTransfer(VideoSurfaceTransferKind Kind, float Exponent)
{
    public static VideoSurfaceTransfer Srgb { get; } = new(VideoSurfaceTransferKind.Srgb, 0);

    /// <summary>What a display's own signal is encoded with, absent anything more specific.</summary>
    public static VideoSurfaceTransfer Gamma22 { get; } = Power(2.2f);

    /// <summary>The 2.4 exponent used by BT.1886; its display-black offset is applied separately.</summary>
    public static VideoSurfaceTransfer Bt1886 { get; } = Power(2.4f);

    public static VideoSurfaceTransfer Power(float exponent) =>
        new(VideoSurfaceTransferKind.Power, Math.Clamp(exponent, 1.0f, 10.0f));
}

public enum Bt709Interpretation
{
    PlatformPreferred,
    Gamma1961,
    Gamma22,
    Gamma24,
    Srgb,
}

/// <summary>
/// What the platform shows an ordinary, bounded surface through.
/// </summary>
/// <remarks>
/// <para>
/// An extended-range surface takes light rather than a signal, so the renderer has to put the
/// signal into light itself, and the picture only lands where the same signal handed over untouched
/// would have if it undoes exactly the curve the platform would otherwise have applied.
/// </para>
/// <para>
/// Where the platform reports the encoding it prefers for the surface, that is the answer. Where it
/// does not, the head's own is used, and the platforms disagree: DXGI defines its standard swap
/// chain color space, <c>RGB_FULL_G22_NONE_P709</c>, as the sRGB curve -- "linear segment + 2.4
/// power" -- while wp_color_manager_v1 leaves an untagged surface compositor implementation defined,
/// and both compositors that implement it settled on a pure 2.2 power law.
/// </para>
/// <para>
/// Undoing the wrong one costs the shadows: the sRGB toe arrives on a power-law compositor as
/// lifted blacks, and the other way round as crushed ones.
/// </para>
/// </remarks>
public static class VideoSurfaceTransfers
{
    private static int bt709Preference;

    public static event Action? Bt709PreferenceChanged;

    public static Bt709Interpretation Bt709Preference
    {
        get => (Bt709Interpretation)Volatile.Read(ref bt709Preference);
        set
        {
            if (Interlocked.Exchange(ref bt709Preference, (int)value) != (int)value)
                Bt709PreferenceChanged?.Invoke();
        }
    }

    public static VideoSurfaceTransfer? ForBt709() => Bt709Preference switch
    {
        Bt709Interpretation.Gamma1961 => VideoSurfaceTransfer.Power(1.9609375f),
        Bt709Interpretation.Gamma22 => VideoSurfaceTransfer.Gamma22,
        Bt709Interpretation.Gamma24 => VideoSurfaceTransfer.Bt1886,
        Bt709Interpretation.Srgb => VideoSurfaceTransfer.Srgb,
        _ => Bt709Video,
    };

    /// <summary>
    /// What to assume where the platform reports nothing. Set by the application head before any
    /// window exists, and not written again.
    /// </summary>
    public static VideoSurfaceTransfer StandardRange { get; set; } = VideoSurfaceTransfer.Srgb;

    /// <summary>Optional platform interpretation of BT.709 video, independent of surface encoding.</summary>
    public static VideoSurfaceTransfer? Bt709Video { get; set; }

    /// <summary>What the platform said about the surface, or null where it said nothing usable.</summary>
    public static VideoSurfaceTransfer? Reported(VideoOutputColorVolume? colorVolume) => colorVolume switch
    {
        { Transfer: VideoOutputTransferFunction.Srgb } => VideoSurfaceTransfer.Srgb,
        { Transfer: VideoOutputTransferFunction.Power } volume =>
            VideoSurfaceTransfer.Power((float)volume.TransferExponent),

        // BT.1886 is a 2.4 power law once its black level is folded in, and that offset is below
        // what the surface quantizes to anyway.
        { Transfer: VideoOutputTransferFunction.Bt1886 } => VideoSurfaceTransfer.Power(2.4f),

        // An HDR or linear encoding says nothing about what a bounded signal on the same display
        // would have meant, which is the only thing being asked here.
        _ => null,
    };

    /// <summary>What to undo, taking the platform's answer where there is one.</summary>
    public static VideoSurfaceTransfer For(VideoOutputColorVolume? colorVolume) =>
        Reported(colorVolume) ?? StandardRange;
}

public static class VideoOutputColorSpaceExtensions
{
    public const double HdrReferenceWhiteNits = 203;
    private const double PqMaximumNits = 10000;
    private const double MaximumBt2020ToBt709ComponentGain = 1.660491;

    /// <summary>Whether the target can carry values outside <c>[0, 1]</c>.</summary>
    public static bool IsExtendedRange(this VideoOutputColorSpace target) =>
        target == VideoOutputColorSpace.ScRgbLinear;

    /// <summary>
    /// Whether the reported rendering target permits highlights above diffuse white.
    /// </summary>
    public static bool HasHdrHeadroom(VideoOutputColorVolume? colorVolume) =>
        colorVolume?.HeadroomRatio is > 1 and var headroom && double.IsFinite(headroom);

    public static bool UsesAbsoluteLuminance(this VideoOutputColorSpace target,
        bool hdrPassthrough, VideoOutputColorVolume? colorVolume) =>
        target.IsExtendedRange() && hdrPassthrough && colorVolume is
        {
            ToneMapping: VideoToneMappingMode.Client,
            LuminanceBasis: VideoLuminanceBasis.DisplayReported or VideoLuminanceBasis.Nominal,
            ReferenceWhiteScale: > 0,
            ReferenceWhiteNits: > 0,
            HeadroomRatio: >= 1,
            TargetLuminance.MaximumNits: > 0,
        } volume &&
        double.IsFinite(volume.ReferenceWhiteScale.Value) &&
        double.IsFinite(volume.ReferenceWhiteNits.Value) &&
        double.IsFinite(volume.HeadroomRatio.Value) &&
        double.IsFinite(volume.TargetLuminance.Value.MaximumNits!.Value);

    /// <summary>
    /// Resolves source-light normalization and mapping ownership from the negotiated contract.
    /// Client targets with reported or nominal white and peak use absolute tone mapping;
    /// incomplete luminance uses relative headroom, with SDR when current headroom is unknown.
    /// </summary>
    public static VideoHdrOutputMapping ResolveHdrOutput(
        this VideoOutputColorSpace target,
        double sourcePeakLuminance,
        bool hdrPassthrough,
        double componentGain = MaximumBt2020ToBt709ComponentGain,
        VideoOutputColorVolume? colorVolume = null) =>
        target.ResolveHdrOutput(sourcePeakLuminance, hdrPassthrough, null, componentGain, colorVolume);

    public static VideoHdrOutputMapping ResolveHdrOutput(
        this VideoOutputColorSpace target,
        double sourcePeakLuminance,
        bool hdrPassthrough,
        VideoSdrToneMappingOptions? sdrToneMapping,
        double componentGain = MaximumBt2020ToBt709ComponentGain,
        VideoOutputColorVolume? colorVolume = null)
    {
        bool knownWhite = target.IsExtendedRange() &&
            colorVolume?.ReferenceWhiteScale is > 0 and var scale && double.IsFinite(scale);
        double whiteScale = knownWhite ? colorVolume!.Value.ReferenceWhiteScale!.Value : 1;
        bool platform = knownWhite && hdrPassthrough &&
            colorVolume?.ToneMapping == VideoToneMappingMode.Platform;
        bool absolute = target.UsesAbsoluteLuminance(hdrPassthrough, colorVolume);
        double headroom = knownWhite && hdrPassthrough &&
            colorVolume?.ToneMapping == VideoToneMappingMode.Client &&
            colorVolume?.HeadroomRatio is >= 1 and var ratio && double.IsFinite(ratio)
                ? ratio
                : 1;
        double linearNitsPerUnit = absolute
            ? colorVolume!.Value.ReferenceWhiteNits!.Value
            : HdrReferenceWhiteNits;
        double targetPeak = absolute
            ? Math.Min(colorVolume!.Value.TargetLuminance!.Value.MaximumNits!.Value,
                linearNitsPerUnit * headroom)
            : HdrReferenceWhiteNits * headroom;
        double physicalNitsScale = 1;
        bool customTarget = sdrToneMapping is { TargetHeadroom: not null } or { TargetMaximumNits: not null };
        if (customTarget)
        {
            VideoSdrToneMappingOptions options = sdrToneMapping!;
            options.Validate();
            if (platform)
            {
                double available = colorVolume?.HeadroomRatio ?? 1;
                headroom = double.IsFinite(available) ? Math.Max(available, 1) : 1;
            }

            platform = false;
            if (headroom <= 1)
                absolute = false;
            double? targetMaximum = options.Algorithm == VideoSdrToneMappingAlgorithm.Bt2446A
                ? options.TargetMaximumNits : null;
            double reportedWhite = colorVolume?.ReferenceWhiteNits ?? HdrReferenceWhiteNits;
            linearNitsPerUnit = options.Algorithm != VideoSdrToneMappingAlgorithm.Bt2446A
                ? options.ReferenceWhiteNits
                : headroom > 1 && double.IsFinite(reportedWhite) && reportedWhite > 0
                    ? reportedWhite
                    : targetMaximum ?? options.ReferenceWhiteNits;
            targetPeak = headroom > 1
                ? Math.Min(targetMaximum ?? linearNitsPerUnit * (options.TargetHeadroom ?? headroom),
                    linearNitsPerUnit * headroom)
                : linearNitsPerUnit;
            if (absolute)
            {
                physicalNitsScale = reportedWhite / linearNitsPerUnit;
                if (colorVolume?.TargetLuminance?.MaximumNits is > 0 and var maximum)
                    targetPeak = Math.Min(targetPeak, maximum / physicalNitsScale);
            }
        }
        else if (!platform && targetPeak <= linearNitsPerUnit)
        {
            VideoSdrToneMappingOptions options = sdrToneMapping ?? VideoSdrToneMappingOptions.Default;
            options.Validate();
            linearNitsPerUnit = options.ReferenceWhiteNits;
            targetPeak = options.ReferenceWhiteNits;
            absolute = false;
        }

        double componentPeak = sourcePeakLuminance *
            (double.IsFinite(componentGain) ? Math.Max(componentGain, 1) : 1);
        bool adapt = !platform && (headroom <= 1 || componentPeak > targetPeak);
        return new VideoHdrOutputMapping(
            adapt ? targetPeak : sourcePeakLuminance,
            linearNitsPerUnit,
            absolute && colorVolume?.LuminanceBasis == VideoLuminanceBasis.DisplayReported
                ? (adapt ? targetPeak : sourcePeakLuminance) * physicalNitsScale
                : null,
            AdaptsToDisplay: adapt,
            RelativeLuminance: !absolute,
            PlatformToneMapping: platform,
            ReferenceWhiteScale: whiteScale);
    }

    public static double EffectiveHdrSourcePeak(double declaredPeakLuminance) =>
        Math.Clamp(double.IsFinite(declaredPeakLuminance) && declaredPeakLuminance > 0
            ? declaredPeakLuminance : 1000.0, 100.0, PqMaximumNits);
}
