using System;
using System.Collections.Generic;
using HbBuffer = HarfBuzzSharp.Buffer;
using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Subtitles.Text;

/// <summary>One positioned glyph, in em fractions so that the result is size independent.</summary>
internal readonly struct ShapedGlyph
{
    public ShapedGlyph(ushort glyph, float advance, float offsetX, float offsetY, bool rotated)
    {
        Glyph = glyph;
        Advance = advance;
        OffsetX = offsetX;
        OffsetY = offsetY;
        Rotated = rotated;
    }

    public ushort Glyph { get; }

    /// <summary>Horizontal advance as a fraction of the em square.</summary>
    public float Advance { get; }

    public float OffsetX { get; }

    /// <summary>Vertical offset in screen orientation: positive moves the glyph down.</summary>
    public float OffsetY { get; }

    /// <summary>
    /// Set for a character an <c>@</c> font turns on its side; <see cref="Advance"/> is then the
    /// glyph's vertical advance, because that is the room it takes up once turned.
    /// </summary>
    public bool Rotated { get; }
}

/// <summary>
/// Runs HarfBuzz over a piece of text and caches the result.
/// </summary>
/// <remarks>
/// Shaping is done once per (face, string) pair and stored in em fractions, so the same cache entry
/// serves every font size, every <c>\fscx</c> and every frame the text is on screen. Subtitles repeat
/// the same words constantly, which makes this a very high hit-rate cache for a very small table.
/// </remarks>
internal sealed class TextShaper : IDisposable
{
    /// <summary>
    /// First codepoint an <c>@</c> font turns on its side; below it VSFilter left text upright.
    /// </summary>
    private const int VerticalLowerBound = 0x02F1;

    private static readonly ShapedGlyph[] Empty = [];

    /// <summary>The vertical forms and vertical kerning an <c>@</c> font is expected to provide.</summary>
    private static readonly HarfBuzzSharp.Feature[] VerticalFeatures =
    [
        new(new HarfBuzzSharp.Tag('v', 'e', 'r', 't'), 1, 0, uint.MaxValue),
        new(new HarfBuzzSharp.Tag('v', 'k', 'n', 'a'), 1, 0, uint.MaxValue),
    ];

    private readonly Dictionary<(SubtitleFont Font, string Text, bool Vertical), ShapedGlyph[]> cache = [];
    private readonly HbBuffer buffer = new();

    public ShapedGlyph[] Shape(SubtitleFont font, string text, bool vertical = false)
    {
        if (string.IsNullOrEmpty(text))
            return Empty;

        var key = (font, text, vertical);
        if (cache.TryGetValue(key, out ShapedGlyph[]? cached))
            return cached;

        buffer.ClearContents();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();

        // The line still runs left to right: an @ font turns the glyphs, not the layout.
        if (vertical)
            font.Shaper.Shape(buffer, VerticalFeatures);
        else
            font.Shaper.Shape(buffer);

        ReadOnlySpan<HarfBuzzSharp.GlyphInfo> infos = buffer.GetGlyphInfoSpan();
        ReadOnlySpan<HarfBuzzSharp.GlyphPosition> positions = buffer.GetGlyphPositionSpan();

        float scale = 1f / font.UnitsPerEm;
        var glyphs = new ShapedGlyph[infos.Length];
        for (int i = 0; i < infos.Length; i++)
        {
            var glyph = (ushort)infos[i].Codepoint;
            bool rotated = vertical && IsRotated(text, infos[i].Cluster);

            glyphs[i] = new ShapedGlyph(
                glyph,
                rotated ? font.GetVerticalAdvance(glyph) : positions[i].XAdvance * scale,
                positions[i].XOffset * scale,
                // HarfBuzz measures up, canvases measure down.
                -positions[i].YOffset * scale,
                rotated);
        }

        if (cache.Count > 4096)
            cache.Clear();

        cache[key] = glyphs;
        return glyphs;
    }

    public void Dispose() => buffer.Dispose();

    private static bool IsRotated(string text, uint cluster)
    {
        int i = (int)cluster;
        if (i < 0 || i >= text.Length)
            return false;

        int codepoint = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
            ? char.ConvertToUtf32(text[i], text[i + 1])
            : text[i];

        return codepoint >= VerticalLowerBound;
    }
}
