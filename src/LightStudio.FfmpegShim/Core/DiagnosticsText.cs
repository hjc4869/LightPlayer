using System;
using System.Text;

namespace LightStudio.FfmpegShim.Core;

/// <summary>
/// The two-column layout the diagnostics reports share: a fixed label column and content wrapped to
/// a fixed total width, so the reports stay aligned and never spill in a monospaced view.
/// </summary>
public sealed class DiagnosticsText
{
    public const int LineWidth = 80;

    public const int LabelWidth = 13;

    private static readonly string Indent = new(' ', LabelWidth);

    private readonly StringBuilder text = new();

    /// <summary>Starts a field. Content longer than the line wraps into the content column.</summary>
    public DiagnosticsText Add(string label, string content) =>
        Append(label.Length < LabelWidth ? label.PadRight(LabelWidth) : label + " ", content);

    /// <summary>Adds a further line of the field above, under the same label.</summary>
    public DiagnosticsText AddContinued(string content) => Append(Indent, content);

    /// <summary>Adds lines another report has already laid out this way.</summary>
    public DiagnosticsText AddBlock(string block)
    {
        if (string.IsNullOrEmpty(block))
            return this;

        StartLine();
        text.Append(block.TrimEnd('\n'));
        return this;
    }

    public override string ToString() => text.ToString();

    private DiagnosticsText Append(string prefix, string content)
    {
        content = content.Trim();
        int width = Math.Max(1, LineWidth - prefix.Length);

        while (true)
        {
            StartLine();

            if (content.Length <= width)
            {
                text.Append(prefix).Append(content);
                return this;
            }

            // A single token wider than the column is broken rather than allowed to spill.
            int cut = content.LastIndexOf(' ', width);
            if (cut <= 0)
                cut = width;

            text.Append(prefix).Append(content[..cut].TrimEnd());
            content = content[cut..].TrimStart();
            prefix = Indent;
            width = LineWidth - LabelWidth;
        }
    }

    private void StartLine()
    {
        if (text.Length > 0)
            text.Append('\n');
    }
}
