using System;
using System.IO;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Interop;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

public sealed partial class StillImageNative : IDisposable
{
    public required VideoFrame Frame { get; init; }
    public byte[] IccProfile { get; init; } = [];
    public int ColorPrimaries { get; init; }
    public int ColorTransfer { get; init; }

    public void Dispose() => Frame.Release();
}

public static unsafe partial class StillImage
{
    private static long nextPhotoPool;

    public static StillImageNative DecodeNative(Stream stream, uint? itemId = null,
        CancellationToken cancellationToken = default) => DecodeNative(stream, itemId, cancellationToken, AVCodecID.AV_CODEC_ID_NONE);

    public static StillImageNative DecodeNative(Stream stream, uint? itemId,
        CancellationToken cancellationToken, AVCodecID codecHint)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        bool hasExif = ExifOrientation.TryRead(stream, out int rotation, out bool mirror);
        bool associated = IsAssociatedTiff(stream);
        return Open(stream, true, 0, (parameters, decoded) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            VideoFrame frame = RetainNative(decoded, parameters, associated);
            if (frame is null)
                return null;
            ApplyColor(frame, decoded, parameters);
            ApplyOrientation(frame, decoded, parameters, hasExif, rotation, mirror);
            AVFrameSideData* profile = av_frame_get_side_data(decoded, AVFrameSideDataType.AV_FRAME_DATA_ICC_PROFILE);
            return new StillImageNative
            {
                Frame = frame,
                IccProfile = profile != null && profile->data != null && profile->size is > 0 and <= 4 * 1024 * 1024
                    ? new ReadOnlySpan<byte>(profile->data, (int)profile->size).ToArray() : [],
                ColorPrimaries = (int)(decoded->color_primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED
                    ? parameters->color_primaries : decoded->color_primaries),
                ColorTransfer = (int)ReadTransfer(decoded, parameters),
            };
        }, itemId: itemId, preserveHardware: true, codecHint: codecHint);
    }

    private static VideoFrame RetainNative(AVFrame* decoded, AVCodecParameters* parameters, bool associatedAlpha = false)
    {
        AVFrame* retained = av_frame_alloc();
        if (retained == null)
            return null;
        if (av_frame_ref(retained, decoded) < 0)
        {
            av_frame_free(&retained);
            return null;
        }
        IntPtr address = (IntPtr)retained;
        try
        {
            AVPixFmtDescriptor* descriptor = av_pix_fmt_desc_get((AVPixelFormat)retained->format);
            bool hasAlpha = descriptor != null && (descriptor->flags & AV_PIX_FMT_FLAG_ALPHA) != 0;
            if (retained->hw_frames_ctx != null)
            {
                var frames = (AVHWFramesContext*)retained->hw_frames_ctx->data;
                VideoPixelFormat? layout = VideoFrameMetadata.MapLayout(frames->sw_format);
                if (layout is null)
                    return null;
                VideoSurfaceKind kind = (AVPixelFormat)retained->format switch
                {
                    AVPixelFormat.AV_PIX_FMT_VAAPI => VideoSurfaceKind.Vaapi,
                    AVPixelFormat.AV_PIX_FMT_D3D11 => VideoSurfaceKind.Direct3D11,
                    AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX => VideoSurfaceKind.VideoToolbox,
                    _ => VideoSurfaceKind.None,
                };
                void* device = frames->device_ctx->hwctx;
                if (kind == VideoSurfaceKind.None || kind != VideoSurfaceKind.VideoToolbox && device == null)
                    return null;
                var frame = VideoFrame.CreateHosted(retained->width, retained->height, layout.Value);
                long pool = -Interlocked.Increment(ref nextPhotoPool);
                frame.Surface = new VideoSurfaceHandle(kind, kind == VideoSurfaceKind.VideoToolbox ? IntPtr.Zero : *(IntPtr*)device,
                    (long)(nint)retained->data[kind == VideoSurfaceKind.Direct3D11 ? 0u : 3u],
                    kind == VideoSurfaceKind.Direct3D11 ? (long)(nint)retained->data[1] : 0,
                    pool);
                frame.SurfaceWidth = frames->width;
                frame.SurfaceHeight = frames->height;
                frame.Recycled = _ =>
                {
                    ReleaseNative(address);
                    VideoFrameCapabilities.RetireSurfacePool(pool);
                };
                frame.DownloadPhotoSurface = () => DownloadNative(address, frame);
                retained = null;
                return frame;
            }

            if (hasAlpha && IsAlphaPremultiplied(retained, associatedAlpha))
                return PackNative(retained, descriptor->comp[0].depth > 8, associatedAlpha);

            VideoPixelFormat? mapped = (AVPixelFormat)retained->format switch
            {
                AVPixelFormat.AV_PIX_FMT_BGRA => VideoPixelFormat.Bgra32,
                AVPixelFormat.AV_PIX_FMT_RGBA64LE => VideoPixelFormat.Rgba64,
                _ => VideoFrameMetadata.MapLayout((AVPixelFormat)retained->format),
            };
            if (mapped is not null)
            {
                var frame = VideoFrame.CreateBorrowed(retained->width, retained->height, mapped.Value,
                    [(IntPtr)retained->data[0], (IntPtr)retained->data[1], (IntPtr)retained->data[2]],
                    [retained->linesize[0], retained->linesize[1], retained->linesize[2]],
                    _ => ReleaseNative(address));
                frame.HasAlpha = hasAlpha;
                retained = null;
                return frame;
            }
            if (descriptor != null && (hasAlpha || (descriptor->flags & AV_PIX_FMT_FLAG_RGB) != 0 || descriptor->nb_components < 3))
            {
                VideoFrame packed = PackNative(retained, descriptor->comp[0].depth > 8);
                if (packed is not null)
                    packed.HasAlpha = hasAlpha;
                return packed;
            }
            return Convert(retained, parameters);
        }
        finally
        {
            if (retained != null)
                av_frame_free(&retained);
        }
    }

    private static VideoFrame PackNative(AVFrame* decoded, bool deep, bool associatedAlpha = false)
    {
        VideoFrame frame = VideoFrame.CreateStandalone(decoded->width, decoded->height,
            deep ? VideoPixelFormat.Rgba64 : VideoPixelFormat.Bgra32);
        AVPixFmtDescriptor* descriptor = av_pix_fmt_desc_get((AVPixelFormat)decoded->format);
        frame.HasAlpha = descriptor != null && (descriptor->flags & AV_PIX_FMT_FLAG_ALPHA) != 0;
        SwsContext* scaler = sws_getContext(decoded->width, decoded->height, (AVPixelFormat)decoded->format,
            decoded->width, decoded->height, deep ? AVPixelFormat.AV_PIX_FMT_RGBA64LE : AVPixelFormat.AV_PIX_FMT_BGRA,
            SwsBilinear, null, null, null);
        if (scaler == null)
            return Fail(frame);
        try
        {
            if (!Scale(scaler, decoded, frame, decoded->height))
                return Fail(frame);
            if (frame.HasAlpha && IsAlphaPremultiplied(decoded, associatedAlpha))
                UnpremultiplyPhoto(frame);
            return frame;
        }
        finally { sws_freeContext(scaler); }
    }

    private static VideoFrame DownloadNative(IntPtr address, VideoFrame source)
    {
        AVFrame* decoded = av_frame_alloc();
        if (decoded == null)
            return null;
        try
        {
            int result = D3D11HardwareContext.TransferFrame(decoded, (AVFrame*)address);
            if (result >= 0)
                result = av_frame_copy_props(decoded, (AVFrame*)address);
            if (result < 0)
                throw new FfmpegException($"Could not download photo surface: {FfmpegError.Describe(result)}", result);
            AVCodecParameters parameters = default;
            VideoFrame frame = RetainNative(decoded, &parameters);
            if (frame is null)
                return null;
            frame.ColorSpace = source.ColorSpace;
            frame.ColorRange = source.ColorRange;
            frame.ColorPrimaries = source.ColorPrimaries;
            frame.ColorTransfer = source.ColorTransfer;
            frame.ChromaLocation = source.ChromaLocation;
            frame.FieldOrder = source.FieldOrder;
            frame.PhotoColor = source.PhotoColor;
            frame.GainMap = source.GainMap?.AddRef();
            return frame;
        }
        finally
        {
            av_frame_free(&decoded);
        }
    }

    private static void ReleaseNative(IntPtr address)
    {
        AVFrame* frame = (AVFrame*)address;
        av_frame_free(&frame);
    }
}