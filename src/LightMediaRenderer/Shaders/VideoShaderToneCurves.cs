using System;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Software;

namespace LightMediaRenderer.Shaders;

/// <summary>
/// Tone curves that exist only as shader stages. Each maps luminance in nits and only compresses,
/// so a source within the target peak passes through without one.
/// </summary>
internal static class VideoShaderToneCurves
{
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

    /// <summary>Maps neutral luminance in nits through the curve's equations, in software.</summary>
    public static void Evaluate(VideoSdrToneMappingAlgorithm algorithm, float[] parameters,
        ReadOnlySpan<float> nits, Span<float> mapped)
    {
        int count = Math.Min(nits.Length, mapped.Length);
        float targetPeak = parameters[3];
        for (int index = 0; index < count; index++)
        {
            float luminance = MathF.Max(nits[index], 0);
            float value = algorithm switch
            {
                VideoSdrToneMappingAlgorithm.Clip => luminance,
                VideoSdrToneMappingAlgorithm.Mobius => Mobius(luminance, parameters),
                VideoSdrToneMappingAlgorithm.Reinhard => luminance / (parameters[0] * luminance + parameters[1]),
                VideoSdrToneMappingAlgorithm.Hable =>
                    Hable(luminance * parameters[0]) / Hable(parameters[1]) * targetPeak,
                VideoSdrToneMappingAlgorithm.Gamma => luminance > parameters[0]
                    ? parameters[0] * MathF.Pow(luminance / parameters[0], parameters[1])
                    : luminance,
                VideoSdrToneMappingAlgorithm.Linear => VideoToneCurve.PqToNits(
                    MathF.Min(VideoToneCurve.NitsToPq(luminance) * parameters[0], parameters[1])),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Not a shader-only tone curve."),
            };
            mapped[index] = MathF.Min(value, targetPeak);
        }
    }

    private static float Mobius(float luminance, float[] parameters)
    {
        float excess = MathF.Max(luminance - parameters[0], 0);
        return MathF.Min(luminance, parameters[0]) + parameters[1] * excess / (parameters[1] + excess);
    }

    private static float Hable(float x)
    {
        const float a = 0.15f, b = 0.50f, c = 0.10f, d = 0.20f, e = 0.02f, f = 0.30f;
        return (x * (a * x + c * b) + d * e) / (x * (a * x + b) + d * f) - e / f;
    }
}
