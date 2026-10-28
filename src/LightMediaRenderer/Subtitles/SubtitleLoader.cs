using System;
using System.IO;
using System.Text;
using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Subtitles;

/// <summary>Text subtitle formats this loader can parse.</summary>
internal enum SubtitleFormat
{
    /// <summary>Advanced SubStation Alpha, or its SSA predecessor.</summary>
    Ass,

    /// <summary>SubRip (<c>.srt</c>).</summary>
    SubRip,
}

/// <summary>
/// Loads a text subtitle file into an <see cref="AssDocument"/>.
/// </summary>
/// <remarks>
/// The format is decided from the contents rather than the extension, because renaming a script is
/// far more common than a file that genuinely looks like both. Non-ASS formats are converted to
/// events against a generated style, so the renderer only ever sees one document type.
/// </remarks>
public static class SubtitleLoader
{
    /// <summary>Text and bitmap subtitle patterns suitable for the player's open dialog.</summary>
    public static string[] Patterns => ["*.ass", "*.ssa", "*.srt", "*.sup", "*.pgs"];

    /// <summary>
    /// Whether <paramref name="path"/> names a plain text subtitle script rather than a bitmap
    /// stream or container. Decided from the extension alone, because the file is not read.
    /// </summary>
    public static bool IsScriptFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string extension = Path.GetExtension(path);
        return extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".srt", StringComparison.OrdinalIgnoreCase);
    }

    public static AssDocument Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return Load(stream, path);
    }

    /// <param name="path">Optional file name, used only to break a tie the contents cannot.</param>
    public static AssDocument Load(Stream stream, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd(), path);
    }

    public static AssDocument Parse(string text, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Detect(text, path) == SubtitleFormat.SubRip
            ? SrtParser.Parse(new StringReader(text))
            : AssParser.Parse(new StringReader(text));
    }

    /// <summary>
    /// Parses a script libavcodec converted from a plain text stream such as SubRip or WebVTT, whose
    /// style is generated rather than authored.
    /// </summary>
    public static AssDocument ParsePlainText(string script)
    {
        AssDocument document = Parse(script);
        document.IsPlainText = true;
        return document;
    }

    private static SubtitleFormat Detect(string text, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("[V4", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase))
        {
            return SubtitleFormat.Ass;
        }

        if (text.Contains("-->", StringComparison.Ordinal))
            return SubtitleFormat.SubRip;

        return path is not null && Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase)
            ? SubtitleFormat.SubRip
            : SubtitleFormat.Ass;
    }
}
