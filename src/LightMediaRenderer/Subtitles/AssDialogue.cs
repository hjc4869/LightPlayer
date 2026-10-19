using System;

namespace LightMediaRenderer.Subtitles;

/// <summary>
/// One <c>Dialogue:</c> row, with its override tags still embedded in <see cref="Text"/>.
/// </summary>
internal sealed class AssDialogue
{
    public int Layer { get; set; }

    public TimeSpan Start { get; set; }

    public TimeSpan End { get; set; }

    public string StyleName { get; set; } = "Default";

    public string Actor { get; set; } = string.Empty;

    public string Effect { get; set; } = string.Empty;

    /// <summary>Per-event left margin; 0 means "inherit from the style".</summary>
    public int MarginL { get; set; }

    public int MarginR { get; set; }

    public int MarginV { get; set; }

    /// <summary>The raw event body, override blocks included.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Order within the file, used to keep same-layer events stacked deterministically.</summary>
    public int Index { get; internal set; }

    public bool IsActiveAt(TimeSpan time) => time >= Start && time < End;
}
