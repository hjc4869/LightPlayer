using System;
using System.Collections.Generic;
using LightStudio.NativeBindings.Types;
using LightStudio.Logging;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>Managed access to the hardware device types exposed by the loaded FFmpeg build.</summary>
public static unsafe partial class FfmpegHardwareDevices
{
    /// <summary>
    /// Returns the hardware device types supported by both the current platform policy and the
    /// loaded FFmpeg build, in automatic-selection order.
    /// </summary>
    public static IReadOnlyList<string> GetPreferredTypesForCurrentPlatform()
    {
        string[] preferred = GetPlatformPreference();
        if (preferred.Length == 0)
            return Array.Empty<string>();

        var available = new HashSet<string>(GetAvailableTypes(), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(preferred.Length);
        foreach (string type in preferred)
        {
            if (available.Contains(type))
                result.Add(type);
        }

        return result;
    }

    public static IReadOnlyList<string> GetAvailableTypes()
    {
        var available = new List<string>();

        try
        {
            AVHWDeviceType type = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            while ((type = av_hwdevice_iterate_types(type)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                string name = av_hwdevice_get_type_name(type);
                if (!string.IsNullOrEmpty(name))
                    available.Add(name);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
            NotSupportedException or TypeInitializationException)
        {
            AppLog.Error("FFmpeg.Device", "Could not enumerate FFmpeg hardware device types.", exception);
        }

        return available;
    }

    private static string[] GetPlatformPreference()
    {
        if (OperatingSystem.IsAndroid())
            return ["mediacodec"];
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            return ["videotoolbox"];
        if (OperatingSystem.IsWindows())
            return ["d3d11va"];
        if (OperatingSystem.IsLinux() && !OperatingSystem.IsBrowser())
            return ["vaapi", "vulkan"];
        return [];
    }

    internal static AVHWDeviceType FindType(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return av_hwdevice_find_type_by_name(name);
    }
}