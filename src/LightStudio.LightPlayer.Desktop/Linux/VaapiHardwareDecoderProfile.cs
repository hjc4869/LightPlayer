#if LINUX
using System;
using System.Collections.Generic;
using System.IO;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.Desktop.Linux;

/// <summary>
/// What VA-API can be opened with on this machine: which DRM node decodes, and the arguments
/// libavutil's VA-API device creation reads.
/// </summary>
internal sealed class VaapiHardwareDecoderProfile : IHardwareDecoderProfile
{
    private const string DeviceDirectory = "/dev/dri";

    private static readonly IReadOnlyList<HardwareOptionDescriptor> Options =
    [
        new(
            HardwareDecoderApis.DeviceCreate,
            HardwareDecoderParameters.Options,
            "connection_type",
            "Connection",
            "How the VA-API display is opened. DRM talks to the render node directly; X11 goes " +
            "through the running server, which is the only way to decode on a remote display.",
            Kind: HardwareSettingKind.Choice,
            Choices:
            [
                new HardwareSettingChoice("drm", "DRM render node"),
                new HardwareSettingChoice("x11", "X11 display"),
            ]),
        new(
            HardwareDecoderApis.DeviceCreate,
            HardwareDecoderParameters.Options,
            "kernel_driver",
            "Kernel driver",
            "Picks the render node by the driver behind it (i915, amdgpu, radeon) when no device " +
            "is chosen above."),
    ];

    public string DeviceType => "vaapi";

    /// <summary>
    /// The render nodes, which is what VA-API decodes through. Card nodes are left out: they need
    /// the DRM master, which a session's compositor already holds.
    /// </summary>
    public IReadOnlyList<HardwareDeviceInfo> GetDevices()
    {
        if (!Directory.Exists(DeviceDirectory))
        {
            return [];
        }

        var devices = new List<HardwareDeviceInfo>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(DeviceDirectory, "renderD*"))
            {
                string node = Path.GetFileName(path);
                string driver = ReadDriver(node);
                devices.Add(new HardwareDeviceInfo(
                    path,
                    driver.Length == 0 ? node : $"{node} ({driver})"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return devices;
        }

        devices.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        return devices;
    }

    public IReadOnlyList<HardwareOptionDescriptor> GetOptions() => Options;

    /// <summary>VA-API device creation reads no flags; the argument is there for other types.</summary>
    public IReadOnlyList<HardwareFlagDescriptor> GetFlags() => [];

    private static string ReadDriver(string node)
    {
        try
        {
            foreach (string line in File.ReadLines($"/sys/class/drm/{node}/device/uevent"))
            {
                if (line.StartsWith("DRIVER=", StringComparison.Ordinal))
                {
                    return line["DRIVER=".Length..];
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return string.Empty;
    }
}
#endif
