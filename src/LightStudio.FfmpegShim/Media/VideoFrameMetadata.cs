using System;
using System.Buffers.Binary;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Interop;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

/// <summary>
/// Reads FFmpeg's description of a picture into the terms the renderer works in.
/// </summary>
/// <remarks>
/// Shared rather than duplicated because a still and a moving picture are described by exactly the
/// same tags, and a photo that disagreed with a video about what BT.2020 or SMPTE 432 meant would
/// be wrong in a way only a side-by-side comparison could catch.
/// </remarks>
public static unsafe partial class VideoFrameMetadata
{
    private const uint IccCicpSignature = 0x63696370;
    private const int IccTagTableOffset = 128;
    private const int IccTagRecordSize = 12;

    /// <summary>
    /// Layouts the renderer can sample as they stand. Anything absent here still decodes, but has
    /// to be converted first, so an exotic source costs performance rather than failing.
    /// </summary>
    /// <remarks>
    /// Only little-endian sample orders are listed because that is what a decoder produces natively
    /// on every platform in use.
    /// </remarks>
    public static VideoPixelFormat? MapLayout(AVPixelFormat format) => format switch
    {
        AVPixelFormat.AV_PIX_FMT_NV12 => VideoPixelFormat.Nv12,
        AVPixelFormat.AV_PIX_FMT_NV21 => VideoPixelFormat.Nv21,
        AVPixelFormat.AV_PIX_FMT_P010LE => VideoPixelFormat.P010,
        AVPixelFormat.AV_PIX_FMT_P012LE => VideoPixelFormat.P012,
        AVPixelFormat.AV_PIX_FMT_P016LE => VideoPixelFormat.P016,

        AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P => VideoPixelFormat.Yuv420p,
        AVPixelFormat.AV_PIX_FMT_YUV422P or AVPixelFormat.AV_PIX_FMT_YUVJ422P => VideoPixelFormat.Yuv422p,
        AVPixelFormat.AV_PIX_FMT_YUV444P or AVPixelFormat.AV_PIX_FMT_YUVJ444P => VideoPixelFormat.Yuv444p,

        AVPixelFormat.AV_PIX_FMT_YUV420P10LE => VideoPixelFormat.Yuv420p10,
        AVPixelFormat.AV_PIX_FMT_YUV422P10LE => VideoPixelFormat.Yuv422p10,
        AVPixelFormat.AV_PIX_FMT_YUV444P10LE => VideoPixelFormat.Yuv444p10,

        AVPixelFormat.AV_PIX_FMT_YUV420P12LE => VideoPixelFormat.Yuv420p12,
        AVPixelFormat.AV_PIX_FMT_YUV422P12LE => VideoPixelFormat.Yuv422p12,
        AVPixelFormat.AV_PIX_FMT_YUV444P12LE => VideoPixelFormat.Yuv444p12,

        AVPixelFormat.AV_PIX_FMT_YUV420P16LE => VideoPixelFormat.Yuv420p16,
        AVPixelFormat.AV_PIX_FMT_YUV422P16LE => VideoPixelFormat.Yuv422p16,
        AVPixelFormat.AV_PIX_FMT_YUV444P16LE => VideoPixelFormat.Yuv444p16,

        _ => null,
    };

    /// <summary>The J formats are the deprecated way of saying full range and carry no range tag.</summary>
    public static bool IsFullRangeFormat(AVPixelFormat format) => format
        is AVPixelFormat.AV_PIX_FMT_YUVJ420P
        or AVPixelFormat.AV_PIX_FMT_YUVJ422P
        or AVPixelFormat.AV_PIX_FMT_YUVJ444P;

    /// <summary>Untagged content is left-sited: that is what MPEG-2, H.264 and HEVC mean by silence.</summary>
    public static VideoChromaLocation MapChromaLocation(AVChromaLocation location) => location switch
    {
        AVChromaLocation.AVCHROMA_LOC_CENTER => VideoChromaLocation.Center,
        AVChromaLocation.AVCHROMA_LOC_TOPLEFT => VideoChromaLocation.TopLeft,
        AVChromaLocation.AVCHROMA_LOC_TOP => VideoChromaLocation.Top,
        AVChromaLocation.AVCHROMA_LOC_BOTTOMLEFT => VideoChromaLocation.BottomLeft,
        AVChromaLocation.AVCHROMA_LOC_BOTTOM => VideoChromaLocation.Bottom,
        _ => VideoChromaLocation.Left,
    };

    /// <summary>Unflagged HD content is BT.709 and unflagged SD is BT.601, as swscale assumes.</summary>
    public static VideoColorSpace MapColorSpace(AVColorSpace colorSpace, int height) => colorSpace switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => VideoColorSpace.Bt709,
        AVColorSpace.AVCOL_SPC_BT470BG or
        AVColorSpace.AVCOL_SPC_SMPTE170M or
        AVColorSpace.AVCOL_SPC_SMPTE240M => VideoColorSpace.Bt601,
        AVColorSpace.AVCOL_SPC_BT2020_NCL or
        AVColorSpace.AVCOL_SPC_BT2020_CL => VideoColorSpace.Bt2020,
        _ => height >= 720 ? VideoColorSpace.Bt709 : VideoColorSpace.Bt601,
    };

    public static VideoColorPrimaries MapColorPrimaries(
        AVColorPrimaries primaries,
        VideoColorSpace colorSpace) => primaries switch
    {
        AVColorPrimaries.AVCOL_PRI_BT709 => VideoColorPrimaries.Bt709,
        AVColorPrimaries.AVCOL_PRI_BT2020 => VideoColorPrimaries.Bt2020,

        // SMPTE 240M shares 170M's chromaticities and differs only in its transfer.
        AVColorPrimaries.AVCOL_PRI_SMPTE170M or
        AVColorPrimaries.AVCOL_PRI_SMPTE240M => VideoColorPrimaries.Smpte170M,
        AVColorPrimaries.AVCOL_PRI_BT470BG => VideoColorPrimaries.Bt470Bg,
        AVColorPrimaries.AVCOL_PRI_BT470M => VideoColorPrimaries.Bt470M,
        AVColorPrimaries.AVCOL_PRI_SMPTE432 => VideoColorPrimaries.DisplayP3,
        AVColorPrimaries.AVCOL_PRI_SMPTE431 => VideoColorPrimaries.DciP3,
        _ => colorSpace == VideoColorSpace.Bt2020
            ? VideoColorPrimaries.Bt2020
            : VideoColorPrimaries.Bt709,
    };

    public static VideoColorTransfer MapColorTransfer(AVColorTransferCharacteristic transfer) => transfer switch
    {
        AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 => VideoColorTransfer.Pq,
        AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 => VideoColorTransfer.Hlg,
        AVColorTransferCharacteristic.AVCOL_TRC_LINEAR => VideoColorTransfer.Linear,
        AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1 => VideoColorTransfer.Srgb,
        _ => VideoColorTransfer.Bt709,
    };

    /// <summary>
    /// Reads the standardized color description carried by an ICC <c>cicp</c> tag. JPEG and PNG
    /// decoders expose the profile as frame side data but do not copy these values to the ordinary
    /// FFmpeg color fields.
    /// </summary>
    internal static bool TryReadIccCicp(
        AVFrame* input,
        out AVColorPrimaries primaries,
        out AVColorTransferCharacteristic transfer)
    {
        primaries = AVColorPrimaries.AVCOL_PRI_UNSPECIFIED;
        transfer = AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED;
        if (input == null)
            return false;

        AVFrameSideData* profile = av_frame_get_side_data(input, AVFrameSideDataType.AV_FRAME_DATA_ICC_PROFILE);
        return profile != null && profile->data != null && profile->size <= int.MaxValue &&
            TryReadIccCicp(new ReadOnlySpan<byte>(profile->data, (int)profile->size), out primaries, out transfer);
    }

    internal static bool TryReadIccCicp(
        ReadOnlySpan<byte> profile,
        out AVColorPrimaries primaries,
        out AVColorTransferCharacteristic transfer)
    {
        primaries = AVColorPrimaries.AVCOL_PRI_UNSPECIFIED;
        transfer = AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED;
        if (profile.Length < IccTagTableOffset + sizeof(uint))
            return false;

        uint count = BinaryPrimitives.ReadUInt32BigEndian(profile[IccTagTableOffset..]);
        int availableRecords = (profile.Length - IccTagTableOffset - sizeof(uint)) / IccTagRecordSize;
        if (count > availableRecords)
            return false;

        for (int index = 0; index < count; index++)
        {
            int recordOffset = IccTagTableOffset + sizeof(uint) + (index * IccTagRecordSize);
            ReadOnlySpan<byte> record = profile.Slice(recordOffset, IccTagRecordSize);
            if (BinaryPrimitives.ReadUInt32BigEndian(record) != IccCicpSignature)
                continue;

            uint payloadOffset = BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
            uint payloadSize = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            if (payloadSize < 12 ||
                payloadOffset > profile.Length ||
                payloadSize > profile.Length - payloadOffset)
            {
                return false;
            }

            ReadOnlySpan<byte> payload = profile.Slice((int)payloadOffset, (int)payloadSize);
            if (BinaryPrimitives.ReadUInt32BigEndian(payload) != IccCicpSignature)
                return false;

            primaries = (AVColorPrimaries)payload[8];
            transfer = (AVColorTransferCharacteristic)payload[9];
            return true;
        }

        return false;
    }

    /// <summary>
    /// The brightest light the picture claims to reach, in cd/m^2, or zero where it says nothing.
    /// Content light level is preferred over the mastering display, because it describes the
    /// picture rather than the monitor it was graded on.
    /// </summary>
    public static double ReadHdrPeakLuminance(AVFrame* input)
    {
        if (input == null)
            return 0;

        AVFrameSideData* contentLight = av_frame_get_side_data(
            input,
            AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
        if (contentLight != null && contentLight->data != null)
        {
            var metadata = (AVContentLightMetadata*)contentLight->data;
            if (metadata->MaxCLL > 0)
                return metadata->MaxCLL;
        }

        return ReadHdrMasteringLuminance(input)?.MaximumNits ?? 0;
    }

    public static VideoLuminance? ReadHdrMasteringLuminance(AVFrame* input)
    {
        if (input == null)
            return null;

        AVFrameSideData* masteringDisplay = av_frame_get_side_data(
            input,
            AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
        return masteringDisplay != null
            ? ReadHdrMasteringLuminance((AVMasteringDisplayMetadata*)masteringDisplay->data)
            : null;
    }

    internal static VideoLuminance? ReadHdrMasteringLuminance(AVMasteringDisplayMetadata* metadata)
    {
        if (metadata == null || metadata->has_luminance == 0)
            return null;
        double black = Utils.RationalToDouble(metadata->min_luminance);
        double peak = Utils.RationalToDouble(metadata->max_luminance);
        double? minimum = double.IsFinite(black) && black >= 0 ? black : null;
        double? maximum = double.IsFinite(peak) && peak > 0 ? peak : null;
        if (minimum > maximum)
            minimum = null;
        return minimum is not null || maximum is not null ? new VideoLuminance(minimum, maximum) : null;
    }
}
