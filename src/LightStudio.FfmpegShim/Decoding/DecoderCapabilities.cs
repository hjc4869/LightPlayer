#nullable enable
using System;
using System.Collections.Concurrent;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>Which codecs the loaded FFmpeg build can decode, and whether a hardware device may take them.</summary>
public static unsafe class DecoderCapabilities
{
    private static readonly ConcurrentDictionary<string, bool> devices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the build has a decoder for the FFmpeg codec name, such as <c>h264</c>.</summary>
    public static bool HasDecoder(string codecName)
    {
        ArgumentException.ThrowIfNullOrEmpty(codecName);
        AVCodecDescriptor* descriptor = avcodec_descriptor_get_by_name(codecName);
        return descriptor != null && avcodec_find_decoder(descriptor->id) != null;
    }

    /// <summary>
    /// Whether a decoder for the codec advertises one of this platform's hardware device types and that
    /// device opens. Whether the device decodes a given profile or size is only known once it tries.
    /// </summary>
    public static bool HasHardwareDecoder(string codecName)
    {
        ArgumentException.ThrowIfNullOrEmpty(codecName);
        AVCodecDescriptor* descriptor = avcodec_descriptor_get_by_name(codecName);
        if (descriptor == null)
            return false;

        foreach (string deviceName in FfmpegHardwareDevices.GetPreferredTypesForCurrentPlatform())
        {
            AVHWDeviceType deviceType = FfmpegHardwareDevices.FindType(deviceName);
            if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE || !Advertises(descriptor->id, deviceType))
                continue;
            if (devices.GetOrAdd(deviceName, static (_, type) => DeviceOpens(type), deviceType))
                return true;
        }

        return false;
    }

    private static bool Advertises(AVCodecID codecId, AVHWDeviceType deviceType)
    {
        void* iterator = null;
        AVCodec* codec;
        while ((codec = av_codec_iterate(&iterator)) != null)
        {
            if (codec->id != codecId || av_codec_is_decoder(codec) == 0)
                continue;

            for (int index = 0; ; index++)
            {
                AVCodecHWConfig* config = avcodec_get_hw_config(codec, index);
                if (config == null)
                    break;

                // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX
                if (config->device_type == deviceType && ((int)config->methods & 1) != 0)
                    return true;
            }
        }

        return false;
    }

    private static bool DeviceOpens(AVHWDeviceType deviceType)
    {
        AVBufferRef* device = null;
        int opened = av_hwdevice_ctx_create(&device, deviceType, null, null, 0);
        if (device != null)
            av_buffer_unref(&device);
        return opened >= 0;
    }
}
