using System;
using System.Collections.Generic;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.Logging;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>
/// Builds the video decoder for a stream, giving platforms a chance to supply a hardware decoder
/// that libavcodec knows nothing about. Returns <c>null</c> to decline the stream.
/// </summary>
public delegate IVideoDecoder VideoDecoderProvider(IPacketSource source, VideoDecoderOptions options);

/// <summary>
/// Chooses the video decoder for a stream. FFmpeg is always the last resort, so a platform decoder
/// that declines, throws or is switched off in settings never costs playback.
/// </summary>
public static class VideoDecoderFactory
{
    private static readonly object sync = new();
    private static readonly List<(string DeviceType, VideoDecoderProvider Provider)> providers = new();

    /// <summary>
    /// Registers a decoder under a hardware device type name of its own, which then appears in the
    /// hardware decoder setting alongside FFmpeg's device types.
    /// </summary>
    public static void RegisterHardwareProvider(string deviceType, VideoDecoderProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceType);
        ArgumentNullException.ThrowIfNull(provider);

        lock (sync)
        {
            providers.RemoveAll(entry => string.Equals(entry.DeviceType, deviceType, StringComparison.OrdinalIgnoreCase));
            providers.Add((deviceType, provider));
        }
    }

    /// <summary>Hardware device types offerable to the user, registered providers first.</summary>
    public static IReadOnlyList<string> GetAvailableHardwareDeviceTypes()
    {
        var types = new List<string>();
        lock (sync)
        {
            foreach ((string deviceType, _) in providers)
                types.Add(deviceType);
        }

        types.AddRange(FfmpegHardwareDevices.GetPreferredTypesForCurrentPlatform());
        return types;
    }

    public static IVideoDecoder Create(IPacketSource source, VideoDecoderOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= VideoDecoderOptions.Default;

        foreach (VideoDecoderProvider provider in SelectProviders(options))
        {
            IVideoDecoder decoder;
            try
            {
                decoder = provider(source, options);
            }
            catch (Exception exception)
            {
                // A provider that cannot set itself up is a reason to use FFmpeg, not to fail.
                AppLog.Warning(
                    "Video.Decoder",
                    $"Video decoder provider {provider.Method.DeclaringType?.FullName ?? provider.Method.Name} failed.",
                    exception);
                continue;
            }

            if (decoder is not null)
                return decoder;
        }

        return new FfmpegVideoDecoder(source, options);
    }

    private static List<VideoDecoderProvider> SelectProviders(VideoDecoderOptions options)
    {
        var selected = new List<VideoDecoderProvider>();
        if (!options.EnableHardwareDecoding)
            return selected;

        lock (sync)
        {
            foreach ((string deviceType, VideoDecoderProvider provider) in providers)
            {
                // An explicit choice names either one of these providers or an FFmpeg device type;
                // in the latter case none of them apply and FfmpegVideoDecoder handles the request.
                if (string.IsNullOrWhiteSpace(options.HardwareDeviceType))
                    selected.Add(provider);
                else if (string.Equals(deviceType, options.HardwareDeviceType, StringComparison.OrdinalIgnoreCase))
                    return [provider];
            }
        }

        return selected;
    }
}
