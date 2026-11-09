using System;
using System.Threading;
using AVFoundation;
using Avalonia.Threading;
using CoreMedia;
using CoreVideo;
using Foundation;
using LightMediaRenderer.Avalonia.Rendering;
using LightMediaRenderer.Contracts;
using LightStudio.Logging;
using ObjCRuntime;

namespace LightStudio.LightPlayer.iOS.Playback;

/// <summary>
/// Feeds the system's picture-in-picture window through an <see cref="AVSampleBufferDisplayLayer"/>.
/// No GPU work of the app's own is involved, since a backgrounded app may not submit any:
/// VideoToolbox pixel buffers are enqueued as they are, and every other frame is copied into a
/// pooled 4:2:0 or BGRA buffer on a worker thread, turned upright on the way.
/// </summary>
internal sealed unsafe class PictureInPictureVideoSink : IVideoSink
{
    /// <summary>Shorter side above which copied frames keep every other row and column.</summary>
    private const int DecimationThreshold = 1440;

    private const int TimeScale = 90000;

    private readonly AVSampleBufferDisplayLayer layer;
    private readonly CMTimebase timebase;
    private readonly object sync = new();
    private readonly AutoResetEvent wake = new(false);

    private VideoFrame? pending;
    private bool accepting;
    private bool primed;
    private bool hasImage;
    private Thread? worker;
    private CVPixelBufferPool? pool;
    private CVPixelFormatType poolFormat;
    private int poolWidth;
    private int poolHeight;
    private VideoPixelFormat? unsupportedFormat;
    private bool failureReported;

    public PictureInPictureVideoSink(AVSampleBufferDisplayLayer layer, CMTimebase timebase)
    {
        this.layer = layer;
        this.timebase = timebase;
    }

    /// <summary>Raised on the UI thread when the layer goes from empty to showing a picture.</summary>
    public event Action? ImageShown;

    /// <summary>Starts taking frames. Called on the UI thread.</summary>
    public void Start()
    {
        lock (sync)
            accepting = true;

        EnsureWorker();
    }

    /// <summary>Stops taking frames, and empties the layer unless <paramref name="keepImage"/>. Called on the UI thread.</summary>
    public void Stop(bool keepImage)
    {
        VideoFrame? dropped;
        lock (sync)
        {
            accepting = false;
            primed = false;
            dropped = pending;
            pending = null;
            if (!keepImage && hasImage)
            {
                RemoveImage();
                hasImage = false;
            }
        }

        dropped?.Release();
    }

    /// <summary>
    /// Shows one frame while not taking any, so the layer has a picture of the right shape before
    /// the window is asked for. Called on the UI thread.
    /// </summary>
    public void Prime(VideoFrame frame)
    {
        VideoFrame? replaced;
        lock (sync)
        {
            replaced = pending;
            pending = frame.AddRef();
            primed = true;
        }

        replaced?.Release();
        EnsureWorker();
        wake.Set();
    }

    public void Present(VideoFrame frame)
    {
        VideoFrame? replaced;
        lock (sync)
        {
            if (!accepting)
                return;

            // The worker keeps only the newest frame: one it could not convert in time is dropped
            // rather than queued behind, so a slow copy lowers the rate instead of the latency.
            replaced = pending;
            pending = frame.AddRef();
        }

        replaced?.Release();
        wake.Set();
    }

    public void ClearVideo()
    {
        VideoFrame? dropped;
        lock (sync)
        {
            dropped = pending;
            pending = null;
        }

        dropped?.Release();
    }

    private void EnsureWorker()
    {
        if (worker is not null)
            return;

        worker = new Thread(Run) { Name = "pip-frames", IsBackground = true };
        worker.Start();
    }

    private void Run()
    {
        while (true)
        {
            wake.WaitOne();
            VideoFrame? frame;
            lock (sync)
            {
                frame = pending;
                pending = null;
            }

            if (frame is null)
                continue;

            try
            {
                Show(frame);
            }
            catch (Exception exception)
            {
                if (!failureReported)
                {
                    failureReported = true;
                    AppLog.Error("iOS.PictureInPicture", "Could not show a video frame.", exception);
                }
            }
            finally
            {
                frame.Release();
            }
        }
    }

    private void Show(VideoFrame frame)
    {
        // A picture the device letterboxed to the page's size only arrives until the request for it
        // is withdrawn, which entering picture in picture does.
        if (frame.CoversOutput || frame.IsEndOfStream)
            return;

        using CVPixelBuffer? buffer = frame.Surface is { Kind: VideoSurfaceKind.VideoToolbox, IsValid: true }
            ? CanEnqueueSurface(frame) ? WrapSurface(frame) : CopySurface(frame)
            : CopyPlanes(frame);
        if (buffer is null)
            return;

        using CMVideoFormatDescription? format = CMVideoFormatDescription.CreateForImageBuffer(buffer, out CMFormatDescriptionError formatError);
        if (format is null)
            throw new InvalidOperationException($"Could not describe a {buffer.PixelFormatType} pixel buffer: {formatError}.");

        CMTime time = double.IsFinite(frame.Pts) ? CMTime.FromSeconds(frame.Pts, TimeScale) : CMTime.Invalid;
        var timing = new CMSampleTimingInfo
        {
            Duration = CMTime.Invalid,
            PresentationTimeStamp = time,
            DecodeTimeStamp = CMTime.Invalid,
        };
        using CMSampleBuffer? sample = CMSampleBuffer.CreateReadyWithImageBuffer(buffer, format, ref timing, out CMSampleBufferError sampleError);
        if (sample is null)
            throw new InvalidOperationException($"Could not wrap a pixel buffer for display: {sampleError}.");

        // The player's scheduler already presents each frame at its time; the layer's timebase only
        // tells the system's controls where playback is.
        if (sample.GetSampleAttachments(true) is [{ } attachments, ..])
            attachments.DisplayImmediately = true;

        bool shown;
        lock (sync)
        {
            if (!accepting && !primed)
                return;

            Enqueue(sample);
            primed = false;
            shown = !hasImage;
            hasImage = true;
        }

        if (!time.IsInvalid)
            timebase.Time = time;
        if (shown && ImageShown is { } handler)
            Dispatcher.UIThread.Post(handler);
    }

    private void Enqueue(CMSampleBuffer sample)
    {
        if (OperatingSystem.IsIOSVersionAtLeast(17))
        {
            AVSampleBufferVideoRenderer renderer = layer.SampleBufferRenderer;
            if (renderer.Status == AVQueuedSampleBufferRenderingStatus.Failed || renderer.RequiresFlushToResumeDecoding)
                renderer.Flush();
            renderer.Enqueue(sample);
            return;
        }

        if (layer.Status == AVQueuedSampleBufferRenderingStatus.Failed || layer.RequiresFlushToResumeDecoding)
            layer.Flush();
        layer.Enqueue(sample);
    }

    private void RemoveImage()
    {
        if (OperatingSystem.IsIOSVersionAtLeast(17))
            layer.SampleBufferRenderer.FlushWithRemovalOfDisplayedImage(true, static () => { });
        else
            layer.FlushAndRemoveImage();
    }

    /// <summary>Whether the layer can take the decoder's own buffer, with nothing to turn or convert.</summary>
    private static bool CanEnqueueSurface(VideoFrame frame) =>
        NormalizedRotation(frame) == 0 &&
        !frame.MirrorHorizontally &&
        frame.Format is VideoPixelFormat.Nv12 or VideoPixelFormat.P010 or VideoPixelFormat.Bgra32;

    private static CVPixelBuffer? WrapSurface(VideoFrame frame)
    {
        var buffer = Runtime.GetINativeObject<CVPixelBuffer>((IntPtr)frame.Surface.Id, false);
        if (buffer is null)
            return null;

        // The pool's buffers can be larger than the picture where the codec rounded the coded size
        // up; the clean aperture keeps the padding out of the window.
        int width = (int)buffer.Width, height = (int)buffer.Height;
        if (width > frame.Width || height > frame.Height)
        {
            using var aperture = new NSMutableDictionary();
            aperture[CVImageBuffer.CleanApertureWidthKey] = NSNumber.FromDouble(frame.Width);
            aperture[CVImageBuffer.CleanApertureHeightKey] = NSNumber.FromDouble(frame.Height);
            aperture[CVImageBuffer.CleanApertureHorizontalOffsetKey] = NSNumber.FromDouble((frame.Width - width) / 2.0);
            aperture[CVImageBuffer.CleanApertureVerticalOffsetKey] = NSNumber.FromDouble((frame.Height - height) / 2.0);
            buffer.SetAttachment(CVImageBuffer.CleanApertureKey, aperture, CVAttachmentMode.ShouldPropagate);
        }

        Describe(buffer, frame, frame.Format != VideoPixelFormat.Bgra32, frame.PixelAspectRatio);
        return buffer;
    }

    private CVPixelBuffer? CopySurface(VideoFrame frame)
    {
        using var source = Runtime.GetINativeObject<CVPixelBuffer>((IntPtr)frame.Surface.Id, false);
        if (source is null || !SurfaceMatches(source.PixelFormatType, frame.Format))
        {
            ReportUnsupported(frame.Format);
            return null;
        }

        CVReturn locked = source.Lock(CVPixelBufferLock.ReadOnly);
        if (locked != CVReturn.Success)
            throw new InvalidOperationException($"Could not read a decoded pixel buffer: {locked}.");

        try
        {
            var planes = new SourcePlanes();
            if (source.IsPlanar)
            {
                for (int plane = 0; plane < (int)source.PlaneCount && plane < 3; plane++)
                {
                    planes.Data[plane] = (byte*)source.GetBaseAddress(plane);
                    planes.Stride[plane] = (int)source.GetBytesPerRowOfPlane(plane);
                }
            }
            else
            {
                planes.Data[0] = (byte*)source.BaseAddress;
                planes.Stride[0] = (int)source.BytesPerRow;
            }

            return Copy(frame, planes);
        }
        finally
        {
            source.Unlock(CVPixelBufferLock.ReadOnly);
        }
    }

    private CVPixelBuffer? CopyPlanes(VideoFrame frame)
    {
        if (frame.PlaneCount == 0 || frame.GetPlane(0) == IntPtr.Zero)
        {
            ReportUnsupported(frame.Format);
            return null;
        }

        var planes = new SourcePlanes();
        for (int plane = 0; plane < frame.PlaneCount && plane < 3; plane++)
        {
            planes.Data[plane] = (byte*)frame.GetPlane(plane);
            planes.Stride[plane] = frame.GetStride(plane);
        }

        return Copy(frame, planes);
    }

    private CVPixelBuffer? Copy(VideoFrame frame, SourcePlanes planes)
    {
        bool rgb = frame.Format == VideoPixelFormat.Bgra32;
        VideoPlanarLayout layout = VideoPlanarLayout.For(frame.Format);
        if (!rgb && (!VideoPlanarLayout.IsPlanar(frame.Format) || layout.PlaneCount is not (2 or 3)))
        {
            ReportUnsupported(frame.Format);
            return null;
        }

        int rotation = NormalizedRotation(frame);
        bool turned = rotation is 90 or 270;
        int step = Math.Min(frame.Width, frame.Height) > DecimationThreshold ? 2 : 1;
        int sourceWidth = frame.Width / step, sourceHeight = frame.Height / step;
        int width = turned ? sourceHeight : sourceWidth, height = turned ? sourceWidth : sourceHeight;
        if (!rgb)
        {
            width &= ~1;
            height &= ~1;
        }

        if (width < 2 || height < 2)
            return null;

        bool wide = !rgb && layout.BytesPerSample == 2;
        bool full = frame.ColorRange == VideoColorRange.Full;
        CVPixelFormatType format = rgb ? CVPixelFormatType.CV32BGRA
            : wide ? full ? CVPixelFormatType.CV420YpCbCr10BiPlanarFullRange : CVPixelFormatType.CV420YpCbCr10BiPlanarVideoRange
            : full ? CVPixelFormatType.CV420YpCbCr8BiPlanarFullRange : CVPixelFormatType.CV420YpCbCr8BiPlanarVideoRange;
        CVPixelBuffer target = Rent(format, width, height);
        CVReturn locked = target.Lock(CVPixelBufferLock.None);
        if (locked != CVReturn.Success)
        {
            target.Dispose();
            throw new InvalidOperationException($"Could not write a pixel buffer: {locked}.");
        }

        try
        {
            var map = new Mapping(rotation, frame.MirrorHorizontally, step, sourceWidth, sourceHeight);
            if (rgb)
            {
                CopyBgra(planes, (byte*)target.BaseAddress, (int)target.BytesPerRow, width, height, map);
            }
            else
            {
                int shift = wide && !layout.MsbAligned ? 16 - layout.BitDepth : 0;
                var luma = (byte*)target.GetBaseAddress(0);
                var chroma = (byte*)target.GetBaseAddress(1);
                int lumaStride = (int)target.GetBytesPerRowOfPlane(0);
                int chromaStride = (int)target.GetBytesPerRowOfPlane(1);
                if (wide)
                {
                    CopyLuma16(planes, shift, luma, lumaStride, width, height, map);
                    CopyChroma16(planes, layout, shift, chroma, chromaStride, width / 2, height / 2, map);
                }
                else
                {
                    CopyLuma8(planes, luma, lumaStride, width, height, map);
                    CopyChroma8(planes, layout, chroma, chromaStride, width / 2, height / 2, map);
                }
            }
        }
        finally
        {
            target.Unlock(CVPixelBufferLock.None);
        }

        double aspect = frame.PixelAspectRatio > 0 && double.IsFinite(frame.PixelAspectRatio) ? frame.PixelAspectRatio : 1;
        Describe(target, frame, !rgb, turned ? 1 / aspect : aspect);
        return target;
    }

    private CVPixelBuffer Rent(CVPixelFormatType format, int width, int height)
    {
        if (pool is null || poolFormat != format || poolWidth != width || poolHeight != height)
        {
            pool?.Dispose();
            var attributes = new CVPixelBufferAttributes(format, width, height) { AllocateWithIOSurface = true };
            pool = new CVPixelBufferPool(new CVPixelBufferPoolSettings { MinimumBufferCount = 3 }, attributes);
            poolFormat = format;
            poolWidth = width;
            poolHeight = height;
        }

        return pool.CreatePixelBuffer(null, out CVReturn error)
            ?? throw new InvalidOperationException($"Could not allocate a {width}x{height} {format} pixel buffer: {error}.");
    }

    private static void CopyLuma8(SourcePlanes source, byte* target, int targetStride, int width, int height, Mapping map)
    {
        byte* plane = source.Data[0];
        int stride = source.Stride[0];
        if (map.IsIdentity)
        {
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy(plane + (long)y * stride, target + (long)y * targetStride, width, width);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            byte* row = target + (long)y * targetStride;
            for (int x = 0; x < width; x++)
            {
                (int sx, int sy) = map.Source(x, y);
                row[x] = plane[(long)sy * stride + sx];
            }
        }
    }

    private static void CopyLuma16(SourcePlanes source, int shift, byte* target, int targetStride, int width, int height, Mapping map)
    {
        byte* plane = source.Data[0];
        int stride = source.Stride[0];
        if (map.IsIdentity && shift == 0)
        {
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy(plane + (long)y * stride, target + (long)y * targetStride, width * 2, width * 2);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            var row = (ushort*)(target + (long)y * targetStride);
            for (int x = 0; x < width; x++)
            {
                (int sx, int sy) = map.Source(x, y);
                row[x] = (ushort)(((ushort*)(plane + (long)sy * stride))[sx] << shift);
            }
        }
    }

    /// <summary>
    /// Writes interleaved Cb,Cr at half the output size. Each output pair takes the source chroma
    /// sample under the top-left luma sample of its block, which drops the extra chroma resolution
    /// of 4:2:2 and 4:4:4 sources.
    /// </summary>
    private static void CopyChroma8(SourcePlanes source, VideoPlanarLayout layout, byte* target, int targetStride,
        int width, int height, Mapping map)
    {
        Chroma chroma = Chroma.Of(source, layout);
        if (map.IsIdentity && layout.InterleavedChroma && !layout.SwappedChroma)
        {
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy(source.Data[1] + (long)y * source.Stride[1], target + (long)y * targetStride, width * 2, width * 2);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            byte* row = target + (long)y * targetStride;
            for (int x = 0; x < width; x++)
            {
                (int sx, int sy) = map.Source(x * 2, y * 2);
                sx >>= layout.ChromaShiftX;
                sy >>= layout.ChromaShiftY;
                row[x * 2] = chroma.Cb[(long)sy * chroma.CbStride + (long)sx * chroma.Pitch];
                row[x * 2 + 1] = chroma.Cr[(long)sy * chroma.CrStride + (long)sx * chroma.Pitch];
            }
        }
    }

    private static void CopyChroma16(SourcePlanes source, VideoPlanarLayout layout, int shift, byte* target, int targetStride,
        int width, int height, Mapping map)
    {
        Chroma chroma = Chroma.Of(source, layout);
        if (map.IsIdentity && shift == 0 && layout.InterleavedChroma && !layout.SwappedChroma)
        {
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy(source.Data[1] + (long)y * source.Stride[1], target + (long)y * targetStride, width * 4, width * 4);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            var row = (ushort*)(target + (long)y * targetStride);
            for (int x = 0; x < width; x++)
            {
                (int sx, int sy) = map.Source(x * 2, y * 2);
                sx >>= layout.ChromaShiftX;
                sy >>= layout.ChromaShiftY;
                row[x * 2] = (ushort)(*(ushort*)(chroma.Cb + (long)sy * chroma.CbStride + (long)sx * chroma.Pitch) << shift);
                row[x * 2 + 1] = (ushort)(*(ushort*)(chroma.Cr + (long)sy * chroma.CrStride + (long)sx * chroma.Pitch) << shift);
            }
        }
    }

    private static void CopyBgra(SourcePlanes source, byte* target, int targetStride, int width, int height, Mapping map)
    {
        byte* plane = source.Data[0];
        int stride = source.Stride[0];
        if (map.IsIdentity)
        {
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy(plane + (long)y * stride, target + (long)y * targetStride, width * 4, width * 4);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            var row = (uint*)(target + (long)y * targetStride);
            for (int x = 0; x < width; x++)
            {
                (int sx, int sy) = map.Source(x, y);
                row[x] = ((uint*)(plane + (long)sy * stride))[sx];
            }
        }
    }

    /// <summary>Tags a buffer with the color and pixel shape the renderer would have drawn it with.</summary>
    private static void Describe(CVPixelBuffer buffer, VideoFrame frame, bool yuv, double pixelAspectRatio)
    {
        if (yuv)
            buffer.SetAttachment(CVImageBuffer.YCbCrMatrixKey, Matrix(frame.ColorSpace), CVAttachmentMode.ShouldPropagate);
        buffer.SetAttachment(CVImageBuffer.ColorPrimariesKey, Primaries(frame.ColorPrimaries), CVAttachmentMode.ShouldPropagate);
        buffer.SetAttachment(CVImageBuffer.TransferFunctionKey, Transfer(frame.ColorTransfer), CVAttachmentMode.ShouldPropagate);

        if (double.IsFinite(pixelAspectRatio) && pixelAspectRatio > 0 && Math.Abs(pixelAspectRatio - 1) > 0.001)
        {
            const int Denominator = 10000;
            using var spacing = new NSMutableDictionary();
            spacing[CVImageBuffer.PixelAspectRatioHorizontalSpacingKey] =
                NSNumber.FromInt32((int)Math.Round(pixelAspectRatio * Denominator));
            spacing[CVImageBuffer.PixelAspectRatioVerticalSpacingKey] = NSNumber.FromInt32(Denominator);
            buffer.SetAttachment(CVImageBuffer.PixelAspectRatioKey, spacing, CVAttachmentMode.ShouldPropagate);
        }
        else
        {
            buffer.RemoveAttachment(CVImageBuffer.PixelAspectRatioKey);
        }
    }

    private static NSString Matrix(VideoColorSpace space) => space switch
    {
        VideoColorSpace.Bt601 => CVImageBuffer.YCbCrMatrix_ITU_R_601_4,
        VideoColorSpace.Bt2020 => CVImageBuffer.YCbCrMatrix_ITU_R_2020,
        _ => CVImageBuffer.YCbCrMatrix_ITU_R_709_2,
    };

    private static NSString Primaries(VideoColorPrimaries primaries) => primaries switch
    {
        VideoColorPrimaries.Bt2020 => CVImageBuffer.ColorPrimaries_ITU_R_2020,
        VideoColorPrimaries.Smpte170M or VideoColorPrimaries.Bt470M => CVImageBuffer.ColorPrimaries_SMPTE_C,
        VideoColorPrimaries.Bt470Bg => CVImageBuffer.ColorPrimaries_EBU_3213,
        VideoColorPrimaries.DisplayP3 => CVImageBuffer.ColorPrimaries_P3_D65,
        VideoColorPrimaries.DciP3 => CVImageBuffer.ColorPrimaries_DCI_P3,
        _ => CVImageBuffer.ColorPrimaries_ITU_R_709_2,
    };

    private static NSString Transfer(VideoColorTransfer transfer) => transfer switch
    {
        VideoColorTransfer.Pq => CVImageBuffer.TransferFunction_SMPTE_ST_2084_PQ,
        VideoColorTransfer.Hlg => CVImageBuffer.TransferFunction_ITU_R_2100_HLG,
        VideoColorTransfer.Srgb => CVImageBuffer.TransferFunction_sRGB,
        _ => CVImageBuffer.TransferFunction_ITU_R_709_2,
    };

    private static bool SurfaceMatches(CVPixelFormatType type, VideoPixelFormat format) => format switch
    {
        VideoPixelFormat.Nv12 => type is CVPixelFormatType.CV420YpCbCr8BiPlanarVideoRange or CVPixelFormatType.CV420YpCbCr8BiPlanarFullRange,
        VideoPixelFormat.P010 => type is CVPixelFormatType.CV420YpCbCr10BiPlanarVideoRange or CVPixelFormatType.CV420YpCbCr10BiPlanarFullRange,
        VideoPixelFormat.Yuv420p => type is CVPixelFormatType.CV420YpCbCr8Planar or CVPixelFormatType.CV420YpCbCr8PlanarFullRange,
        VideoPixelFormat.Bgra32 => type == CVPixelFormatType.CV32BGRA,
        _ => false,
    };

    private static int NormalizedRotation(VideoFrame frame) => ((frame.Rotation % 360) + 360) % 360 / 90 * 90;

    private void ReportUnsupported(VideoPixelFormat format)
    {
        if (unsupportedFormat == format)
            return;

        unsupportedFormat = format;
        AppLog.Warning("iOS.PictureInPicture", $"Cannot show {format} frames in picture in picture.");
    }

    private sealed class SourcePlanes
    {
        public byte*[] Data { get; } = new byte*[3];

        public int[] Stride { get; } = new int[3];
    }

    /// <summary>Takes an output position to the source sample it shows: turned, mirrored and decimated.</summary>
    private readonly struct Mapping
    {
        private readonly int ax, bx, cx, ay, by, cy, step;

        /// <param name="width">Source width after decimation.</param>
        /// <param name="height">Source height after decimation.</param>
        public Mapping(int rotation, bool mirror, int step, int width, int height)
        {
            // The inverse of the permutation VideoFrameSnapshots applies, so the picture turns the
            // way the renderer turns it.
            (ax, bx, cx, ay, by, cy) = rotation switch
            {
                90 => (0, 1, 0, -1, 0, height - 1),
                180 => (-1, 0, width - 1, 0, -1, height - 1),
                270 => (0, -1, width - 1, 1, 0, 0),
                _ => (1, 0, 0, 0, 1, 0),
            };
            if (mirror)
            {
                ax = -ax;
                bx = -bx;
                cx = width - 1 - cx;
            }

            this.step = step;
            IsIdentity = rotation == 0 && !mirror && step == 1;
        }

        public bool IsIdentity { get; }

        public (int X, int Y) Source(int x, int y) =>
            ((ax * x + bx * y + cx) * step, (ay * x + by * y + cy) * step);
    }

    private readonly struct Chroma(byte* cb, byte* cr, int cbStride, int crStride, int pitch)
    {
        public byte* Cb { get; } = cb;

        public byte* Cr { get; } = cr;

        public int CbStride { get; } = cbStride;

        public int CrStride { get; } = crStride;

        /// <summary>Bytes from one sample of a component to the next along a row.</summary>
        public int Pitch { get; } = pitch;

        public static Chroma Of(SourcePlanes source, VideoPlanarLayout layout)
        {
            int bytes = layout.BytesPerSample;
            if (!layout.InterleavedChroma)
                return new(source.Data[1], source.Data[2], source.Stride[1], source.Stride[2], bytes);

            byte* pair = source.Data[1];
            return layout.SwappedChroma
                ? new(pair + bytes, pair, source.Stride[1], source.Stride[1], bytes * 2)
                : new(pair, pair + bytes, source.Stride[1], source.Stride[1], bytes * 2);
        }
    }
}
