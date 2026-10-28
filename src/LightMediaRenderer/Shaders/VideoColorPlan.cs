using System;
using System.Collections.Generic;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Rendering;
using LightMediaRenderer.Software;
using SkiaSharp;

namespace LightMediaRenderer.Shaders;

internal enum VideoTransferStage
{
    /// <summary>The signal already carries the surface's encoding and is passed through.</summary>
    Direct,
    Pq,
    Hlg,

    /// <summary>The sRGB curve, toe and all.</summary>
    Srgb,

    /// <summary>BT.1886, including display-black compensation.</summary>
    Bt1886,

    /// <summary>The power law a signal is shown through when nothing says otherwise.</summary>
    Power,

    /// <summary>Already linear light; only the primaries and the range still need work.</summary>
    Linear,
}

internal enum VideoFitStage
{
    None,
    Compress,
    Extended,
}

internal enum VideoEncodeStage
{
    /// <summary>Handed over as linear light, which is what an extended-range surface wants.</summary>
    Linear,
    Srgb,

    /// <summary>Put back through a pure power law, for a surface shown through one.</summary>
    Power,

    /// <summary>Already encoded by the source; only clipped and quantized.</summary>
    Direct,
}

/// <summary>
/// What one frame has to be put through to reach one surface, decided once per draw.
/// </summary>
/// <remarks>
/// The gamut conversion and the range fit belong after the picture has been resized, not before.
/// A matrix and a resampling filter are both linear operators, so applying them in either order
/// gives the same picture; doing the matrix last means the filter only ever sees scene light, which
/// is non-negative and bounded by the source's own peak. Nothing has to be scaled into an interval
/// to survive the filter, and no color outside the display's primaries can be clipped on the way.
/// </remarks>
internal sealed class VideoColorPlan
{
    // Changes between corresponding samples in consecutive frames, in normalized signal units.
    // A few 8-bit steps keep sensor noise from softening unchanged detail.
    private const float MotionFloor = 0.015f;
    private const float MotionCeiling = 0.06f;

    // What an edge running diagonally has to beat the vertical tap by, summed over the three
    // components, before the missing row is averaged along it instead.
    private const float DiagonalPenalty = 0.05f;

    // HDR targets keep libplacebo's partial knee adaptation toward the target range.
    private static readonly VideoSdrToneMappingOptions HdrSplineOptions = new() { KneeAdaptation = 0.4f };

    private static readonly float[] DolbyVisionLmsToRgb =
    [
        3.06441879f, -2.16597676f, 0.10155818f,
        -0.65612108f, 1.78554118f, -0.12943749f,
        0.01736321f, -0.04725154f, 1.03004253f,
    ];

    private VideoColorPlan(Dictionary<string, float[]> uniforms) => Uniforms = uniforms;

    private string? DolbyVisionStage { get; init; }

    private IReadOnlyList<VideoShaderUniform> DolbyVisionUniforms { get; init; } =
        Array.Empty<VideoShaderUniform>();

    public VideoTransferStage Transfer { get; private init; }

    public bool ToneMap { get; private init; }

    public string ToneMappingAlgorithm
    {
        get
        {
            if (!ToneMap)
                return "none";
            if (Hdr10PlusToneMap)
                return "SMPTE ST 2094-40 (HDR10+)";
            if (SceneAdaptiveToneMap)
                return "PQ scene-adaptive spline";
            return StaticToneCurve switch
            {
                VideoSdrToneMappingAlgorithm.Bt2446A => "BT.2446 Method A",
                VideoSdrToneMappingAlgorithm.Clip => "clip",
                VideoSdrToneMappingAlgorithm.Mobius => "Mobius",
                VideoSdrToneMappingAlgorithm.Reinhard => "Reinhard",
                VideoSdrToneMappingAlgorithm.Hable => "Hable filmic",
                VideoSdrToneMappingAlgorithm.Gamma => "gamma with knee",
                VideoSdrToneMappingAlgorithm.Linear => "PQ linear",
                _ => "BT.2390",
            };
        }
    }

    public bool PhotoGainMap { get; private init; }

    public bool PhotoColor { get; private init; }

    public bool PhotoAlpha { get; init; }

    public bool DolbyVision { get; private init; }

    public bool DolbyVisionEnhancementLayer { get; private init; }

    public bool Hdr10Plus { get; private init; }

    public bool Hdr10PlusToneMap { get; private init; }

    public bool SceneAdaptiveToneMap { get; private init; }

    /// <summary>The curve applied when neither HDR10+ nor the scene-adaptive spline is.</summary>
    private VideoSdrToneMappingAlgorithm StaticToneCurve { get; init; } = VideoSdrToneMappingAlgorithm.Bt2390;

    public bool ScaleScene { get; private init; }

    public bool ConvertGamut { get; private init; }

    public VideoFitStage Fit { get; private init; }

    public VideoEncodeStage Encode { get; private init; }

    /// <summary>The curve the platform shows a bounded surface through, in both directions.</summary>
    public VideoSurfaceTransfer SurfaceTransfer { get; private init; }

    /// <summary>The curve a signal carrying no light of its own is read as.</summary>
    public VideoSurfaceTransfer SourceTransfer { get; private init; }

    public bool Dither { get; private init; }

    /// <summary>Whether the rows woven in from the second field are separated out again.</summary>
    public bool Deinterlace { get; private init; }

    public bool HasPreviousFrame { get; set; }

    public VideoDebandingQuality Debanding { get; private init; }

    /// <summary>
    /// The depth the picture is finally quantized to, or 0 where nothing in reach quantizes it.
    /// Reported whether or not <see cref="Dither"/> follows from it.
    /// </summary>
    public int OutputBitDepth { get; private init; }

    public Dictionary<string, float[]> Uniforms { get; }

    /// <summary>Whether the picture is carried as light rather than as the signal it arrived in.</summary>
    public bool IsLinear => Transfer != VideoTransferStage.Direct;

    /// <summary>
    /// Whether a frame's light or the output primaries require color conversion.
    /// </summary>
    public static bool NeedsColorProcessing(VideoFrame frame, VideoOutputColorSpace target) =>
        frame.PhotoColor is not null ||
        frame.DolbyVision.IsPresent ||
        frame.Hdr10Plus.IsPresent ||
        (frame.ColorTransfer == VideoColorTransfer.Bt709 && VideoSurfaceTransfers.ForBt709() is not null) ||
        frame.ColorTransfer is VideoColorTransfer.Srgb or VideoColorTransfer.Pq or
            VideoColorTransfer.Hlg or VideoColorTransfer.Linear ||
        VideoColorGamut.NeedsConversion(frame.ColorPrimaries, target.GetPrimaries());

    /// <summary>
    /// Whether the frame's rows were sampled at two instants, and so have to be read a field at a
    /// time rather than as the picture they are laid out as.
    /// </summary>
    public static bool NeedsDeinterlacing(VideoFrame frame) => frame.FieldOrder.IsInterlaced();

    public static VideoColorPlan Create(
        VideoFrame frame,
        VideoPlanarLayout layout,
        bool requiresSceneBuffer,
        VideoOutputColorSpace target,
        bool hdrPassthrough,
        VideoOutputColorVolume? colorVolume,
        int panelBitDepth = 0,
        VideoDebandingQuality debanding = VideoDebandingQuality.Off,
        VideoSdrToneMappingOptions? sdrToneMapping = null)
    {
        bool dolbyVision = frame.DolbyVision.IsPresent;
        VideoFrame? enhancementLayer = frame.EnhancementLayer;
        bool dolbyVisionEnhancementLayer = dolbyVision &&
            frame.DolbyVision.EnhancementLayerResidual &&
            enhancementLayer is not null;
        bool hdr10Plus = !dolbyVision && frame.Hdr10Plus.IsPresent;
        VideoColorTransfer colorTransfer = dolbyVision ? VideoColorTransfer.Pq : frame.ColorTransfer;
        VideoColorPrimaries colorPrimaries = dolbyVision ? VideoColorPrimaries.Bt2020 : frame.ColorPrimaries;
        bool hdr = colorTransfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg;
        VideoColorPrimaries outputPrimaries = target.GetPrimaries();
        VideoColorGamut.GamutConversion gamut = VideoColorGamut.ToTarget(colorPrimaries, outputPrimaries);
        bool extended = target.IsExtendedRange();
        VideoSurfaceTransfer? bt709Interpretation = VideoSurfaceTransfers.ForBt709();
        bool readSdrAsMastered = colorTransfer == VideoColorTransfer.Bt709 &&
            bt709Interpretation is null && extended &&
            VideoOutputColorSpaceExtensions.HasHdrHeadroom(colorVolume);

        VideoSurfaceTransfer surfaceTransfer = VideoSurfaceTransfers.For(target, colorVolume);
        VideoSurfaceTransfer sourceTransfer = colorTransfer == VideoColorTransfer.Srgb
            ? VideoSurfaceTransfer.Srgb
            : colorTransfer == VideoColorTransfer.Bt709 && bt709Interpretation is { } bt709Transfer
            ? bt709Transfer
            : readSdrAsMastered
            ? VideoSurfaceTransfer.Bt1886
            : surfaceTransfer;

        bool colorManaged = NeedsColorProcessing(frame, target);
        VideoTransferStage transfer = colorTransfer switch
        {
            VideoColorTransfer.Pq => VideoTransferStage.Pq,
            VideoColorTransfer.Hlg => VideoTransferStage.Hlg,
            VideoColorTransfer.Linear => VideoTransferStage.Linear,
            VideoColorTransfer.Srgb => VideoTransferStage.Srgb,

            // BT.709 names the camera-side OETF rather than a display EOTF. The host's video
            // interpretation takes precedence over the surface's round-trip curve.
            _ when colorManaged || requiresSceneBuffer || extended => readSdrAsMastered
                ? VideoTransferStage.Bt1886
                : sourceTransfer.Kind == VideoSurfaceTransferKind.Power
                    ? VideoTransferStage.Power
                    : VideoTransferStage.Srgb,
            _ => VideoTransferStage.Direct,
        };

        float sourcePeak = ResolveSourcePeak(frame, target, hdrPassthrough, colorVolume);
        VideoHdrOutputMapping hdrOutput = target.ResolveHdrOutput(
            sourcePeak,
            hdrPassthrough && hdr,
            sdrToneMapping,
            componentGain: gamut.MaximumComponentGain,
            colorVolume: colorVolume);

        bool directHdr = hdr && extended && hdrPassthrough && !hdrOutput.AdaptsToDisplay;
        bool toneMap = hdr && !directHdr;
        double linearNitsPerUnit = hdrOutput.LinearNitsPerUnit;
        double toneMapPeak = Math.Max(hdrOutput.ToneMapPeakNits, 1);
        bool sdrToneMap = toneMap && toneMapPeak <= linearNitsPerUnit;
        if (sdrToneMap && sourcePeak <= toneMapPeak)
            toneMap = false;
        VideoSdrToneMappingOptions toneOptions = sdrToneMapping ?? VideoSdrToneMappingOptions.Default;
        bool customToneMap = sdrToneMap || sdrToneMapping is not null;
        if (customToneMap)
            toneOptions.Validate();
        if (customToneMap && VideoShaderToneCurves.Supports(toneOptions.Algorithm) && sourcePeak <= (float)toneMapPeak)
            toneMap = false;

        double unitScale = hdrOutput.ReferenceWhiteScale;
        double sceneScale = (hdr ? 1.0 / linearNitsPerUnit : 1.0) * unitScale;
        double fitPeak = (hdr ? toneMapPeak / linearNitsPerUnit : 1.0) * unitScale;

        VideoFitStage fit = (colorManaged, directHdr, extended) switch
        {
            (false, _, _) => VideoFitStage.None,
            (_, true, _) => VideoFitStage.None,
            (_, _, true) => hdr ? VideoFitStage.Extended : VideoFitStage.None,
            _ => VideoFitStage.Compress,
        };

        VideoEncodeStage encode = extended
            ? VideoEncodeStage.Linear
            : transfer == VideoTransferStage.Direct
                ? VideoEncodeStage.Direct
                : surfaceTransfer.Kind == VideoSurfaceTransferKind.Power
                    ? VideoEncodeStage.Power
                    : VideoEncodeStage.Srgb;

        // Dithered only where the picture carries more than survives the quantizer: an 8-bit source
        // banding its way onto an 8-bit panel is the source's banding, and noise cannot undo it.
        int outputBitDepth = ResolveOutputBitDepth(extended, panelBitDepth);
        bool dither = outputBitDepth > 0 &&
            (debanding != VideoDebandingQuality.Off || hdr || layout.BitDepth > outputBitDepth);
        bool convertGamut = VideoColorGamut.NeedsConversion(colorPrimaries, outputPrimaries) && colorManaged;
        bool scaleScene = Math.Abs(sceneScale - 1) > 1e-9;

        var uniforms = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (dolbyVision)
            AddDolbyVisionUniforms(
                uniforms,
                frame.DolbyVision,
                layout,
                dolbyVisionEnhancementLayer ? enhancementLayer!.Layout : null);

        bool deinterlace = NeedsDeinterlacing(frame);
        if (deinterlace)
        {
            uniforms[VideoShaderUniforms.DeinterlaceParams] =
            [
                frame.FieldOrder == VideoFieldOrder.BottomFieldFirst ? 1 : 0,
                MotionFloor,
                MotionCeiling,
                DiagonalPenalty,
            ];
            uniforms[VideoShaderUniforms.DeinterlaceSize] =
                [frame.Width, frame.Height, layout.BytesPerSample == 2 ? (float)layout.SampleScale : 1, 0];
        }

        if (transfer == VideoTransferStage.Hlg || toneMap)
        {
            float systemGamma = hdrOutput.RelativeLuminance
                ? 1.2f
                : 1.2f + (0.42f * MathF.Log10(sourcePeak / 1000.0f));
            uniforms[VideoShaderUniforms.SourceLuma] =
                [(float)gamut.Luma.Red, (float)gamut.Luma.Green, (float)gamut.Luma.Blue, systemGamma];
        }

        if (transfer == VideoTransferStage.Hlg)
            uniforms[VideoShaderUniforms.TransferParams] = [sourcePeak, 0, 0, 0];
        else if (transfer == VideoTransferStage.Bt1886)
            uniforms[VideoShaderUniforms.TransferParams] = Bt1886Parameters(colorVolume);

        if (surfaceTransfer.Kind == VideoSurfaceTransferKind.Power ||
            sourceTransfer.Kind == VideoSurfaceTransferKind.Power)
        {
            uniforms[VideoShaderUniforms.SurfaceParams] =
            [
                sourceTransfer.Exponent,
                sourceTransfer.Exponent > 0 ? 1.0f / sourceTransfer.Exponent : 0,
                surfaceTransfer.Exponent,
                surfaceTransfer.Exponent > 0 ? 1.0f / surfaceTransfer.Exponent : 0,
            ];
        }

        // FFmpeg can carry local selectors, but libplacebo's global color mapping likewise uses
        // processing window zero. The remaining windows stay on the frame for diagnostics.
        Hdr10PlusProcessingWindow? hdr10PlusWindow = hdr10Plus
            ? frame.Hdr10Plus.Windows[0]
            : null;
        bool hdr10PlusToneMap = toneMap &&
            (!customToneMap || toneOptions.Algorithm == VideoSdrToneMappingAlgorithm.Auto) && hdr10PlusWindow is
            { ToneMapping: true, AnchorCount: > 0 } &&
            frame.Hdr10Plus.TargetedSystemDisplayMaximumLuminance > 0;
        bool sceneAdaptiveToneMap = toneMap && !hdr10PlusToneMap && (customToneMap
            ? toneOptions.Algorithm is VideoSdrToneMappingAlgorithm.Auto or VideoSdrToneMappingAlgorithm.Spline
            : dolbyVision || hdr10Plus);
        VideoSdrToneMappingAlgorithm staticToneCurve = toneMap && customToneMap &&
            toneOptions.Algorithm is not (VideoSdrToneMappingAlgorithm.Auto or VideoSdrToneMappingAlgorithm.Spline)
                ? toneOptions.Algorithm
                : VideoSdrToneMappingAlgorithm.Bt2390;
        if (hdr10PlusToneMap)
        {
            AddHdr10PlusToneUniforms(
                uniforms,
                frame.Hdr10Plus,
                sourcePeak,
                (float)toneMapPeak);
        }
        else if (sceneAdaptiveToneMap)
        {
            AddSceneAdaptiveToneUniforms(
                uniforms,
                NitsToPq(dolbyVision ? PqToNits(frame.DolbyVision.SourceMinimumPq) : 0),
                NitsToPq(dolbyVision
                    ? PqToNits(frame.DolbyVision.SceneAveragePq)
                    : frame.Hdr10Plus.SceneAverageLuminance),
                NitsToPq(sourcePeak),
                NitsToPq((float)toneMapPeak),
                customToneMap ? toneOptions : HdrSplineOptions);
        }
        else if (staticToneCurve == VideoSdrToneMappingAlgorithm.Bt2446A)
        {
            uniforms[VideoShaderUniforms.ToneParams] =
            [
                sourcePeak,
                (float)toneMapPeak,
                1 + 32 * MathF.Pow(sourcePeak / 10000, 1 / 2.4f),
                1 + 32 * MathF.Pow((float)toneMapPeak / 10000, 1 / 2.4f),
            ];
        }
        else if (VideoShaderToneCurves.Supports(staticToneCurve))
        {
            uniforms[VideoShaderUniforms.ToneParams] =
                VideoShaderToneCurves.Parameters(toneOptions, sourcePeak, (float)toneMapPeak);
        }
        else if (toneMap)
        {
            float sourceMax = NitsToPq(sourcePeak);
            float targetMax = NitsToPq((float)toneMapPeak);
            uniforms[VideoShaderUniforms.ToneParams] =
                [sourceMax, targetMax, targetMax / sourceMax, customToneMap ? toneOptions.Bt2390KneeOffset : 0.5f];
        }

        if (scaleScene)
            uniforms[VideoShaderUniforms.SceneParams] = [(float)sceneScale, 0, 0, 0];

        if (convertGamut)
        {
            double[] matrix = gamut.Matrix;
            uniforms[VideoShaderUniforms.GamutR] = [(float)matrix[0], (float)matrix[1], (float)matrix[2], 0];
            uniforms[VideoShaderUniforms.GamutG] = [(float)matrix[3], (float)matrix[4], (float)matrix[5], 0];
            uniforms[VideoShaderUniforms.GamutB] = [(float)matrix[6], (float)matrix[7], (float)matrix[8], 0];
        }

        if (fit != VideoFitStage.None)
            uniforms[VideoShaderUniforms.OutputParams] = [(float)fitPeak, 0, 0, 0];

        if (dither)
        {
            float seed = double.IsFinite(frame.Pts)
                ? (float)(Math.Abs(frame.Pts * 60.0) % 1024.0) * 0.75487767f
                : 0;
            uniforms[VideoShaderUniforms.DitherParams] = [1.0f / ((1 << outputBitDepth) - 1), seed, 0, 0];
        }

        if (debanding != VideoDebandingQuality.Off)
        {
            VideoDebandingProfile profile = VideoDebandingProfile.For(debanding);
            float seed = double.IsFinite(frame.Pts)
                ? (float)(Math.Abs(frame.Pts * 60.0) % 1024.0) * 0.75487767f
                : 0;
            uniforms[VideoShaderUniforms.DebandParams] =
                [profile.Threshold, profile.Radius, seed, profile.Grain];
        }

        return new VideoColorPlan(uniforms)
        {
            PhotoGainMap = VideoPhotoGainMap.Configure(frame.GainMap, target, hdrPassthrough, colorVolume, uniforms),
            PhotoColor = VideoPhotoColor.Configure(frame.PhotoColor, uniforms),
            PhotoAlpha = frame.HasAlpha,
            Transfer = transfer,
            ToneMap = toneMap,
            DolbyVision = dolbyVision,
            DolbyVisionEnhancementLayer = dolbyVisionEnhancementLayer,
            DolbyVisionStage = dolbyVision
                ? VideoColorStages.CreateDolbyVision(
                    frame.DolbyVision,
                    layout.SwappedChroma,
                    dolbyVisionEnhancementLayer,
                    enhancementLayer?.Layout.SwappedChroma ?? false)
                : null,
            DolbyVisionUniforms = dolbyVision
                ? VideoShaderUniforms.DolbyVision(frame.DolbyVision, dolbyVisionEnhancementLayer)
                : Array.Empty<VideoShaderUniform>(),
            Hdr10Plus = hdr10Plus,
            Hdr10PlusToneMap = hdr10PlusToneMap,
            SceneAdaptiveToneMap = sceneAdaptiveToneMap,
            StaticToneCurve = staticToneCurve,
            ScaleScene = scaleScene,
            ConvertGamut = convertGamut,
            Fit = fit,
            Encode = encode,
            SurfaceTransfer = surfaceTransfer,
            SourceTransfer = sourceTransfer,
            Dither = dither,
            Deinterlace = deinterlace,
            Debanding = debanding,
            OutputBitDepth = outputBitDepth,
        };
    }

    /// <summary>
    /// What the picture is finally quantized to, or 0 where nothing in reach does. A bounded
    /// surface quantizes to its own eight bits; an extended-range one stores floats and leaves it
    /// to the compositor, which is why only the user can say what happens after that. Where both
    /// have a say the coarser one wins, since the picture has to survive it either way.
    /// </summary>
    private static int ResolveOutputBitDepth(bool extended, int panelBitDepth)
    {
        int surfaceBitDepth = extended ? 0 : 8;
        if (panelBitDepth <= 0)
            return surfaceBitDepth;

        return surfaceBitDepth > 0 ? Math.Min(surfaceBitDepth, panelBitDepth) : panelBitDepth;
    }

    private static float[] Bt1886Parameters(VideoOutputColorVolume? colorVolume)
    {
        double black = colorVolume is
            { LuminanceBasis: VideoLuminanceBasis.DisplayReported or VideoLuminanceBasis.Nominal, ReferenceWhiteNits: > 0,
                TargetLuminance.MinimumNits: >= 0 } volume &&
            double.IsFinite(volume.ReferenceWhiteNits.Value) &&
            double.IsFinite(volume.TargetLuminance.Value.MinimumNits!.Value)
                ? Math.Clamp(volume.TargetLuminance.Value.MinimumNits.Value / volume.ReferenceWhiteNits.Value, 0, 1 - 1e-6)
                : 0;
        double blackRoot = Math.Pow(black, 1 / 2.4);
        double range = 1 - blackRoot;
        return [(float)Math.Pow(range, 2.4), (float)(blackRoot / range), 2.4f, 1 / 2.4f];
    }

    /// <summary>
    /// Reports what the picture ends up as, for the diagnostics that describe the color chain.
    /// </summary>
    public static VideoHdrOutputMapping ResolveOutput(
        VideoFrame frame,
        VideoOutputColorSpace target,
        bool hdrPassthrough,
        VideoOutputColorVolume? colorVolume,
        VideoSdrToneMappingOptions? sdrToneMapping = null)
    {
        bool dolbyVision = frame.DolbyVision.IsPresent;
        bool hdr = dolbyVision || frame.ColorTransfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg;
        return target.ResolveHdrOutput(
            ResolveSourcePeak(frame, target, hdrPassthrough, colorVolume),
            hdrPassthrough && hdr,
            sdrToneMapping,
            componentGain: VideoColorGamut.MaximumComponentGain(
                dolbyVision ? VideoColorPrimaries.Bt2020 : frame.ColorPrimaries, target.GetPrimaries()),
            colorVolume: colorVolume);
    }

    /// <summary>
    /// Maps neutral BT.2020 PQ luminance through the curve <see cref="Create"/> selects for static
    /// metadata, writing output relative to SDR white before the surface's white scale.
    /// </summary>
    public static VideoHdrOutputMapping EvaluateToneCurve(
        VideoOutputColorSpace target,
        bool hdrPassthrough,
        VideoOutputColorVolume? colorVolume,
        VideoSdrToneMappingOptions? sdrToneMapping,
        float sourcePeak,
        ReadOnlySpan<float> sourceNits,
        Span<float> output)
    {
        VideoHdrOutputMapping hdrOutput = target.ResolveHdrOutput(
            sourcePeak,
            hdrPassthrough,
            sdrToneMapping,
            componentGain: VideoColorGamut.MaximumComponentGain(VideoColorPrimaries.Bt2020, target.GetPrimaries()),
            colorVolume: colorVolume);
        bool toneMap = !(target.IsExtendedRange() && hdrPassthrough && !hdrOutput.AdaptsToDisplay);
        float linearNitsPerUnit = (float)hdrOutput.LinearNitsPerUnit;
        float toneMapPeak = (float)Math.Max(hdrOutput.ToneMapPeakNits, 1);
        bool sdrToneMap = toneMap && toneMapPeak <= linearNitsPerUnit;
        if (sdrToneMap && sourcePeak <= toneMapPeak)
            toneMap = false;
        VideoSdrToneMappingOptions toneOptions = sdrToneMapping ?? VideoSdrToneMappingOptions.Default;
        bool customToneMap = sdrToneMap || sdrToneMapping is not null;
        if (customToneMap)
            toneOptions.Validate();
        bool shaderCurve = customToneMap && VideoShaderToneCurves.Supports(toneOptions.Algorithm);
        if (shaderCurve && sourcePeak <= toneMapPeak)
            toneMap = false;

        if (toneMap && shaderCurve)
        {
            int count = Math.Min(sourceNits.Length, output.Length);
            float[] clamped = new float[count];
            for (int index = 0; index < count; index++)
                clamped[index] = Math.Clamp(sourceNits[index], 0, sourcePeak);
            VideoShaderToneCurves.Evaluate(toneOptions.Algorithm,
                VideoShaderToneCurves.Parameters(toneOptions, sourcePeak, toneMapPeak), clamped, output);
            for (int index = 0; index < count; index++)
                output[index] /= linearNitsPerUnit;
            return hdrOutput;
        }

        VideoToneCurve curve = !toneMap
            ? new VideoToneCurve()
            : customToneMap && toneOptions.Algorithm is VideoSdrToneMappingAlgorithm.Auto or VideoSdrToneMappingAlgorithm.Spline
                ? VideoToneCurve.Spline(0, 0, NitsToPq(sourcePeak), NitsToPq(toneMapPeak), toneOptions)
                : customToneMap && toneOptions.Algorithm == VideoSdrToneMappingAlgorithm.Bt2446A
                    ? VideoToneCurve.Bt2446A(sourcePeak, toneMapPeak)
                    : VideoToneCurve.Bt2390(sourcePeak, toneMapPeak, customToneMap ? toneOptions.Bt2390KneeOffset : 0.5f);
        for (int index = 0; index < sourceNits.Length && index < output.Length; index++)
        {
            float nits = Math.Clamp(sourceNits[index], 0, sourcePeak);
            float mapped = curve.Kind == SoftwareToneCurve.None ? nits : VideoCpuColorPipeline.MapLuminance(curve, (ScalarColor)nits).Value;
            output[index] = mapped / linearNitsPerUnit;
        }

        return hdrOutput;
    }

    private static float ResolveSourcePeak(VideoFrame frame, VideoOutputColorSpace target,
        bool hdrPassthrough, VideoOutputColorVolume? colorVolume)
    {
        if (!frame.DolbyVision.IsPresent && frame.ColorTransfer == VideoColorTransfer.Hlg &&
            (!target.UsesAbsoluteLuminance(hdrPassthrough, colorVolume) ||
             !VideoOutputColorSpaceExtensions.HasHdrHeadroom(colorVolume)))
            return 1000;

        double peak = frame.DolbyVision.IsPresent && frame.DolbyVision.EffectivePeakLuminance > 0
            ? frame.DolbyVision.EffectivePeakLuminance
            : frame.Hdr10Plus.IsPresent && !frame.DolbyVision.IsPresent && frame.Hdr10Plus.EffectivePeakLuminance > 0
                ? frame.Hdr10Plus.EffectivePeakLuminance
                : frame.PhotoPeakLuminance is > 0 and var measured && double.IsFinite(measured)
                    ? measured : frame.HdrPeakLuminance;
        return (float)VideoOutputColorSpaceExtensions.EffectiveHdrSourcePeak(peak);
    }

    /// <summary>Adds the stages that turn the decoder's samples into scene light.</summary>
    public void AddSceneStages(VideoShaderBuilder builder, string sampleSource, bool matrixDecode)
    {
        if (IsLinear && !DolbyVision)
            builder.AddStage(VideoColorStages.LinearHelpers);

        AddSignalStages(builder, sampleSource, matrixDecode);
        bool dolbyVisionNits = DolbyVision && Debanding == VideoDebandingQuality.Off;
        if (dolbyVisionNits)
            builder.AddStage(VideoColorStages.ProcessDolbyVisionNits);
        else if (PhotoColor)
            VideoPhotoColor.AddStage(builder, PhotoAlpha);
        else
        {
            AddDebandingStage(builder);
            if (PhotoAlpha)
                builder.AddStage(VideoPhotoColor.AlphaStage(true));
        }
        if (PhotoGainMap)
            VideoPhotoGainMap.AddStage(builder);
        AddSceneTransformStages(builder, dolbyVisionNits || PhotoColor);
    }

    /// <summary>Adds the stages that turn decoder samples into normalized nonlinear RGB.</summary>
    public void AddSignalStages(VideoShaderBuilder builder, string sampleSource, bool matrixDecode)
    {
        builder.AddStage(sampleSource);
        if (Deinterlace)
        {
            if (HasPreviousFrame)
                builder.AddChild(VideoShaderChildren.PreviousSamples);
            builder
                .AddUniform(VideoShaderUniforms.DeinterlaceParams)
                .AddUniform(VideoShaderUniforms.DeinterlaceSize)
                .AddStage(HasPreviousFrame ? VideoColorStages.SamplePreviousFrame : VideoColorStages.SampleCurrentFrame)
                .AddStage(VideoColorStages.DeinterlaceMotionAdaptive);
        }
        else
        {
            builder.AddStage(VideoColorStages.DeinterlaceNone);
        }

        if (DolbyVision)
        {
            builder
                .AddStage(VideoColorStages.LinearHelpers)
                .AddUniforms(DolbyVisionUniforms)
                .AddStage(DolbyVisionStage!);
            return;
        }

        if (matrixDecode)
        {
            builder
                .AddUniform(VideoShaderUniforms.MatrixR)
                .AddUniform(VideoShaderUniforms.MatrixG)
                .AddUniform(VideoShaderUniforms.MatrixB)
                .AddStage(VideoColorStages.DecodeMatrix);
        }
        else
        {
            builder.AddStage(PhotoColor
                ? "float3 decodeSignal(float2 coord) { return sampleFrame(coord); }"
                : VideoColorStages.DecodeIdentity);
        }

    }

    /// <summary>Adds transfer and scene conversion after a staged nonlinear RGB image.</summary>
    public void AddSceneFromSignalStages(VideoShaderBuilder builder)
    {
        if (IsLinear)
            builder.AddStage(VideoColorStages.LinearHelpers);

        builder.AddStage(VideoColorStages.DecodeSignalSource);
        if (PhotoColor)
            VideoPhotoColor.AddStage(builder, hasAlpha: false);
        else
            builder.AddStage(VideoColorStages.DebandNone);
        if (PhotoGainMap)
            VideoPhotoGainMap.AddStage(builder);
        AddSceneTransformStages(builder, PhotoColor);
    }

    public void AddDebandingStage(VideoShaderBuilder builder)
    {
        if (Debanding == VideoDebandingQuality.Off)
        {
            builder.AddStage(VideoColorStages.DebandNone);
            return;
        }

        builder
            .AddUniform(VideoShaderUniforms.DebandParams)
            .AddStage(Debanding switch
            {
                VideoDebandingQuality.High => VideoColorStages.DebandHigh,
                VideoDebandingQuality.Medium => VideoColorStages.DebandMedium,
                _ => VideoColorStages.DebandLow,
            });
    }

    private void AddSceneTransformStages(VideoShaderBuilder builder, bool signalIsLinear = false)
    {
        if (signalIsLinear)
        {
            builder.AddStage(VideoColorStages.TransferNone);
        }
        else
        {
            builder.AddUniforms(SourceUniforms()).AddStage(Transfer switch
            {
                VideoTransferStage.Pq => VideoColorStages.TransferPq,
                VideoTransferStage.Hlg => VideoColorStages.TransferHlg,
                VideoTransferStage.Srgb => VideoColorStages.TransferSrgb,
                VideoTransferStage.Bt1886 => VideoColorStages.TransferBt1886,
                VideoTransferStage.Power => VideoColorStages.TransferPower,
                _ => VideoColorStages.TransferNone,
            });
        }

        if (Hdr10PlusToneMap)
        {
            builder
                .AddUniform(VideoShaderUniforms.SourceLuma)
                .AddUniform(VideoShaderUniforms.Hdr10PlusParams)
                .AddUniform(VideoShaderUniforms.Hdr10PlusCurve)
                .AddStage(VideoColorStages.ToneHdr10Plus);
        }
        else if (SceneAdaptiveToneMap)
        {
            builder
                .AddUniform(VideoShaderUniforms.SourceLuma)
                .AddUniform(VideoShaderUniforms.ToneParams)
                .AddUniform(VideoShaderUniforms.ToneCurve0)
                .AddUniform(VideoShaderUniforms.ToneCurve1)
                .AddStage(VideoColorStages.ToneSceneAdaptive);
        }
        else if (ToneMap)
        {
            builder
                .AddUniform(VideoShaderUniforms.SourceLuma)
                .AddUniform(VideoShaderUniforms.ToneParams)
                .AddStage(StaticToneCurve switch
                {
                    VideoSdrToneMappingAlgorithm.Bt2390 => VideoColorStages.ToneBt2390,
                    VideoSdrToneMappingAlgorithm.Bt2446A => VideoColorStages.ToneBt2446A,
                    var curve => VideoShaderToneCurves.Stage(curve),
                });
        }
        else
        {
            builder.AddStage(VideoColorStages.ToneNone);
        }

        if (ScaleScene)
            builder.AddUniform(VideoShaderUniforms.SceneParams).AddStage(VideoColorStages.SceneScaled);
        else
            builder.AddStage(VideoColorStages.SceneNone);
    }

    /// <summary>Adds the stages that turn scene light into what the surface stores.</summary>
    public void AddOutputStages(VideoShaderBuilder builder)
    {
        if (Fit != VideoFitStage.None)
            builder.AddStage(VideoColorStages.OutputHelpers);

        if (Dither)
        {
            builder
                .AddUniform(VideoShaderUniforms.DitherParams)
                .AddUniform(VideoShaderUniforms.DitherX)
                .AddUniform(VideoShaderUniforms.DitherY)
                .AddUniform(VideoShaderUniforms.DitherW)
                .AddStage(VideoColorStages.DitherHelpers);
        }

        if (ConvertGamut)
        {
            builder
                .AddUniform(VideoShaderUniforms.GamutR)
                .AddUniform(VideoShaderUniforms.GamutG)
                .AddUniform(VideoShaderUniforms.GamutB)
                .AddStage(VideoColorStages.GamutMatrix);
        }
        else
        {
            builder.AddStage(VideoColorStages.GamutNone);
        }

        if (Fit != VideoFitStage.None)
            builder.AddUniform(VideoShaderUniforms.OutputParams);

        builder.AddStage(Fit switch
        {
            VideoFitStage.Compress => VideoColorStages.FitCompress,
            VideoFitStage.Extended => VideoColorStages.FitExtended,
            _ => VideoColorStages.FitNone,
        });

        bool powerSurface = SurfaceTransfer.Kind == VideoSurfaceTransferKind.Power;
        if (Encode == VideoEncodeStage.Power || (Encode == VideoEncodeStage.Linear && Dither && powerSurface))
            builder.AddUniform(VideoShaderUniforms.SurfaceParams);

        builder.AddStage((Encode, Dither) switch
        {
            (VideoEncodeStage.Linear, true) => powerSurface
                ? VideoColorStages.EncodeLinearDitheredPower
                : VideoColorStages.EncodeLinearDitheredSrgb,
            (VideoEncodeStage.Linear, false) => VideoColorStages.EncodeLinear,
            (VideoEncodeStage.Srgb, true) => VideoColorStages.EncodeSrgbDithered,
            (VideoEncodeStage.Srgb, false) => VideoColorStages.EncodeSrgb,
            (VideoEncodeStage.Power, true) => VideoColorStages.EncodePowerDithered,
            (VideoEncodeStage.Power, false) => VideoColorStages.EncodePower,
            (_, true) => VideoColorStages.EncodeDirectDithered,
            _ => VideoColorStages.EncodeDirect,
        });
    }

    /// <summary>Places the dither grid on the physical pixels the surface will quantize.</summary>
    public void SetDitherTransform(SKMatrix transform)
    {
        if (!Dither)
            return;

        Uniforms[VideoShaderUniforms.DitherX] = [transform.ScaleX, transform.SkewX, transform.TransX];
        Uniforms[VideoShaderUniforms.DitherY] = [transform.SkewY, transform.ScaleY, transform.TransY];
        Uniforms[VideoShaderUniforms.DitherW] = [transform.Persp0, transform.Persp1, transform.Persp2];
    }

    /// <summary>
    /// Builds the rows of the color matrix, each with the sample offset folded into <c>.w</c> so a
    /// plain dot product against (y, cb, cr, 1) gives the channel.
    /// </summary>
    public void SetColorMatrix(VideoFrame frame, VideoPlanarLayout layout)
    {
        if (DolbyVision)
            return;

        (double kr, double kb) = frame.ColorSpace switch
        {
            VideoColorSpace.Bt601 => (0.299, 0.114),
            VideoColorSpace.Bt2020 => (0.2627, 0.0593),
            _ => (0.2126, 0.0722),
        };

        double kg = 1.0 - kr - kb;
        bool full = frame.ColorRange == VideoColorRange.Full;

        // Every sample first becomes the 0-1 value its code point stands for, whatever container it
        // arrived in. Legal-range code points scale by 2^(depth - 8), while normalization still
        // spans the depth's full code range rather than 255 at depths above 8.
        double sample = layout.SampleScale;
        double codeScale = 1 << (layout.BitDepth - 8);
        double maxCode = (1 << layout.BitDepth) - 1;
        double lumaOffset = full ? 0.0 : 16.0 * codeScale / maxCode;
        double lumaGain = (full ? 1.0 : maxCode / (219.0 * codeScale)) * sample;
        double chromaOffset = 128.0 * codeScale / maxCode;
        double chromaGain = (full ? 1.0 : maxCode / (224.0 * codeScale)) * sample;

        double toCr = 2.0 * (1.0 - kr) * chromaGain;
        double toCb = 2.0 * (1.0 - kb) * chromaGain;
        double greenCb = -2.0 * kb * (1.0 - kb) / kg * chromaGain;
        double greenCr = -2.0 * kr * (1.0 - kr) / kg * chromaGain;

        // The Cr,Cb layouts differ only in which sampled component each coefficient meets.
        bool swap = layout.SwappedChroma;
        Uniforms[VideoShaderUniforms.MatrixR] =
            Row(lumaGain, swap ? toCr : 0.0, swap ? 0.0 : toCr, lumaOffset, chromaOffset, sample);
        Uniforms[VideoShaderUniforms.MatrixG] =
            Row(lumaGain, swap ? greenCr : greenCb, swap ? greenCb : greenCr, lumaOffset, chromaOffset, sample);
        Uniforms[VideoShaderUniforms.MatrixB] =
            Row(lumaGain, swap ? 0.0 : toCb, swap ? toCb : 0.0, lumaOffset, chromaOffset, sample);
    }

    private IEnumerable<string> SourceUniforms()
    {
        if (Transfer == VideoTransferStage.Power)
            yield return VideoShaderUniforms.SurfaceParams;

        if (Transfer == VideoTransferStage.Bt1886)
        {
            yield return VideoShaderUniforms.TransferParams;
            yield break;
        }

        if (Transfer != VideoTransferStage.Hlg)
            yield break;

        yield return VideoShaderUniforms.SourceLuma;
        yield return VideoShaderUniforms.TransferParams;
    }

    private static void AddDolbyVisionUniforms(
        Dictionary<string, float[]> uniforms,
        DolbyVisionMetadata metadata,
        VideoPlanarLayout layout,
        VideoPlanarLayout? enhancementLayout)
    {
        uniforms[VideoShaderUniforms.DoviParams] =
            [(float)layout.SampleScale, (float)(enhancementLayout?.SampleScale ?? 0), 0, 0];
        uniforms[VideoShaderUniforms.DoviNonlinearMatrix] =
            CreateDolbyVisionNonlinearMatrix(metadata.NonlinearMatrix, metadata.NonlinearOffset);
        uniforms[VideoShaderUniforms.DoviLinearMatrix] =
            CreateDolbyVisionLinearMatrix(metadata.LinearMatrix);
        if (enhancementLayout is not null)
        {
            uniforms[VideoShaderUniforms.DoviNlqOffset] = metadata.NlqOffset;
            uniforms[VideoShaderUniforms.DoviNlqSlope] = metadata.NlqDeadzoneSlope;
            uniforms[VideoShaderUniforms.DoviNlqThreshold] = metadata.NlqDeadzoneThreshold;
        }

        DolbyVisionCurveShape[] shapes = VideoColorStages.DescribeDolbyVision(metadata);
        for (int component = 0; component < shapes.Length; component++)
        {
            DolbyVisionCurveShape shape = shapes[component];
            DolbyVisionReshapingCurve curve = metadata.Curves[component];
            if (shape.Pivots)
            {
                uniforms[VideoShaderUniforms.DoviPivots[component]] =
                    curve.Pivots[..(shape.PieceCount + 1)];
            }

            if (shape.Polynomial)
            {
                uniforms[VideoShaderUniforms.DoviPolynomial[component]] =
                    curve.PolynomialCoefficients[
                        ..(shape.PieceCount * DolbyVisionReshapingCurve.PolynomialCoefficientCount)];
            }

            if (shape.MmrOrder > 0)
            {
                uniforms[VideoShaderUniforms.DoviMmrConstants[component]] =
                    curve.MmrConstants[..shape.PieceCount];
                uniforms[VideoShaderUniforms.DoviMmrCoefficients[component]] =
                    PackDolbyVisionMmrCoefficients(
                        curve.MmrCoefficients,
                        shape.PieceCount,
                        shape.MmrOrder);
            }
        }
    }

    private static float[] CreateDolbyVisionNonlinearMatrix(float[] matrix, float[] offset)
    {
        var affine = new float[12];
        for (int row = 0; row < 3; row++)
        {
            int source = row * 3;
            int destination = row * 4;
            affine[destination] = matrix[source];
            affine[destination + 1] = matrix[source + 1];
            affine[destination + 2] = matrix[source + 2];
            affine[destination + 3] = -(
                matrix[source] * offset[0] +
                matrix[source + 1] * offset[1] +
                matrix[source + 2] * offset[2]);
        }

        return affine;
    }

    private static float[] CreateDolbyVisionLinearMatrix(float[] matrix)
    {
        var combined = new float[9];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                combined[(row * 3) + column] =
                    DolbyVisionLmsToRgb[row * 3] * matrix[column] +
                    DolbyVisionLmsToRgb[(row * 3) + 1] * matrix[3 + column] +
                    DolbyVisionLmsToRgb[(row * 3) + 2] * matrix[6 + column];
            }
        }

        return combined;
    }

    private static float[] PackDolbyVisionMmrCoefficients(
        float[] coefficients,
        int pieceCount,
        int order)
    {
        var packed = new float[pieceCount * order * 8];
        for (int piece = 0; piece < pieceCount; piece++)
        {
            for (int index = 0; index < order; index++)
            {
                int source = ((piece * DolbyVisionReshapingCurve.MaximumMmrOrder) + index) *
                    DolbyVisionReshapingCurve.MmrCoefficientCount;
                int destination = ((piece * order) + index) * 8;
                Array.Copy(coefficients, source, packed, destination, 3);
                Array.Copy(coefficients, source + 3, packed, destination + 4, 4);
            }
        }

        return packed;
    }

    private static void AddSceneAdaptiveToneUniforms(
        Dictionary<string, float[]> uniforms,
        float sourceMinimum,
        float sceneAverage,
        float sourceMaximum,
        float targetMaximum,
        VideoSdrToneMappingOptions options)
    {
        VideoToneCurve curve = VideoToneCurve.Spline(
            sourceMinimum, sceneAverage, sourceMaximum, targetMaximum, options);
        uniforms[VideoShaderUniforms.ToneParams] = curve.Parameters;
        uniforms[VideoShaderUniforms.ToneCurve0] = curve.Curve0;
        uniforms[VideoShaderUniforms.ToneCurve1] = curve.Curve1;
    }

    private static void AddHdr10PlusToneUniforms(
        Dictionary<string, float[]> uniforms,
        Hdr10PlusMetadata metadata,
        float sourceMaximum,
        float targetMaximum)
    {
        VideoToneCurve curve = VideoToneCurve.Hdr10Plus(metadata, sourceMaximum, targetMaximum);
        uniforms[VideoShaderUniforms.Hdr10PlusParams] = curve.Parameters;
        uniforms[VideoShaderUniforms.Hdr10PlusCurve] = curve.Controls;
    }

    private static float[] Row(
        double y,
        double first,
        double second,
        double lumaOffset,
        double chromaOffset,
        double sample) =>
    [
        (float)y,
        (float)first,
        (float)second,
        // The offsets are in signal units, so the sample scale has to come back out of the gains.
        (float)-((y * lumaOffset + (first + second) * chromaOffset) / sample),
    ];

    private static float PqToNits(float signal)
    {
        float power = MathF.Pow(Math.Clamp(signal, 0, 1), 1 / 78.84375f);
        return 10000 * MathF.Pow(Math.Max(power - 0.8359375f, 0) /
            Math.Max(18.8515625f - 18.6875f * power, 1e-6f), 1 / 0.1593017578125f);
    }

    private static float NitsToPq(float nits)
    {
        if (nits <= 0)
            return 0;

        const float M1 = 0.1593017578125f;
        const float M2 = 78.84375f;
        const float C1 = 0.8359375f;
        const float C2 = 18.8515625f;
        const float C3 = 18.6875f;
        float level = MathF.Pow(Math.Clamp(nits / 10000.0f, 0, 1), M1);
        return MathF.Pow((C1 + C2 * level) / (1 + C3 * level), M2);
    }
}
