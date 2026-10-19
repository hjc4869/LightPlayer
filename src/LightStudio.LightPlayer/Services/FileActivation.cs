using System;
using System.Collections.Generic;
using System.IO;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Resolves command-line and desktop activation arguments into playable targets.
/// Launch targets may be existing local files or absolute HTTP(S) stream URLs;
/// file-only callers can continue to extract local paths.
/// </summary>
public static class FileActivation
{
    /// <summary>Extracts playable local files and HTTP(S) stream URLs from launch arguments.</summary>
    public static IReadOnlyList<string> ExtractTargets(IEnumerable<string> args)
    {
        var targets = new List<string>();
        if (args is null)
        {
            return targets;
        }

        foreach (string arg in args)
        {
            if (StreamUrl.TryParseLink(arg, out StreamLink? streamLink))
            {
                foreach (Uri source in streamLink.Sources)
                {
                    targets.Add(source.AbsoluteUri);
                }

                continue;
            }

            var path = ToLocalPath(arg);
            if (path is not null && File.Exists(path))
            {
                targets.Add(path);
            }
        }

        return targets;
    }

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
            // e.g. http/https/smb -- not supported for local playback. Single-letter
            // schemes are Windows drive letters and fall through as paths.
            if (uri.Scheme.Length > 1)
            {
                return null;
            }
        }

        return arg;
    }

    /// <summary>Converts a local path or stream URL to a URI suitable for activation transport.</summary>
    public static string ToActivationUri(string target) =>
        StreamUrl.TryParse(target, out var streamUri)
            ? streamUri.AbsoluteUri
            : ToFileUri(target);

    /// <summary>Converts an activation URI to a local path or normalized HTTP(S) stream URL.</summary>
    public static string? ToActivationTarget(string? value) =>
        StreamUrl.TryParse(value, out var streamUri)
            ? streamUri.AbsoluteUri
            : ToLocalPath(value);

    /// <summary>Converts a local path to an absolute <c>file://</c> URI for D-Bus forwarding.</summary>
    public static string ToFileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}
