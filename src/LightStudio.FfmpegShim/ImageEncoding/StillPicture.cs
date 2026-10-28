#nullable enable
using System;
using LightStudio.FfmpegShim.Interop;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.ImageEncoding;

/// <summary>An FFmpeg-owned picture in one of the layouts the still encoders accept.</summary>
public sealed unsafe class StillPicture : IDisposable
{
    private AVFrame* frame;

    public StillPicture(int width, int height, AVPixelFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        (PlaneCount, ChromaShiftX, ChromaShiftY, _) = Describe(format);
        frame = av_frame_alloc();
        if (frame == null)
            throw new OutOfMemoryException();
        frame->width = width;
        frame->height = height;
        frame->format = (int)format;
        int allocated = av_frame_get_buffer(frame, 64);
        if (allocated < 0)
        {
            AVFrame* failed = frame;
            av_frame_free(&failed);
            frame = null;
            FfmpegError.ThrowIfError(allocated, "av_frame_get_buffer");
        }
    }

    public int Width => Frame->width;

    public int Height => Frame->height;

    public AVPixelFormat Format => (AVPixelFormat)Frame->format;

    public int PlaneCount { get; }

    /// <summary>Horizontal chroma subsampling as a power of two; zero for full chroma.</summary>
    public int ChromaShiftX { get; }

    public int ChromaShiftY { get; }

    public AVColorPrimaries Primaries
    {
        get => Frame->color_primaries;
        set => Frame->color_primaries = value;
    }

    public AVColorTransferCharacteristic Transfer
    {
        get => Frame->color_trc;
        set => Frame->color_trc = value;
    }

    public AVColorSpace Matrix
    {
        get => Frame->colorspace;
        set => Frame->colorspace = value;
    }

    public AVColorRange Range
    {
        get => Frame->color_range;
        set => Frame->color_range = value;
    }

    internal AVFrame* Frame => frame != null ? frame : throw new ObjectDisposedException(nameof(StillPicture));

    public IntPtr GetPlane(int index) => (IntPtr)Frame->data[(uint)index];

    public int GetStride(int index) => Frame->linesize[(uint)index];

    public int GetPlaneWidth(int index) => IsChromaPlane(index) ? (Width + (1 << ChromaShiftX) - 1) >> ChromaShiftX : Width;

    public int GetPlaneHeight(int index) => IsChromaPlane(index) ? (Height + (1 << ChromaShiftY) - 1) >> ChromaShiftY : Height;

    /// <summary>Bytes one pixel of the plane occupies, counting every interleaved component.</summary>
    public int GetPixelBytes(int index) => Describe(Format).PixelBytes[index];

    /// <summary>
    /// Copies a region into a new picture of the given size, repeating the edge pixels wherever the
    /// region extends past this picture.
    /// </summary>
    public StillPicture Crop(int left, int top, int width, int height)
    {
        var target = new StillPicture(width, height, Format)
        {
            Primaries = Primaries,
            Transfer = Transfer,
            Matrix = Matrix,
            Range = Range,
        };
        try
        {
            for (int plane = 0; plane < PlaneCount; plane++)
            {
                bool chroma = IsChromaPlane(plane);
                int shiftX = chroma ? ChromaShiftX : 0, shiftY = chroma ? ChromaShiftY : 0;
                int pixelBytes = GetPixelBytes(plane);
                int sourceWidth = GetPlaneWidth(plane), sourceHeight = GetPlaneHeight(plane);
                int targetWidth = target.GetPlaneWidth(plane), targetHeight = target.GetPlaneHeight(plane);
                int originX = left >> shiftX, originY = top >> shiftY;
                byte* source = (byte*)GetPlane(plane);
                byte* destination = (byte*)target.GetPlane(plane);
                int sourceStride = GetStride(plane), targetStride = target.GetStride(plane);
                for (int y = 0; y < targetHeight; y++)
                {
                    byte* sourceRow = source + (long)Math.Clamp(originY + y, 0, sourceHeight - 1) * sourceStride;
                    byte* targetRow = destination + (long)y * targetStride;
                    int inside = Math.Clamp(sourceWidth - originX, 0, targetWidth);
                    if (inside > 0)
                        Buffer.MemoryCopy(sourceRow + (long)originX * pixelBytes, targetRow, targetStride, (long)inside * pixelBytes);
                    for (int x = Math.Max(inside, 0); x < targetWidth; x++)
                        Buffer.MemoryCopy(sourceRow + (long)Math.Clamp(originX + x, 0, sourceWidth - 1) * pixelBytes,
                            targetRow + (long)x * pixelBytes, pixelBytes, pixelBytes);
                }
            }

            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        AVFrame* owned = frame;
        frame = null;
        if (owned != null)
            av_frame_free(&owned);
    }

    private bool IsChromaPlane(int index) => index is 1 or 2 && (ChromaShiftX > 0 || ChromaShiftY > 0);

    private static (int Planes, int ShiftX, int ShiftY, int[] PixelBytes) Describe(AVPixelFormat format) => format switch
    {
        AVPixelFormat.AV_PIX_FMT_YUV420P => (3, 1, 1, [1, 1, 1]),
        AVPixelFormat.AV_PIX_FMT_YUV422P => (3, 1, 0, [1, 1, 1]),
        AVPixelFormat.AV_PIX_FMT_YUV444P or AVPixelFormat.AV_PIX_FMT_GBRP => (3, 0, 0, [1, 1, 1]),
        AVPixelFormat.AV_PIX_FMT_YUV420P10LE or AVPixelFormat.AV_PIX_FMT_YUV420P12LE => (3, 1, 1, [2, 2, 2]),
        AVPixelFormat.AV_PIX_FMT_YUV422P10LE or AVPixelFormat.AV_PIX_FMT_YUV422P12LE => (3, 1, 0, [2, 2, 2]),
        AVPixelFormat.AV_PIX_FMT_YUV444P10LE or AVPixelFormat.AV_PIX_FMT_YUV444P12LE => (3, 0, 0, [2, 2, 2]),
        AVPixelFormat.AV_PIX_FMT_NV12 => (2, 1, 1, [1, 2]),
        AVPixelFormat.AV_PIX_FMT_P010LE => (2, 1, 1, [2, 4]),
        AVPixelFormat.AV_PIX_FMT_GRAY8 => (1, 0, 0, [1]),
        AVPixelFormat.AV_PIX_FMT_GRAY10LE or AVPixelFormat.AV_PIX_FMT_GRAY12LE or AVPixelFormat.AV_PIX_FMT_GRAY16LE => (1, 0, 0, [2]),
        AVPixelFormat.AV_PIX_FMT_RGB24 => (1, 0, 0, [3]),
        AVPixelFormat.AV_PIX_FMT_RGBA => (1, 0, 0, [4]),
        AVPixelFormat.AV_PIX_FMT_RGB48LE => (1, 0, 0, [6]),
        AVPixelFormat.AV_PIX_FMT_RGBA64LE => (1, 0, 0, [8]),
        _ => throw new NotSupportedException($"Still pictures do not support {format}."),
    };
}
