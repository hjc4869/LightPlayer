using LightMediaRenderer.Contracts;
using System;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

/// <summary>Copies FFmpeg's HDR10+ side data into stable frame-owned storage.</summary>
public static unsafe partial class Hdr10PlusMetadataReader
{
    private const float LinearRgbToNits = 10000.0f;

    public static bool TryRead(ReadOnlySpan<byte> data, Hdr10PlusMetadata destination)
    {
        destination.Reset();
        if (data.Length < sizeof(AVDynamicHDRPlus)) return false;
        fixed (byte* pointer = data)
        {
            if (!TryRead((AVDynamicHDRPlus*)pointer, destination))
            {
                destination.Reset();
                return false;
            }
        }
        destination.IsPresent = true;
        return true;
    }

    public static bool TryRead(AVFrame* frame, Hdr10PlusMetadata destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Reset();
        AVFrameSideData* sideData = frame == null ? null :
            av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS);
        if (sideData == null || sideData->data == null || sideData->size < (nuint)sizeof(AVDynamicHDRPlus))
        {
            return false;
        }

        if (!TryRead((AVDynamicHDRPlus*)sideData->data, destination))
        {
            destination.Reset();
            return false;
        }

        destination.IsPresent = true;
        return true;
    }

    internal static bool TryRead(AVDynamicHDRPlus* source, Hdr10PlusMetadata destination)
    {
        if (source == null || source->application_version > 1 ||
            source->num_windows is < 1 or > Hdr10PlusMetadata.MaximumWindows ||
            !TryReadRational(
                source->targeted_system_display_maximum_luminance,
                1.0f,
                0,
                10000,
                out float targetedLuminance))
        {
            return false;
        }

        destination.ApplicationVersion = source->application_version;
        destination.WindowCount = source->num_windows;
        destination.TargetedSystemDisplayMaximumLuminance = targetedLuminance;

        for (uint windowIndex = 0; windowIndex < source->num_windows; windowIndex++)
        {
            AVHDRPlusColorTransformParams sourceWindow = source->@params[windowIndex];
            Hdr10PlusProcessingWindow window = destination.Windows[windowIndex];
            for (uint component = 0; component < 3; component++)
            {
                if (!TryReadRational(
                    sourceWindow.maxscl[component],
                    LinearRgbToNits,
                    0,
                    10000,
                    out window.SceneMaximumLuminance[component]))
                {
                    return false;
                }
            }

            if (!TryReadRational(
                sourceWindow.average_maxrgb,
                LinearRgbToNits,
                0,
                10000,
                out float sceneAverage) ||
                sourceWindow.num_distribution_maxrgb_percentiles >
                    Hdr10PlusProcessingWindow.MaximumPercentiles)
            {
                return false;
            }

            window.SceneAverageLuminance = sceneAverage;
            window.PercentileCount = sourceWindow.num_distribution_maxrgb_percentiles;
            for (uint percentileIndex = 0;
                percentileIndex < sourceWindow.num_distribution_maxrgb_percentiles;
                percentileIndex++)
            {
                AVHDRPlusPercentile percentile = sourceWindow.distribution_maxrgb[percentileIndex];
                if (percentile.percentage > 100 || !TryReadRational(
                    percentile.percentile,
                    LinearRgbToNits,
                    0,
                    10000,
                    out window.PercentileLuminance[percentileIndex]))
                {
                    return false;
                }

                window.Percentages[percentileIndex] = percentile.percentage;
            }

            // ST 2094-40 B.3 uses the last distribution entry wherever MaxSCL is absent.
            float histogramPeak = window.PercentileCount > 0
                ? window.PercentileLuminance[window.PercentileCount - 1]
                : 0;
            for (int component = 0; component < window.SceneMaximumLuminance.Length; component++)
            {
                if (window.SceneMaximumLuminance[component] == 0)
                    window.SceneMaximumLuminance[component] = histogramPeak;
            }

            if (sourceWindow.tone_mapping_flag > 1 ||
                sourceWindow.num_bezier_curve_anchors > Hdr10PlusProcessingWindow.MaximumAnchors)
            {
                return false;
            }

            window.ToneMapping = sourceWindow.tone_mapping_flag == 1;
            window.AnchorCount = sourceWindow.num_bezier_curve_anchors;
            if (window.ToneMapping)
            {
                if (!TryReadRational(sourceWindow.knee_point_x, 1, 0, 1, out float kneeX) ||
                    !TryReadRational(sourceWindow.knee_point_y, 1, 0, 1, out float kneeY))
                {
                    return false;
                }

                window.KneeX = kneeX;
                window.KneeY = kneeY;
                for (uint anchor = 0; anchor < sourceWindow.num_bezier_curve_anchors; anchor++)
                {
                    if (!TryReadRational(
                        sourceWindow.bezier_curve_anchors[anchor],
                        1,
                        0,
                        1,
                        out window.Anchors[anchor]))
                    {
                        return false;
                    }
                }
            }
        }

        Hdr10PlusProcessingWindow first = destination.Windows[0];
        float maximumComponent = Math.Max(
            first.SceneMaximumLuminance[0],
            Math.Max(first.SceneMaximumLuminance[1], first.SceneMaximumLuminance[2]));
        float maximumLuminance =
            (0.2627f * first.SceneMaximumLuminance[0]) +
            (0.6780f * first.SceneMaximumLuminance[1]) +
            (0.0593f * first.SceneMaximumLuminance[2]);
        float averageLuminance = maximumComponent > 0
            ? first.SceneAverageLuminance * maximumLuminance / maximumComponent
            : 0;

        // Application version 1 repurposes the 5% histogram slot as Nit99y, a direct
        // 99.99th-percentile luminance reading. libplacebo prefers it over the MaxSCL estimate.
        if (source->application_version == 1 && first.PercentileCount == 9 &&
            first.Percentages[1] == 5 && first.Percentages[2] == 10 &&
            first.PercentileLuminance[1] > 0.01f)
        {
            float profileMaximum = first.PercentileLuminance[1];
            averageLuminance = maximumComponent > 0
                ? first.SceneAverageLuminance * profileMaximum / maximumComponent
                : 0;
            maximumLuminance = profileMaximum;
        }

        destination.EffectivePeakLuminance = maximumLuminance;
        destination.SceneAverageLuminance = averageLuminance;
        return true;
    }

    private static bool TryReadRational(
        AVRational value,
        float scale,
        float minimum,
        float maximum,
        out float result)
    {
        result = value.den == 0 ? 0 : (float)((double)value.num / value.den * scale);
        return value.den > 0 && float.IsFinite(result) && result >= minimum && result <= maximum;
    }
}