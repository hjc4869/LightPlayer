using System.Collections.Generic;
using System.Text;

namespace LightStudio.MediaLibraryCore.Lyrics;

public class ParsedLrc
{
    public string Album { get; internal set; }

    public string Artist { get; internal set; }

    public string Author { get; internal set; }

    public string Title { get; internal set; }

    public string LrcAuthor { get; internal set; }

    public long Offset { get; set; }

    public string CacheFileName { get; set; }

    public List<LrcSentence> Sentences { get; } = new List<LrcSentence>(32);

    public int GetPositionFromTime(long ms)
    {
        if (Sentences.Count == 0 || ms < Sentences[0].Time)
            return 0;

        for (int i = 0; i < Sentences.Count; i++)
        {
            if (ms < Sentences[i].Time)
                return i - 1;
        }

        return Sentences.Count - 1;
    }

    public string SaveAsLrc()
    {
        StringBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(Album)) builder.AppendLine($"[al:{Album}]");
        if (!string.IsNullOrWhiteSpace(Artist)) builder.AppendLine($"[ar:{Artist}]");
        if (!string.IsNullOrWhiteSpace(Author)) builder.AppendLine($"[au:{Author}]");
        if (!string.IsNullOrWhiteSpace(Title)) builder.AppendLine($"[ti:{Title}]");
        if (!string.IsNullOrWhiteSpace(LrcAuthor)) builder.AppendLine($"[by:{LrcAuthor}]");
        if (Offset != 0) builder.AppendLine($"[offset:{Offset:+#;-#;0}]");
        foreach (var sentence in Sentences)
        {
            var hundredthSec = (sentence.Time / 10) % 100;
            var sec = (sentence.Time / 1000) % 60;
            var min = sentence.Time / 60000;
            builder.AppendLine($"[{min:00}:{sec:00}.{hundredthSec:00}]{sentence.Content}");
        }

        return builder.ToString();
    }
}