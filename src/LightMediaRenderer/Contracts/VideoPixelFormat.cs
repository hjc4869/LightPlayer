using System;

namespace LightMediaRenderer.Contracts;

/// <summary>Memory layout of a decoded frame handed to a renderer.</summary>
/// <remarks>
/// Everything except <see cref="Bgra32"/> is handed over exactly as the decoder produced it, so
/// nothing on the decode thread converts color. <see cref="VideoPlanarLayout"/> describes the
/// geometry, which is what the frame allocator and the renderer work from; the names here exist so
/// the format a frame arrived in stays legible in a debugger and in diagnostics.
/// </remarks>
public enum VideoPixelFormat
{
    /// <summary>8 bits per channel, byte order B,G,R,A. Matches Skia's Bgra8888 on little endian.</summary>
    Bgra32,

    /// <summary>4:2:0, 8-bit, luma plane then an interleaved Cb/Cr plane.</summary>
    Nv12,

    /// <summary>As <see cref="Nv12"/> with the chroma pair in Cr,Cb order.</summary>
    Nv21,

    /// <summary>4:2:0, 10-bit in 16-bit samples aligned to the high bits, interleaved chroma.</summary>
    P010,

    /// <summary>4:2:0, 12-bit in 16-bit samples aligned to the high bits, interleaved chroma.</summary>
    P012,

    /// <summary>4:2:0, 16-bit samples, interleaved chroma.</summary>
    P016,

    Yuv420p,
    Yuv422p,
    Yuv444p,

    Yuv420p10,
    Yuv422p10,
    Yuv444p10,

    Yuv420p12,
    Yuv422p12,
    Yuv444p12,

    Yuv420p16,
    Yuv422p16,
    Yuv444p16,

    /// <summary>
    /// The pixels live in a renderer-owned texture named by <see cref="VideoFrame.ExternalHandle"/>
    /// and never enter the frame's buffer, which stays empty.
    /// </summary>
    ExternalTexture,

    Rgba64,

    RgbaF16,
}

/// <summary>Color matrix a planar frame's chroma was encoded with.</summary>
public enum VideoColorSpace
{
    Bt601,
    Bt709,
    Bt2020,
}

/// <summary>Chromaticities the RGB signal is expressed in after YUV conversion.</summary>
public enum VideoColorPrimaries
{
    Bt709,
    Bt2020,

    /// <summary>BT.601 525-line, also SMPTE 170M and 240M: what NTSC-rate SD was mastered on.</summary>
    Smpte170M,

    /// <summary>BT.601 625-line, BT.470 System B/G: the PAL and SECAM primaries.</summary>
    Bt470Bg,

    /// <summary>The 1953 NTSC primaries, BT.470 System M. Far wider in green than anything since.</summary>
    Bt470M,

    /// <summary>DCI-P3 chromaticities on D65, as SMPTE ST 432-1 and Apple's Display P3 use.</summary>
    DisplayP3,

    /// <summary>DCI-P3 chromaticities on the theatrical DCI white point, SMPTE ST 431-2.</summary>
    DciP3,
}

/// <summary>Transfer function used to encode the RGB signal.</summary>
public enum VideoColorTransfer
{
    Bt709,
    Srgb,
    Linear,
    Pq,
    Hlg,
}

/// <summary>Whether a planar frame's samples use the full range or the studio subset.</summary>
public enum VideoColorRange
{
    Limited,
    Full,
}

/// <summary>
/// How a picture's rows were sampled: all at one instant, or as two fields half a frame apart and
/// woven together a row at a time.
/// </summary>
public enum VideoFieldOrder
{
    /// <summary>Neither the container nor the codec said, so the renderer has to decide for itself.</summary>
    Unknown,

    Progressive,

    /// <summary>The even rows were sampled first.</summary>
    TopFieldFirst,

    /// <summary>The odd rows were sampled first.</summary>
    BottomFieldFirst,
}

public static class VideoFieldOrders
{
    /// <summary>Reads an <c>AVFieldOrder</c>, whose values are fixed by FFmpeg's public headers.</summary>
    /// <remarks>
    /// Which field is coded first and which is displayed first are told apart by FFmpeg but not
    /// here: a renderer showing one frame at a time only has to know which rows were sampled
    /// earlier, not the order the encoder stored them in.
    /// </remarks>
    public static VideoFieldOrder FromFfmpeg(int fieldOrder) => fieldOrder switch
    {
        1 => VideoFieldOrder.Progressive,
        2 or 4 => VideoFieldOrder.TopFieldFirst,
        3 or 5 => VideoFieldOrder.BottomFieldFirst,
        _ => VideoFieldOrder.Unknown,
    };

    /// <summary>Whether the two fields of a frame are separated in time, and so can comb.</summary>
    public static bool IsInterlaced(this VideoFieldOrder order) =>
        order is VideoFieldOrder.TopFieldFirst or VideoFieldOrder.BottomFieldFirst;
}

/// <summary>
/// Where a subsampled chroma sample sits relative to the luma samples it covers. The names match
/// FFmpeg's, and <see cref="Left"/> is what MPEG-2, H.264 and HEVC use unless they say otherwise.
/// </summary>
public enum VideoChromaLocation
{
    /// <summary>Aligned with the leftmost luma column, halfway down the luma rows.</summary>
    Left,

    /// <summary>Centered in both axes, as MPEG-1 and JPEG use.</summary>
    Center,

    /// <summary>Aligned with the top-left luma sample.</summary>
    TopLeft,

    /// <summary>Centered horizontally, aligned with the top luma row.</summary>
    Top,

    /// <summary>Aligned with the leftmost luma column, on the bottom luma row.</summary>
    BottomLeft,

    /// <summary>Centered horizontally, aligned with the bottom luma row.</summary>
    Bottom,
}

/// <summary>Geometry and sample packing of a <see cref="VideoPixelFormat"/>.</summary>
/// <param name="PlaneCount">Separately addressable planes.</param>
/// <param name="ChromaShiftX">Right shift taking luma columns to chroma columns.</param>
/// <param name="ChromaShiftY">Right shift taking luma rows to chroma rows.</param>
/// <param name="BitDepth">Meaningful bits per sample, which can be narrower than the container.</param>
/// <param name="BytesPerSample">Container width, 1 or 2.</param>
/// <param name="InterleavedChroma">Cb and Cr share one plane rather than having one each.</param>
/// <param name="SwappedChroma">The interleaved pair is Cr,Cb instead of Cb,Cr.</param>
/// <param name="MsbAligned">Samples sit in the high bits of the container, as P010 and P012 do.</param>
public readonly record struct VideoPlanarLayout(
    int PlaneCount,
    int ChromaShiftX,
    int ChromaShiftY,
    int BitDepth,
    int BytesPerSample,
    bool InterleavedChroma,
    bool SwappedChroma,
    bool MsbAligned)
{
    /// <summary>The layout used for packed formats, which have no chroma planes to describe.</summary>
    public static VideoPlanarLayout Packed { get; } = new(1, 0, 0, 8, 4, false, false, false);

    /// <summary>The layout used for frames that carry no CPU-side planes at all.</summary>
    public static VideoPlanarLayout None { get; } = new(0, 0, 0, 8, 0, false, false, false);

    /// <summary>
    /// Logical layout of normalized Y, Cb, and Cr values already expanded into three texture
    /// channels. The values no longer have a byte container, so one keeps their sample scale at 1.
    /// </summary>
    public static VideoPlanarLayout NormalizedYuv(int bitDepth) =>
        new(1, 0, 0, Math.Clamp(bitDepth, 8, 16), 1, false, false, false);

    /// <summary>
    /// Multiplier taking a sample normalized over its whole container to the 0-1 signal range. It is
    /// 1 whenever the samples fill the container, and otherwise undoes the padding: 10-bit samples
    /// in a 16-bit container reach only 1023 of 65535 unless they were shifted up first.
    /// </summary>
    public double SampleScale
    {
        get
        {
            if (BytesPerSample == 1)
                return 1.0;

            int maximum = ((1 << BitDepth) - 1) << (MsbAligned ? 16 - BitDepth : 0);
            return 65535.0 / maximum;
        }
    }

    public static VideoPlanarLayout For(VideoPixelFormat format) => format switch
    {
        VideoPixelFormat.Nv12 => Interleaved(8, 1, msbAligned: false, swapped: false),
        VideoPixelFormat.Nv21 => Interleaved(8, 1, msbAligned: false, swapped: true),
        VideoPixelFormat.P010 => Interleaved(10, 2, msbAligned: true, swapped: false),
        VideoPixelFormat.P012 => Interleaved(12, 2, msbAligned: true, swapped: false),
        VideoPixelFormat.P016 => Interleaved(16, 2, msbAligned: false, swapped: false),

        VideoPixelFormat.Yuv420p => Planar(8, 1, 1, 1),
        VideoPixelFormat.Yuv422p => Planar(8, 1, 1, 0),
        VideoPixelFormat.Yuv444p => Planar(8, 1, 0, 0),

        VideoPixelFormat.Yuv420p10 => Planar(10, 2, 1, 1),
        VideoPixelFormat.Yuv422p10 => Planar(10, 2, 1, 0),
        VideoPixelFormat.Yuv444p10 => Planar(10, 2, 0, 0),

        VideoPixelFormat.Yuv420p12 => Planar(12, 2, 1, 1),
        VideoPixelFormat.Yuv422p12 => Planar(12, 2, 1, 0),
        VideoPixelFormat.Yuv444p12 => Planar(12, 2, 0, 0),

        VideoPixelFormat.Yuv420p16 => Planar(16, 2, 1, 1),
        VideoPixelFormat.Yuv422p16 => Planar(16, 2, 1, 0),
        VideoPixelFormat.Yuv444p16 => Planar(16, 2, 0, 0),

        VideoPixelFormat.ExternalTexture => None,
        VideoPixelFormat.Rgba64 or VideoPixelFormat.RgbaF16 => new(1, 0, 0, 16, 8, false, false, false),
        _ => Packed,
    };

    /// <summary>Whether the renderer has to combine planes rather than blit the frame.</summary>
    public static bool IsPlanar(VideoPixelFormat format) =>
        format is not (VideoPixelFormat.Bgra32 or VideoPixelFormat.Rgba64 or VideoPixelFormat.RgbaF16 or VideoPixelFormat.ExternalTexture);

    private static VideoPlanarLayout Interleaved(int bitDepth, int bytes, bool msbAligned, bool swapped) =>
        new(2, 1, 1, bitDepth, bytes, InterleavedChroma: true, swapped, msbAligned);

    private static VideoPlanarLayout Planar(int bitDepth, int bytes, int shiftX, int shiftY) =>
        new(3, shiftX, shiftY, bitDepth, bytes, InterleavedChroma: false, SwappedChroma: false, MsbAligned: false);

    /// <summary>Columns in a chroma plane of a frame this wide.</summary>
    public int ChromaWidth(int width) => (width + (1 << ChromaShiftX) - 1) >> ChromaShiftX;

    /// <summary>Rows in a chroma plane of a frame this tall.</summary>
    public int ChromaHeight(int height) => (height + (1 << ChromaShiftY) - 1) >> ChromaShiftY;

    /// <summary>
    /// Offsets, in chroma samples, from where a plain division of the luma coordinate lands to
    /// where the sample actually sits. Dividing alone assumes the chroma sample is centered over the
    /// luma samples it covers, which is only true of the centered sitings.
    /// </summary>
    public (double X, double Y) ChromaSiting(VideoChromaLocation location)
    {
        double horizontal = location is VideoChromaLocation.Left
            or VideoChromaLocation.TopLeft
            or VideoChromaLocation.BottomLeft
            ? Center(ChromaShiftX)
            : 0.0;

        double vertical = location switch
        {
            VideoChromaLocation.TopLeft or VideoChromaLocation.Top => Center(ChromaShiftY),
            VideoChromaLocation.BottomLeft or VideoChromaLocation.Bottom => -Center(ChromaShiftY),
            _ => 0.0,
        };

        return (horizontal, vertical);

        // Distance from the middle of the covered luma samples to the first of them.
        static double Center(int shift) => 0.5 - 0.5 / (1 << shift);
    }
}
