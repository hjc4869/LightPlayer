using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Subtitles;

/// <summary>
/// Reads an ASS/SSA script into an <see cref="AssDocument"/>.
/// </summary>
/// <remarks>
/// Field order is taken from each section's <c>Format:</c> line rather than assumed, because both
/// the SSA and ASS dialects are in circulation and tools reorder columns. Anything the renderer does
/// not understand -- unknown sections, unknown columns, malformed rows -- is skipped instead of
/// aborting the parse: a subtitle file is decoration, and half of it is better than none of it.
/// </remarks>
internal static class AssParser
{
    private enum Section
    {
        None,
        ScriptInfo,
        Styles,
        LegacyStyles,
        Events,
    }

    public static AssDocument Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var document = new AssDocument();
        var section = Section.None;
        string[]? styleFormat = null;
        string[]? eventFormat = null;

        while (reader.ReadLine() is { } rawLine)
        {
            ReadOnlySpan<char> line = rawLine.AsSpan().Trim();
            if (line.IsEmpty || line[0] == ';' || line.StartsWith("!:"))
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                section = IdentifySection(line[1..^1]);
                styleFormat = null;
                eventFormat = null;
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;

            ReadOnlySpan<char> key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim().ToString();

            switch (section)
            {
                case Section.ScriptInfo:
                    ApplyScriptInfo(document.ScriptInfo, key, value);
                    break;

                case Section.Styles:
                case Section.LegacyStyles:
                    if (key.Equals("Format", StringComparison.OrdinalIgnoreCase))
                        styleFormat = SplitFields(value, int.MaxValue);
                    else if (key.Equals("Style", StringComparison.OrdinalIgnoreCase))
                        AddStyle(document, styleFormat, value, section == Section.LegacyStyles);
                    break;

                case Section.Events:
                    if (key.Equals("Format", StringComparison.OrdinalIgnoreCase))
                        eventFormat = SplitFields(value, int.MaxValue);
                    else if (key.Equals("Dialogue", StringComparison.OrdinalIgnoreCase))
                        AddDialogue(document, eventFormat, value);
                    break;
            }
        }

        document.Finish();
        return document;
    }

    private static Section IdentifySection(ReadOnlySpan<char> name)
    {
        name = name.Trim();
        if (name.Equals("Script Info", StringComparison.OrdinalIgnoreCase))
            return Section.ScriptInfo;
        if (name.Equals("Events", StringComparison.OrdinalIgnoreCase))
            return Section.Events;
        if (name.Equals("V4 Styles", StringComparison.OrdinalIgnoreCase))
            return Section.LegacyStyles;
        if (name.StartsWith("V4", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith("Styles", StringComparison.OrdinalIgnoreCase))
            return Section.Styles;
        if (name.Contains("Styles", StringComparison.OrdinalIgnoreCase))
            return Section.Styles;
        return Section.None;
    }

    private static void ApplyScriptInfo(AssScriptInfo info, ReadOnlySpan<char> key, string value)
    {
        if (key.Equals("PlayResX", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseInt(value, out int x) && x > 0)
                info.PlayResX = x;
        }
        else if (key.Equals("PlayResY", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseInt(value, out int y) && y > 0)
                info.PlayResY = y;
        }
        else if (key.Equals("WrapStyle", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseInt(value, out int wrap))
                info.WrapStyle = wrap;
        }
        else if (key.Equals("LayoutResX", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseInt(value, out int layoutX) && layoutX > 0)
                info.LayoutResX = layoutX;
        }
        else if (key.Equals("LayoutResY", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseInt(value, out int layoutY) && layoutY > 0)
                info.LayoutResY = layoutY;
        }
        else if (key.Equals("ScaledBorderAndShadow", StringComparison.OrdinalIgnoreCase))
        {
            info.ScaledBorderAndShadow = value.StartsWith("y", StringComparison.OrdinalIgnoreCase) ||
                                         value.StartsWith("1", StringComparison.Ordinal) ||
                                         value.StartsWith("t", StringComparison.OrdinalIgnoreCase);
        }
        else if (key.Equals("Title", StringComparison.OrdinalIgnoreCase))
        {
            info.Title = value;
        }
    }

    private static void AddStyle(AssDocument document, string[]? format, string row, bool legacy)
    {
        format ??= legacy ? DefaultLegacyStyleFormat : DefaultStyleFormat;
        string[] fields = SplitFields(row, format.Length);
        var style = new AssStyle();
        bool named = false;

        for (int i = 0; i < format.Length && i < fields.Length; i++)
        {
            string field = fields[i];
            switch (Normalize(format[i]))
            {
                case "name": style.Name = field; named = true; break;
                case "fontname": style.FontName = field; break;
                case "fontsize": style.FontSize = ParseDouble(field, style.FontSize); break;
                case "primarycolour": style.PrimaryColour = AssColor.Parse(field, style.PrimaryColour); break;
                case "secondarycolour": style.SecondaryColour = AssColor.Parse(field, style.SecondaryColour); break;
                case "tertiarycolour":
                case "outlinecolour": style.OutlineColour = AssColor.Parse(field, style.OutlineColour); break;
                case "backcolour": style.BackColour = AssColor.Parse(field, style.BackColour); break;
                case "bold": style.Bold = ParseFlag(field); break;
                case "italic": style.Italic = ParseFlag(field); break;
                case "underline": style.Underline = ParseFlag(field); break;
                case "strikeout": style.StrikeOut = ParseFlag(field); break;
                case "scalex": style.ScaleX = ParseDouble(field, style.ScaleX); break;
                case "scaley": style.ScaleY = ParseDouble(field, style.ScaleY); break;
                case "spacing": style.Spacing = ParseDouble(field, style.Spacing); break;
                case "angle": style.Angle = ParseDouble(field, style.Angle); break;
                case "borderstyle": style.BorderStyle = (int)ParseDouble(field, style.BorderStyle); break;
                case "outline": style.Outline = ParseDouble(field, style.Outline); break;
                case "shadow": style.Shadow = ParseDouble(field, style.Shadow); break;
                case "alignment":
                    int alignment = (int)ParseDouble(field, style.Alignment);
                    style.Alignment = legacy ? ConvertLegacyAlignment(alignment) : ClampAlignment(alignment);
                    break;
                case "marginl": style.MarginL = (int)ParseDouble(field, style.MarginL); break;
                case "marginr": style.MarginR = (int)ParseDouble(field, style.MarginR); break;
                case "marginv": style.MarginV = (int)ParseDouble(field, style.MarginV); break;
                case "encoding": style.Encoding = (int)ParseDouble(field, style.Encoding); break;
            }
        }

        if (named && style.Name.Length > 0)
            document.AddStyle(style);
    }

    private static void AddDialogue(AssDocument document, string[]? format, string row)
    {
        format ??= DefaultEventFormat;
        string[] fields = SplitFields(row, format.Length);
        var dialogue = new AssDialogue();
        bool timed = false;

        for (int i = 0; i < format.Length && i < fields.Length; i++)
        {
            string field = fields[i];
            switch (Normalize(format[i]))
            {
                case "layer":
                case "marked": dialogue.Layer = TryParseInt(field, out int layer) ? layer : 0; break;
                case "start":
                    if (TryParseTime(field, out TimeSpan start)) { dialogue.Start = start; timed = true; }
                    break;
                case "end":
                    if (TryParseTime(field, out TimeSpan end)) dialogue.End = end;
                    break;
                case "style": dialogue.StyleName = field; break;
                case "name":
                case "actor": dialogue.Actor = field; break;
                case "effect": dialogue.Effect = field; break;
                case "marginl": dialogue.MarginL = TryParseInt(field, out int left) ? left : 0; break;
                case "marginr": dialogue.MarginR = TryParseInt(field, out int right) ? right : 0; break;
                case "marginv": dialogue.MarginV = TryParseInt(field, out int vertical) ? vertical : 0; break;
                case "text": dialogue.Text = field; break;
            }
        }

        if (timed && dialogue.End > dialogue.Start)
            document.AddEvent(dialogue);
    }

    /// <summary>
    /// Splits a comma separated row; the final column keeps every remaining comma, which is what
    /// makes commas inside dialogue text safe.
    /// </summary>
    private static string[] SplitFields(string row, int count)
    {
        var fields = new List<string>();
        int start = 0;
        for (int i = 0; i < row.Length && fields.Count < count - 1; i++)
        {
            if (row[i] != ',')
                continue;
            fields.Add(row[start..i].Trim());
            start = i + 1;
        }

        fields.Add(row[start..].Trim());
        return [.. fields];
    }

    private static string Normalize(string field)
    {
        char[] buffer = new char[field.Length];
        int length = 0;
        foreach (char c in field)
        {
            if (char.IsLetter(c))
                buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer, 0, length);
    }

    /// <summary>SSA counts 1-3 along the bottom and adds 4 for top titles, 8 for mid titles.</summary>
    private static int ConvertLegacyAlignment(int value)
    {
        int horizontal = value & 0x3;
        if (horizontal is < 1 or > 3)
            horizontal = 2;

        if ((value & 0x8) != 0)
            return horizontal + 3;
        if ((value & 0x4) != 0)
            return horizontal + 6;
        return horizontal;
    }

    private static int ClampAlignment(int value) => value is >= 1 and <= 9 ? value : 2;

    private static bool ParseFlag(string value)
        => TryParseInt(value, out int number) && number != 0;

    private static double ParseDouble(string value, double fallback)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            ? result
            : fallback;

    private static bool TryParseInt(string value, out int result)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            result = (int)number;
            return true;
        }

        result = 0;
        return false;
    }

    /// <summary>Parses <c>h:mm:ss.cc</c>, tolerating missing components and extra digits.</summary>
    internal static bool TryParseTime(ReadOnlySpan<char> value, out TimeSpan time)
    {
        time = default;
        ReadOnlySpan<char> span = value.Trim();
        if (span.IsEmpty)
            return false;

        double total = 0;
        double current = 0;
        int parts = 0;
        bool any = false;

        for (int i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || span[i] == ':')
            {
                total = total * 60 + current;
                current = 0;
                parts++;
                if (parts > 3)
                    return false;
                continue;
            }

            char c = span[i];
            if (c == '.' || c == ',')
            {
                double fraction = 0;
                double scale = 0.1;
                for (int j = i + 1; j < span.Length && char.IsAsciiDigit(span[j]); j++)
                {
                    fraction += (span[j] - '0') * scale;
                    scale *= 0.1;
                    i = j;
                }

                current += fraction;
                any = true;
                continue;
            }

            if (!char.IsAsciiDigit(c))
                return false;

            current = current * 10 + (c - '0');
            any = true;
        }

        if (!any || total > TimeSpan.MaxValue.TotalSeconds)
            return false;

        time = TimeSpan.FromSeconds(total);
        return true;
    }

    private static readonly string[] DefaultStyleFormat =
    [
        "Name", "Fontname", "Fontsize", "PrimaryColour", "SecondaryColour", "OutlineColour",
        "BackColour", "Bold", "Italic", "Underline", "StrikeOut", "ScaleX", "ScaleY", "Spacing",
        "Angle", "BorderStyle", "Outline", "Shadow", "Alignment", "MarginL", "MarginR", "MarginV",
        "Encoding",
    ];

    private static readonly string[] DefaultLegacyStyleFormat =
    [
        "Name", "Fontname", "Fontsize", "PrimaryColour", "SecondaryColour", "TertiaryColour",
        "BackColour", "Bold", "Italic", "BorderStyle", "Outline", "Shadow", "Alignment",
        "MarginL", "MarginR", "MarginV", "AlphaLevel", "Encoding",
    ];

    private static readonly string[] DefaultEventFormat =
    [
        "Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text",
    ];
}
