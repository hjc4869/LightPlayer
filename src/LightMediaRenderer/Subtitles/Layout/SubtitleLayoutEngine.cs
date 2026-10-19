using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Subtitles.Text;
using LightMediaRenderer.Subtitles;
using SkiaSharp;

namespace LightMediaRenderer.Subtitles.Layout;

/// <summary>
/// Shapes the spans of an event and arranges them into lines.
/// </summary>
/// <remarks>
/// Text is cut into wrap candidates before shaping -- whitespace runs, words, and single CJK
/// characters -- and each candidate is shaped on its own. Losing cross-word kerning is invisible in
/// practice and it buys a greedy line breaker that never has to re-shape, plus a shaping cache whose
/// keys are words rather than whole sentences.
/// </remarks>
internal sealed class SubtitleLayoutEngine : IDisposable
{
    private readonly SubtitleFontCache fonts = new();
    private readonly TextShaper shaper = new();
    private readonly List<Piece> pieces = [];

    public EventLayout Layout(SegmentedEvent segmented, AssScriptInfo info)
    {
        var layout = new EventLayout
        {
            Alignment = segmented.Alignment,
            HasPosition = segmented.HasPosition,
            PositionX = (float)segmented.PositionX,
            PositionY = (float)segmented.PositionY,
            Angle = (float)segmented.Angle,
            AngleX = (float)segmented.AngleX,
            AngleY = (float)segmented.AngleY,
            ShearX = (float)segmented.ShearX,
            ShearY = (float)segmented.ShearY,
            HasOrigin = segmented.HasOrigin,
            OriginX = (float)segmented.OriginX,
            OriginY = (float)segmented.OriginY,
            HasMove = segmented.HasMove,
            MoveX1 = (float)segmented.MoveX1,
            MoveY1 = (float)segmented.MoveY1,
            MoveX2 = (float)segmented.MoveX2,
            MoveY2 = (float)segmented.MoveY2,
            MoveStart = (float)segmented.MoveStart,
            MoveEnd = (float)segmented.MoveEnd,
            Fade = segmented.Fade,
            Clip = segmented.Clip,
            ClipPath = BuildClipPath(segmented.Clip),
            IsAnimated = segmented.IsAnimated,
            MarginL = segmented.MarginL,
            MarginR = segmented.MarginR,
            MarginV = segmented.MarginV,
        };

        BuildPieces(segmented);

        float maxWidth = Math.Max(1f, info.PlayResX - segmented.MarginL - segmented.MarginR);
        bool wrap = segmented.WrapStyle != 2;
        Pack(layout, maxWidth, wrap);
        Arrange(layout);
        MeasureKaraoke(layout);
        return layout;
    }

    /// <summary>Builds the <c>\clip</c> shape in script coordinates, or <c>null</c> for none.</summary>
    private static SKPath? BuildClipPath(SubtitleClip? clip)
    {
        if (clip is null || clip.IsRectangle || clip.Drawing is null)
            return null;

        SKPath? path = DrawingParser.Parse(clip.Drawing);
        if (path is null)
            return null;

        float unit = 1f / (1 << (Math.Max(1, clip.DrawingScale) - 1));
        if (unit != 1f)
            path.Transform(SKMatrix.CreateScale(unit, unit));
        return path;
    }

    public void Dispose()
    {
        shaper.Dispose();
        fonts.Dispose();
    }

    /// <summary>Turns spans into shaped wrap candidates.</summary>
    private void BuildPieces(SegmentedEvent segmented)
    {
        pieces.Clear();
        SpanStyle? lastStyle = null;

        foreach (SubtitleSpan span in segmented.Spans)
        {
            if (span.IsLineBreak)
            {
                // \n only breaks where wrapping is switched off; elsewhere it behaves as a space.
                if (span.IsHardBreak || segmented.WrapStyle == 2)
                    pieces.Add(Piece.Break());
                else if (lastStyle is not null)
                    pieces.Add(Measure(lastStyle, " "));
                continue;
            }

            SpanStyle style = span.Style!;
            lastStyle = style;

            if (span.IsDrawing)
            {
                pieces.Add(MeasureDrawing(style, span.Text));
                continue;
            }

            foreach (Range range in EnumerateTokens(span.Text))
            {
                string token = span.Text[range];
                if (token.Length == 0)
                    continue;

                pieces.Add(Measure(style, token));
            }
        }
    }

    /// <summary>
    /// Turns a <c>\p</c> block into a piece whose path already sits on the baseline.
    /// </summary>
    /// <remarks>
    /// Drawings are sized by the <c>\p</c> scale and <c>\fscx</c>/<c>\fscy</c> alone -- the font size
    /// has no say. VSFilter hangs the bottom of the shape's bounding box off the baseline, and
    /// <c>\pbo</c> then pushes it back down; libass reproduces that, so this does too.
    /// </remarks>
    private static Piece MeasureDrawing(SpanStyle style, string commands)
    {
        SKPath? path = DrawingParser.Parse(commands);
        if (path is null)
            return Piece.Empty(style);

        SKRect bounds = path.Bounds;
        float unit = 1f / (1 << (Math.Max(1, style.DrawingScale) - 1));
        float scaleX = unit * (float)style.ScaleX;
        float scaleY = unit * (float)style.ScaleY;
        float shift = bounds.Height - (float)style.DrawingBaselineOffset;

        var transform = SKMatrix.CreateScale(scaleX, scaleY);
        transform = SKMatrix.Concat(transform, SKMatrix.CreateTranslation(0, -shift));
        path.Transform(transform);

        return new Piece(
            style,
            commands,
            IsSpace: false,
            IsBreak: false,
            Font: null,
            Path: path,
            Glyphs: [],
            Width: bounds.Width * scaleX,
            Ascent: Math.Max(0, bounds.Height - (float)style.DrawingBaselineOffset) * scaleY,
            Descent: Math.Max(0, (float)style.DrawingBaselineOffset) * scaleY,
            EmSize: 0);
    }

    private Piece Measure(SpanStyle style, string token)
    {
        bool isSpace = IsBreakingSpace(token[0]);
        int sample = char.IsHighSurrogate(token[0]) && token.Length > 1 && char.IsLowSurrogate(token[1])
            ? char.ConvertToUtf32(token[0], token[1])
            : token[0];

        SubtitleFont? font = fonts.Resolve(style.FontName, style.Bold, style.Italic, style.Script, sample);
        if (font is null)
            return Piece.Empty(style);

        bool vertical = SubtitleFontCache.IsVertical(style.FontName);
        ShapedGlyph[] glyphs = shaper.Shape(font, token, vertical);

        // ASS font sizes measure the OS/2 window height, not the em square.
        double emSize = style.FontSize / font.DesignHeight;
        SKFont sized = fonts.GetSizedFont(font, emSize, style.ScaleX, style.ScaleY, style.Italic);

        float advanceScale = (float)(emSize * style.ScaleX);
        float spacing = (float)(style.Spacing * style.ScaleX);
        float width = 0;
        foreach (ShapedGlyph glyph in glyphs)
            width += (glyph.Advance * advanceScale) + spacing;

        // The line box is exactly the font size tall, split by the font's window metrics.
        float ascent = (float)(style.FontSize * font.AscentRatio * style.ScaleY);
        float descent = (float)(style.FontSize * (1 - font.AscentRatio) * style.ScaleY);

        // Turned glyphs cannot go in a text blob, which positions but never rotates.
        SKPath? path = !isSpace && HasRotated(glyphs)
            ? BuildTurnedPath(fonts.GetSizedFont(font, emSize, 1, 1, style.Italic), font, glyphs, style, emSize)
            : null;

        return new Piece(style, token, isSpace, false, sized, path, glyphs, width, ascent, descent, emSize);
    }

    private static bool HasRotated(ShapedGlyph[] glyphs)
    {
        foreach (ShapedGlyph glyph in glyphs)
        {
            if (glyph.Rotated)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Lays a run out horizontally, but with every <c>@</c> font glyph turned a quarter turn.
    /// </summary>
    /// <remarks>
    /// This is all an <c>@</c> font does: the line itself still runs left to right, and it is the
    /// <c>\frz</c> the script pairs with the font that stands the line up. Turning the glyphs the
    /// other way is what leaves them upright once that rotation is applied -- without it the whole
    /// line, characters and all, ends up lying on its side.
    ///
    /// The glyphs are turned at their natural size and the <c>\fscx</c>/<c>\fscy</c> scale is applied
    /// to the finished run, so the scale acts on the turned shape rather than the upright one, which
    /// is the order VSFilter used.
    /// </remarks>
    private static SKPath BuildTurnedPath(SKFont plain, SubtitleFont font, ShapedGlyph[] glyphs, SpanStyle style, double emSize)
    {
        using var builder = new SKPathBuilder();
        float scale = (float)emSize;
        float spacing = (float)style.Spacing;
        float descender = (float)(font.DescenderRatio * emSize);
        float pen = 0;

        foreach (ShapedGlyph glyph in glyphs)
        {
            using SKPath? outline = plain.GetGlyphPath(glyph.Glyph);
            if (outline is not null && !outline.IsEmpty)
            {
                SKMatrix place = SKMatrix.CreateTranslation(pen + (glyph.OffsetX * scale), glyph.OffsetY * scale);
                if (glyph.Rotated)
                {
                    // Anticlockwise on screen, so the glyph's top ends up at the near end of the
                    // advance and a \frz270 brings it back upright.
                    float shift = (glyph.Advance * scale) + descender;
                    place = SKMatrix.Concat(place, new SKMatrix(0, 1, shift, -1, 0, -descender, 0, 0, 1));
                }

                outline.Transform(place);
                builder.AddPath(outline);
            }

            pen += (glyph.Advance * scale) + spacing;
        }

        SKPath result = builder.Detach();
        result.Transform(SKMatrix.CreateScale((float)style.ScaleX, (float)style.ScaleY));
        return result;
    }

    /// <summary>Greedy line breaking over the measured pieces.</summary>
    private void Pack(EventLayout layout, float maxWidth, bool wrap)
    {
        var current = new LayoutLine();
        var pending = new List<Piece>();
        float pendingWidth = 0;
        float lineWidth = 0;
        float defaultAscent = 0;
        float defaultDescent = 0;

        // Empty lines still take up space, so they borrow the metrics of the event's first font.
        foreach (Piece piece in pieces)
        {
            if (piece.IsBreak || piece.Font is null)
                continue;
            defaultAscent = piece.Ascent;
            defaultDescent = piece.Descent;
            break;
        }

        void Commit()
        {
            if (current.Runs.Count == 0)
            {
                current.Ascent = defaultAscent;
                current.Descent = defaultDescent;
            }

            current.Width = lineWidth;
            layout.Lines.Add(current);
            current = new LayoutLine();
            pending.Clear();
            pendingWidth = 0;
            lineWidth = 0;
        }

        void Append(Piece piece)
        {
            if (piece.Path is not null || (piece.Font is not null && piece.Glyphs.Length > 0))
            {
                LayoutRun? run = BuildRun(piece, lineWidth);
                if (run is not null)
                    current.Runs.Add(run);
            }

            current.Ascent = Math.Max(current.Ascent, piece.Ascent);
            current.Descent = Math.Max(current.Descent, piece.Descent);
            lineWidth += piece.Width;
        }

        foreach (Piece piece in pieces)
        {
            if (piece.IsBreak)
            {
                // Trailing spaces before an explicit break never widen the line.
                Commit();
                continue;
            }

            if (piece.IsSpace)
            {
                pending.Add(piece);
                pendingWidth += piece.Width;
                continue;
            }

            if (wrap && current.Runs.Count > 0 && lineWidth + pendingWidth + piece.Width > maxWidth)
            {
                Commit();
                Append(piece);
                continue;
            }

            foreach (Piece space in pending)
                Append(space);
            pending.Clear();
            pendingWidth = 0;
            Append(piece);
        }

        Commit();
    }

    private static LayoutRun? BuildRun(Piece piece, float offsetX)
    {
        if (piece.Path is not null)
        {
            return new LayoutRun
            {
                Style = piece.Style,
                Path = piece.Path,
                Font = piece.Font,
                OffsetX = offsetX,
                Width = piece.Width,
            };
        }

        var positions = new SKPoint[piece.Glyphs.Length];
        var ids = new ushort[piece.Glyphs.Length];
        float advanceScale = (float)(piece.EmSize * piece.Style.ScaleX);
        float verticalScale = (float)(piece.EmSize * piece.Style.ScaleY);
        float spacing = (float)(piece.Style.Spacing * piece.Style.ScaleX);
        float pen = 0;

        for (int i = 0; i < piece.Glyphs.Length; i++)
        {
            ShapedGlyph glyph = piece.Glyphs[i];
            ids[i] = glyph.Glyph;
            positions[i] = new SKPoint(pen + (glyph.OffsetX * advanceScale), glyph.OffsetY * verticalScale);
            pen += (glyph.Advance * advanceScale) + spacing;
        }

        SKTextBlob? blob = SKTextBlob.CreatePositioned(
            MemoryMarshal.AsBytes(ids.AsSpan()),
            SKTextEncoding.GlyphId,
            piece.Font!,
            positions);

        if (blob is null)
            return null;

        return new LayoutRun
        {
            Style = piece.Style,
            Blob = blob,
            Font = piece.Font!,
            GlyphIndices = ids,
            GlyphPositions = positions,
            OffsetX = offsetX,
            Width = piece.Width,
        };
    }

    /// <summary>Stacks the lines and applies the horizontal alignment inside the block.</summary>
    private static void Arrange(EventLayout layout)
    {
        float width = 0;
        float height = 0;
        foreach (LayoutLine line in layout.Lines)
        {
            line.OffsetY = height;
            height += line.Height;
            width = Math.Max(width, line.Width);
        }

        layout.Width = width;
        layout.Height = height;

        foreach (LayoutLine line in layout.Lines)
        {
            line.OffsetX = layout.HorizontalAlignment switch
            {
                1 => (width - line.Width) / 2f,
                2 => width - line.Width,
                _ => 0f,
            };
        }
    }

    /// <summary>
    /// Records where each run sits inside its karaoke syllable.
    /// </summary>
    /// <remarks>
    /// A syllable is cut into several runs by word wrapping, but <c>\kf</c> has to sweep across the
    /// whole of it, so every run needs the syllable's total width and its own offset into it. Runs of
    /// one syllable share a <see cref="SpanStyle"/> instance, which makes them trivial to group.
    /// </remarks>
    private static void MeasureKaraoke(EventLayout layout)
    {
        SpanStyle? syllable = null;
        float width = 0;
        var runs = new List<LayoutRun>();

        void Close()
        {
            foreach (LayoutRun run in runs)
                run.KaraokeWidth = width;
            runs.Clear();
            width = 0;
        }

        foreach (LayoutLine line in layout.Lines)
        {
            foreach (LayoutRun run in line.Runs)
            {
                if (run.Style.Karaoke == KaraokeMode.None)
                    continue;

                if (!ReferenceEquals(run.Style, syllable))
                {
                    Close();
                    syllable = run.Style;
                }

                run.KaraokeOffset = width;
                runs.Add(run);
                width += run.Width;
            }
        }

        Close();
    }

    /// <summary>
    /// Splits text into wrap candidates: whitespace runs, single CJK characters, and words.
    /// </summary>
    private static IEnumerable<Range> EnumerateTokens(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            if (IsBreakingSpace(text[i]))
            {
                while (i < text.Length && IsBreakingSpace(text[i]))
                    i++;
            }
            else if (IsIdeographic(text[i]))
            {
                i++;
            }
            else
            {
                while (i < text.Length && !IsBreakingSpace(text[i]) && !IsIdeographic(text[i]))
                    i++;
            }

            yield return new Range(start, i);
        }
    }

    private static bool IsBreakingSpace(char c) => char.IsWhiteSpace(c) && c != '\u00A0';

    /// <summary>CJK text has no spaces, so every ideograph is its own break opportunity.</summary>
    private static bool IsIdeographic(char c)
        => c is (>= '\u2E80' and <= '\u9FFF')
            or (>= '\uAC00' and <= '\uD7AF')
            or (>= '\uF900' and <= '\uFAFF')
            or (>= '\uFF00' and <= '\uFF60')
            or (>= '\uFFE0' and <= '\uFFE6');

    private readonly record struct Piece(
        SpanStyle Style,
        string Text,
        bool IsSpace,
        bool IsBreak,
        SKFont? Font,
        SKPath? Path,
        ShapedGlyph[] Glyphs,
        float Width,
        float Ascent,
        float Descent,
        double EmSize)
    {
        public static Piece Break() => new(null!, string.Empty, false, true, null, null, [], 0, 0, 0, 0);

        public static Piece Empty(SpanStyle style) => new(style, string.Empty, false, false, null, null, [], 0, 0, 0, 0);
    }
}
