using System;
using System.Collections.Generic;
using System.IO;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Subtitles.Text;
using SkiaSharp;

namespace LightMediaRenderer.Subtitles;

/// <summary>
/// Faces the subtitle renderer may fall back on when the platform's font manager cannot serve a
/// request.
/// </summary>
/// <remarks>
/// Subtitles are drawn straight through Skia, so they see <see cref="SKFontManager.Default"/> and
/// nothing of Avalonia's own font collections. On the platforms that have real system fonts that is
/// all they need; in the browser, where no API exposes the installed fonts, the manager is empty and
/// a CJK script comes out as boxes unless the faces are handed over here. Register them once during
/// start-up, before any video is shown: the files are read the first time a lookup actually reaches
/// them, not when they are registered.
/// </remarks>
public static class SubtitleFontFallbacks
{
    private static readonly object sync = new();
    private static readonly List<Func<Stream>> sources = [];
    private static readonly List<SKTypeface> typefaces = [];
    private static int opened;
    private static volatile bool any;

    /// <summary>
    /// Registers a font file, opened the first time a lookup needs it. Only the first face of a
    /// collection is taken, so register each face of a family separately.
    /// </summary>
    public static void Add(Func<Stream> open)
    {
        ArgumentNullException.ThrowIfNull(open);

        lock (sync)
        {
            sources.Add(open);
            any = true;
        }
    }

    /// <summary>Best registered face of <paramref name="family"/>, or <c>null</c> if none carries that name.</summary>
    internal static SKTypeface? MatchFamily(string family, SKFontStyle style)
    {
        if (!any)
            return null;

        lock (sync)
        {
            Open();

            SKTypeface? best = null;
            foreach (SKTypeface typeface in typefaces)
            {
                if (string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
                    best = Better(best, typeface, style);
            }

            return best;
        }
    }

    /// <summary>Best registered face able to draw <paramref name="codepoint"/>, whatever its family.</summary>
    /// <param name="script">
    /// Region the line is written for. Faces whose family name says they belong to it win over ones
    /// that merely have the character, so that registering, say, both a Japanese and a Simplified
    /// Chinese Noto build gives each language its own stroke shapes.
    /// </param>
    internal static SKTypeface? MatchCharacter(int codepoint, SKFontStyle style, SubtitleScript script)
    {
        if (!any)
            return null;

        lock (sync)
        {
            Open();

            SKTypeface? best = null;
            SKTypeface? bestOfScript = null;
            foreach (SKTypeface typeface in typefaces)
            {
                using var font = new SKFont(typeface);
                if (!font.ContainsGlyph(codepoint))
                    continue;

                best = Better(best, typeface, style);
                if (script != SubtitleScript.Unknown && SubtitleScripts.FromFamilyName(typeface.FamilyName) == script)
                    bestOfScript = Better(bestOfScript, typeface, style);
            }

            return bestOfScript ?? best;
        }
    }

    /// <summary>Best registered face of any family, for when nothing else resolved at all.</summary>
    internal static SKTypeface? MatchAny(SKFontStyle style)
    {
        if (!any)
            return null;

        lock (sync)
        {
            Open();

            SKTypeface? best = null;
            foreach (SKTypeface typeface in typefaces)
                best = Better(best, typeface, style);

            return best;
        }
    }

    /// <summary>Family names of every registered face. Opens the registered files.</summary>
    internal static List<string> FamilyNames()
    {
        var names = new List<string>();
        if (!any)
            return names;

        lock (sync)
        {
            Open();
            foreach (SKTypeface typeface in typefaces)
                names.Add(typeface.FamilyName);
        }

        return names;
    }

    private static void Open()
    {
        for (; opened < sources.Count; opened++)
        {
            try
            {
                using Stream stream = sources[opened]();
                if (SKTypeface.FromStream(stream) is { } typeface)
                    typefaces.Add(typeface);
            }
            catch (Exception)
            {
                // A font that cannot be read is simply not a fallback.
            }
        }
    }

    private static SKTypeface Better(SKTypeface? current, SKTypeface candidate, SKFontStyle style)
        => current is null || Distance(candidate, style) < Distance(current, style) ? candidate : current;

    // Width first, then slant, then weight, as CSS matches faces within a family.
    private static int Distance(SKTypeface typeface, SKFontStyle style)
        => (Math.Abs(typeface.FontWidth - style.Width) * 10000)
            + (typeface.FontSlant == style.Slant ? 0 : 1000)
            + Math.Abs(typeface.FontWeight - style.Weight);
}
