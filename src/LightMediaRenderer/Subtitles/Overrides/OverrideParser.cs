using System;
using System.Collections.Generic;
using System.Text;
using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Subtitles.Overrides;

/// <summary>
/// Turns the body of a dialogue event into a flat token stream.
/// </summary>
/// <remarks>
/// Tag names are matched against a table longest-first, which is the only reliable way to tell
/// <c>\fr</c> from <c>\frz</c>, <c>\b</c> from <c>\bord</c> or <c>\c</c> from <c>\clip</c> -- the
/// format has no separator between a tag and its argument. Arguments run to the next backslash that
/// is not nested inside parentheses, so the tag list nested inside <c>\t(0,500,\frz90)</c> arrives
/// intact and can be retokenized with <see cref="ParseTags"/>.
/// </remarks>
internal static class OverrideParser
{
    /// <summary>Every tag the tokenizer knows how to delimit, ordered longest name first.</summary>
    private static readonly string[] KnownTags = BuildTagTable();

    public static void Parse(string text, List<OverrideToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        tokens.Clear();
        if (string.IsNullOrEmpty(text))
            return;

        var literal = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '{')
            {
                int close = text.IndexOf('}', i + 1);
                if (close < 0)
                {
                    // An unbalanced brace is literal text, exactly as VSFilter treats it.
                    literal.Append(c);
                    i++;
                    continue;
                }

                Flush(literal, tokens);
                ParseBlock(text.AsSpan(i + 1, close - i - 1), tokens);
                i = close + 1;
                continue;
            }

            if (c == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                switch (next)
                {
                    case 'N':
                        Flush(literal, tokens);
                        tokens.Add(OverrideToken.LineBreak(hard: true));
                        break;
                    case 'n':
                        Flush(literal, tokens);
                        tokens.Add(OverrideToken.LineBreak(hard: false));
                        break;
                    case 'h':
                        literal.Append('\u00A0');
                        break;
                    case '{':
                    case '}':
                    case '\\':
                        literal.Append(next);
                        break;
                    default:
                        literal.Append(c).Append(next);
                        break;
                }

                i += 2;
                continue;
            }

            literal.Append(c);
            i++;
        }

        Flush(literal, tokens);
    }

    /// <summary>
    /// Tokenizes the body of an override block, appending to <paramref name="tokens"/>.
    /// </summary>
    /// <remarks>Used for the tag list nested inside <c>\t(...)</c>.</remarks>
    public static void ParseTags(ReadOnlySpan<char> block, List<OverrideToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ParseBlock(block, tokens);
    }

    private static void Flush(StringBuilder literal, List<OverrideToken> tokens)
    {
        if (literal.Length == 0)
            return;

        tokens.Add(OverrideToken.Text(literal.ToString()));
        literal.Clear();
    }

    private static void ParseBlock(ReadOnlySpan<char> block, List<OverrideToken> tokens)
    {
        int i = 0;
        while (i < block.Length)
        {
            if (block[i] != '\\')
            {
                // Free text inside a block is a comment.
                i++;
                continue;
            }

            i++;
            while (i < block.Length && block[i] == ' ')
                i++;

            ReadOnlySpan<char> rest = block[i..];
            string? name = MatchTag(rest);
            if (name is null)
            {
                int length = 0;
                while (length < rest.Length && char.IsAsciiLetterOrDigit(rest[length]))
                    length++;
                if (length == 0)
                    continue;
                name = rest[..length].ToString().ToLowerInvariant();
            }

            i += name.Length;

            // \K is the one tag whose case is meaningful: it is a synonym for \kf.
            if (name.Length == 1 && rest[0] == 'K')
                name = "kf";

            int start = i;
            int depth = 0;
            while (i < block.Length)
            {
                char c = block[i];
                if (c == '(')
                    depth++;
                else if (c == ')')
                    depth = Math.Max(0, depth - 1);
                else if (c == '\\' && depth == 0)
                    break;
                i++;
            }

            tokens.Add(OverrideToken.Tag(name, block[start..i].Trim().ToString()));
        }
    }

    private static string? MatchTag(ReadOnlySpan<char> text)
    {
        foreach (string tag in KnownTags)
        {
            if (text.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                return tag;
        }

        return null;
    }

    private static string[] BuildTagTable()
    {
        string[] tags =
        [
            "fscx", "fscy", "frz", "frx", "fry", "fr", "fsp", "fs", "fn", "fe",
            "fax", "fay", "fade", "fad",
            "bord", "xbord", "ybord", "shad", "xshad", "yshad", "blur", "be",
            "alpha", "1a", "2a", "3a", "4a", "1c", "2c", "3c", "4c", "c",
            "an", "a", "pos", "b", "i", "u", "s", "r", "q",
            "iclip", "clip", "move", "org", "pbo", "p", "t",
            "kf", "ko", "kt", "k",
        ];

        Array.Sort(tags, static (a, b) => b.Length.CompareTo(a.Length));
        return tags;
    }
}
