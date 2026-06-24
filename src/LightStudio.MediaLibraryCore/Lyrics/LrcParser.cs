using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LightStudio.MediaLibraryCore.Lyrics;

public static class LrcParser
{
    static Lazy<LrcSentenceComparer> Comparer = new Lazy<LrcSentenceComparer>();

    public static ParsedLrc Parse(string lrcText)
    {
        return Parse(new StringReader(lrcText));
    }

    public static ParsedLrc Parse(Stream lrcStream, bool leaveOpen = true)
    {
        using (var reader = new StreamReader(lrcStream,
            Encoding.UTF8, false, 1024, leaveOpen))
        {
            return Parse(reader);
        }
    }

    public static ParsedLrc Parse(TextReader reader)
    {
        ParsedLrc lrc = new ParsedLrc();

        string line;

        // TextReader will handle the newline well.
        while ((line = reader.ReadLine()?.Trim()) != null)
        {
            var parts = ExtractMorpheme(line);
            if (parts == null || parts.Length < 1) continue;
            if (!TryParseTime(parts[0], out _))
            {
                TryParseMetadata(parts[0], lrc);
                continue;
            }

            var content = parts[parts.Length - 1];
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (TryParseTime(parts[i], out var time))
                {
                    lrc.Sentences.Add(new LrcSentence(time, content));
                }
            }
        }

        // List<T>.Sort still creates wrapper around Comparison<T>.
        // A straightforward method is using IComparer<T>.
        lrc.Sentences.Sort(Comparer.Value);

        return lrc;
    }

    static string[] ExtractMorpheme(string line)
    {
        List<string> parts = new List<string>(4);

        if (line.Length < 3 || line[0] != '[') return null;
        int borderPos;
        if ((borderPos = line.IndexOf(']')) < 2) return null;
        parts.Add(line.Substring(1, borderPos - 1));

        // Check if it has more timestamps
        int lastBorderPos;
        if (borderPos != (lastBorderPos = line.LastIndexOf(']')))
        {
            int nextPos;
            do
            {
                nextPos = line.IndexOf(']', borderPos + 1);
                // +2 because of ][
                parts.Add(line.Substring(borderPos + 2, nextPos - borderPos - 2));
                borderPos = nextPos;
            } while (nextPos < lastBorderPos);
        }
        if (lastBorderPos != line.Length - 1)
        {
            parts.Add(line.Substring(lastBorderPos + 1));
        }
        else
        {
            parts.Add(string.Empty);
        }

        return parts.ToArray();
    }

    static void TryParseMetadata(string part, ParsedLrc lrc)
    {
        var s = part.IndexOf(':');
        if (s == -1) return;
        switch (part.Substring(0, s))
        {
            case "al":
                lrc.Album = part.Substring(s + 1);
                break;
            case "ar":
                lrc.Artist = part.Substring(s + 1);
                break;
            case "au":
                lrc.Author = part.Substring(s + 1);
                break;
            case "by":
                lrc.LrcAuthor = part.Substring(s + 1);
                break;
            case "offset":
                if (long.TryParse(part.Substring(s + 1), out var offset))
                {
                    lrc.Offset = offset;
                }
                break;
            case "ti":
                lrc.Title = part.Substring(s + 1);
                break;
        }
    }

    static bool TryParseTime(string time, out long parsedTime)
    {
        parsedTime = 0;
        string[] timeParts = time.Split(':', '.');
        if (timeParts == null || timeParts.Length < 3 || timeParts[2].Length > 3)
        {
            return false;
        }

        if (int.TryParse(timeParts[0], out var minute) &&
            int.TryParse(timeParts[1], out var second) &&
            int.TryParse(timeParts[2].PadRight(3, '0'), out var milliSeconds))
        {
            parsedTime = minute * 60000L + second * 1000L + milliSeconds;
            return true;
        }

        return false;
    }

    class LrcSentenceComparer : IComparer<LrcSentence>
    {
        public int Compare(LrcSentence x, LrcSentence y)
        {
            if (y == null && x == null)
                return 0;
            if (y == null)
                return 1;
            if (x == null)
                return -1;

            return x.Time.CompareTo(y.Time);
        }
    }
}