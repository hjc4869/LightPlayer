using System;
using System.Collections.Generic;
using System.IO;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Resolves media file activation arguments — command-line arguments and
/// file-manager "Open with" invocations — into local file paths. Accepts both
/// plain paths and <c>file://</c> URIs (the desktop entry launches with
/// <c>%U</c>, and the single-instance channel forwards URIs), and filters to
/// files that exist so option flags and stale paths are ignored.
/// </summary>
public static class FileActivation
{
    /// <summary>Extracts the existing local file paths from launch arguments.</summary>
    public static IReadOnlyList<string> ExtractPaths(IEnumerable<string> args)
    {
        var paths = new List<string>();
        if (args is null)
        {
            return paths;
        }

        foreach (var arg in args)
        {
            var path = ToLocalPath(arg);
            if (path is not null && File.Exists(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>
    /// Converts a single argument (a local path or a <c>file://</c> URI) to a
    /// local path. Returns <c>null</c> for option flags (leading <c>-</c>) and for
    /// non-file URI schemes (e.g. <c>http</c>), which are not playable locally.
    /// </summary>
    public static string? ToLocalPath(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-'))
        {
            return null;
        }

        if (Uri.TryCreate(arg, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return uri.LocalPath;
            }

            // Absolute URI with a real (multi-character) scheme that isn't a file,
            // e.g. http/https/smb — not supported for local playback. Single-letter
            // schemes are Windows drive letters and fall through as paths.
            if (uri.Scheme.Length > 1)
            {
                return null;
            }
        }

        return arg;
    }

    /// <summary>Converts a local path to an absolute <c>file://</c> URI for D-Bus forwarding.</summary>
    public static string ToFileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}
