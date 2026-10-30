using System;
using System.Collections.Generic;
using LightMediaRenderer.Contracts;
using SkiaSharp;

namespace LightMediaRenderer.Shaders;

/// <summary>
/// The filters the renderer can be asked for. Registering replaces the implementation for a
/// quality, which is how an algorithm written in SkSL is introduced without touching the pipeline.
/// </summary>
public static class VideoScalers
{
    private static readonly object sync = new();
    private static readonly Dictionary<VideoScalingQuality, IVideoScaler> scalers = new()
    {
        [VideoScalingQuality.NearestNeighbour] = new SkiaVideoScaler(
            VideoScalingQuality.NearestNeighbour,
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
            RequiresSceneBuffer: false),
        [VideoScalingQuality.Bilinear] = new SkiaVideoScaler(
            VideoScalingQuality.Bilinear,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            RequiresSceneBuffer: false),

        // A mip chain built per plane is not the chain the picture would get, so this one only ever
        // runs on the scene buffer. Skia still builds and samples the chain.
        [VideoScalingQuality.Trilinear] = new SkiaVideoScaler(
            VideoScalingQuality.Trilinear,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
            RequiresSceneBuffer: true),
        [VideoScalingQuality.Mitchell] = new CubicVideoScaler(VideoScalingQuality.Mitchell, 1 / 3f, 1 / 3f),
        [VideoScalingQuality.CatmullRom] = new CubicVideoScaler(VideoScalingQuality.CatmullRom, 0, 0.5f),
        [VideoScalingQuality.Lanczos3] = new LanczosVideoScaler(),
        [VideoScalingQuality.Jinc] = new JincVideoScaler(),
    };

    public static bool SupportsPhotoSceneCaching(IVideoScaler scaler) =>
        scaler is SkiaVideoScaler or CubicVideoScaler or LanczosVideoScaler or JincVideoScaler;

    /// <summary>Whether the filter mixes neighboring samples rather than picking one.</summary>
    internal static bool Blends(IVideoScaler scaler) =>
        scaler.Function is not null ||
        scaler.Sampling.Filter != SKFilterMode.Nearest ||
        scaler.Sampling.Mipmap != SKMipmapMode.None;

    public static IVideoScaler For(VideoScalingQuality quality)
    {
        lock (sync)
        {
            return scalers.TryGetValue(quality, out IVideoScaler? scaler)
                ? scaler
                : scalers[VideoScalingQuality.Bilinear];
        }
    }

    /// <summary>
    /// Chooses the scaler for the picture's physical output size. A mixed transform uses the
    /// upscaler so a downsampling filter cannot blur the axis that is being enlarged.
    /// </summary>
    public static VideoScalingQuality SelectQuality(
        VideoScalingQuality upscaling,
        VideoScalingQuality downscaling,
        int sourceWidth,
        int sourceHeight,
        double destinationWidth,
        double destinationHeight)
    {
        bool grows = destinationWidth > sourceWidth || destinationHeight > sourceHeight;
        bool shrinks = destinationWidth < sourceWidth || destinationHeight < sourceHeight;
        return shrinks && !grows ? downscaling : upscaling;
    }

    public static void Register(IVideoScaler scaler)
    {
        ArgumentNullException.ThrowIfNull(scaler);
        lock (sync)
        {
            scalers[scaler.Quality] = scaler;
        }
    }

    /// <summary>Runs <paramref name="stage"/> before an otherwise unchanged display scaler.</summary>
    public static IVideoScaler WithPreScaleStage(IVideoScaler scaler, IVideoScaleStage stage)
    {
        ArgumentNullException.ThrowIfNull(scaler);
        ArgumentNullException.ThrowIfNull(stage);
        return new StagedVideoScaler(scaler, stage);
    }

    internal static IVideoScaler WithoutPreScaleStage(IVideoScaler scaler)
    {
        while (scaler is StagedVideoScaler staged)
            scaler = staged.Inner;
        return scaler;
    }

    /// <summary>
    /// The filter to use where Skia draws the picture itself, which is the same one unless it
    /// carries a kernel.
    /// </summary>
    /// <remarks>
    /// A filter written in SkSL asks Skia for unfiltered taps because it weights them itself, so
    /// handing that sampling to a plain draw resizes the picture with no filter at all.
    /// </remarks>
    public static IVideoScaler ForDirectDraw(IVideoScaler scaler)
    {
        ArgumentNullException.ThrowIfNull(scaler);
        if (scaler is StagedVideoScaler staged)
            return ForDirectDraw(staged.Inner);
        return scaler.Function is null ? scaler : For(VideoScalingQuality.Bilinear);
    }

    private sealed record StagedVideoScaler(IVideoScaler Inner, IVideoScaleStage Stage) : IVideoScaler
    {
        public VideoScalingQuality Quality => Inner.Quality;

        public IVideoScaleStage PreScaleStage => Stage;

        public bool RequiresSceneBuffer => true;

        public SKSamplingOptions Sampling => Inner.Sampling;

        public string? Function => Inner.Function;

        public IReadOnlyList<string> Uniforms => Inner.Uniforms;

        public void AddUniforms(IDictionary<string, float[]> uniforms, in VideoScalerContext context) =>
            Inner.AddUniforms(uniforms, context);
    }
}

/// <summary>A filter Skia's own sampler performs, which is one texture read in the shader.</summary>
internal sealed record SkiaVideoScaler(
    VideoScalingQuality Quality,
    SKSamplingOptions Sampling,
    bool RequiresSceneBuffer) : IVideoScaler
{
    public string? Function => null;
}

/// <summary>
/// Separable Mitchell-Netravali cubic, evaluated as sixteen taps in the fragment program.
/// </summary>
/// <remarks>
/// Skia's own cubic saturates its result, which is correct for a picture but not for scene light:
/// it would clip both the highlights above the surface's reference white and, once the picture
/// reaches the display primaries, the negative components that carry colors outside them. Taking
/// the taps here also keeps the arithmetic in the fragment program's own precision rather than in
/// whatever the intermediate texture can hold.
/// </remarks>
internal sealed record CubicVideoScaler(VideoScalingQuality Quality, float B, float C) : IVideoScaler
{
    public bool RequiresSceneBuffer => true;

    // The kernel does its own weighting, so the taps have to arrive unfiltered.
    public SKSamplingOptions Sampling { get; } = new(SKFilterMode.Nearest, SKMipmapMode.None);

    public IReadOnlyList<string> Uniforms { get; } = [VideoShaderUniforms.ScalerParams];

    public string Function => """
        float cubicKernel(float distance) {
            float b = scalerParams.x;
            float c = scalerParams.y;
            float x = abs(distance);
            float x2 = x * x;
            float x3 = x2 * x;

            if (x < 1.0) {
                return ((12.0 - 9.0 * b - 6.0 * c) * x3 +
                        (-18.0 + 12.0 * b + 6.0 * c) * x2 +
                        (6.0 - 2.0 * b)) / 6.0;
            }

            if (x < 2.0) {
                return ((-b - 6.0 * c) * x3 +
                        (6.0 * b + 30.0 * c) * x2 +
                        (-12.0 * b - 48.0 * c) * x +
                        (8.0 * b + 24.0 * c)) / 6.0;
            }

            return 0.0;
        }

        float3 scaleScene(float2 coord) {
            float2 base = floor(coord - 0.5) + 0.5;
            float2 offset = coord - base;
            float3 total = float3(0.0);
            float weight = 0.0;

            for (int row = -1; row <= 2; ++row) {
                float rowWeight = cubicKernel(float(row) - offset.y);
                for (int column = -1; column <= 2; ++column) {
                    float tapWeight = rowWeight * cubicKernel(float(column) - offset.x);
                    float2 tap = base + float2(float(column), float(row));
                    total += tapWeight * float4(source.eval(tap)).rgb;
                    weight += tapWeight;
                }
            }

            // The kernel sums to one, so this only matters to a windowed filter reusing the shape.
            return total / max(weight, 1e-6);
        }
        """;

    public void AddUniforms(IDictionary<string, float[]> uniforms, in VideoScalerContext context) =>
        uniforms[VideoShaderUniforms.ScalerParams] = [B, C, 0, 0];
}

/// <summary>
/// Separable Lanczos over three lobes each side: thirty-six taps in the fragment program.
/// </summary>
/// <remarks>
/// The kernel is fixed in the source's own pixels, which is what an upscale wants: every tap the
/// window covers is a sample the picture actually has. A heavy downscale asks for a wider kernel
/// than a fixed tap count can carry, and is what the mipmapped filter is there for.
/// </remarks>
internal sealed record LanczosVideoScaler : IVideoScaler
{
    public VideoScalingQuality Quality => VideoScalingQuality.Lanczos3;

    public bool RequiresSceneBuffer => true;

    // The kernel does its own weighting, so the taps have to arrive unfiltered.
    public SKSamplingOptions Sampling { get; } = new(SKFilterMode.Nearest, SKMipmapMode.None);

    public string Function => """
        float lanczosKernel(float distance) {
            float x = abs(distance);
            if (x >= 3.0) {
                return 0.0;
            }

            float scaled = 3.14159265358979 * x;
            if (scaled < 1e-4) {
                return 1.0;
            }

            return 3.0 * sin(scaled) * sin(scaled / 3.0) / (scaled * scaled);
        }

        float3 scaleScene(float2 coord) {
            float2 base = floor(coord - 0.5) + 0.5;
            float2 offset = coord - base;
            float3 total = float3(0.0);
            float weight = 0.0;

            for (int row = -2; row <= 3; ++row) {
                float rowWeight = lanczosKernel(float(row) - offset.y);
                for (int column = -2; column <= 3; ++column) {
                    float tapWeight = rowWeight * lanczosKernel(float(column) - offset.x);
                    float2 tap = base + float2(float(column), float(row));
                    total += tapWeight * float4(source.eval(tap)).rgb;
                    weight += tapWeight;
                }
            }

            return total / max(weight, 1e-6);
        }
        """;
}

/// <summary>
/// Elliptically weighted Lanczos: a jinc lobe windowed by the jinc whose first zero is the support
/// radius, taken over the sixty-four taps that radius can reach.
/// </summary>
/// <remarks>
/// The weight follows the distance to the sample rather than the distance along each axis, so the
/// filter has no preferred direction and a diagonal edge keeps the detail a separable filter loses
/// there. A polar kernel is no partition of unity, so the taps are normalized by their own sum.
/// </remarks>
internal sealed record JincVideoScaler : IVideoScaler
{
    public VideoScalingQuality Quality => VideoScalingQuality.Jinc;

    public bool RequiresSceneBuffer => true;

    public SKSamplingOptions Sampling { get; } = new(SKFilterMode.Nearest, SKMipmapMode.None);

    // The support is the jinc's third zero, so the window has to reach four samples out.
    public string Function => """
        float besselJ1(float x) {
            // Abramowitz and Stegun 9.4.4 and 9.4.6, which hold to a part in ten million.
            if (x < 3.0) {
                float t = x / 3.0;
                float t2 = t * t;
                return x * (0.5 + t2 * (-0.56249985 + t2 * (0.21093573 + t2 * (-0.03954289 +
                    t2 * (0.00443319 + t2 * (-0.00031761 + t2 * 0.00001109))))));
            }

            float t = 3.0 / x;
            float amplitude = 0.79788456 + t * (0.00000156 + t * (0.01659667 + t * (0.00017105 +
                t * (-0.00249511 + t * (0.00113653 - t * 0.00020033)))));
            float phase = x - 2.35619449 + t * (0.12499612 + t * (0.00005650 + t * (-0.00637879 +
                t * (0.00074348 + t * (0.00079824 - t * 0.00029166)))));
            return amplitude * cos(phase) * inversesqrt(x);
        }

        float jinc(float radius) {
            float scaled = 3.14159265358979 * radius;
            if (scaled < 1e-4) {
                return 1.0;
            }

            return 2.0 * besselJ1(scaled) / scaled;
        }

        float jincKernel(float radius) {
            if (radius >= 3.2383154) {
                return 0.0;
            }

            return jinc(radius) * jinc(radius * (1.2196699 / 3.2383154));
        }

        float3 scaleScene(float2 coord) {
            float2 base = floor(coord - 0.5) + 0.5;
            float2 offset = coord - base;
            float3 total = float3(0.0);
            float weight = 0.0;

            for (int row = -3; row <= 4; ++row) {
                float verticalDistance = float(row) - offset.y;
                for (int column = -3; column <= 4; ++column) {
                    float horizontalDistance = float(column) - offset.x;
                    float tapWeight = jincKernel(sqrt(
                        horizontalDistance * horizontalDistance +
                        verticalDistance * verticalDistance));
                    if (tapWeight != 0.0) {
                        float2 tap = base + float2(float(column), float(row));
                        total += tapWeight * float4(source.eval(tap)).rgb;
                        weight += tapWeight;
                    }
                }
            }

            return total / max(weight, 1e-6);
        }
        """;
}
