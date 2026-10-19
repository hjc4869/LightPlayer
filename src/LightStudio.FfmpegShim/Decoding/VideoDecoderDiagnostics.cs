using System;
using System.Collections.Generic;
using System.Text;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>Route decoded pixels take from the decoder to the renderer.</summary>
public enum VideoFrameDeliveryPath
{
    Unknown,

    /// <summary>Software decode; the planes are handed over as decoded and converted by the shader.</summary>
    SoftwarePlanar,

    /// <summary>Software decode; swscale flattens to BGRA because the renderer cannot sample the layout.</summary>
    SoftwareConverted,

    /// <summary>Hardware decode; the surface itself is handed over and never read back.</summary>
    HardwareSurface,

    /// <summary>Hardware decode; the planes are read back into a pooled buffer, the shader converts.</summary>
    HardwareReadback,

    /// <summary>Hardware decode; read back and then flattened to BGRA by swscale.</summary>
    HardwareConverted,

    /// <summary>The decoder wrote straight into a texture the renderer owns.</summary>
    ExternalTexture,

    /// <summary>Copied planes from a backend that does not report hardware usage.</summary>
    CopiedPlanar,

    /// <summary>Copied, converted pixels from a backend that does not report hardware usage.</summary>
    CopiedConverted,
}

/// <summary>
/// What a video decoder actually ended up doing, as opposed to what it was asked to do. Detailed
/// enough to name the exact configuration a playback problem was seen under.
/// </summary>
/// <remarks>
/// A snapshot: decoders publish a new one when the configuration changes rather than measuring on
/// demand, so reading this never touches decoder state from the polling thread.
/// </remarks>
public sealed record VideoDecoderDiagnostics
{
    /// <summary>Reported while no video stream is open.</summary>
    public static VideoDecoderDiagnostics None { get; } = new();

    /// <summary>Which decoding backend this is, e.g. <c>libavcodec</c> or <c>WebCodecs</c>.</summary>
    public string Implementation { get; init; } = string.Empty;

    /// <summary>Codec of the stream, e.g. <c>h264</c>.</summary>
    public string CodecName { get; init; } = string.Empty;

    /// <summary>The particular decoder chosen for it, which may be a hardware wrapper.</summary>
    public string DecoderName { get; init; } = string.Empty;

    public string DecoderDescription { get; init; } = string.Empty;

    public string Profile { get; init; } = string.Empty;

    /// <summary>Codec level in its own units, or a negative value when the stream declares none.</summary>
    public int Level { get; init; } = -1;

    public int Width { get; init; }

    public int Height { get; init; }

    public double PixelAspectRatio { get; init; } = 1.0;

    /// <summary>Degrees the picture has to be turned clockwise for display: 0, 90, 180 or 270.</summary>
    public int Rotation { get; init; }

    /// <summary>Whether the picture is mirrored left to right before <see cref="Rotation"/>.</summary>
    public bool MirrorHorizontally { get; init; }

    /// <summary>Bits per luma sample of the decoded picture, 0 before the first frame.</summary>
    public int BitDepth { get; init; }

    public bool IsHardwareAccelerated { get; init; }

    /// <summary>False when the backend does not expose actual hardware usage.</summary>
    public bool IsHardwareAccelerationKnown { get; init; } = true;

    /// <summary>Requested device type or acceleration preference, or empty for platform defaults.</summary>
    public string RequestedHardwareDeviceType { get; init; } = string.Empty;

    /// <summary>Device type actually in use, empty when decoding in software.</summary>
    public string HardwareDeviceType { get; init; } = string.Empty;

    /// <summary>Device types that were tried, in the order they were tried.</summary>
    public IReadOnlyList<string> HardwareCandidates { get; init; } = Array.Empty<string>();

    /// <summary>Hardware selection details, including why hardware is unavailable or its use is unknown.</summary>
    public string HardwareStatus { get; init; } = string.Empty;

    /// <summary>Pixel format the hardware decoder negotiated, e.g. <c>vaapi</c>.</summary>
    public string HardwarePixelFormat { get; init; } = string.Empty;

    /// <summary>Layout the samples are actually in, e.g. <c>nv12</c>: a surface's or a software decoder's.</summary>
    public string SamplePixelFormat { get; init; } = string.Empty;

    /// <summary>Format the frames reach the renderer in, null before the first frame.</summary>
    public VideoPixelFormat? OutputFormat { get; init; }

    /// <summary>Kind of surface this decoder can produce, <see cref="VideoSurfaceKind.None"/> when it cannot.</summary>
    public VideoSurfaceKind SurfaceKind { get; init; }

    /// <summary>Kind of surface the renderer has been found able to import.</summary>
    public VideoSurfaceKind RendererSurfaceKind { get; init; }

    /// <summary>Widest sample container the renderer can sample; 0 forces everything through swscale.</summary>
    public int RendererMaxPlanarBytesPerSample { get; init; }

    /// <summary>Whether decoder and renderer agree on a surface, so frames can skip the readback.</summary>
    public bool ZeroCopyAvailable => SurfaceKind != VideoSurfaceKind.None && RendererSurfaceKind == SurfaceKind;

    /// <summary>Whether the last frame actually took that route.</summary>
    public bool ZeroCopyActive { get; init; }

    /// <summary>
    /// Size the device scaler is resizing frames to before they leave the decoder, or zero while
    /// the picture is handed over at its decoded size and the display filter resizes it.
    /// </summary>
    public (int Width, int Height) DeviceScaledSize { get; init; }

    /// <summary>Why the device is not resizing frames, or empty when the question does not arise.</summary>
    public string DeviceScalingStatus { get; init; } = string.Empty;

    /// <summary>
    /// A decoder's own account of why frames are or are not going straight to the renderer, for the
    /// cases the fields above cannot express (the browser, where it depends on reaching the
    /// renderer's graphics context at all).
    /// </summary>
    public string ZeroCopyStatus { get; init; } = string.Empty;

    public VideoFrameDeliveryPath DeliveryPath { get; init; }

    /// <summary>Recent decoder work per output frame, or 0 while there is not enough data.</summary>
    public double DecodeFrameTimeMilliseconds { get; init; }

    /// <summary>Recent packet-submission to decoded-frame delay, or 0 while it is unknown.</summary>
    public double EstimatedDecodeLatencyMilliseconds { get; init; }

    public int ThreadCount { get; init; }

    /// <summary>Threading libavcodec was asked for, e.g. <c>frame+slice</c>.</summary>
    public string ThreadType { get; init; } = string.Empty;

    public VideoColorSpace ColorSpace { get; init; }

    public VideoColorPrimaries ColorPrimaries { get; init; }

    public VideoColorTransfer ColorTransfer { get; init; }

    public double HdrPeakLuminance { get; init; }

    public VideoColorRange ColorRange { get; init; }

    public VideoChromaLocation ChromaLocation { get; init; }

    /// <summary>Dolby Vision profile declared by the stream, or zero when none was declared.</summary>
    public int DolbyVisionProfile { get; init; }

    /// <summary>Dolby Vision level declared by the stream, or zero when none was declared.</summary>
    public int DolbyVisionLevel { get; init; }

    /// <summary>Base-layer signal compatibility identifier used to qualify profile 8.</summary>
    public int DolbyVisionCompatibilityId { get; init; }

    /// <summary>
    /// Whether the last delivered frame carries usable RPU metadata. Null before the first
    /// frame, false when a Dolby Vision stream is being shown through its compatible base layer.
    /// </summary>
    public bool? DolbyVisionActive { get; init; }

    /// <summary>Whether the last delivered Dolby Vision frame included a decoded FEL residual.</summary>
    public bool? DolbyVisionEnhancementLayerActive { get; init; }

    /// <summary>HDR10+ application version carried by the last delivered frame.</summary>
    public int Hdr10PlusApplicationVersion { get; init; }

    public int Hdr10PlusWindowCount { get; init; }

    public double Hdr10PlusTargetLuminance { get; init; }

    public double Hdr10PlusSceneAverageLuminance { get; init; }

    public int Hdr10PlusAnchorCount { get; init; }

    public bool Hdr10PlusToneMapping { get; init; }

    /// <summary>Whether the last delivered frame carried valid HDR10+ metadata.</summary>
    public bool? Hdr10PlusActive { get; init; }

    /// <summary>Whether a hardware decoder failed mid-stream and was replaced by a software one.</summary>
    public bool SoftwareFallbackOccurred { get; init; }

    public string ToDisplayString()
    {
        var text = new DiagnosticsText();

        if (string.IsNullOrEmpty(Implementation))
            return text.Add("Decoder", "none").ToString();

        var line = new StringBuilder($"{Implementation} / {DecoderName}");
        if (!string.IsNullOrEmpty(CodecName) && !string.Equals(CodecName, DecoderName, StringComparison.Ordinal))
            line.Append($" (codec {CodecName})");
        if (!string.IsNullOrEmpty(Profile))
            line.Append($"  profile {Profile}");
        if (Level >= 0)
            line.Append($"  level {Level}");
        text.Add("Decoder", line.ToString());

        if (!string.IsNullOrEmpty(DecoderDescription))
            text.AddContinued(DecoderDescription);

        line.Clear().Append($"{Width}x{Height}");
        if (Math.Abs(PixelAspectRatio - 1.0) > 0.001)
            line.Append($"  par {PixelAspectRatio:0.###}");
        if (Rotation != 0)
            line.Append($"  rotate {Rotation}\u00b0");
        if (MirrorHorizontally)
            line.Append("  mirrored");
        if (BitDepth > 0)
            line.Append($"  {BitDepth}-bit");
        line.Append($"  chroma {ChromaLocation}");
        text.Add("Picture", line.ToString());

        line.Clear().Append($"{ColorSpace}/{ColorPrimaries}  {ColorTransfer}  {ColorRange}");
        if (DolbyVisionProfile > 0 || DolbyVisionActive == true)
            line.Append($"  {DolbyVisionName}");
        if (Hdr10PlusActive == true)
            line.Append($"  HDR10+ v{Hdr10PlusApplicationVersion} available");
        if (ColorTransfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg && HdrPeakLuminance > 0)
            line.Append($"  peak {HdrPeakLuminance:0} nits");
        text.Add("Color", line.ToString());

        line.Clear();
        if (!IsHardwareAccelerationKnown)
        {
            line.Append("actual unknown");
            if (!string.IsNullOrEmpty(HardwareStatus))
                line.Append($" - {HardwareStatus}");
        }
        else if (IsHardwareAccelerated)
        {
            line.Append(HardwareDeviceType);
            if (!string.IsNullOrEmpty(HardwarePixelFormat))
                line.Append($"  fmt {HardwarePixelFormat}");
        }
        else
        {
            line.Append("software");
            if (!string.IsNullOrEmpty(HardwareStatus))
                line.Append($" - {HardwareStatus}");
        }

        line.Append(string.IsNullOrEmpty(RequestedHardwareDeviceType)
            ? "  asked auto"
            : $"  asked {RequestedHardwareDeviceType}");
        if (HardwareCandidates.Count > 0)
            line.Append($"  tried {string.Join(", ", HardwareCandidates)}");
        else if (!SoftwareFallbackOccurred)
            line.Append("  tried none");
        if (SoftwareFallbackOccurred)
            line.Append("  (fell back after a hardware decoder failure)");
        text.Add("Hardware", line.ToString());

        text.Add("Zero copy", DeliveryPath switch
        {
            VideoFrameDeliveryPath.ExternalTexture => "yes - decoded into a renderer-owned texture",
            _ when !string.IsNullOrEmpty(ZeroCopyStatus) => $"no - {ZeroCopyStatus}",
            _ when SurfaceKind == VideoSurfaceKind.None => "no - decoder makes pixels, not surfaces",
            _ when !ZeroCopyAvailable => $"no - {SurfaceKind} surfaces, renderer imports {RendererSurfaceKind}",
            _ => ZeroCopyActive ? $"yes, in use ({SurfaceKind})" : $"ready, not in use ({SurfaceKind})",
        });

        line.Clear().Append(DeliveryPath.ToString());
        if (!string.IsNullOrEmpty(SamplePixelFormat))
            line.Append($"  samples {SamplePixelFormat}");
        if (OutputFormat is { } output)
            line.Append($" -> {output}");
        text.Add("Frame path", line.ToString());

        text.Add("Hw scale", DeviceScaledSize switch
        {
            (> 0, > 0) => $"{DeviceScaledSize.Width}x{DeviceScaledSize.Height} on {HardwareDeviceType}",
            _ when !string.IsNullOrEmpty(DeviceScalingStatus) => $"no - {DeviceScalingStatus}",
            _ => "no - the display filter resizes",
        });

        line.Clear().Append($"surfaces {RendererSurfaceKind}  planar max {RendererMaxPlanarBytesPerSample} B/sample");
        text.Add("Render caps", line.ToString());

        line.Clear();
        if (ThreadCount > 0)
            line.Append($"threads {ThreadCount}");
        if (!string.IsNullOrEmpty(ThreadType))
            line.Append($"  requested {ThreadType}");
        if (line.Length > 0)
            text.Add("Decode cfg", line.ToString());

        return text.ToString();
    }

    private string DolbyVisionName
    {
        get
        {
            string profile = DolbyVisionProfile switch
            {
                8 when DolbyVisionCompatibilityId > 0 =>
                    $" P8.{DolbyVisionCompatibilityId}",
                > 0 => $" P{DolbyVisionProfile}",
                _ => string.Empty,
            };
            string level = DolbyVisionLevel > 0 ? $" L{DolbyVisionLevel}" : string.Empty;
            string state = DolbyVisionActive switch
            {
                true when DolbyVisionEnhancementLayerActive == true => " FEL available",
                true => " available",
                false => " base layer only",
                null => " declared, awaiting frame",
            };
            return $"DV{profile}{level}{state}";
        }
    }
}
