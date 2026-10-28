using System;
using System.Runtime.InteropServices;

namespace LightMediaRenderer.Contracts;

/// <summary>Where the pixels of a <see cref="VideoFrameSnapshot"/> come from.</summary>
public enum VideoFrameSnapshotSource
{
    /// <summary>The decoded frame at its stored resolution, before scaling and display processing.</summary>
    Decoded,

    /// <summary>The picture as drawn, at its size on screen and in the output surface's encoding.</summary>
    Rendered,
}

/// <summary>
/// One upright video picture as interleaved RGB samples in native memory, with the color encoding
/// they carry. Rows are tightly packed; samples are bytes up to 8 bits and little-endian 16-bit
/// words spanning the full range beyond that.
/// </summary>
public sealed unsafe class VideoFrameSnapshot : IDisposable
{
    private readonly long length;
    private byte* samples;

    public VideoFrameSnapshot(int width, int height, int bitDepth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(bitDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitDepth, 16);
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        RowBytes = checked(width * 3 * BytesPerSample);
        length = (long)RowBytes * height;
        samples = (byte*)NativeMemory.Alloc((nuint)length);
        GC.AddMemoryPressure(length);
    }

    ~VideoFrameSnapshot() => Free();

    public int Width { get; }

    public int Height { get; }

    /// <summary>Meaningful bits per sample, which is the source's precision rather than the container's.</summary>
    public int BitDepth { get; }

    public int BytesPerSample => BitDepth > 8 ? 2 : 1;

    public int RowBytes { get; }

    public VideoColorPrimaries Primaries { get; init; } = VideoColorPrimaries.Bt709;

    public VideoColorTransfer Transfer { get; init; } = VideoColorTransfer.Bt709;

    /// <summary>Width over height of one pixel as it is meant to be seen.</summary>
    public double PixelAspectRatio { get; init; } = 1;

    /// <summary>Presentation time of the frame on the media timeline.</summary>
    public TimeSpan Timestamp { get; init; }

    public VideoFrameSnapshotSource Source { get; init; }

    /// <summary>Whether the samples carry PQ or HLG light rather than a standard dynamic range signal.</summary>
    public bool IsHdr => Transfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg;

    /// <summary>The first sample; valid until the snapshot is disposed.</summary>
    public IntPtr Address => (IntPtr)Pointer;

    public Span<byte> GetRow(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return new Span<byte>(Pointer + (long)y * RowBytes, RowBytes);
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    private byte* Pointer => samples != null ? samples : throw new ObjectDisposedException(nameof(VideoFrameSnapshot));

    private void Free()
    {
        byte* owned = samples;
        samples = null;
        if (owned == null)
            return;
        NativeMemory.Free(owned);
        GC.RemoveMemoryPressure(length);
    }
}
