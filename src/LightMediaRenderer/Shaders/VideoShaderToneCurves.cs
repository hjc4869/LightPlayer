using System;
using System.Collections.Generic;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Software;
using SkiaSharp;

namespace LightMediaRenderer.Shaders;

/// <summary>
/// Tone curves that exist only as shader stages. Each maps luminance in nits and only compresses,
/// so a source within the target peak passes through without one.
/// </summary>
internal static class VideoShaderToneCurves
{
    private const string EvaluateMain = """
        float4 main(float2 coord) {
            float nits = float4(source.eval(coord)).r;
            return float4(toneMap(float3(nits)).r, 0.0, 0.0, 1.0);
        }
        """;

    private static readonly float[] NeutralLuma = [1, 0, 0, 0];

    public static bool Supports(VideoSdrToneMappingAlgorithm algorithm) => algorithm is
        VideoSdrToneMappingAlgorithm.Clip or VideoSdrToneMappingAlgorithm.Mobius or
        VideoSdrToneMappingAlgorithm.Reinhard or VideoSdrToneMappingAlgorithm.Hable or
        VideoSdrToneMappingAlgorithm.Gamma or VideoSdrToneMappingAlgorithm.Linear;

    public static string Stage(VideoSdrToneMappingAlgorithm algorithm) => algorithm switch
    {
        VideoSdrToneMappingAlgorithm.Clip => VideoColorStages.ToneClip,
        VideoSdrToneMappingAlgorithm.Mobius => VideoColorStages.ToneMobius,
        VideoSdrToneMappingAlgorithm.Reinhard => VideoColorStages.ToneReinhard,
        VideoSdrToneMappingAlgorithm.Hable => VideoColorStages.ToneHable,
        VideoSdrToneMappingAlgorithm.Gamma => VideoColorStages.ToneGamma,
        VideoSdrToneMappingAlgorithm.Linear => VideoColorStages.ToneLinear,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Not a shader-only tone curve."),
    };

    /// <summary>The stage's tone parameters, for a source peak above a positive target peak, in nits.</summary>
    public static float[] Parameters(VideoSdrToneMappingOptions options, float sourcePeak, float targetPeak)
    {
        float knee = options.LinearKnee * targetPeak;
        switch (options.Algorithm)
        {
            case VideoSdrToneMappingAlgorithm.Clip:
                return [0, 0, 0, targetPeak];
            case VideoSdrToneMappingAlgorithm.Mobius:
                // Continues the knee's value and unit slope, and lands the source peak on the target peak.
                return [knee, (targetPeak - knee) * (sourcePeak - knee) / (sourcePeak - targetPeak), 0, targetPeak];
            case VideoSdrToneMappingAlgorithm.Reinhard:
            {
                float offset = (1 - options.ReinhardContrast) / options.ReinhardContrast * targetPeak;
                float gain = targetPeak * (sourcePeak + offset) / sourcePeak;
                return [1 / gain, offset / gain, 0, targetPeak];
            }
            case VideoSdrToneMappingAlgorithm.Hable:
                return [1 / targetPeak, sourcePeak / targetPeak, 0, targetPeak];
            case VideoSdrToneMappingAlgorithm.Gamma:
                // A straight line in log-log space from the knee to the source and target peaks.
                return [knee, MathF.Log(targetPeak / knee) / MathF.Log(sourcePeak / knee), 0, targetPeak];
            case VideoSdrToneMappingAlgorithm.Linear:
            {
                float targetSignal = VideoToneCurve.NitsToPq(targetPeak);
                return [targetSignal / VideoToneCurve.NitsToPq(sourcePeak) * options.LinearExposure,
                    targetSignal, 0, targetPeak];
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Algorithm, "Not a shader-only tone curve.");
        }
    }

    /// <summary>Maps neutral luminance in nits through the curve's stage on Skia's raster backend.</summary>
    public static unsafe void Evaluate(VideoSdrToneMappingAlgorithm algorithm, float[] parameters,
        ReadOnlySpan<float> nits, Span<float> mapped)
    {
        int count = Math.Min(nits.Length, mapped.Length);
        if (count == 0)
            return;

        VideoShaderProgram program = new VideoShaderBuilder()
            .AddUniform(VideoShaderUniforms.SourceLuma)
            .AddUniform(VideoShaderUniforms.ToneParams)
            .AddChild(VideoShaderChildren.Source)
            .AddStage(VideoColorStages.LinearHelpers)
            .AddStage(Stage(algorithm))
            .Build(EvaluateMain) ?? throw new InvalidOperationException($"The {algorithm} tone curve did not compile.");

        var info = new SKImageInfo(count, 1, SKColorType.RgbaF32, SKAlphaType.Premul);
        float[] pixels = new float[count * 4];
        for (int index = 0; index < count; index++)
        {
            pixels[index * 4] = nits[index];
            pixels[index * 4 + 3] = 1;
        }

        fixed (float* data = pixels)
        {
            using SKImage input = SKImage.FromPixelCopy(info, (IntPtr)data, info.RowBytes) ??
                throw new InvalidOperationException("The tone curve input could not be created.");
            using SKShader child = input.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Nearest));
            using SKShader shader = program.ToShader(
                new Dictionary<string, float[]>
                {
                    [VideoShaderUniforms.SourceLuma] = NeutralLuma,
                    [VideoShaderUniforms.ToneParams] = parameters,
                },
                new Dictionary<string, SKShader> { [VideoShaderChildren.Source] = child },
                out string failure) ?? throw new InvalidOperationException($"The tone curve shader failed: {failure}.");

            // The input was copied, so the surface draws over the same buffer.
            using SKSurface surface = SKSurface.Create(info, (IntPtr)data, info.RowBytes) ??
                throw new InvalidOperationException("The tone curve surface could not be created.");
            using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
            surface.Canvas.DrawPaint(paint);
        }

        for (int index = 0; index < count; index++)
            mapped[index] = pixels[index * 4];
    }
}
