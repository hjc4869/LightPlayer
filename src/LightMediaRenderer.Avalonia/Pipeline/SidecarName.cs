using System;
using System.IO;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>
/// Names a sidecar track by the part of its file name the media's own name does not already say:
/// <c>show.mkv</c> next to <c>show.ass</c> and <c>show.chinese.ass</c> reads as "Default" and
/// "chinese" instead of two copies of the video's name.
/// </summary>
internal static class SidecarName
{
    public static string Describe(string? sidecarPath, string? mediaPath, string defaultTrackName)
    {
        string name = Path.GetFileNameWithoutExtension(sidecarPath ?? string.Empty);
        string stem = Path.GetFileNameWithoutExtension(mediaPath ?? string.Empty);

        if (stem.Length != 0 && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
        {
            if (name.Length == stem.Length)
                return defaultTrackName;
            if (name[stem.Length] == '.')
                return name[(stem.Length + 1)..];
        }

        // Not named after the media -- a file the user picked out themselves keeps its own name.
        return name.Length == 0 ? defaultTrackName : name;
    }
}
