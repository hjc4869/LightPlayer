#if MACOS || IOS
using System;
using System.Runtime.InteropServices;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Interop;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;
using LightStudio.NativeBindings;
using LightStudio.NativeBindings.Types;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>Resizes CoreVideo surfaces with a VideoToolbox pixel-transfer session.</summary>
[Include("libavformat/avformat.h")]
[Include("libavutil/hwcontext.h")]
internal sealed unsafe partial class VideoToolboxVideoScaler(long id, AVBufferRef* device, int surfaceBudget)
    : IHardwareVideoScaler
{
    private const double RebuildCooldownSeconds = 0.4;

    private IntPtr session;
    private AVBufferRef* pool;
    private AVFrame* output;
    private IntPtr sourcePool;
    private AVPixelFormat sourceFormat;
    private AVColorRange sourceRange;
    private VideoOutputRequest? configured;
    private double lastConfigured = double.NegativeInfinity;
    private IntPtr aperture;
    private (int Width, int Height, double Left, double Top) apertureBounds;
    private bool abandoned;

    public static void Register() => HardwareVideoScalerFactory.Register(VideoSurfaceKind.VideoToolbox,
        static (id, device, timeBase, surfaceBudget) => new VideoToolboxVideoScaler(id, device, surfaceBudget));

    public bool IsAvailable => !abandoned;
    public string Status { get; private set; } = string.Empty;
    public (int Width, int Height) ActiveSize { get; private set; }
    public bool CoversOutput => false;

    public bool TryScale(AVFrame* input, VideoOutputRequest request, out AVFrame* result)
    {
        result = input;
        ActiveSize = default;
        if (abandoned || request.PictureWidth <= 0 || request.PictureHeight <= 0 ||
            input->format != (int)AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX ||
            input->hw_frames_ctx == null || input->data[3] == null)
            return false;

        var frames = (AVHWFramesContext*)input->hw_frames_ctx->data;
        if (frames->sw_format is not (AVPixelFormat.AV_PIX_FMT_NV12 or AVPixelFormat.AV_PIX_FMT_P010LE or AVPixelFormat.AV_PIX_FMT_BGRA))
        {
            Abandon($"VideoToolbox scaling does not support {frames->sw_format} surfaces.");
            return false;
        }

        request = VideoOutputRequest.ForPicture(request.PictureWidth, request.PictureHeight,
            request.PackedRgb && CanConvertToRgb(input, frames->sw_format));
        if (request.PictureWidth == input->width && request.PictureHeight == input->height && !request.PackedRgb)
        {
            ActiveSize = (input->width, input->height);
            return true;
        }

        try
        {
            AVColorRange range = ((VideoToolboxFramesContext*)frames->hwctx)->ColorRange;
            bool changedSource = sourcePool != (IntPtr)input->hw_frames_ctx->data ||
                sourceFormat != frames->sw_format || sourceRange != range;
            if (pool == null || changedSource || configured != request &&
                (configured?.PackedRgb != request.PackedRgb || MediaTime.Now - lastConfigured >= RebuildCooldownSeconds))
            {
                Configure(input, frames, range, request);
            }

            VideoOutputRequest active = configured!;
            av_frame_unref(output);
            FfmpegError.ThrowIfError(av_hwframe_get_buffer(pool, output, 0), "av_hwframe_get_buffer(VideoToolbox)");
            FfmpegError.ThrowIfError(av_frame_copy_props(output, input), "av_frame_copy_props(VideoToolbox)");

            SetSourceAperture(input);
            int transferred = Native.VTPixelTransferSessionTransferImage(session,
                (IntPtr)input->data[3], (IntPtr)output->data[3]);
            CheckStatus(transferred, "VTPixelTransferSessionTransferImage");

            output->crop_left = output->crop_top = output->crop_right = output->crop_bottom = 0;
            if (active.PackedRgb)
            {
                output->colorspace = AVColorSpace.AVCOL_SPC_RGB;
                output->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            }
            ActiveSize = (active.PictureWidth, active.PictureHeight);
            result = output;
            return true;
        }
        catch (FfmpegException exception)
        {
            AppLog.Warning("FFmpeg.Scale", $"#{id} VideoToolbox pixel transfer failed.", exception);
            Abandon(exception.Message);
            return false;
        }
    }

    private static bool CanConvertToRgb(AVFrame* frame, AVPixelFormat format) =>
        format == AVPixelFormat.AV_PIX_FMT_NV12 &&
        frame->color_range is AVColorRange.AVCOL_RANGE_MPEG or AVColorRange.AVCOL_RANGE_JPEG &&
        frame->color_trc is AVColorTransferCharacteristic.AVCOL_TRC_BT709 or AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1 &&
        frame->colorspace is AVColorSpace.AVCOL_SPC_BT709 or AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M;

    private void Configure(AVFrame* input, AVHWFramesContext* source, AVColorRange range, VideoOutputRequest request)
    {
        Release();
        CheckStatus(Native.VTPixelTransferSessionCreate(IntPtr.Zero, out session), "VTPixelTransferSessionCreate");
        CheckStatus(Native.VTSessionSetProperty(session, Native.ScalingMode, Native.CropSourceToCleanAperture),
            "VTSessionSetProperty(ScalingMode)");
        CheckStatus(Native.VTSessionSetProperty(session, Native.RealTime, Native.BooleanTrue),
            "VTSessionSetProperty(RealTime)");

        pool = av_hwframe_ctx_alloc(device);
        output = av_frame_alloc();
        if (pool == null || output == null)
            throw new FfmpegException("Could not allocate VideoToolbox scaler frames.", 0);

        var frames = (AVHWFramesContext*)pool->data;
        frames->format = AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX;
        frames->sw_format = request.PackedRgb ? AVPixelFormat.AV_PIX_FMT_BGRA : source->sw_format;
        frames->width = request.PictureWidth;
        frames->height = request.PictureHeight;
        frames->initial_pool_size = surfaceBudget;
        ((VideoToolboxFramesContext*)frames->hwctx)->ColorRange = request.PackedRgb ? AVColorRange.AVCOL_RANGE_JPEG : range;
        FfmpegError.ThrowIfError(av_hwframe_ctx_init(pool), "av_hwframe_ctx_init(VideoToolbox)");

        sourcePool = (IntPtr)input->hw_frames_ctx->data;
        sourceFormat = source->sw_format;
        sourceRange = range;
        configured = request;
        lastConfigured = MediaTime.Now;
        Status = string.Empty;
        AppLog.Information("FFmpeg.Decode", $"#{id} VideoToolbox is resizing {input->width}x{input->height} to " +
            $"{request.PictureWidth}x{request.PictureHeight}" + (request.PackedRgb ? ", converting SDR to RGB." : "."));
    }

    private void SetSourceAperture(AVFrame* input)
    {
        if (input->width <= 0 || input->height <= 0 ||
            input->crop_left >= (ulong)input->width || input->crop_right >= (ulong)input->width ||
            input->crop_top >= (ulong)input->height || input->crop_bottom >= (ulong)input->height ||
            input->crop_left + input->crop_right >= (ulong)input->width ||
            input->crop_top + input->crop_bottom >= (ulong)input->height)
            throw new FfmpegException("Invalid VideoToolbox source crop.", 0);

        int width = input->width - (int)input->crop_left - (int)input->crop_right;
        int height = input->height - (int)input->crop_top - (int)input->crop_bottom;
        IntPtr buffer = (IntPtr)input->data[3];
        nuint bufferWidth = Native.CVPixelBufferGetWidth(buffer);
        nuint bufferHeight = Native.CVPixelBufferGetHeight(buffer);
        if ((nuint)input->width > bufferWidth || (nuint)input->height > bufferHeight)
            throw new FfmpegException("VideoToolbox source exceeds its pixel buffer.", 0);

        var bounds = (width, height, input->crop_left + width / 2.0 - bufferWidth / 2.0,
            input->crop_top + height / 2.0 - bufferHeight / 2.0);
        if (aperture == IntPtr.Zero || apertureBounds != bounds)
        {
            if (aperture != IntPtr.Zero)
                Native.CFRelease(aperture);
            aperture = IntPtr.Zero;
            aperture = Native.CreateAperture(bounds.width, bounds.height, bounds.Item3, bounds.Item4);
            apertureBounds = bounds;
        }
        Native.CVBufferSetAttachment(buffer, Native.CleanAperture, aperture, 1);
    }

    private static void CheckStatus(int result, string operation)
    {
        if (result != 0)
            throw new FfmpegException($"{operation} failed with OSStatus {result}.", result);
    }

    private void Abandon(string reason)
    {
        abandoned = true;
        Status = reason;
        Release();
        AppLog.Warning("FFmpeg.Decode", $"#{id} VideoToolbox scaling was withdrawn: {reason}");
    }

    private void Release()
    {
        if (session != IntPtr.Zero)
        {
            Native.VTPixelTransferSessionInvalidate(session);
            Native.CFRelease(session);
            session = IntPtr.Zero;
        }
        AVFrame* retainedFrame = output;
        output = null;
        av_frame_free(&retainedFrame);
        AVBufferRef* retainedPool = pool;
        pool = null;
        av_buffer_unref(&retainedPool);
        if (aperture != IntPtr.Zero)
        {
            Native.CFRelease(aperture);
            aperture = IntPtr.Zero;
        }
        configured = null;
        ActiveSize = default;
    }

    public void Dispose() => Release();

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoToolboxFramesContext
    {
        public AVColorRange ColorRange;
    }

    private static class Native
    {
        private const string VideoToolbox = "/System/Library/Frameworks/VideoToolbox.framework/VideoToolbox";
        private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private static readonly IntPtr VideoToolboxLibrary = NativeLibrary.Load(VideoToolbox);
        private static readonly IntPtr CoreVideoLibrary = NativeLibrary.Load(CoreVideo);
        private static readonly IntPtr CoreFoundationLibrary = NativeLibrary.Load(CoreFoundation);

        public static readonly IntPtr ScalingMode = Constant(VideoToolboxLibrary, "kVTPixelTransferPropertyKey_ScalingMode");
        public static readonly IntPtr CropSourceToCleanAperture = Constant(VideoToolboxLibrary, "kVTScalingMode_CropSourceToCleanAperture");
        public static readonly IntPtr RealTime = Constant(VideoToolboxLibrary, "kVTPixelTransferPropertyKey_RealTime");
        public static readonly IntPtr BooleanTrue = Constant(CoreFoundationLibrary, "kCFBooleanTrue");
        public static readonly IntPtr CleanAperture = Constant(CoreVideoLibrary, "kCVImageBufferCleanApertureKey");
        private static readonly IntPtr ApertureWidth = Constant(CoreVideoLibrary, "kCVImageBufferCleanApertureWidthKey");
        private static readonly IntPtr ApertureHeight = Constant(CoreVideoLibrary, "kCVImageBufferCleanApertureHeightKey");
        private static readonly IntPtr ApertureOffsetX = Constant(CoreVideoLibrary, "kCVImageBufferCleanApertureHorizontalOffsetKey");
        private static readonly IntPtr ApertureOffsetY = Constant(CoreVideoLibrary, "kCVImageBufferCleanApertureVerticalOffsetKey");
        private static readonly IntPtr DictionaryKeys = NativeLibrary.GetExport(CoreFoundationLibrary, "kCFTypeDictionaryKeyCallBacks");
        private static readonly IntPtr DictionaryValues = NativeLibrary.GetExport(CoreFoundationLibrary, "kCFTypeDictionaryValueCallBacks");

        private static IntPtr Constant(IntPtr library, string name) => *(IntPtr*)NativeLibrary.GetExport(library, name);

        public static IntPtr CreateAperture(int width, int height, double left, double top)
        {
            IntPtr* keys = stackalloc IntPtr[4] { ApertureWidth, ApertureHeight, ApertureOffsetX, ApertureOffsetY };
            double* components = stackalloc double[4] { width, height, left, top };
            IntPtr* values = stackalloc IntPtr[4] { IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero };
            try
            {
                for (int index = 0; index < 4; index++)
                {
                    values[index] = CFNumberCreate(IntPtr.Zero, 13, &components[index]);
                    if (values[index] == IntPtr.Zero)
                        throw new FfmpegException("Could not allocate a CoreVideo aperture number.", 0);
                }
                IntPtr dictionary = CFDictionaryCreate(IntPtr.Zero, keys, values, 4, DictionaryKeys, DictionaryValues);
                return dictionary != IntPtr.Zero ? dictionary : throw new FfmpegException("Could not allocate a CoreVideo aperture.", 0);
            }
            finally
            {
                for (int index = 0; index < 4; index++)
                    if (values[index] != IntPtr.Zero)
                        CFRelease(values[index]);
            }
        }

        [DllImport(VideoToolbox)]
        public static extern int VTPixelTransferSessionCreate(IntPtr allocator, out IntPtr session);
        [DllImport(VideoToolbox)]
        public static extern void VTPixelTransferSessionInvalidate(IntPtr session);
        [DllImport(VideoToolbox)]
        public static extern int VTPixelTransferSessionTransferImage(IntPtr session, IntPtr source, IntPtr destination);
        [DllImport(VideoToolbox)]
        public static extern int VTSessionSetProperty(IntPtr session, IntPtr key, IntPtr value);
        [DllImport(CoreVideo)]
        public static extern nuint CVPixelBufferGetWidth(IntPtr buffer);
        [DllImport(CoreVideo)]
        public static extern nuint CVPixelBufferGetHeight(IntPtr buffer);
        [DllImport(CoreVideo)]
        public static extern void CVBufferSetAttachment(IntPtr buffer, IntPtr key, IntPtr value, uint mode);
        [DllImport(CoreFoundation)]
        public static extern void CFRelease(IntPtr value);
        [DllImport(CoreFoundation)]
        private static extern IntPtr CFNumberCreate(IntPtr allocator, nint type, void* value);
        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr* keys, IntPtr* values,
            nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);
    }
}
#endif