#nullable enable
using System;
using System.IO;

namespace LightStudio.MediaLibraryCore.IO;

/// <summary>
/// Reads a name out of a media identifier for display. Identifiers are not always local paths: the
/// browser head names a file the user granted <c>fsa://{root}/{name}</c> and Android hands out
/// content URIs, both with percent-encoded segments that must not reach the screen as typed.
/// </summary>
public static class MediaIdentityNames
{
    /// <summary>The file's own name, as the user's file manager spells it, extension included.</summary>
    public static string FileName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        if (!path.Contains("://", StringComparison.Ordinal))
        {
            return Path.GetFileName(path);
        }

        // Decoded before the leaf is taken, so a separator hidden inside an escaped segment
        // (Android's "primary%3AMusic%2Fsong.mp3") splits the same way the file manager shows it.
        var decoded = Uri.UnescapeDataString(path);
        var separator = decoded.LastIndexOfAny(['/', '\\']);
        return separator >= 0 ? decoded[(separator + 1)..] : decoded;
    }

    /// <summary>The file's name without its extension, for titles standing in for missing tags.</summary>
    public static string Title(string? path)
    {
        var name = FileName(path);
        var extension = name.LastIndexOf('.');
        return extension > 0 ? name[..extension] : name;
    }
}
