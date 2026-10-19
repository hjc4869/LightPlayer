using System;
using System.Threading;
using Avalonia.Skia;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Rendering;

/// <summary>
/// The state of the color chain between the decoder and the display, at each stage it can fail at.
/// </summary>
/// <remarks>
/// <para>
/// Wide gamut output only happens when every stage agrees, and each of them can decline for its own
/// reasons on the machine it runs on: the application head has to ask for it, the windowing backend
/// has to negotiate it with the compositor and the driver, and only then does the renderer have
/// somewhere to put a picture brighter than white. When it silently does not happen, the useful
/// question is which stage stopped, so each one is recorded as it becomes known rather than
/// inferred afterwards from the result.
/// </para>
/// <para>
/// Process-wide, because it describes the window and the graphics device the whole application
/// shares. Written from the render thread and read from the UI thread, so the snapshot is swapped
/// in one reference at a time.
/// </para>
/// </remarks>
public static class VideoOutputDiagnostics
{
    private const double LogIntervalSeconds = 5.0;
    private const double RenderTimeSmoothingFactor = 0.1;
    private const double RenderSampleResetSeconds = 1.0;

    public static IVideoRenderDiagnostics Renderer { get; } = new RendererDiagnostics();

    private static VideoOutputStatus current = VideoOutputStatus.Unknown;
    private static string lastLogged = string.Empty;
    private static double lastLogTime = double.NegativeInfinity;
    private static bool frameSurfaceObserved;
    private static double renderCpuFrameTimeMilliseconds;
    private static double lastRenderSampleTime;

    /// <summary>What the chain currently looks like.</summary>
    public static VideoOutputStatus Current
    {
        get
        {
            VideoOutputStatus status = Volatile.Read(ref current);
            double frameTime = Volatile.Read(ref renderCpuFrameTimeMilliseconds);
            return status.RenderCpuFrameTimeMilliseconds == frameTime
                ? status
                : status with { RenderCpuFrameTimeMilliseconds = frameTime };
        }
    }

    /// <summary>
    /// Records what the application head asked the windowing backend for, before any window exists.
    /// The string is the head's own name for the mode, since only it knows which backend it is
    /// talking to.
    /// </summary>
    public static void RecordRequest(string request)
    {
        VideoOutputStatus status = Volatile.Read(ref current);
        Volatile.Write(ref current, status with { Requested = request });
    }

    /// <summary>
    /// Records what the surface being drawn into turned out to be and what the video pipeline
    /// decided to do about it. Called on every draw; only a change is logged.
    /// </summary>
    public static void RecordSurface(
        ISkiaSharpApiLease lease,
        VideoOutputColorSpace target,
        VideoColorTransfer transfer,
        VideoHdrOutputMapping mapping,
        VideoScalingQuality scaling,
        bool hdrPassthrough,
        VideoOutputColorVolume? colorVolume = null,
        bool isProbe = false)
    {
        if (isProbe && Volatile.Read(ref frameSurfaceObserved))
            return;

        if (!isProbe)
            Volatile.Write(ref frameSurfaceObserved, true);

        VideoOutputStatus previous = Volatile.Read(ref current);
        VideoOutputStatus status = previous with
        {
            SurfaceObserved = true,
            SurfaceEncoding = lease.GetSurfaceEncoding(),
            SurfaceColorSpace = lease.GetSurfaceColorSpaceName(),
            SurfaceTransfer = VideoSurfaceTransfers.For(colorVolume),
            SurfaceTransferReported = VideoSurfaceTransfers.Reported(colorVolume) is not null,
            SdrTransfer = transfer == VideoColorTransfer.Bt709 ? VideoSurfaceTransfers.ForBt709() : null,
            SdrReadAsMastered = transfer == VideoColorTransfer.Bt709 &&
                VideoSurfaceTransfers.ForBt709() is null && target.IsExtendedRange() &&
                VideoOutputColorSpaceExtensions.HasHdrHeadroom(colorVolume),
            SurfaceNitsPerUnit = target.IsExtendedRange() ? colorVolume?.SurfaceNitsPerUnit : null,
            TargetMinimumNits = colorVolume?.TargetLuminance?.MinimumNits,
            TargetMaximumNits = colorVolume?.TargetLuminance?.MaximumNits,
            ReferenceWhiteScale = colorVolume?.ReferenceWhiteScale,
            HeadroomRatio = colorVolume?.HeadroomRatio,
            MaximumHeadroomRatio = colorVolume?.MaximumHeadroomRatio,
            ToneMapping = colorVolume?.ToneMapping ?? VideoToneMappingMode.Unknown,
            LuminanceBasis = colorVolume?.LuminanceBasis ?? VideoLuminanceBasis.Unknown,
            Target = target,
            SourceTransfer = transfer,
            OutputNitsPerUnit = colorVolume?.ReferenceWhiteNits,
            ToneMapPeakNits = mapping.ToneMapPeakNits,
            LinearNitsPerUnit = mapping.LinearNitsPerUnit,
            PhysicalPeakNits = mapping.PhysicalPeakNits,
            RelativeLuminance = mapping.RelativeLuminance,
            PlatformToneMapping = mapping.PlatformToneMapping,
            HdrAdaptedToDisplay = transfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg && mapping.AdaptsToDisplay,
            Scaling = scaling,

            // Which filter a newly asked for one turns into is not known until a frame is drawn.
            EffectiveScaling = scaling == previous.Scaling ? previous.EffectiveScaling : null,
            ScalingDeclined = scaling == previous.Scaling ? previous.ScalingDeclined : string.Empty,
            HdrPassthroughEnabled = hdrPassthrough,
        };

        if (status == previous)
            return;

        Volatile.Write(ref current, status);
        Publish(status);
    }

    /// <summary>
    /// Records the filter the picture was actually drawn with, and where that is not the one asked
    /// for, what stopped it. Called on every draw; only a change is logged.
    /// </summary>
    /// <remarks>
    /// A filter carrying its own kernel needs a scene buffer and a program of its own, and a
    /// graphics backend can refuse either. The picture still arrives, resized by something else,
    /// which is a difference in quality that nothing else in the chain would show.
    /// </remarks>
    public static void RecordScaling(VideoScalingQuality effective, string declined)
    {
        VideoOutputStatus previous = Volatile.Read(ref current);
        VideoOutputStatus status = previous with
        {
            EffectiveScaling = effective,
            ScalingDeclined = declined,
        };

        if (status == previous)
            return;

        Volatile.Write(ref current, status);
        Publish(status);
    }

    /// <summary>Records whether the last frame needed a color program at all.</summary>
    public static void RecordColorProgram(bool drawnWithoutShader)
    {
        VideoOutputStatus previous = Volatile.Read(ref current);
        VideoOutputStatus status = previous with
        {
            DrawnWithoutShader = drawnWithoutShader,
            ToneMappingAlgorithm = drawnWithoutShader ? "none" : previous.ToneMappingAlgorithm,
        };

        if (status == previous)
            return;

        Volatile.Write(ref current, status);
        Publish(status);
    }

    public static void RecordToneMapping(string algorithm)
    {
        VideoOutputStatus previous = Volatile.Read(ref current);
        VideoOutputStatus status = previous with { ToneMappingAlgorithm = algorithm };

        if (status == previous)
            return;

        Volatile.Write(ref current, status);
        Publish(status);
    }

    /// <summary>Records the pre-scale graph that contributed to the last frame actually drawn.</summary>
    public static void RecordPreScale(VideoPreScaleStatus? active)
    {
        VideoOutputStatus previous = Volatile.Read(ref current);
        VideoOutputStatus status = previous with { ActivePreScale = active };

        if (status == previous)
            return;

        Volatile.Write(ref current, status);
        Publish(status);
    }

    /// <summary>Records the dynamic HDR path selected for the last frame successfully drawn.</summary>
    public static void RecordDynamicHdr(
        bool dolbyVisionApplied,
        VideoHdr10PlusStatus? hdr10Plus)
    {
        VideoOutputStatus previous = Volatile.Read(ref current);
        VideoOutputStatus status = previous with
        {
            DolbyVisionApplied = dolbyVisionApplied,
            ActiveHdr10Plus = dolbyVisionApplied ? null : hdr10Plus,
        };

        if (status == previous)
            return;

        Volatile.Write(ref current, status);
        Publish(status);
    }

    /// <summary>
    /// Records what the last frame was quantized to on its way out and whether it was dithered
    /// there. Called on every draw; only a change is logged.
    /// </summary>
    /// <remarks>
    /// Worth reporting even when nothing happens: a picture banding on a surface the renderer
    /// believes to be unquantized is exactly the case the panel depth setting exists for, and the
    /// only way to tell it apart from a picture that was already banded is to say which of them the
    /// renderer thought it was looking at.
    /// </remarks>
    public static void RecordDither(int outputBitDepth, bool dithered, bool panelPinned)
    {
        VideoOutputStatus previous = Volatile.Read(ref current);
        VideoOutputStatus status = previous with
        {
            OutputBitDepth = outputBitDepth,
            Dithered = dithered,
            PanelBitDepthPinned = panelPinned,
        };

        if (status == previous)
            return;

        Volatile.Write(ref current, status);
        Publish(status);
    }

    // Worth a line in the log because the answer is a property of the session rather than of the
    // build, so a report from elsewhere is the only way to see what happened there.
    private static void Publish(VideoOutputStatus status)
    {
        double now = MediaTime.Now;
        double previousLogTime = Volatile.Read(ref lastLogTime);
        if (now - previousLogTime < LogIntervalSeconds)
            return;

        string line = status.ToDisplayString();
        if (Volatile.Read(ref lastLogged) == line ||
            Interlocked.CompareExchange(ref lastLogTime, now, previousLogTime) != previousLogTime)
            return;

        if (Interlocked.Exchange(ref lastLogged, line) != line)
            AppLog.Information("Video.Output", line.Replace("\n", "  "));
    }

    /// <summary>
    /// Records wall time spent submitting one video draw through Skia. This deliberately does not
    /// wait for GPU completion: doing so would serialize the render thread and distort playback.
    /// </summary>
    public static void RecordRenderCpuTime(double seconds)
    {
        if (!(seconds > 0) || !double.IsFinite(seconds))
            return;

        double now = MediaTime.Now;
        double sample = seconds * 1000.0;
        double existing = Volatile.Read(ref renderCpuFrameTimeMilliseconds);
        double smoothed = existing > 0 && now - lastRenderSampleTime <= RenderSampleResetSeconds
            ? existing + ((sample - existing) * RenderTimeSmoothingFactor)
            : sample;

        lastRenderSampleTime = now;
        Volatile.Write(ref renderCpuFrameTimeMilliseconds, smoothed);
    }

    private sealed class RendererDiagnostics : IVideoRenderDiagnostics, IVideoToneMappingDiagnostics
    {
        public void RecordScaling(VideoScalingQuality effective, string declined) =>
            VideoOutputDiagnostics.RecordScaling(effective, declined);

        public void RecordColorProgram(bool drawnWithoutShader) =>
            VideoOutputDiagnostics.RecordColorProgram(drawnWithoutShader);

        public void RecordToneMapping(string algorithm) =>
            VideoOutputDiagnostics.RecordToneMapping(algorithm);

        public void RecordPreScale(VideoPreScaleStatus? active) =>
            VideoOutputDiagnostics.RecordPreScale(active);

        public void RecordDynamicHdr(bool dolbyVisionApplied, VideoHdr10PlusStatus? hdr10Plus) =>
            VideoOutputDiagnostics.RecordDynamicHdr(dolbyVisionApplied, hdr10Plus);

        public void RecordDither(int outputBitDepth, bool dithered, bool panelPinned) =>
            VideoOutputDiagnostics.RecordDither(outputBitDepth, dithered, panelPinned);
    }
}

/// <summary>One reading of the color chain. See <see cref="VideoOutputDiagnostics"/>.</summary>
public sealed record VideoOutputStatus
{
    public static VideoOutputStatus Unknown { get; } = new();

    /// <summary>What the application head asked for, or empty where it asked for nothing.</summary>
    public string Requested { get; init; } = string.Empty;

    /// <summary>Whether a surface has been drawn into yet, without which the rest means nothing.</summary>
    public bool SurfaceObserved { get; init; }

    /// <summary>Bit depth and layout the windowing backend negotiated for the surface.</summary>
    public string SurfaceEncoding { get; init; } = string.Empty;

    /// <summary>Color space the surface is read in, which the compositor has to have agreed to.</summary>
    public string SurfaceColorSpace { get; init; } = string.Empty;

    /// <summary>The curve a bounded signal on this surface is shown through.</summary>
    public VideoSurfaceTransfer SurfaceTransfer { get; init; }

    /// <summary>Whether the platform reported <see cref="SurfaceTransfer"/> rather than it being assumed.</summary>
    public bool SurfaceTransferReported { get; init; }

    public VideoSurfaceTransfer? SdrTransfer { get; init; }

    /// <summary>
    /// Whether a BT.709 SDR signal is read as its BT.1886 master rather than as the surface's own
    /// curve, which is what the display having headroom above diffuse white calls for.
    /// </summary>
    public bool SdrReadAsMastered { get; init; }

    /// <summary>Absolute input scale, when supplied by the surface contract.</summary>
    public double? SurfaceNitsPerUnit { get; init; }

    /// <summary>The darkest luminance the platform reports that the target can reproduce.</summary>
    public double? TargetMinimumNits { get; init; }

    public double? TargetMaximumNits { get; init; }

    public double? ReferenceWhiteScale { get; init; }

    public double? HeadroomRatio { get; init; }

    public double? MaximumHeadroomRatio { get; init; }

    public VideoToneMappingMode ToneMapping { get; init; }

    public VideoLuminanceBasis LuminanceBasis { get; init; }

    public bool RelativeLuminance { get; init; }

    public bool PlatformToneMapping { get; init; }

    /// <summary>What the video pipeline is producing for it.</summary>
    public VideoOutputColorSpace Target { get; init; }

    /// <summary>Transfer function of the picture last drawn.</summary>
    public VideoColorTransfer SourceTransfer { get; init; }

    /// <summary>Reported reference-white nits, qualified by the luminance basis.</summary>
    public double? OutputNitsPerUnit { get; init; }

    /// <summary>Peak in the source-reference luminance domain used by the HDR tone mapper.</summary>
    public double ToneMapPeakNits { get; init; }

    /// <summary>
    /// Source-domain nits normalized to diffuse white before the surface white scale is applied.
    /// </summary>
    public double LinearNitsPerUnit { get; init; }

    /// <summary>Output peak estimated from display-reported luminance, when available.</summary>
    public double? PhysicalPeakNits { get; init; }

    /// <summary>Whether source highlights were mapped to the target display volume.</summary>
    public bool HdrAdaptedToDisplay { get; init; }

    public string ToneMappingAlgorithm { get; init; } = "not reported";

    /// <summary>
    /// Filter the picture is resized with. Reported next to the color chain because the resampling
    /// filters take a different route through it than the plain ones.
    /// </summary>
    public VideoScalingQuality Scaling { get; init; }

    /// <summary>Filter the last frame was actually resized with. See <see cref="ScalingDeclined"/>.</summary>
    public VideoScalingQuality? EffectiveScaling { get; init; }

    /// <summary>What stopped <see cref="Scaling"/> from being the filter that ran, if anything.</summary>
    public string ScalingDeclined { get; init; } = string.Empty;

    /// <summary>Pre-scale graph that contributed to the last frame, or null when none ran.</summary>
    public VideoPreScaleStatus? ActivePreScale { get; init; }

    /// <summary>Whether the last frame drawn used its Dolby Vision metadata.</summary>
    public bool DolbyVisionApplied { get; init; }

    /// <summary>HDR10+ metadata and mapping selected for the last frame, if any.</summary>
    public VideoHdr10PlusStatus? ActiveHdr10Plus { get; init; }

    /// <summary>
    /// Whether the picture reached the surface without a color program of the player's own, which
    /// is what the low-power path is trying to achieve and the only way to see that it did.
    /// </summary>
    public bool DrawnWithoutShader { get; init; }

    /// <summary>The one stage of the chain the user decides, rather than the machine.</summary>
    public bool HdrPassthroughEnabled { get; init; } = true;

    /// <summary>What the last frame is quantized to, or 0 where nothing in reach quantizes it.</summary>
    public int OutputBitDepth { get; init; }

    /// <summary>Whether the last frame was dithered towards <see cref="OutputBitDepth"/>.</summary>
    public bool Dithered { get; init; }

    /// <summary>Whether the depth came from the user rather than from the surface.</summary>
    public bool PanelBitDepthPinned { get; init; }

    /// <summary>Recent wall time spent on the CPU side of one video draw.</summary>
    public double RenderCpuFrameTimeMilliseconds { get; init; }

    /// <summary>Whether the picture is being shown as it was mastered rather than mapped to fit.</summary>
    public bool IsHdrPassthrough =>
        HdrPassthroughEnabled &&
        !HdrAdaptedToDisplay &&
        !RelativeLuminance &&
        !PlatformToneMapping &&
        Target.IsExtendedRange() &&
        SourceTransfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg;

    public string ToDisplayString()
    {
        string requested = string.IsNullOrEmpty(Requested) ? "nothing" : Requested;
        var text = new DiagnosticsText();

        if (!SurfaceObserved)
            return text.Add("Surface", $"asked {requested}  not drawn into yet").ToString();

        string surfaceTransfer = Target.IsExtendedRange()
            ? "linear light"
            : $"shown through {TransferName(SurfaceTransfer)} ({(SurfaceTransferReported ? "reported" : "assumed")})";
        text.Add("Surface", $"asked {requested}  got {SurfaceEncoding}/{SurfaceColorSpace}  {surfaceTransfer}");

        bool hdr = SourceTransfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg;
        string relativeMapping = !HdrAdaptedToDisplay
            ? "no client highlight compression"
            : ToneMappingAlgorithm switch
            {
                "none" => $"no tone curve (source fits within {ToneMapPeakNits / LinearNitsPerUnit:0.###}x white)",
                "not reported" => $"tone/gamut mapped to {ToneMapPeakNits / LinearNitsPerUnit:0.###}x white",
                var algorithm => $"{algorithm} tone/gamut mapped to {ToneMapPeakNits / LinearNitsPerUnit:0.###}x white",
            };
        string video = (hdr, Target.IsExtendedRange()) switch
        {
            (true, true) when PlatformToneMapping =>
                $"{TargetName}, platform tone mapping, source highlights preserved",
            (true, _) when RelativeLuminance =>
                $"{TargetName}, reference-white-relative HDR, {relativeMapping}",
            (true, _) => $"{TargetName}, absolute HDR ({LuminanceBasis}), " +
                (HdrAdaptedToDisplay ? $"tone/gamut mapped to {ToneMapPeakNits:0} nits" : "no client highlight compression"),
            (_, true) => $"{TargetName} ({NitsText})",
            _ => "sRGB",
        };

        // Said outright, because a wide gamut surface that is being tone mapped into anyway looks
        // like a stage that failed rather than one that was declined.
        if (!HdrPassthroughEnabled)
            video += ", advanced color output off";

        if (!hdr && SdrTransfer is { } sourceTransfer)
            video += $", SDR read through {TransferName(sourceTransfer)}";
        else if (SdrReadAsMastered && !hdr)
            video += ", SDR read as BT.1886";

        video += (Dithered, OutputBitDepth, PanelBitDepthPinned) switch
        {
            (true, var depth, true) => $", dithered to {depth}-bit panel",
            (true, var depth, false) => $", dithered to {depth}-bit",
            (false, > 0, true) => $", no dither ({OutputBitDepth}-bit panel)",
            (false, > 0, false) => $", no dither ({OutputBitDepth}-bit surface)",
            _ => ", no dither",
        };

        text.Add("Output", video);
        if (Target.IsExtendedRange())
            text.Add("HDR contract", $"mapping {ToneMapping}, current headroom {RatioText(HeadroomRatio)}, " +
                $"potential {RatioText(MaximumHeadroomRatio)}, white scale {RatioText(ReferenceWhiteScale)}, " +
                $"luminance {LuminanceBasis}; {NitsText}");
        if (DolbyVisionApplied)
        {
            text.Add("Dynamic HDR", "Dolby Vision metadata applied");
        }
        else if (ActiveHdr10Plus is { } hdr10Plus)
        {
            string windows = hdr10Plus.WindowCount == 1
                ? "1 window"
                : $"{hdr10Plus.WindowCount} windows";
            string average = hdr10Plus.SceneAverageNits > 0
                ? $", average {hdr10Plus.SceneAverageNits:0}"
                : string.Empty;
            string mapping = hdr10Plus.Mapping switch
            {
                VideoHdr10PlusMapping.St2094_40 => "ST 2094-40 applied",
                VideoHdr10PlusMapping.SceneAdaptiveSpline => "scene-adaptive spline applied",
                _ => "metadata read, no dynamic mapping applied",
            };
            text.Add(
                "Dynamic HDR",
                $"HDR10+ v{hdr10Plus.ApplicationVersion}, {windows}, target " +
                $"{hdr10Plus.MetadataTargetNits:0} nits, scene peak " +
                $"{hdr10Plus.ScenePeakNits:0}{average} nits, " +
                $"{hdr10Plus.AnchorCount} anchors, {mapping}");
        }
        if (ActivePreScale is { } preScale)
        {
            string passes = preScale.PassCount == 1
                ? "1 shader pass"
                : $"{preScale.PassCount} shader passes";
            text.Add(
                "Pre-scale",
                $"{preScale.Name}  {preScale.InputWidth}x{preScale.InputHeight} -> " +
                $"{preScale.OutputWidth}x{preScale.OutputHeight}  {passes}");
        }
        text.Add("Scaling", ScalingName);
        text.Add("Low Power", DrawnWithoutShader ? "Yes - Skia drew the picture" : "No - GPU video pipeline");
        return text.ToString();
    }

    private string ScalingName => EffectiveScaling is { } effective && effective != Scaling
        ? $"{Scaling} declined, drawn with {effective}" +
            (ScalingDeclined.Length > 0 ? $": {ScalingDeclined}" : string.Empty)
        : Scaling.ToString();

    private string TargetName =>
        Target == VideoOutputColorSpace.ScRgbLinear ? "scRGB linear" : "sRGB";

    private string NitsText => OutputNitsPerUnit is { } white
        ? $"{LuminanceBasis} white {white:0.##} nits, target peak " +
            (TargetMaximumNits is { } peak ? $"{peak:0.##} nits" : "unknown")
        : "absolute white unknown";

    private static string RatioText(double? value) => value is { } ratio ? $"{ratio:0.###}x" : "unknown";

    private static string TransferName(VideoSurfaceTransfer transfer) => transfer.Kind == VideoSurfaceTransferKind.Power
        ? $"gamma {transfer.Exponent:0.###}"
        : "the sRGB curve";
}
