using LightMediaRenderer.Shaders;
using LightMediaRenderer.Contracts;
using System;

namespace LightMediaRenderer.Avalonia.Shaders;

/// <summary>Resolves the host's pre-scale stage selection.</summary>
public static class VideoScaleStageConfiguration
{
    public static Func<string?, IVideoScaleStage?>? StageProvider { get; set; }

    public static IVideoScaler Attach(IVideoScaler scaler, string? packageOverride)
    {
        ArgumentNullException.ThrowIfNull(scaler);
        IVideoScaleStage? stage = Resolve(packageOverride);
        return stage is not null
            ? VideoScalers.WithPreScaleStage(scaler, stage)
            : scaler;
    }

    /// <summary>Whether a pre-scale stage would run, which is what makes the coded size matter.</summary>
    public static bool HasStage(string? packageOverride) => Resolve(packageOverride) is not null;

    private static IVideoScaleStage? Resolve(string? packageOverride)
    {
        if (string.Equals(
            packageOverride,
            VideoShaderPackageCatalog.DisabledOverride,
            StringComparison.Ordinal))
        {
            return null;
        }

        return StageProvider?.Invoke(packageOverride);
    }
}