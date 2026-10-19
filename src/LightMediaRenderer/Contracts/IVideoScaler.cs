using System;
using System.Collections.Generic;
using SkiaSharp;

namespace LightMediaRenderer.Contracts;

/// <summary>What a filter is being asked to do, in the picture's own pixel coordinates.</summary>
public readonly record struct VideoScalerContext(
    int SourceWidth,
    int SourceHeight,
    double DestinationWidth,
    double DestinationHeight)
{
    public double ScaleX => SourceWidth > 0 ? DestinationWidth / SourceWidth : 1;

    public double ScaleY => SourceHeight > 0 ? DestinationHeight / SourceHeight : 1;
}

/// <summary>Source properties available when deciding whether a pre-scale stage should run.</summary>
public readonly record struct VideoScaleStageContext(
    VideoScalerContext Scaling,
    VideoColorTransfer Transfer);

/// <summary>
/// An optional nonlinear RGB pass that materializes a new source image before transfer decoding
/// and before the selected scaler fits scene light to the output. A stage may preserve the source
/// size or return a fixed upscale such as 2x.
/// </summary>
public interface IVideoScaleStage
{
    string Name { get; }

    /// <summary>Number of shader passes scheduled by this stage.</summary>
    int PassCount => 1;

    /// <summary>Whether this stage understands the frame's signal domain.</summary>
    bool Supports(in VideoScaleStageContext context) => true;

    /// <summary>Whether the stage should run for this source-to-destination transform.</summary>
    bool ShouldRun(in VideoScaleStageContext context) => Supports(context);

    /// <summary>Resolves a selector to the concrete stage for this frame.</summary>
    IVideoScaleStage? Resolve(in VideoScaleStageContext context) =>
        ShouldRun(context) ? this : null;

    /// <summary>Returns a staged nonlinear RGB image owned by the caller, or null with a reason.</summary>
    SKImage? Render(
        SKImage source,
        GRContext? grContext,
        in VideoScaleStageContext context,
        out string declined);
}

/// <summary>
/// One way of resizing the picture. Implementations either hand the work to Skia's own sampler or
/// carry the SkSL that does it.
/// </summary>
/// <remarks>
/// A filter reads and writes scene light: linear, in the source's own primaries, non-negative, and
/// already tone mapped. Everything that could make a sample negative or out of range happens after
/// the filter has run, so an implementation never has to defend against values it cannot represent
/// and never needs the picture rescaled to fit an interval.
/// </remarks>
public interface IVideoScaler
{
    VideoScalingQuality Quality { get; }

    /// <summary>An independent nonlinear RGB stage that runs before this scaler, when present.</summary>
    IVideoScaleStage? PreScaleStage => null;

    /// <summary>
    /// Whether the picture has to be converted into a scene buffer before this filter runs. False
    /// lets the filter run on the decoder's planes as they are sampled, in a single pass.
    /// </summary>
    bool RequiresSceneBuffer { get; }

    /// <summary>How Skia is asked to sample the image this filter reads.</summary>
    SKSamplingOptions Sampling { get; }

    /// <summary>
    /// SkSL defining <c>float3 scaleScene(float2 coord)</c> over the <c>source</c> child, or null
    /// to read one sample and leave the filtering to <see cref="Sampling"/>.
    /// </summary>
    string? Function { get; }

    /// <summary>Names of the shader uniforms read by <see cref="Function"/>.</summary>
    IReadOnlyList<string> Uniforms => [];

    void AddUniforms(IDictionary<string, float[]> uniforms, in VideoScalerContext context)
    {
    }
}

