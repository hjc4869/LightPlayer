using System;
using System.Collections.Generic;
using LightMediaRenderer.Shaders;
using SkiaSharp;

namespace LightMediaRenderer.Contracts;

/// <summary>One frame, an optional pre-scale graph, and one final display filter.</summary>
public readonly record struct VideoRenderRequest(
    SKCanvas Canvas,
    GRContext? GrContext,
    VideoFrame Frame,
    SKRect Destination,
    byte Alpha,
    IVideoScaler Scaler,
    VideoOutputColorSpace Target,
    bool HdrPassthrough,
    VideoOutputColorVolume? ColorVolume,
    int PanelBitDepth = 0,
    VideoDebandingQuality Debanding = VideoDebandingQuality.Off)
{
    /// <summary>Where the planes come from, or null to read the frame's own buffer.</summary>
    public IVideoPlaneSource? PlaneSource { get; init; }

    /// <summary>An already interleaved picture, for frames that never had separate planes.</summary>
    public SKImage? PackedImage { get; init; }

    public VideoDeinterlaceHistory? DeinterlaceHistory { get; init; }

    public long FrameSequence { get; init; }

    public SKImage? SampledImage { get; init; }

    public SKImage? PreviousSamples { get; init; }

    public VideoScalingQuality? UpscalingQuality { get; init; }

    public VideoScalingQuality? DownscalingQuality { get; init; }

    /// <summary>HDR tone curves with optional display targets; null uses the renderer defaults.</summary>
    public VideoSdrToneMappingOptions? SdrToneMapping { get; init; }

    public IVideoFrameImageCache? ImageCache { get; init; }

    public IVideoRenderDiagnostics? Diagnostics { get; init; }

    public SKRectI? SceneBounds { get; init; }

    public SKRectI? SamplingBounds { get; init; }

    public bool RetainFullPlanes { get; init; }

    public SKRect? PhotoSourceBounds { get; init; }

    public SKSizeI PhotoSourceSize { get; init; }

    public SKRect? DeviceViewport { get; init; }

    /// <summary>
    /// How the samples this draw reads are laid out. A packed image of a frame that has its own
    /// planes keeps the frame's depth, so a deep RGB source is still known to be deep.
    /// </summary>
    public VideoPlanarLayout Layout => PackedImage is null
        ? Frame.Layout
        : Frame.ExternalTextureColorModel == VideoExternalTextureColorModel.Yuv
            ? VideoPlanarLayout.NormalizedYuv(Frame.ExternalTextureBitDepth)
            : Frame.Format == VideoPixelFormat.ExternalTexture
                ? VideoPlanarLayout.Packed
                : Frame.Layout;
}

