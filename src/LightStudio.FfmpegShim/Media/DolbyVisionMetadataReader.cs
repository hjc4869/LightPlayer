using LightMediaRenderer.Contracts;
using System;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

/// <summary>
/// Copies FFmpeg's offset-based DOVI side data into the stable representation the renderer uses.
/// </summary>
public static unsafe partial class DolbyVisionMetadataReader
{
    private const float PqCodeMaximum = 4095.0f;

    public static bool TryRead(ReadOnlySpan<byte> data, int profile, int level, int compatibilityId, DolbyVisionMetadata destination)
    {
        destination.Reset();
        fixed (byte* pointer = data)
        {
            if (!TryRead(pointer, (ulong)data.Length, profile, level, compatibilityId, destination))
            {
                destination.Reset();
                return false;
            }
        }
        destination.IsPresent = true;
        return true;
    }

    public static bool TryRead(
        AVFrame* frame,
        int profile,
        int level,
        int compatibilityId,
        DolbyVisionMetadata destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Reset();
        AVFrameSideData* sideData = frame == null ? null :
            av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA);
        if (sideData == null || sideData->data == null)
        {
            return false;
        }

        if (!TryRead(sideData->data, (ulong)sideData->size, profile, level, compatibilityId, destination))
        {
            destination.Reset();
            return false;
        }

        destination.IsPresent = true;
        return true;
    }

    internal static bool TryRead(
        byte* data,
        ulong size,
        int profile,
        int level,
        int compatibilityId,
        DolbyVisionMetadata destination)
    {
        if (data == null || size < (ulong)sizeof(AVDOVIMetadata))
            return false;

        var metadata = (AVDOVIMetadata*)data;
        if (!Contains(size, metadata->header_offset, (nuint)sizeof(AVDOVIRpuDataHeader)) ||
            !Contains(size, metadata->mapping_offset, (nuint)sizeof(AVDOVIDataMapping)) ||
            !Contains(size, metadata->color_offset, (nuint)sizeof(AVDOVIColorMetadata)))
        {
            return false;
        }

        var header = (AVDOVIRpuDataHeader*)(data + (nint)metadata->header_offset);
        var mapping = (AVDOVIDataMapping*)(data + (nint)metadata->mapping_offset);
        var color = (AVDOVIColorMetadata*)(data + (nint)metadata->color_offset);
        bool enhancementLayerResidual = HasNontrivialResidual(header, mapping);
        if (header->bl_bit_depth is < 8 or > 16 ||
            header->coef_log2_denom > 63 ||
            (header->disable_residual_flag == 0 && mapping->nlq_method_idc != AVDOVINLQMethod.AV_DOVI_NLQ_LINEAR_DZ) ||
            (enhancementLayerResidual && header->el_bit_depth is < 8 or > 16))
        {
            return false;
        }

        destination.Profile = profile;
        destination.Level = level;
        destination.CompatibilityId = compatibilityId;
        destination.BaseLayerBitDepth = header->bl_bit_depth;
        destination.EnhancementLayerBitDepth = header->el_bit_depth;
        destination.BaseLayerFullRange = header->bl_video_full_range_flag != 0;

        double coefficientScale = Math.ScaleB(1.0, -header->coef_log2_denom);
        double pivotScale = 1.0 / ((1 << header->bl_bit_depth) - 1);
        AVDOVIReshapingCurve* firstCurve = &mapping->curves.element0;
        for (int component = 0; component < DolbyVisionMetadata.ComponentCount; component++)
        {
            if (!ReadCurve(firstCurve + component, destination.Curves[component], pivotScale, coefficientScale))
                return false;
        }

        for (int index = 0; index < DolbyVisionMetadata.MatrixElementCount; index++)
        {
            if (!TryReadRational(color->ycc_to_rgb_matrix[index], out destination.NonlinearMatrix[index]) ||
                !TryReadRational(color->rgb_to_lms_matrix[index], out destination.LinearMatrix[index]))
            {
                return false;
            }
        }

        for (int index = 0; index < DolbyVisionMetadata.ComponentCount; index++)
        {
            if (!TryReadRational(color->ycc_to_rgb_offset[index], out destination.NonlinearOffset[index]))
                return false;
        }

        destination.EnhancementLayerResidual = enhancementLayerResidual;
        if (destination.EnhancementLayerResidual)
        {
            double enhancementMaximum = (1L << header->el_bit_depth) - 1;
            AVDOVINLQParams* firstNlq = &mapping->nlq.element0;
            for (int component = 0; component < DolbyVisionMetadata.ComponentCount; component++)
            {
                AVDOVINLQParams* nlq = firstNlq + component;
                destination.NlqOffset[component] = (float)(nlq->nlq_offset / enhancementMaximum);
                destination.NlqDeadzoneSlope[component] =
                    (float)(enhancementMaximum * nlq->linear_deadzone_slope * coefficientScale);
                destination.NlqDeadzoneThreshold[component] =
                    (float)((nlq->linear_deadzone_threshold - (0.5 * nlq->linear_deadzone_slope)) *
                        coefficientScale);
                if (!float.IsFinite(destination.NlqOffset[component]) ||
                    !float.IsFinite(destination.NlqDeadzoneSlope[component]) ||
                    !float.IsFinite(destination.NlqDeadzoneThreshold[component]))
                {
                    return false;
                }
            }
        }

        destination.SourceMinimumPq = color->source_min_pq / PqCodeMaximum;
        destination.SourceMaximumPq = color->source_max_pq / PqCodeMaximum;
        if (!(destination.SourceMaximumPq > destination.SourceMinimumPq))
            return false;

        ReadLevelOne(data, size, metadata, destination);
        float effectivePeak = destination.SceneMaximumPq > 0
            ? destination.SceneMaximumPq
            : destination.SourceMaximumPq;
        destination.EffectivePeakLuminance = PqToNits(effectivePeak);
        return double.IsFinite(destination.EffectivePeakLuminance) &&
            destination.EffectivePeakLuminance > 0;
    }

    /// <summary>MEL retains residual syntax but codes identity NLQ, so it needs no EL picture.</summary>
    private static bool HasNontrivialResidual(AVDOVIRpuDataHeader* header, AVDOVIDataMapping* mapping)
    {
        if (header->disable_residual_flag != 0)
            return false;

        ulong unity = 1UL << header->coef_log2_denom;
        return mapping->nlq_method_idc != AVDOVINLQMethod.AV_DOVI_NLQ_LINEAR_DZ ||
            !IsTrivialNlq(&mapping->nlq.element0, unity) ||
            !IsTrivialNlq(&mapping->nlq.element1, unity) ||
            !IsTrivialNlq(&mapping->nlq.element2, unity);
    }

    private static bool IsTrivialNlq(AVDOVINLQParams* nlq, ulong unity) =>
        nlq->nlq_offset == 0 &&
        nlq->vdr_in_max == unity &&
        nlq->linear_deadzone_slope == 0 &&
        nlq->linear_deadzone_threshold == 0;

    private static bool ReadCurve(
        AVDOVIReshapingCurve* source,
        DolbyVisionReshapingCurve destination,
        double pivotScale,
        double coefficientScale)
    {
        int pivotCount = source->num_pivots;
        if (pivotCount is < 2 or > DolbyVisionReshapingCurve.MaximumPivots)
            return false;

        destination.PivotCount = pivotCount;
        for (int pivot = 0; pivot < pivotCount; pivot++)
        {
            destination.Pivots[pivot] = (float)(source->pivots[pivot] * pivotScale);
            if (pivot > 0 && destination.Pivots[pivot] < destination.Pivots[pivot - 1])
                return false;
        }

        for (int piece = 0; piece < pivotCount - 1; piece++)
        {
            AVDOVIMappingMethod method = source->mapping_idc[piece];
            if (method == AVDOVIMappingMethod.AV_DOVI_MAPPING_POLYNOMIAL)
            {
                destination.Methods[piece] = 0;
                int order = source->poly_order[piece];
                if (order is < 1 or > 2)
                    return false;

                int targetBase = piece * DolbyVisionReshapingCurve.PolynomialCoefficientCount;
                for (int coefficient = 0; coefficient <= order; coefficient++)
                {
                    destination.PolynomialCoefficients[targetBase + coefficient] =
                        (float)(source->poly_coef[piece][coefficient] * coefficientScale);
                }
            }
            else if (method == AVDOVIMappingMethod.AV_DOVI_MAPPING_MMR)
            {
                destination.Methods[piece] = 1;
                int order = source->mmr_order[piece];
                if (order is < 1 or > DolbyVisionReshapingCurve.MaximumMmrOrder)
                    return false;

                destination.MmrOrders[piece] = order;
                destination.MmrConstants[piece] =
                    (float)(source->mmr_constant[piece] * coefficientScale);
                int sourceBase = piece * DolbyVisionReshapingCurve.MaximumMmrOrder *
                    DolbyVisionReshapingCurve.MmrCoefficientCount;
                for (int mmrOrder = 0; mmrOrder < order; mmrOrder++)
                {
                    int orderBase = sourceBase +
                        (mmrOrder * DolbyVisionReshapingCurve.MmrCoefficientCount);
                    for (int coefficient = 0;
                        coefficient < DolbyVisionReshapingCurve.MmrCoefficientCount;
                        coefficient++)
                    {
                        destination.MmrCoefficients[orderBase + coefficient] =
                            (float)(source->mmr_coef[piece][mmrOrder][coefficient] * coefficientScale);
                    }
                }
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadRational(AVRational rational, out float value)
    {
        value = rational.den == 0
            ? 0
            : rational.num / (float)rational.den;
        return rational.den != 0 && float.IsFinite(value);
    }

    private static void ReadLevelOne(
        byte* data,
        ulong size,
        AVDOVIMetadata* metadata,
        DolbyVisionMetadata destination)
    {
        if (metadata->num_ext_blocks is <= 0 or > AV_DOVI_MAX_EXT_BLOCKS ||
            metadata->ext_block_size < (nuint)sizeof(AVDOVIDmData))
        {
            return;
        }

        ulong first = (ulong)metadata->ext_block_offset;
        ulong stride = (ulong)metadata->ext_block_size;
        for (int index = 0; index < metadata->num_ext_blocks; index++)
        {
            ulong offset = first + ((ulong)index * stride);
            if (offset < first || !Contains(size, offset, stride))
                return;

            var block = (AVDOVIDmData*)(data + (nint)offset);
            if (block->level != 1)
                continue;

            destination.SceneMaximumPq = block->l1.max_pq / PqCodeMaximum;
            destination.SceneAveragePq = block->l1.avg_pq / PqCodeMaximum;
            return;
        }
    }

    private static bool Contains(ulong total, nuint offset, nuint length) =>
        Contains(total, (ulong)offset, (ulong)length);

    private static bool Contains(ulong total, ulong offset, ulong length) =>
        offset <= total && length <= total - offset;

    private static double PqToNits(float signal)
    {
        const double M1 = 0.1593017578125;
        const double M2 = 78.84375;
        const double C1 = 0.8359375;
        const double C2 = 18.8515625;
        const double C3 = 18.6875;
        double power = Math.Pow(Math.Clamp(signal, 0, 1), 1.0 / M2);
        double level = Math.Max(power - C1, 0) / Math.Max(C2 - (C3 * power), 1e-12);
        return 10000.0 * Math.Pow(level, 1.0 / M1);
    }

}