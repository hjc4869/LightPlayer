using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LightMediaRenderer.Contracts;
using SkiaSharp;

namespace LightMediaRenderer.Subtitles.Layout;

/// <summary>A shaped, positioned piece of text sharing one style, or one vector drawing.</summary>
internal sealed class LayoutRun : IDisposable
{
    private SKTextBlob? blob;
    private bool blobCreated;
    private SKPath? textPath;
    private bool textPathCreated;

    public required SpanStyle Style { get; init; }

    /// <summary>A <c>\p</c> shape, already scaled and shifted onto the baseline.</summary>
    public SKPath? Path { get; init; }

    public SKFont? Font { get; init; }

    public ushort[]? GlyphIndices { get; init; }

    /// <summary>Glyph origins relative to the run's origin, with the baseline at y = 0.</summary>
    public SKPoint[]? GlyphPositions { get; init; }

    /// <summary>Offset from the start of the line.</summary>
    public required float OffsetX { get; init; }

    public required float Width { get; init; }

    /// <summary>How far into its karaoke syllable this run starts; syllables can span runs.</summary>
    public float KaraokeOffset { get; set; }

    /// <summary>Width of the whole syllable this run belongs to.</summary>
    public float KaraokeWidth { get; set; }

    public SKTextBlob? GetBlob()
    {
        if (blobCreated)
            return blob;

        blobCreated = true;
        if (Font is null || GlyphIndices is null || GlyphPositions is null)
            return null;

        blob = SKTextBlob.CreatePositioned(
            MemoryMarshal.AsBytes(GlyphIndices.AsSpan()),
            SKTextEncoding.GlyphId,
            Font,
            GlyphPositions);
        return blob;
    }

    public SKPath? GetTextPath()
    {
        if (textPathCreated)
            return textPath;

        textPathCreated = true;
        if (Font is null || GlyphIndices is null || GlyphPositions is null)
            return null;

        using var result = new SKPathBuilder();
        for (int index = 0; index < GlyphIndices.Length; index++)
        {
            using SKPath? glyphPath = Font.GetGlyphPath(GlyphIndices[index]);
            if (glyphPath is null)
                return null;

            glyphPath.Transform(SKMatrix.CreateTranslation(
                GlyphPositions[index].X,
                GlyphPositions[index].Y));
            result.AddPath(glyphPath);
        }

        textPath = result.Detach();
        return textPath;
    }

    /// <summary>
    /// Takes over the blob and text path of an identical run from an earlier animation step, so the
    /// GPU caches keyed on those objects keep hitting while only colors or placement change.
    /// </summary>
    public void AdoptFrom(LayoutRun previous)
    {
        if (blobCreated || textPathCreated || Font is null || !ReferenceEquals(Font, previous.Font) ||
            GlyphIndices is null || previous.GlyphIndices is null ||
            GlyphPositions is null || previous.GlyphPositions is null ||
            !GlyphIndices.AsSpan().SequenceEqual(previous.GlyphIndices) ||
            !MemoryMarshal.AsBytes(GlyphPositions.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(previous.GlyphPositions.AsSpan())))
            return;

        (blob, blobCreated, textPath, textPathCreated) = (previous.blob, previous.blobCreated, previous.textPath, previous.textPathCreated);
        (previous.blob, previous.blobCreated, previous.textPath, previous.textPathCreated) = (null, false, null, false);
    }

    public void Dispose()
    {
        blob?.Dispose();
        Path?.Dispose();
        textPath?.Dispose();
    }
}

/// <summary>One visual line, after wrapping.</summary>
internal sealed class LayoutLine
{
    public List<LayoutRun> Runs { get; } = [];

    public float Width { get; set; }

    /// <summary>Distance from the top of the line to its baseline.</summary>
    public float Ascent { get; set; }

    public float Descent { get; set; }

    /// <summary>Offset of the line's top edge inside the event block.</summary>
    public float OffsetY { get; set; }

    /// <summary>Horizontal offset inside the event block, from the alignment.</summary>
    public float OffsetX { get; set; }

    public float Height => Ascent + Descent;
}

/// <summary>
/// A laid out event: lines in script coordinates plus everything needed to anchor them.
/// </summary>
internal sealed class EventLayout : IDisposable
{
    public List<LayoutLine> Lines { get; } = [];

    public float Width { get; set; }

    public float Height { get; set; }

    public int Alignment { get; set; } = 2;

    public bool HasPosition { get; set; }

    public float PositionX { get; set; }

    public float PositionY { get; set; }

    /// <summary>Counter-clockwise rotation around Z, in degrees.</summary>
    public float Angle { get; set; }

    /// <summary>Rotation around X and Y, which together with <see cref="Angle"/> need perspective.</summary>
    public float AngleX { get; set; }

    public float AngleY { get; set; }

    public float ShearX { get; set; }

    public float ShearY { get; set; }

    public bool HasOrigin { get; set; }

    public float OriginX { get; set; }

    public float OriginY { get; set; }

    public bool HasMove { get; set; }

    public float MoveX1 { get; set; }

    public float MoveY1 { get; set; }

    public float MoveX2 { get; set; }

    public float MoveY2 { get; set; }

    public float MoveStart { get; set; }

    public float MoveEnd { get; set; }

    public SubtitleFade? Fade { get; set; }

    public SubtitleClip? Clip { get; set; }

    /// <summary>The <c>\clip</c> shape, built once from <see cref="Clip"/>.</summary>
    public SKPath? ClipPath { get; set; }

    /// <summary>Set when the event holds a <c>\t</c>, which makes this layout time dependent.</summary>
    public bool IsAnimated { get; set; }

    /// <summary>Seconds from the start of the event during which some <c>\t</c> is in progress.</summary>
    public double AnimationStart { get; set; }

    public double AnimationEnd { get; set; }

    /// <summary>Which animation step this layout was built for; see the renderer's cache.</summary>
    public int AnimationSlot { get; set; } = -1;

    public float MarginL { get; set; }

    public float MarginR { get; set; }

    public float MarginV { get; set; }

    /// <summary>0 = left, 1 = center, 2 = right.</summary>
    public int HorizontalAlignment => (Alignment - 1) % 3;

    /// <summary>0 = bottom, 1 = middle, 2 = top.</summary>
    public int VerticalAlignment => (Alignment - 1) / 3;

    /// <summary>Lets every run reuse what it can from the same run of the previous layout.</summary>
    public void AdoptResources(EventLayout previous)
    {
        int lines = Math.Min(Lines.Count, previous.Lines.Count);
        for (int i = 0; i < lines; i++)
        {
            List<LayoutRun> runs = Lines[i].Runs;
            List<LayoutRun> earlier = previous.Lines[i].Runs;
            int count = Math.Min(runs.Count, earlier.Count);
            for (int j = 0; j < count; j++)
                runs[j].AdoptFrom(earlier[j]);
        }
    }

    public void Dispose()
    {
        foreach (LayoutLine line in Lines)
        {
            foreach (LayoutRun run in line.Runs)
                run.Dispose();
        }

        Lines.Clear();
        ClipPath?.Dispose();
        ClipPath = null;
    }
}
