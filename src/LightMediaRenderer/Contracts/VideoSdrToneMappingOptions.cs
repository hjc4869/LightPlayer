using System;

namespace LightMediaRenderer.Contracts;

public enum VideoSdrToneMappingAlgorithm
{
    /// <summary>Use an authored HDR10+ curve when available, otherwise the PQ spline.</summary>
    Auto,
    Spline,
    Bt2390,
    Bt2446A,
}

/// <summary>HDR tone curves and optional display targets.</summary>
public sealed record VideoSdrToneMappingOptions
{
    public static VideoSdrToneMappingOptions Default { get; } = new();

    public VideoSdrToneMappingAlgorithm Algorithm { get; init; } = VideoSdrToneMappingAlgorithm.Auto;

    /// <summary>Nominal luminance mapped to SDR white, in nits, from 1 to 10000.</summary>
    public double ReferenceWhiteNits { get; init; } = 200;

    public double? TargetHeadroom { get; init; }

    public double? TargetMaximumNits { get; init; }

    /// <summary>BT.2390 knee offset, from 0.5 to 2.</summary>
    public float Bt2390KneeOffset { get; init; } = 1;

    /// <summary>
    /// How far the spline knee moves from its source luminance toward its proportional target position,
    /// from 0 to 1.
    /// </summary>
    public float KneeAdaptation { get; init; }

    /// <summary>Minimum spline pivot as a fraction of the PQ range, greater than 0 and less than 0.5.</summary>
    public float KneeMinimum { get; init; } = 0.1f;

    /// <summary>Maximum spline pivot as a fraction of the PQ range, greater than 0.5 and less than 1.</summary>
    public float KneeMaximum { get; init; } = 0.8f;

    /// <summary>Spline pivot without scene-average metadata, between the minimum and maximum knee.</summary>
    public float KneeDefault { get; init; } = 0.4f;

    /// <summary>Spline slope adjustment for differing source and target peaks, from 0 to 10.</summary>
    public float SlopeTuning { get; init; } = 1.5f;

    /// <summary>Spline slope-tuning exponent offset, from 0 to 1.</summary>
    public float SlopeOffset { get; init; } = 0.2f;

    /// <summary>Spline contrast, from 0 to 1.5; higher values favor midtone contrast over highlight detail.</summary>
    public float SplineContrast { get; init; } = 0.5f;

    public void Validate()
    {
        if (Algorithm is not (VideoSdrToneMappingAlgorithm.Auto or VideoSdrToneMappingAlgorithm.Spline or
            VideoSdrToneMappingAlgorithm.Bt2390 or VideoSdrToneMappingAlgorithm.Bt2446A))
            throw new ArgumentOutOfRangeException(nameof(Algorithm), Algorithm, "Unknown tone-mapping algorithm.");

        ValidateRange(ReferenceWhiteNits, nameof(ReferenceWhiteNits), 1, 10000);
        if (TargetHeadroom is { } headroom)
            ValidateRange(headroom, nameof(TargetHeadroom), 1, 100);
        if (TargetMaximumNits is { } maximum)
            ValidateRange(maximum, nameof(TargetMaximumNits), 1, 10000);
        ValidateRange(Bt2390KneeOffset, nameof(Bt2390KneeOffset), 0.5, 2);
        ValidateRange(KneeAdaptation, nameof(KneeAdaptation), 0, 1);
        ValidateRange(KneeMinimum, nameof(KneeMinimum), 1e-6f, 0.5f - 1e-6f);
        ValidateRange(KneeMaximum, nameof(KneeMaximum), 0.5f + 1e-6f, 1 - 1e-6f);
        ValidateRange(KneeDefault, nameof(KneeDefault), KneeMinimum, KneeMaximum);
        ValidateRange(SlopeTuning, nameof(SlopeTuning), 0, 10);
        ValidateRange(SlopeOffset, nameof(SlopeOffset), 0, 1);
        ValidateRange(SplineContrast, nameof(SplineContrast), 0, 1.5);
    }

    private static void ValidateRange(double value, string name, double minimum, double maximum)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, value, $"Expected a finite value from {minimum} to {maximum}.");
    }
}