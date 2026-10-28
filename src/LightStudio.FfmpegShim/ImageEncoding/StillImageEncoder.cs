#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LightStudio.FfmpegShim.Interop;
using LightStudio.FfmpegShim.Transcoding;
using LightStudio.Logging;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.ImageEncoding;

public enum StillEncodeSpeed
{
    Slower,
    Balanced,
    Faster,

    /// <summary>The encoder's quickest setting; encoders without one beyond <see cref="Faster"/> use that.</summary>
    Fastest,
}

/// <param name="Quality">1 to 100; 100 is lossless where the codec supports it and near-lossless otherwise.</param>
public readonly record struct StillEncodeSettings(int Quality, StillEncodeSpeed Speed);

/// <summary>Encodes single pictures with FFmpeg's software and hardware encoders.</summary>
public static unsafe class StillImageEncoder
{
    /// <summary>Encodes one picture as an intra-only frame and returns the packet payloads.</summary>
    public static byte[] Encode(StillImageEncoderInfo encoder, StillPicture picture, StillEncodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(picture);
        if (encoder.Hardware is null)
            return EncodeCore(encoder, picture, SoftwareOptions(encoder, settings), settings, null, constantQuality: true);

        AVBufferRef* device = FfmpegTranscodeCapabilities.TryCreateDevice(encoder.Hardware.DeviceType, null);
        if (device == null)
            throw new NotSupportedException($"The {encoder.DisplayName} device is unavailable.");
        try
        {
            if (HardwareOptions(encoder, settings) is { } options)
            {
                try
                {
                    return EncodeCore(encoder, picture, options, settings, device, constantQuality: true);
                }
                catch (FfmpegException exception)
                {
                    // Constant-quality modes depend on the driver; a target size works everywhere.
                    AppLog.Warning("FFmpeg.StillEncode",
                        $"{encoder.Name} rejected constant-quality encoding; retrying with a bit rate.", exception);
                }
            }

            return EncodeCore(encoder, picture, [], settings, device, constantQuality: false);
        }
        finally
        {
            av_buffer_unref(&device);
        }
    }

    private static byte[] EncodeCore(StillImageEncoderInfo info, StillPicture picture, Dictionary<string, string> options,
        StillEncodeSettings settings, AVBufferRef* device, bool constantQuality)
    {
        AVCodec* codec = avcodec_find_encoder_by_name(info.Name);
        if (codec == null)
            throw new NotSupportedException($"This FFmpeg build has no encoder named '{info.Name}'.");

        AVCodecContext* context = avcodec_alloc_context3(codec);
        if (context == null)
            throw new OutOfMemoryException();
        AVPacket* packet = null;
        AVFrame* upload = null;
        AVDictionary* dictionary = null;
        try
        {
            AVFrame* source = picture.Frame;
            context->width = picture.Width;
            context->height = picture.Height;
            context->pix_fmt = picture.Format;
            context->time_base = new AVRational { num = 1, den = 1 };
            context->framerate = new AVRational { num = 1, den = 1 };
            // The only frame is a key frame anyway; an intra-only GOP would make libx265 signal RExt intra profiles.
            context->max_b_frames = 0;
            // Still encoders can otherwise create one large working set for every logical CPU.
            context->thread_count = Math.Min(Environment.ProcessorCount, 8);
            context->color_primaries = picture.Primaries;
            context->color_trc = picture.Transfer;
            context->colorspace = picture.Matrix;
            context->color_range = picture.Range;
            if (device != null)
            {
                context->hw_device_ctx = av_buffer_ref(device);
                if (constantQuality)
                    ApplyHardwareQuality(info, context, settings.Quality);
                else
                    context->bit_rate = TargetBitRate(picture, settings.Quality);
                if (info.Hardware!.ProbeWithHardwareFrames)
                    upload = Upload(context, device, source);
            }

            foreach ((string key, string value) in options)
                av_dict_set(&dictionary, key, value, 0);
            FfmpegError.ThrowIfError(avcodec_open2(context, codec, &dictionary), $"Opening {info.Name}");

            packet = av_packet_alloc();
            using var output = new MemoryStream();
            AVFrame* frame = upload != null ? upload : source;
            frame->pts = 0;
            FfmpegError.ThrowIfError(avcodec_send_frame(context, frame), $"Encoding with {info.Name}");
            FfmpegError.ThrowIfError(avcodec_send_frame(context, null), $"Flushing {info.Name}");
            while (true)
            {
                int received = avcodec_receive_packet(context, packet);
                if (FfmpegError.IsEndOfFile(received) || FfmpegError.IsTryAgain(received))
                    break;
                FfmpegError.ThrowIfError(received, $"Receiving from {info.Name}");
                output.Write(new ReadOnlySpan<byte>(packet->data, packet->size));
                av_packet_unref(packet);
            }

            if (output.Length == 0)
                throw new InvalidDataException($"{info.Name} produced no data.");
            return output.ToArray();
        }
        finally
        {
            av_dict_free(&dictionary);
            av_packet_free(&packet);
            av_frame_free(&upload);
            avcodec_free_context(&context);
        }
    }

    /// <summary>Copies the picture into a frame of the encoder's device.</summary>
    private static AVFrame* Upload(AVCodecContext* context, AVBufferRef* device, AVFrame* source)
    {
        AVBufferRef* frames = av_hwframe_ctx_alloc(device);
        if (frames == null)
            throw new OutOfMemoryException();
        try
        {
            AVPixelFormat hardwareFormat = HardwareFormat(device);
            var framesContext = (AVHWFramesContext*)frames->data;
            framesContext->format = hardwareFormat;
            framesContext->sw_format = (AVPixelFormat)source->format;
            framesContext->width = source->width;
            framesContext->height = source->height;
            framesContext->initial_pool_size = 1;
            FfmpegError.ThrowIfError(av_hwframe_ctx_init(frames), "av_hwframe_ctx_init");
            context->pix_fmt = hardwareFormat;
            context->hw_frames_ctx = av_buffer_ref(frames);

            AVFrame* uploaded = av_frame_alloc();
            try
            {
                FfmpegError.ThrowIfError(av_hwframe_get_buffer(frames, uploaded, 0), "av_hwframe_get_buffer");
                FfmpegError.ThrowIfError(av_hwframe_transfer_data(uploaded, source, 0), "av_hwframe_transfer_data");
                FfmpegError.ThrowIfError(av_frame_copy_props(uploaded, source), "av_frame_copy_props");
                return uploaded;
            }
            catch
            {
                av_frame_free(&uploaded);
                throw;
            }
        }
        finally
        {
            av_buffer_unref(&frames);
        }
    }

    private static AVPixelFormat HardwareFormat(AVBufferRef* device)
    {
        AVHWFramesConstraints* constraints = av_hwdevice_get_hwframe_constraints(device, null);
        try
        {
            if (constraints != null && constraints->valid_hw_formats != null &&
                constraints->valid_hw_formats[0] != AVPixelFormat.AV_PIX_FMT_NONE)
                return constraints->valid_hw_formats[0];
        }
        finally
        {
            av_hwframe_constraints_free(&constraints);
        }

        throw new NotSupportedException("The encoder device reports no frame format.");
    }

    private static Dictionary<string, string> SoftwareOptions(StillImageEncoderInfo encoder, StillEncodeSettings settings)
    {
        int quality = Math.Clamp(settings.Quality, 1, 100);
        string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
        StillEncodeSpeed speed = settings.Speed;
        bool fast = speed is StillEncodeSpeed.Faster or StillEncodeSpeed.Fastest;
        return encoder.Name switch
        {
            "libx265" => new()
            {
                ["preset"] = speed == StillEncodeSpeed.Slower ? "slow" : fast ? "fast" : "medium",
                ["x265-params"] = quality >= 100 ? "lossless=1:info=0" : $"qp={Invariant(HevcQp(quality))}:info=0",
            },
            "libkvazaar" => new()
            {
                ["kvazaar-params"] = $"qp={Invariant(HevcQp(quality))},preset=" +
                    (speed == StillEncodeSpeed.Slower ? "slow" : fast ? "veryfast" : "medium"),
            },
            "libaom-av1" => new()
            {
                ["crf"] = Invariant(Av1Quantizer(quality)),
                ["usage"] = "allintra",
                ["cpu-used"] = speed == StillEncodeSpeed.Slower ? "4" : fast ? "8" : "6",
                ["row-mt"] = "1",
                ["still-picture"] = "1",
            },
            "librav1e" => new()
            {
                ["qp"] = Invariant(Av1Quantizer(quality) * 4),
                ["speed"] = speed == StillEncodeSpeed.Slower ? "4" : fast ? "9" : "6",
            },
            "libsvtav1" => new()
            {
                ["crf"] = Invariant(Math.Max(1, Av1Quantizer(quality))),
                ["preset"] = speed == StillEncodeSpeed.Slower ? "4" : fast ? "10" : "8",
            },
            "libjxl" => new()
            {
                ["distance"] = JxlDistance(quality).ToString("0.###", CultureInfo.InvariantCulture),
                ["effort"] = speed switch
                {
                    StillEncodeSpeed.Slower => "8",
                    StillEncodeSpeed.Faster => "4",
                    StillEncodeSpeed.Fastest => "1",
                    _ => "7",
                },
            },
            _ => [],
        };
    }

    /// <summary>Private constant-quality options, or null where the interface only targets a bit rate.</summary>
    private static Dictionary<string, string>? HardwareOptions(StillImageEncoderInfo encoder, StillEncodeSettings settings)
    {
        int quality = Math.Clamp(settings.Quality, 1, 100);
        string qp = (encoder.Codec == StillImageCodec.Av1 ? Av1Quantizer(quality) * 4 : HevcQp(quality))
            .ToString(CultureInfo.InvariantCulture);
        return encoder.Hardware?.Name switch
        {
            TranscodeEncoderInterfaces.Vaapi => new() { ["rc_mode"] = "CQP" },
            TranscodeEncoderInterfaces.Nvenc => new()
            {
                ["rc"] = "constqp",
                ["qp"] = qp,
                ["preset"] = settings.Speed switch
                {
                    StillEncodeSpeed.Slower => "p7",
                    StillEncodeSpeed.Faster or StillEncodeSpeed.Fastest => "p3",
                    _ => "p5",
                },
            },
            TranscodeEncoderInterfaces.AdvancedMediaFramework => new() { ["rc"] = "cqp", ["qp_i"] = qp, ["qp_p"] = qp },
            TranscodeEncoderInterfaces.MediaFoundation => new()
            {
                ["rate_control"] = "quality",
                ["quality"] = quality.ToString(CultureInfo.InvariantCulture),
                ["hw_encoding"] = "1",
            },
            TranscodeEncoderInterfaces.QuickSyncVideo or TranscodeEncoderInterfaces.VideoToolbox => [],
            _ => null,
        };
    }

    /// <summary>Settings that live on the context rather than in the private options.</summary>
    private static void ApplyHardwareQuality(StillImageEncoderInfo encoder, AVCodecContext* context, int quality)
    {
        int qp = encoder.Codec == StillImageCodec.Av1 ? Av1Quantizer(quality) * 4 : HevcQp(quality);
        switch (encoder.Hardware?.Name)
        {
            case TranscodeEncoderInterfaces.Vaapi:
                context->global_quality = qp;
                break;
            case TranscodeEncoderInterfaces.QuickSyncVideo:
                context->flags |= AV_CODEC_FLAG_QSCALE;
                context->global_quality = qp * FF_QP2LAMBDA;
                break;
            case TranscodeEncoderInterfaces.VideoToolbox:
                context->flags |= AV_CODEC_FLAG_QSCALE;
                context->global_quality = quality * FF_QP2LAMBDA;
                break;
        }
    }

    /// <summary>Bits for the whole picture at one frame per second, for encoders without a quality mode.</summary>
    private static long TargetBitRate(StillPicture picture, int quality)
    {
        double bitsPerPixel = 0.1 * Math.Pow(2, (Math.Clamp(quality, 1, 100) - 50) / 12.0);
        return Math.Max(100_000, (long)(picture.Width * (double)picture.Height * bitsPerPixel));
    }

    /// <summary>HEVC QP 51 at quality 0, 20 at 90 (visually lossless for most photos), and 4 at 100.</summary>
    private static int HevcQp(int quality) => (int)Math.Round(quality <= 90
        ? 51 - quality * (31.0 / 90)
        : 20 - (quality - 90) * 1.6);

    /// <summary>AV1 quantizer (0 to 63) 63 at quality 0, 18 at 90, and 2 at 100.</summary>
    private static int Av1Quantizer(int quality) => (int)Math.Round(quality <= 90
        ? 63 - quality * (45.0 / 90)
        : 18 - (quality - 90) * 1.6);

    /// <summary>Maps quality to a Butteraugli distance the way cjxl does; 100 is lossless.</summary>
    private static double JxlDistance(int quality) => quality >= 100 ? 0
        : quality >= 30 ? 0.1 + (100 - quality) * 0.09
        : 6.4 + Math.Pow(2.5, (30 - quality) / 5.0) / 6.25;
}
