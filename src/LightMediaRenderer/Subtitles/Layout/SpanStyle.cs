using LightMediaRenderer.Contracts;
using LightMediaRenderer.Subtitles.Text;
using LightMediaRenderer.Subtitles;

namespace LightMediaRenderer.Subtitles.Layout;

/// <summary>What a karaoke tag does to the run it covers.</summary>
internal enum KaraokeMode
{
    None,

    /// <summary><c>\k</c> -- the whole syllable switches color at its start.</summary>
    Fill,

    /// <summary><c>\kf</c> and <c>\K</c> -- the color sweeps across the syllable.</summary>
    Sweep,

    /// <summary><c>\ko</c> -- like <c>\k</c>, but the outline appears only once sung.</summary>
    Outline,
}

/// <summary>
/// The immutable appearance of one run of characters, after every override tag before it has been
/// applied. All lengths are in script (<c>PlayRes</c>) pixels.
/// </summary>
internal sealed class SpanStyle
{
    public required string FontName { get; init; }

    /// <summary>Regional form the run is written in, which decides what it falls back to.</summary>
    public required SubtitleScript Script { get; init; }

    public required double FontSize { get; init; }

    public required bool Bold { get; init; }

    public required bool Italic { get; init; }

    public required bool Underline { get; init; }

    public required bool StrikeOut { get; init; }

    public required AssColor PrimaryColour { get; init; }

    /// <summary>Only ever drawn by the karaoke tags.</summary>
    public required AssColor SecondaryColour { get; init; }

    public required AssColor OutlineColour { get; init; }

    public required AssColor ShadowColour { get; init; }

    /// <summary>Horizontal scale as a fraction; 1.0 is unscaled.</summary>
    public required double ScaleX { get; init; }

    public required double ScaleY { get; init; }

    /// <summary>Extra advance after every glyph.</summary>
    public required double Spacing { get; init; }

    public required double BorderX { get; init; }

    public required double BorderY { get; init; }

    public required double ShadowX { get; init; }

    public required double ShadowY { get; init; }

    /// <summary>1 = outline plus drop shadow, 3 = opaque box behind the line.</summary>
    public required int BorderStyle { get; init; }

    /// <summary><c>\blur</c>, a Gaussian radius in script pixels.</summary>
    public required double Blur { get; init; }

    /// <summary><c>\be</c>, a count of box-blur passes.</summary>
    public required double BlurEdges { get; init; }

    /// <summary><c>\p</c>; 0 means the span is text, 1 and above a vector drawing.</summary>
    public required int DrawingScale { get; init; }

    /// <summary><c>\pbo</c>, in drawing units.</summary>
    public required double DrawingBaselineOffset { get; init; }

    public required KaraokeMode Karaoke { get; init; }

    /// <summary>Seconds from the start of the event.</summary>
    public required double KaraokeStart { get; init; }

    public required double KaraokeEnd { get; init; }
}
