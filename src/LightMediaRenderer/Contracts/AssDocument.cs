using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LightMediaRenderer.Subtitles.Text;
using LightMediaRenderer.Subtitles;

namespace LightMediaRenderer.Contracts;

/// <summary>
/// A parsed ASS/SSA script.
/// </summary>
public sealed class AssDocument
{
    /// <summary>Used when a script has no styles at all, or names one that does not exist.</summary>
    private static readonly AssStyle FallbackStyle = new();

    private readonly Dictionary<string, AssStyle> stylesByName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<AssStyle> styles = [];
    private readonly List<AssDialogue> events = [];

    private Dictionary<AssStyle, SubtitleScript>? scriptsByStyle;
    private SubtitleScript documentScript;

    internal AssScriptInfo ScriptInfo { get; } = new();

    internal IReadOnlyList<AssStyle> Styles => styles;

    /// <summary>Dialogue events, ordered by start time.</summary>
    internal IReadOnlyList<AssDialogue> Events => events;

    public static AssDocument Parse(string text) => AssParser.Parse(new StringReader(text));

    public static AssDocument Load(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return AssParser.Parse(reader);
    }

    public static AssDocument Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Resolves a style name, falling back to <c>Default</c> and then to built-in defaults.</summary>
    internal AssStyle GetStyle(string? name)
    {
        if (!string.IsNullOrEmpty(name) && stylesByName.TryGetValue(name, out AssStyle? style))
            return style;

        if (stylesByName.TryGetValue("Default", out AssStyle? fallback))
            return fallback;

        return styles.Count > 0 ? styles[0] : FallbackStyle;
    }

    /// <summary>
    /// Appends every event visible at <paramref name="time"/>, ordered by layer then file order.
    /// </summary>
    internal void CollectActive(TimeSpan time, List<AssDialogue> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        destination.Clear();
        foreach (AssDialogue dialogue in events)
        {
            // Events are start-ordered, so nothing after the first future event can be active.
            if (dialogue.Start > time)
                break;
            if (dialogue.End > time)
                destination.Add(dialogue);
        }

        destination.Sort(static (a, b) => a.Layer != b.Layer ? a.Layer.CompareTo(b.Layer) : a.Index.CompareTo(b.Index));
    }

    internal void AddStyle(AssStyle style)
    {
        if (stylesByName.TryAdd(style.Name, style))
            styles.Add(style);
    }

    internal void AddEvent(AssDialogue dialogue)
    {
        dialogue.Index = events.Count;
        events.Add(dialogue);
    }

    internal void Finish()
    {
        // A stable sort keeps Index meaningful for events that share a start time.
        events.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Index.CompareTo(b.Index));
    }

    /// <summary>Which regional Han forms the lines drawn with <paramref name="style"/> are written in.</summary>
    internal SubtitleScript GetScript(AssStyle style)
    {
        EnsureScripts();
        return scriptsByStyle.TryGetValue(style, out SubtitleScript script) ? script : SubtitleScript.Unknown;
    }

    /// <summary>The script of the file as a whole, for styles that carry no text of their own.</summary>
    internal SubtitleScript DocumentScript
    {
        get
        {
            EnsureScripts();
            return documentScript;
        }
    }

    /// <remarks>
    /// A style is classified from every line that uses it rather than line by line, because a short
    /// line often contains no character that has been simplified at all. Voting over the whole file
    /// also means one atypical line cannot make a style change font halfway through.
    /// </remarks>
    [MemberNotNull(nameof(scriptsByStyle))]
    private void EnsureScripts()
    {
        if (scriptsByStyle is not null)
            return;

        Dictionary<AssStyle, ScriptVotes> votes = [];
        var whole = default(ScriptVotes);

        foreach (AssDialogue dialogue in events)
        {
            SubtitleScript script = SubtitleScripts.Detect(dialogue.Text);
            if (script == SubtitleScript.Unknown)
                continue;

            AssStyle style = GetStyle(dialogue.StyleName);
            ref ScriptVotes tally = ref CollectionsMarshal.GetValueRefOrAddDefault(votes, style, out _);
            tally.Add(script);
            whole.Add(script);
        }

        Dictionary<AssStyle, SubtitleScript> resolved = [];
        foreach ((AssStyle style, ScriptVotes tally) in votes)
            resolved[style] = tally.Winner;

        documentScript = whole.Winner;
        scriptsByStyle = resolved;
    }
}
