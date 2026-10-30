using System;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Interop;
using LightStudio.Logging;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

internal sealed unsafe partial class StillImageHardwareDecoder : IDisposable
{
    internal const int MaximumTileWorkers = 16;

    private static readonly delegate* unmanaged[Cdecl]<AVCodecContext*, AVPixelFormat*, AVPixelFormat> FormatCallback = &SelectFormat;
    private readonly AVCodec* codec;
    private readonly AVPixelFormat pixelFormat;
    private readonly AVCodecID codecId;
    private readonly IntPtr[] tileContexts = new IntPtr[MaximumTileWorkers];
    private readonly IntPtr[] tileParameters = new IntPtr[MaximumTileWorkers];
    private AVBufferRef* device;
    private bool disposed;

    private StillImageHardwareDecoder(AVBufferRef* device, AVPixelFormat pixelFormat, AVCodec* codec)
    {
        this.device = device;
        this.pixelFormat = pixelFormat;
        this.codec = codec;
        codecId = codec->id;
    }

    public bool UsedHardware { get; private set; }
    public AVPixelFormat PixelFormat => pixelFormat;

    public static bool IsEnabled => !OperatingSystem.IsBrowser() && !OperatingSystem.IsAndroid() &&
        (OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) &&
        !(AppContext.TryGetSwitch("LightStudio.FfmpegShim.DisablePhotoHardwareDecoding", out bool disabled) && disabled);

    public static StillImageHardwareDecoder TryCreate(AVCodecParameters* parameters)
    {
        if (!IsEnabled || parameters->codec_id is not (AVCodecID.AV_CODEC_ID_HEVC or AVCodecID.AV_CODEC_ID_AV1))
        {
            return null;
        }

        // FFmpeg's D3D11VA decoder accepts HEVC Main and Main 10 profiles.
        if (OperatingSystem.IsWindows() && parameters->codec_id == AVCodecID.AV_CODEC_ID_HEVC &&
            parameters->profile is not (AV_PROFILE_UNKNOWN or AV_PROFILE_HEVC_MAIN or AV_PROFILE_HEVC_MAIN_10))
            return null;

        AVHWDeviceType deviceType = OperatingSystem.IsWindows() ? AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA
            : OperatingSystem.IsLinux() ? AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI
            : OperatingSystem.IsMacOS() ? AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX
            : AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            return null;

        AVCodec* codec = avcodec_find_decoder(parameters->codec_id);
        AVCodecHWConfig* configuration = FindHardwareConfiguration(codec, deviceType);
        if (configuration == null)
        {
            void* iterator = null;
            while ((codec = av_codec_iterate(&iterator)) != null)
            {
                if (codec->id != parameters->codec_id || av_codec_is_decoder(codec) == 0)
                    continue;
                configuration = FindHardwareConfiguration(codec, deviceType);
                if (configuration != null)
                    break;
            }
        }
        if (configuration == null)
            return null;

        AVBufferRef* device = AdoptRendererDevice(deviceType);
        int result = device != null ? 0 : D3D11HardwareContext.CreateDevice(&device, deviceType, null, null, 0);
        if (result < 0)
        {
            if (device != null)
                av_buffer_unref(&device);
            AppLog.Warning("FFmpeg.Photo", $"Could not initialize {deviceType} photo decoding: {FfmpegError.Describe(result)}; using software.");
            return null;
        }

        return new StillImageHardwareDecoder(device, configuration->pix_fmt, codec);
    }

    private static AVCodecHWConfig* FindHardwareConfiguration(AVCodec* codec, AVHWDeviceType deviceType)
    {
        if (codec == null)
            return null;
        for (int index = 0; ; index++)
        {
            AVCodecHWConfig* configuration = avcodec_get_hw_config(codec, index);
            if (configuration == null || configuration->device_type == deviceType && (configuration->methods & 1) != 0)
                return configuration;
        }
    }

    private static AVBufferRef* AdoptRendererDevice(AVHWDeviceType type)
    {
        if (type != AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA ||
            VideoFrameCapabilities.SupportedSurfaceKind != VideoSurfaceKind.Direct3D11)
            return null;
        IntPtr renderer = VideoFrameCapabilities.SupportedSurfaceDevice;
        return renderer == IntPtr.Zero ? null : D3D11HardwareContext.Adopt(renderer, 0x200 | 0x8);
    }

    public AVCodec* FindDecoder(AVCodecParameters* parameters) =>
        parameters->codec_id == codecId ? codec : avcodec_find_decoder(parameters->codec_id);

    public void Configure(AVCodecContext* context)
    {
        if (context->codec_id != codecId)
            return;

        context->hw_device_ctx = av_buffer_ref(device);
        if (context->hw_device_ctx == null)
            return;

        context->opaque = (void*)(nint)pixelFormat;
        context->get_format = FormatCallback;
        context->thread_count = 1;
    }

    public AVCodecContext* GetTileDecoder(AVCodecParameters* parameters, int worker)
    {
        var context = (AVCodecContext*)tileContexts[worker];
        if (context == null)
            return null;
        if (!ParametersMatch((AVCodecParameters*)tileParameters[worker], parameters))
        {
            ReleaseTileDecoder(worker);
            return null;
        }

        avcodec_flush_buffers(context);
        return context;
    }

    public bool CacheTileDecoder(AVCodecContext* context, AVCodecParameters* parameters, int worker)
    {
        AVCodecParameters* retained = avcodec_parameters_alloc();
        if (retained == null)
            return false;
        if (avcodec_parameters_copy(retained, parameters) < 0)
        {
            avcodec_parameters_free(&retained);
            return false;
        }

        tileContexts[worker] = (IntPtr)context;
        tileParameters[worker] = (IntPtr)retained;
        return true;
    }

    internal static bool ParametersMatch(AVCodecParameters* first, AVCodecParameters* second) =>
        first->codec_id == second->codec_id && first->codec_tag == second->codec_tag &&
        first->format == second->format && first->width == second->width && first->height == second->height &&
        first->profile == second->profile && first->level == second->level &&
        first->bits_per_coded_sample == second->bits_per_coded_sample && first->bits_per_raw_sample == second->bits_per_raw_sample &&
        first->sample_aspect_ratio.num == second->sample_aspect_ratio.num && first->sample_aspect_ratio.den == second->sample_aspect_ratio.den &&
        first->framerate.num == second->framerate.num && first->framerate.den == second->framerate.den &&
        first->field_order == second->field_order && first->video_delay == second->video_delay &&
        first->color_range == second->color_range && first->color_primaries == second->color_primaries &&
        first->color_trc == second->color_trc && first->color_space == second->color_space && first->chroma_location == second->chroma_location &&
        first->extradata_size >= 0 && first->extradata_size == second->extradata_size &&
        new ReadOnlySpan<byte>(first->extradata, first->extradata_size).SequenceEqual(new ReadOnlySpan<byte>(second->extradata, second->extradata_size)) &&
        SideDataEquals(first->coded_side_data, first->nb_coded_side_data, second->coded_side_data, second->nb_coded_side_data);

    private static bool SideDataEquals(AVPacketSideData* first, int firstCount, AVPacketSideData* second, int secondCount)
    {
        if (firstCount < 0 || firstCount != secondCount || (firstCount > 0 && (first == null || second == null)))
            return false;

        for (int index = 0; index < firstCount; index++)
        {
            AVPacketSideData left = first[index];
            AVPacketSideData right = second[index];
            if (left.type != right.type || left.size != right.size || left.size > int.MaxValue ||
                (left.size > 0 && (left.data == null || right.data == null)) ||
                !new ReadOnlySpan<byte>(left.data, (int)left.size).SequenceEqual(new ReadOnlySpan<byte>(right.data, (int)right.size)))
            {
                return false;
            }
        }

        return true;
    }

    private void ReleaseTileDecoder(int worker)
    {
        var context = (AVCodecContext*)tileContexts[worker];
        var parameters = (AVCodecParameters*)tileParameters[worker];
        tileContexts[worker] = IntPtr.Zero;
        tileParameters[worker] = IntPtr.Zero;
        if (context != null)
            avcodec_free_context(&context);
        if (parameters != null)
            avcodec_parameters_free(&parameters);
    }

    public bool Download(AVFrame* frame)
    {
        if (frame->format != (int)pixelFormat)
            return true;

        AVFrame* downloaded = av_frame_alloc();
        if (downloaded == null)
            return false;

        try
        {
            int result = D3D11HardwareContext.TransferFrame(downloaded, frame);
            if (result >= 0)
                result = av_frame_copy_props(downloaded, frame);
            if (result >= 0)
                result = av_frame_apply_cropping(downloaded, 1);
            if (result < 0)
            {
                AppLog.Warning("FFmpeg.Photo", $"Could not download {pixelFormat} photo pixels: {FfmpegError.Describe(result)}; using software.");
                return false;
            }

            av_frame_unref(frame);
            av_frame_move_ref(frame, downloaded);
            UsedHardware = true;
            return true;
        }
        finally
        {
            av_frame_free(&downloaded);
        }
    }

    public bool Preserve(AVFrame* frame)
    {
        if (frame->format != (int)pixelFormat)
            return true;
        UsedHardware = true;
        return true;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static AVPixelFormat SelectFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        var preferred = (AVPixelFormat)(nint)context->opaque;
        for (AVPixelFormat* format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
        {
            if (*format == preferred)
                return *format;
        }

        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        for (int worker = 0; worker < tileContexts.Length; worker++)
            ReleaseTileDecoder(worker);
        AVBufferRef* retained = device;
        device = null;
        if (retained != null)
            av_buffer_unref(&retained);
    }
}