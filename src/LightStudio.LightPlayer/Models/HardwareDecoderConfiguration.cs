using System;
using System.Collections.Generic;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// The user's hardware choices for one FFmpeg device type, grouped by the call and argument that
/// consume them (<c>av_hwdevice_ctx_create:opts</c>).
/// </summary>
public sealed class HardwareDecoderConfiguration
{
    /// <summary>Device string for <c>av_hwdevice_ctx_create</c>, or empty for the platform default.</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>Option values, keyed by target then by the option name FFmpeg reads.</summary>
    public Dictionary<string, Dictionary<string, string>> Options { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Names of the flags the user turned on, keyed by target.</summary>
    public Dictionary<string, List<string>> Flags { get; set; } = new(StringComparer.Ordinal);

    public static string Target(string api, string parameter) => string.Concat(api, ":", parameter);

    public static bool TrySplitTarget(string target, out string api, out string parameter)
    {
        int separator = target?.LastIndexOf(':') ?? -1;
        if (separator <= 0 || separator == target!.Length - 1)
        {
            api = string.Empty;
            parameter = string.Empty;
            return false;
        }

        api = target[..separator];
        parameter = target[(separator + 1)..];
        return true;
    }
}
