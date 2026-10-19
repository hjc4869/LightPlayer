using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Shaders;

public static class VideoDebanding
{
    /// <summary>The deepest source whose own quantization is coarse enough to show contours.</summary>
    public const int MaximumSourceBitDepth = 8;

    /// <summary>
    /// Whether smoothing a source is worth what it costs. A deeper source steps finer than a
    /// display resolves, so what banding it shows comes from the output quantizer rather than from
    /// the samples, and dithering is what answers that.
    /// </summary>
    public static bool Applies(VideoDebandingQuality quality, VideoPlanarLayout layout) =>
        quality != VideoDebandingQuality.Off && layout.BitDepth <= MaximumSourceBitDepth;
}

internal readonly record struct VideoDebandingProfile(
    int Iterations,
    float Threshold,
    float Radius,
    float Grain)
{
    public static VideoDebandingProfile For(VideoDebandingQuality quality) => quality switch
    {
        VideoDebandingQuality.Low => new(1, 3.0f / 1000.0f, 16, 0),
        VideoDebandingQuality.Medium => new(2, 3.0f / 1000.0f, 12, 1.0f / 1000.0f),
        VideoDebandingQuality.High => new(4, 3.0f / 1000.0f, 8, 1.0f / 1000.0f),
        _ => default,
    };
}