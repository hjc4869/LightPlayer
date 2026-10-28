using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Subtitles;

/// <summary>
/// Reads a SubRip (<c>.srt</c>) file into an <see cref="AssDocument"/>.
/// </summary>
/// <remarks>
/// SRT has no styling of its own beyond a handful of HTML tags, so a cue becomes an ASS event
/// against one generated style and the markup is rewritten as override tags. Everything downstream --
/// segmenting, shaping, layout, drawing -- is then the ASS pipeline unchanged. Cues are separated by
/// blank lines, but files in the wild lose them often enough that a timing line also ends the
/// previous cue.
/// </remarks>
internal static class SrtParser
{
    /// <summary>The style every cue is rendered with; the values are the ones FFmpeg uses for SRT.</summary>
    private static AssStyle CreateDefaultStyle() => new()
    {
        Name = "Default",
        FontName = "Arial",
        FontSize = 18,
        PrimaryColour = AssColor.White,
        SecondaryColour = AssColor.White,
        OutlineColour = AssColor.Black,
        BackColour = AssColor.Black,
        BorderStyle = 1,
        Outline = 1,
        Shadow = 0,
        Alignment = 2,
        MarginL = 10,
        MarginR = 10,
        MarginV = 10,
    };

    public static AssDocument Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var document = new AssDocument { IsPlainText = true };
        document.AddStyle(CreateDefaultStyle());

        var body = new List<string>();
        TimeSpan start = default;
        TimeSpan end = default;
        bool timed = false;

        while (reader.ReadLine() is { } line)
        {
            if (TryParseTiming(line, out TimeSpan cueStart, out TimeSpan cueEnd))
            {
                // No blank line before this cue: whatever trails the previous one is its index.
                Flush(document, ref timed, start, end, body, dropTrailingIndex: true);
                start = cueStart;
                end = cueEnd;
                timed = true;
                continue;
            }

            if (!timed)
                continue;

            if (line.AsSpan().Trim().IsEmpty)
                Flush(document, ref timed, start, end, body, dropTrailingIndex: false);
            else
                body.Add(line);
        }

        Flush(document, ref timed, start, end, body, dropTrailingIndex: false);

        document.Finish();
        return document;
    }

    private static void Flush(
        AssDocument document,
        ref bool timed,
        TimeSpan start,
        TimeSpan end,
        List<string> body,
        bool dropTrailingIndex)
    {
        if (timed)
        {
            while (body.Count > 0 && body[^1].AsSpan().Trim().IsEmpty)
                body.RemoveAt(body.Count - 1);

            if (dropTrailingIndex && body.Count > 0 && IsIndexLine(body[^1]))
                body.RemoveAt(body.Count - 1);

            string text = ConvertMarkup(body);
            if (end > start && text.Length > 0)
                document.AddEvent(new AssDialogue { Start = start, End = end, Text = text });
        }

        body.Clear();
        timed = false;
    }

    private static bool IsIndexLine(string line)
    {
        ReadOnlySpan<char> span = line.AsSpan().Trim();
        if (span.IsEmpty)
            return false;

        foreach (char c in span)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Recognizes <c>00:00:20,000 --&gt; 00:00:24,400</c>, with or without SubRip's trailing
    /// <c>X1:... Y1:...</c> display coordinates.
    /// </summary>
    private static bool TryParseTiming(string line, out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;

        int arrow = line.IndexOf("-->", StringComparison.Ordinal);
        if (arrow < 0)
            return false;

        return AssParser.TryParseTime(LastToken(line.AsSpan(0, arrow)), out start) &&
               AssParser.TryParseTime(FirstToken(line.AsSpan(arrow + 3)), out end);
    }

    private static ReadOnlySpan<char> FirstToken(ReadOnlySpan<char> span)
    {
        span = span.TrimStart();
        int space = span.IndexOfAny(' ', '\t');
        return space < 0 ? span : span[..space];
    }

    private static ReadOnlySpan<char> LastToken(ReadOnlySpan<char> span)
    {
        span = span.TrimEnd();
        int space = span.LastIndexOfAny(' ', '\t');
        return space < 0 ? span : span[(space + 1)..];
    }

    /// <summary>Attributes a <c>&lt;font&gt;</c> element set, so closing it can restore the enclosing ones.</summary>
    private readonly record struct FontScope(string? Color, string? Face, string? Size);

    private static string ConvertMarkup(List<string> lines)
    {
        var builder = new StringBuilder();
        var fonts = new List<FontScope>();

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                builder.Append("\\N");
            AppendLine(builder, lines[i], fonts);
        }

        return builder.ToString().Trim();
    }

    private static void AppendLine(StringBuilder builder, string line, List<FontScope> fonts)
    {
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];

            if (c == '<' && TryReadTag(line, i, out string name, out string attributes, out bool closing, out int next))
            {
                AppendTag(builder, name, attributes, closing, fonts);
                i = next;
                continue;
            }

            if (c == '{')
            {
                // Scripts routinely smuggle ASS override blocks (\an8, \pos) into SRT; pass them on.
                int close = line.IndexOf('}', i + 1);
                if (close > i + 1 && line[i + 1] == '\\')
                {
                    builder.Append(line, i, close - i + 1);
                    i = close + 1;
                    continue;
                }
            }

            if (c == '&')
            {
                int consumed = AppendEntity(builder, line, i);
                if (consumed > 0)
                {
                    i += consumed;
                    continue;
                }
            }

            AppendEscaped(builder, c);
            i++;
        }
    }

    /// <summary>Escapes the three characters ASS gives meaning to, so cue text cannot become a tag.</summary>
    private static void AppendEscaped(StringBuilder builder, char c)
    {
        if (c is '{' or '}' or '\\')
            builder.Append('\\');
        builder.Append(c);
    }

    private static void AppendEscaped(StringBuilder builder, string text)
    {
        foreach (char c in text)
            AppendEscaped(builder, c);
    }

    /// <summary>Returns how many characters the entity occupied, or 0 if this is a bare ampersand.</summary>
    private static int AppendEntity(StringBuilder builder, string line, int start)
    {
        int end = line.IndexOf(';', start + 1);
        if (end < 0 || end - start > 12)
            return 0;

        string entity = line[start..(end + 1)];
        string decoded = WebUtility.HtmlDecode(entity);
        if (decoded == entity)
            return 0;

        AppendEscaped(builder, decoded);
        return end - start + 1;
    }

    private static bool TryReadTag(
        string line,
        int start,
        out string name,
        out string attributes,
        out bool closing,
        out int next)
    {
        name = string.Empty;
        attributes = string.Empty;
        closing = false;
        next = start;

        int end = line.IndexOf('>', start + 1);
        if (end < 0)
            return false;

        ReadOnlySpan<char> content = line.AsSpan(start + 1, end - start - 1).Trim();
        if (content.IsEmpty)
            return false;

        if (content[0] == '/')
        {
            closing = true;
            content = content[1..].TrimStart();
        }

        int length = 0;
        while (length < content.Length && char.IsAsciiLetterOrDigit(content[length]))
            length++;

        if (length == 0)
            return false;

        name = content[..length].ToString().ToLowerInvariant();
        attributes = content[length..].Trim().TrimEnd('/').ToString();
        next = end + 1;
        return true;
    }

    private static void AppendTag(
        StringBuilder builder,
        string name,
        string attributes,
        bool closing,
        List<FontScope> fonts)
    {
        switch (name)
        {
            case "b":
            case "strong":
                builder.Append(closing ? "{\\b0}" : "{\\b1}");
                break;

            case "i":
            case "em":
                builder.Append(closing ? "{\\i0}" : "{\\i1}");
                break;

            case "u":
                builder.Append(closing ? "{\\u0}" : "{\\u1}");
                break;

            case "s":
            case "strike":
            case "del":
                builder.Append(closing ? "{\\s0}" : "{\\s1}");
                break;

            case "br":
                builder.Append("\\N");
                break;

            case "font":
                if (closing)
                    CloseFont(builder, fonts);
                else
                    OpenFont(builder, attributes, fonts);
                break;

            // Anything else -- <v>, <ruby>, stray HTML -- is dropped rather than printed.
        }
    }

    private static void OpenFont(StringBuilder builder, string attributes, List<FontScope> fonts)
    {
        string? color = ReadAttribute(attributes, "color") is { } value && TryConvertColor(value, out string ass)
            ? ass
            : null;
        string? face = ReadAttribute(attributes, "face");
        string? size = ReadAttribute(attributes, "size") is { } raw &&
                       double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double points) &&
                       points > 0
            ? points.ToString(CultureInfo.InvariantCulture)
            : null;

        fonts.Add(new FontScope(color, face, size));

        if (color is not null)
            builder.Append("{\\c").Append(color).Append('}');
        if (face is not null)
            builder.Append("{\\fn").Append(face).Append('}');
        if (size is not null)
            builder.Append("{\\fs").Append(size).Append('}');
    }

    /// <summary>Restores each attribute to the nearest enclosing font, or to the style if there is none.</summary>
    private static void CloseFont(StringBuilder builder, List<FontScope> fonts)
    {
        if (fonts.Count == 0)
            return;

        FontScope scope = fonts[^1];
        fonts.RemoveAt(fonts.Count - 1);

        if (scope.Color is not null)
            builder.Append("{\\c").Append(Enclosing(fonts, static f => f.Color)).Append('}');
        if (scope.Face is not null)
            builder.Append("{\\fn").Append(Enclosing(fonts, static f => f.Face)).Append('}');
        if (scope.Size is not null)
            builder.Append("{\\fs").Append(Enclosing(fonts, static f => f.Size)).Append('}');
    }

    private static string Enclosing(List<FontScope> fonts, Func<FontScope, string?> select)
    {
        for (int i = fonts.Count - 1; i >= 0; i--)
        {
            if (select(fonts[i]) is { } value)
                return value;
        }

        // An empty argument is how ASS says "back to the style value".
        return string.Empty;
    }

    private static string? ReadAttribute(string attributes, string name)
    {
        ReadOnlySpan<char> span = attributes;
        int from = 0;
        while (from < span.Length)
        {
            int index = span[from..].IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return null;

            index += from;
            int after = index + name.Length;
            bool standalone = index == 0 || !char.IsAsciiLetterOrDigit(span[index - 1]);
            while (after < span.Length && char.IsWhiteSpace(span[after]))
                after++;

            if (!standalone || after >= span.Length || span[after] != '=')
            {
                from = index + name.Length;
                continue;
            }

            after++;
            while (after < span.Length && char.IsWhiteSpace(span[after]))
                after++;

            if (after >= span.Length)
                return null;

            char quote = span[after];
            if (quote is '"' or '\'')
            {
                int close = span[(after + 1)..].IndexOf(quote);
                return close < 0 ? null : span.Slice(after + 1, close).Trim().ToString();
            }

            int space = span[after..].IndexOfAny(' ', '\t');
            return (space < 0 ? span[after..] : span.Slice(after, space)).Trim().ToString();
        }

        return null;
    }

    /// <summary>Converts an HTML color to ASS's <c>&amp;HBBGGRR&amp;</c>.</summary>
    private static bool TryConvertColor(string value, out string ass)
    {
        ass = string.Empty;
        ReadOnlySpan<char> span = value.AsSpan().Trim();
        if (span.IsEmpty)
            return false;

        uint rgb;
        if (span[0] == '#')
        {
            span = span[1..];
            if (span.Length == 3)
            {
                // #rgb is shorthand for #rrggbb.
                Span<char> expanded = stackalloc char[6];
                for (int i = 0; i < 3; i++)
                    expanded[i * 2] = expanded[(i * 2) + 1] = span[i];
                span = expanded.ToString();
            }

            if (span.Length != 6 || !uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                return false;
        }
        else if (span[0] == '&')
        {
            // Already ASS; hand it straight back.
            ass = value.Trim();
            return AssColor.TryParse(ass, out _);
        }
        else if (!TryNamedColor(span, out rgb))
        {
            return false;
        }

        uint bgr = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
        ass = $"&H{bgr:X6}&";
        return true;
    }

    private static bool TryNamedColor(ReadOnlySpan<char> name, out uint rgb)
    {
        rgb = name.Trim().ToString().ToLowerInvariant() switch
        {
            "black" => 0x000000u,
            "silver" => 0xC0C0C0u,
            "gray" or "grey" => 0x808080u,
            "white" => 0xFFFFFFu,
            "maroon" => 0x800000u,
            "red" => 0xFF0000u,
            "purple" => 0x800080u,
            "fuchsia" or "magenta" => 0xFF00FFu,
            "green" => 0x008000u,
            "lime" => 0x00FF00u,
            "olive" => 0x808000u,
            "yellow" => 0xFFFF00u,
            "navy" => 0x000080u,
            "blue" => 0x0000FFu,
            "teal" => 0x008080u,
            "aqua" or "cyan" => 0x00FFFFu,
            "orange" => 0xFFA500u,
            _ => uint.MaxValue,
        };

        return rgb != uint.MaxValue;
    }
}
