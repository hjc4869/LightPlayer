using System.Collections.Generic;
using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Subtitles.Layout;

/// <summary>One run of characters that share a single resolved style, or an explicit line break.</summary>
internal readonly struct SubtitleSpan
{
    private SubtitleSpan(string text, SpanStyle? style, bool isBreak, bool hardBreak, bool drawing)
    {
        Text = text;
        Style = style;
        IsLineBreak = isBreak;
        IsHardBreak = hardBreak;
        IsDrawing = drawing;
    }

    /// <summary>Literal text, or the drawing commands when <see cref="IsDrawing"/> is set.</summary>
    public string Text { get; }

    public SpanStyle? Style { get; }

    public bool IsLineBreak { get; }

    public bool IsHardBreak { get; }

    public bool IsDrawing { get; }

    public static SubtitleSpan FromText(string text, SpanStyle style) => new(text, style, false, false, false);

    public static SubtitleSpan FromDrawing(string commands, SpanStyle style) => new(commands, style, false, false, true);

    public static SubtitleSpan LineBreak(bool hard) => new(string.Empty, null, true, hard, false);
}

/// <summary>The region an event is confined to, from <c>\clip</c> or <c>\iclip</c>.</summary>
internal sealed class SubtitleClip
{
    /// <summary><c>\iclip</c> keeps everything <em>outside</em> the shape.</summary>
    public bool Inverse { get; set; }

    public bool IsRectangle { get; set; }

    public double Left { get; set; }

    public double Top { get; set; }

    public double Right { get; set; }

    public double Bottom { get; set; }

    /// <summary>Drawing commands for the vector form.</summary>
    public string? Drawing { get; set; }

    public int DrawingScale { get; set; } = 1;
}

/// <summary>
/// The whole-event opacity ramp of <c>\fad</c> or <c>\fade</c>, in seconds and 0-255 opacities.
/// </summary>
internal sealed class SubtitleFade
{
    public required double Alpha1 { get; init; }

    public required double Alpha2 { get; init; }

    public required double Alpha3 { get; init; }

    public required double Time1 { get; init; }

    public required double Time2 { get; init; }

    public required double Time3 { get; init; }

    public required double Time4 { get; init; }
}

/// <summary>
/// A dialogue event after override parsing: styled spans plus the event-wide placement it asked for.
/// </summary>
internal sealed class SegmentedEvent
{
    public List<SubtitleSpan> Spans { get; } = [];

    /// <summary>Numpad alignment, 1-9.</summary>
    public int Alignment { get; set; } = 2;

    public bool HasPosition { get; set; }

    public double PositionX { get; set; }

    public double PositionY { get; set; }

    /// <summary>Rotation around Z in degrees, counter-clockwise, applied to the whole event.</summary>
    public double Angle { get; set; }

    /// <summary>Rotation around X, from <c>\frx</c>.</summary>
    public double AngleX { get; set; }

    /// <summary>Rotation around Y, from <c>\fry</c>.</summary>
    public double AngleY { get; set; }

    public double ShearX { get; set; }

    public double ShearY { get; set; }

    /// <summary>Center of rotation from <c>\org</c>; otherwise the alignment anchor is used.</summary>
    public bool HasOrigin { get; set; }

    public double OriginX { get; set; }

    public double OriginY { get; set; }

    public bool HasMove { get; set; }

    public double MoveX1 { get; set; }

    public double MoveY1 { get; set; }

    public double MoveX2 { get; set; }

    public double MoveY2 { get; set; }

    /// <summary>Seconds from the start of the event.</summary>
    public double MoveStart { get; set; }

    public double MoveEnd { get; set; }

    public SubtitleFade? Fade { get; set; }

    public SubtitleClip? Clip { get; set; }

    /// <summary>Set when a <c>\t</c> was seen, so the layout has to be rebuilt as time passes.</summary>
    public bool IsAnimated { get; set; }

    /// <summary>
    /// Seconds from the start of the event between which some <c>\t</c> is still changing; outside
    /// that span every transition sits at its start or end value.
    /// </summary>
    public double AnimationStart { get; set; } = double.PositiveInfinity;

    public double AnimationEnd { get; set; } = double.NegativeInfinity;

    public int MarginL { get; set; }

    public int MarginR { get; set; }

    public int MarginV { get; set; }

    public int WrapStyle { get; set; }
}
