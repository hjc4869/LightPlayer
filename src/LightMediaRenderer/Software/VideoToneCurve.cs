#nullable enable
using System;
using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Software;

internal enum SoftwareToneCurve
{
    None,
    Bt2390,
    Spline,
    Hdr10Plus,
    Bt2446A,
}

// Shared parameter preparation for Shaders/VideoColorPlan.cs and VideoCpuColorPipeline.cs.
internal sealed class VideoToneCurve
{
    public SoftwareToneCurve Kind { get; init; }
    public float[] Parameters { get; init; } = new float[4];
    public float[] Curve0 { get; init; } = new float[4];
    public float[] Curve1 { get; init; } = new float[4];
    public float[] Controls { get; init; } = [];

    public static float PqToNits(float signal)
    {
        float power = MathF.Pow(Math.Clamp(signal, 0, 1), 1 / 78.84375f);
        return 10000 * MathF.Pow(MathF.Max(power - 0.8359375f, 0) /
            MathF.Max(18.8515625f - 18.6875f * power, 1e-6f), 1 / 0.1593017578125f);
    }

    public static float NitsToPq(float nits)
    {
        if (nits <= 0)
            return 0;
        float level = MathF.Pow(Math.Clamp(nits / 10000, 0, 1), 0.1593017578125f);
        return MathF.Pow((0.8359375f + 18.8515625f * level) / (1 + 18.6875f * level), 78.84375f);
    }

    public static VideoToneCurve Bt2390(float sourcePeak, float targetPeak, float kneeOffset) => new()
    {
        Kind = SoftwareToneCurve.Bt2390,
        Parameters = [NitsToPq(sourcePeak), NitsToPq(targetPeak),
            NitsToPq(targetPeak) / NitsToPq(sourcePeak), kneeOffset],
    };

    public static VideoToneCurve Bt2446A(float sourcePeak, float targetPeak) => new()
    {
        Kind = SoftwareToneCurve.Bt2446A,
        Parameters = [sourcePeak, targetPeak, 1 + 32 * MathF.Pow(sourcePeak / 10000, 1 / 2.4f),
            1 + 32 * MathF.Pow(targetPeak / 10000, 1 / 2.4f)],
    };

    public static VideoToneCurve Spline(float sourceMinimum, float sceneAverage, float sourceMaximum,
        float targetMaximum, VideoSdrToneMappingOptions options)
    {
        float inputMaximum = MathF.Max(sourceMaximum, targetMaximum);
        float inputMinimum = Math.Clamp(sourceMinimum, 0, inputMaximum - 1e-6f);
        float outputMaximum = MathF.Min(targetMaximum, inputMaximum);
        float sourceKnee = sceneAverage > 0 ? sceneAverage : Mix(inputMinimum, inputMaximum, options.KneeDefault);
        sourceKnee = Math.Clamp(sourceKnee, Mix(inputMinimum, inputMaximum, options.KneeMinimum),
            Mix(inputMinimum, inputMaximum, options.KneeMaximum));
        float target = (sourceKnee - inputMinimum) / (inputMaximum - inputMinimum);
        float adapted = outputMaximum * target;
        float tuning = 1 - SmoothStep(options.KneeMaximum, options.KneeDefault, target) *
            SmoothStep(options.KneeMinimum, options.KneeDefault, target);
        float adaptation = Mix(options.KneeAdaptation, 1, tuning);
        float targetKnee = Math.Clamp(Mix(sourceKnee, adapted, adaptation),
            outputMaximum * options.KneeMinimum, outputMaximum * options.KneeMaximum);
        float slope = targetKnee / (sourceKnee - inputMinimum);
        float ratio = Math.Clamp(options.SlopeTuning * (inputMaximum / outputMaximum - 1),
            options.SlopeOffset, 1 + options.SlopeOffset);
        slope = MathF.Pow(slope, (1 - options.SplineContrast) * ratio);
        slope = MathF.Min(slope, MathF.Min(2 * targetKnee / (sourceKnee - inputMinimum),
            3 * (outputMaximum - targetKnee) / (inputMaximum - sourceKnee)));
        float inputBelow = inputMinimum - sourceKnee;
        float inputAbove = inputMaximum - sourceKnee;
        float outputBelow = -targetKnee;
        float outputAbove = outputMaximum - targetKnee;
        float quadratic = (outputBelow - slope * inputBelow) / (inputBelow * inputBelow);
        float denominator = 2 * inputAbove * inputAbove;
        float cubic = (slope * inputAbove - outputAbove) / (inputAbove * denominator);
        float square = -3 * (slope * inputAbove - outputAbove) / denominator;
        return new VideoToneCurve
        {
            Kind = SoftwareToneCurve.Spline,
            Parameters = [inputMinimum, inputMaximum, 0, outputMaximum],
            Curve0 = [sourceKnee, targetKnee, quadratic, slope],
            Curve1 = [cubic, square, slope, 0],
        };
    }

    public static VideoToneCurve Hdr10Plus(Hdr10PlusMetadata metadata, float sourceMaximum, float targetMaximum)
    {
        Hdr10PlusProcessingWindow window = metadata.Windows[0];
        float inputMaximum = MathF.Max(sourceMaximum, targetMaximum);
        float outputMaximum = MathF.Min(targetMaximum, inputMaximum);
        float targetedMaximum = Math.Clamp(metadata.TargetedSystemDisplayMaximumLuminance, 0, inputMaximum);
        float kneeX = Math.Clamp(window.KneeX, 0, 1);
        float kneeY = Math.Clamp(window.KneeY, 0, 1);
        int degree = Math.Clamp(window.AnchorCount + 1, 2, 16);
        Span<float> control = stackalloc float[17];
        control.Clear();
        for (int anchor = 0; anchor < window.AnchorCount; anchor++)
            control[anchor + 1] = window.Anchors[anchor];
        control[degree] = 1;
        if (outputMaximum < targetedMaximum)
        {
            float amount = targetedMaximum > 1e-6f ? MathF.Max(0, outputMaximum / targetedMaximum) : 0;
            kneeX *= amount;
            kneeY *= amount;
            float beta = degree * kneeX / MathF.Max(1 - kneeX, 1e-6f);
            float linearKnee = MathF.Min(kneeX * inputMaximum / MathF.Max(outputMaximum, 1e-6f), beta / (beta + 1));
            kneeY = Mix(linearKnee, kneeY, amount);
            for (int point = 2; point <= degree; point++)
                control[point] = Mix(1, control[point], amount);
            control[1] = Mix(Hdr10PlusIntercept(degree, kneeX, kneeY), control[1], amount);
        }
        else if (outputMaximum > targetedMaximum && inputMaximum > targetedMaximum)
        {
            float amount = MathF.Pow(Math.Clamp(1 - (outputMaximum - targetedMaximum) /
                (inputMaximum - targetedMaximum), 0, 1), 1.4f);
            kneeY *= targetedMaximum / MathF.Max(outputMaximum, 1e-6f);
            kneeY = Mix(kneeX * outputMaximum / inputMaximum, kneeY, amount);
            for (int point = 2; point < degree; point++)
                control[point] = Mix((float)point / degree, control[point], amount);
            control[1] = Mix(Hdr10PlusIntercept(degree, kneeX, kneeY), control[1], amount);
        }
        Span<float> elevated = stackalloc float[17];
        for (int currentDegree = degree; currentDegree < 16; currentDegree++)
        {
            elevated[0] = control[0];
            elevated[currentDegree + 1] = control[currentDegree];
            for (int point = 1; point <= currentDegree; point++)
            {
                float amount = (float)point / (currentDegree + 1);
                elevated[point] = amount * control[point - 1] + (1 - amount) * control[point];
            }
            elevated[..(currentDegree + 2)].CopyTo(control);
        }
        return new VideoToneCurve
        {
            Kind = SoftwareToneCurve.Hdr10Plus,
            Parameters = [inputMaximum, outputMaximum, Math.Clamp(kneeX, 0, 1), Math.Clamp(kneeY, 0, 1)],
            Controls = control.ToArray(),
        };
    }

    private static float Hdr10PlusIntercept(int degree, float kneeX, float kneeY) =>
        kneeX <= 0 || kneeY >= 1 ? 1f / degree :
            MathF.Min(kneeY / kneeX * (1 - kneeX) / (1 - kneeY) / degree, 1);

    private static float Mix(float first, float second, float amount) => first + (second - first) * amount;

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge0 == edge1)
            return value < edge0 ? 0 : 1;
        float amount = Math.Clamp((value - edge0) / (edge1 - edge0), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }
}
