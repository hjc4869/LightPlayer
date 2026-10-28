using System;
using System.Collections.Generic;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Subtitles.Layout;
using SkiaSharp;

namespace LightMediaRenderer.Subtitles;

/// <summary>
/// Draws the events of an <see cref="AssDocument"/> that are visible at a given time.
/// </summary>
/// <remarks>
/// Everything is laid out once, in the script's own <c>PlayRes</c> coordinate system, and the canvas
/// carries the transform to the video rectangle. Plain text uses an aspect-aware layout width to
/// preserve font proportions. Skia rasterizes glyphs at the true device size instead of scaling a
/// pre-rendered bitmap.
///
/// Instances are not thread safe on their own but guard themselves with a lock, because a render
/// thread may still be inside <see cref="Render"/> when the owner disposes them.
/// </remarks>
public sealed class AssSubtitleRenderer : IDisposable
{
    private const int MaxCachedLayouts = 128;

    /// <summary>How often an animated event is re-laid out; 50 a second is well past visible.</summary>
    private const double AnimationStepsPerSecond = 50;

    /// <summary>
    /// VSFilter's focal length, 20000 in 64ths of a pixel, measured in the layout resolution.
    /// </summary>
    private const float PerspectiveDistance = 312.5f;

    /// <summary>Converts a <c>\blur</c> radius to a Gaussian sigma, as libass does.</summary>
    private static readonly double BlurSigmaScale = 2 / Math.Sqrt(Math.Log(256));

    private readonly object sync = new();
    private readonly EventSegmenter segmenter = new();
    private readonly SubtitleLayoutEngine engine = new();
    private readonly Dictionary<AssDialogue, EventLayout> layouts = [];
    private readonly Dictionary<int, SKMaskFilter> blurs = [];
    private readonly List<AssDialogue> active = [];
    private readonly List<(int Layer, SKRect Box)> occupied = [];
    private readonly SKPaint paint = new() { IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };

    private AssDocument? cachedDocument;
    private AssScriptInfo cachedScriptInfo = new();
    private bool drawTextAsPath;
    private float strokeStep;
    private bool eventPerspective;
    private bool snapShadows;
    private SKMatrix eventMatrix;
    private SKMatrix eventInverse;
    private bool disposed;

    /// <summary>
    /// Paints every event active at <paramref name="time"/> into <paramref name="target"/>, which is
    /// the rectangle the picture itself occupies.
    /// </summary>
    /// <param name="opacity">Multiplied into every color, for the control's own opacity.</param>
    /// <param name="storage">
    /// Native pixel size of the video. VSFilter measured perspective and blur in video pixels rather
    /// than script pixels, so a script with a small <c>PlayRes</c> over a large picture gets a
    /// stronger <c>\frx</c> and a wider <c>\blur</c>. Leave it empty to measure both in
    /// <c>PlayRes</c>, which is also what happens when the size is not known.
    /// </param>
    public void Render(
        SKCanvas canvas,
        AssDocument document,
        TimeSpan time,
        SKRect target,
        float opacity = 1f,
        SKSize storage = default,
        float colorScale = 1f) =>
        Render(canvas, document, time, target, opacity, storage, colorScale, drawTextAsPath: false);

    public void Render(
        SKCanvas canvas,
        AssDocument document,
        TimeSpan time,
        SKRect target,
        float opacity,
        SKSize storage,
        float colorScale,
        bool drawTextAsPath)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(document);

        if (target.Width <= 0 || target.Height <= 0 || opacity <= 0 ||
            !float.IsFinite(colorScale) || colorScale <= 0)
            return;

        lock (sync)
        {
            if (disposed)
                return;

            this.drawTextAsPath = drawTextAsPath;

            int playResX = document.IsPlainText
                ? Math.Max(1, (int)Math.Round(target.Width / target.Height * document.ScriptInfo.PlayResY))
                : document.ScriptInfo.PlayResX;
            if (!ReferenceEquals(document, cachedDocument) || cachedScriptInfo.PlayResX != playResX)
            {
                DropLayouts();
                cachedDocument = document;
                cachedScriptInfo = document.ScriptInfo;
                if (document.IsPlainText)
                {
                    cachedScriptInfo = cachedScriptInfo.Clone();
                    cachedScriptInfo.PlayResX = playResX;
                }
            }

            document.CollectActive(time, active);
            if (active.Count == 0)
                return;

            AssScriptInfo info = cachedScriptInfo;
            float scaleY = target.Height / info.PlayResY;
            float scaleX = document.IsPlainText ? scaleY : target.Width / info.PlayResX;

            // With ScaledBorderAndShadow off, borders keep their size in screen pixels, so they have
            // to be shrunk by whatever the canvas transform is about to multiply them by.
            float borderScale = info.ScaledBorderAndShadow ? 1f : 1f / scaleY;
            float layoutScale = LayoutScale(info, storage);

            int restore = canvas.Save();
            canvas.ClipRect(target);
            canvas.Translate(target.Left, target.Top);
            canvas.Scale(scaleX, scaleY);

            occupied.Clear();

            foreach (AssDialogue dialogue in active)
            {
                double elapsed = (time - dialogue.Start).TotalSeconds;
                EventLayout layout = GetLayout(document, info, dialogue, elapsed);
                if (layout.Lines.Count == 0)
                    continue;

                float fade = FadeOpacity(layout, elapsed);
                if (fade <= 0)
                    continue;

                Place(layout, info, dialogue.Layer, elapsed, out float anchorX, out float anchorY, out float blockX, out float blockY);

                int eventRestore = canvas.Save();
                ApplyClip(canvas, layout);
                ApplyTransform(canvas, layout, anchorX, anchorY, blockX, blockY, layoutScale);
                PrepareEvent(canvas.TotalMatrix, layout.IsAnimated, scaleY);
                DrawEvent(canvas, layout, blockX, blockY, borderScale, layoutScale, opacity * fade, colorScale, elapsed);
                canvas.RestoreToCount(eventRestore);
            }

            canvas.RestoreToCount(restore);
            TrimCache();
        }
    }

    /// <summary>
    /// Sorted font families text subtitles can be drawn in: the platform's own and any registered
    /// with <see cref="SubtitleFontFallbacks"/>. Enumerating them can be slow, so call this off the UI thread.
    /// </summary>
    public static IReadOnlyList<string> GetAvailableFontFamilies()
    {
        var families = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (string family in SKFontManager.Default.FontFamilies)
            Add(family);
        foreach (string family in SubtitleFontFallbacks.FamilyNames())
            Add(family);
        return [.. families];

        // A leading @ asks GDI for the vertical variant, which would turn every glyph on its side.
        void Add(string? family)
        {
            if (!string.IsNullOrWhiteSpace(family) && !family.StartsWith('@'))
                families.Add(family.Trim());
        }
    }

    /// <summary>How many script pixels one layout pixel is worth; 1 when the resolutions agree.</summary>
    private static float LayoutScale(AssScriptInfo info, SKSize storage)
    {
        float height = info.LayoutResY > 0 ? info.LayoutResY : storage.Height;
        return height > 0 ? info.PlayResY / height : 1f;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;
            DropLayouts();
            foreach (SKMaskFilter filter in blurs.Values)
                filter.Dispose();
            blurs.Clear();
            engine.Dispose();
            paint.Dispose();
        }
    }

    /// <summary>The whole-event opacity from <c>\fad</c> or <c>\fade</c>, 0 to 1.</summary>
    private static float FadeOpacity(EventLayout layout, double elapsed)
    {
        if (layout.Fade is not { } fade)
            return 1f;

        double alpha;
        if (elapsed < fade.Time1)
            alpha = fade.Alpha1;
        else if (elapsed < fade.Time2)
            alpha = Interpolate(fade.Alpha1, fade.Alpha2, elapsed, fade.Time1, fade.Time2);
        else if (elapsed < fade.Time3)
            alpha = fade.Alpha2;
        else if (elapsed < fade.Time4)
            alpha = Interpolate(fade.Alpha2, fade.Alpha3, elapsed, fade.Time3, fade.Time4);
        else
            alpha = fade.Alpha3;

        return (float)(Math.Clamp(alpha, 0, 255) / 255.0);
    }

    private static double Interpolate(double from, double to, double now, double start, double end)
        => end <= start ? to : from + ((to - from) * Math.Clamp((now - start) / (end - start), 0, 1));

    /// <summary>Confines the event to its <c>\clip</c> or <c>\iclip</c> region, in script coordinates.</summary>
    private static void ApplyClip(SKCanvas canvas, EventLayout layout)
    {
        if (layout.Clip is not { } clip)
            return;

        SKClipOperation operation = clip.Inverse ? SKClipOperation.Difference : SKClipOperation.Intersect;
        if (clip.IsRectangle)
        {
            var rect = new SKRect(
                (float)Math.Min(clip.Left, clip.Right),
                (float)Math.Min(clip.Top, clip.Bottom),
                (float)Math.Max(clip.Left, clip.Right),
                (float)Math.Max(clip.Top, clip.Bottom));
            canvas.ClipRect(rect, operation, antialias: true);
        }
        else if (layout.ClipPath is { } path)
        {
            canvas.ClipPath(path, operation, antialias: true);
        }
    }

    /// <summary>
    /// Applies <c>\frx</c>, <c>\fry</c>, <c>\frz</c>, <c>\fax</c> and <c>\fay</c> to the event.
    /// </summary>
    /// <remarks>
    /// The text is a flat plane, so however it is rotated in space the map from the plane to the
    /// screen is still a homography -- one 3x3 matrix with a perspective row, which Skia applies for
    /// free. Building it that way keeps glyphs vector-sharp instead of projecting a rendered bitmap.
    ///
    /// Rotation turns about <c>\org</c> or the alignment anchor, but the shear does not: VSFilter
    /// leans glyphs about the top of the text and drops them away from the start of the line, so
    /// the two halves need different centers.
    /// </remarks>
    private static void ApplyTransform(SKCanvas canvas, EventLayout layout, float anchorX, float anchorY, float blockX, float blockY, float layoutScale)
    {
        bool rotated = Math.Abs(layout.Angle) > 0.001f || Math.Abs(layout.AngleX) > 0.001f || Math.Abs(layout.AngleY) > 0.001f;
        bool sheared = layout.ShearX != 0 || layout.ShearY != 0;
        if (!rotated && !sheared)
            return;

        SKMatrix matrix = SKMatrix.CreateIdentity();

        if (rotated)
        {
            float originX = layout.HasOrigin ? layout.OriginX : anchorX;
            float originY = layout.HasOrigin ? layout.OriginY : anchorY;
            matrix = SKMatrix.Concat(SKMatrix.CreateTranslation(originX, originY), BuildRotation(layout, layoutScale));
            matrix = SKMatrix.Concat(matrix, SKMatrix.CreateTranslation(-originX, -originY));
        }

        if (sheared)
        {
            SKMatrix shear = SKMatrix.CreateIdentity();
            shear.SkewX = layout.ShearX;
            shear.SkewY = layout.ShearY;

            SKMatrix about = SKMatrix.Concat(SKMatrix.CreateTranslation(blockX, blockY), shear);
            about = SKMatrix.Concat(about, SKMatrix.CreateTranslation(-blockX, -blockY));
            matrix = SKMatrix.Concat(matrix, about);
        }

        canvas.Concat(in matrix);
    }

    private static SKMatrix BuildRotation(EventLayout layout, float layoutScale)
    {
        // ASS angles run counter-clockwise, Skia's the other way, hence the negated sines.
        double sx = -Math.Sin(layout.AngleX * Math.PI / 180), cx = Math.Cos(layout.AngleX * Math.PI / 180);
        double sy = Math.Sin(layout.AngleY * Math.PI / 180), cy = Math.Cos(layout.AngleY * Math.PI / 180);
        double sz = -Math.Sin(layout.Angle * Math.PI / 180), cz = Math.Cos(layout.Angle * Math.PI / 180);

        // Each pair is the (x, y) coefficients of one output component; Z first, then X, then Y.
        double x2X = cz, x2Y = -sz;
        double y2X = sz, y2Y = cz;

        double y3X = y2X * cx, y3Y = y2Y * cx;
        double z3X = y2X * sx, z3Y = y2Y * sx;

        double x4X = (x2X * cy) - (z3X * sy), x4Y = (x2Y * cy) - (z3Y * sy);
        double z4X = (x2X * sy) + (z3X * cy), z4Y = (x2Y * sy) + (z3Y * cy);

        float distance = PerspectiveDistance * layoutScale;

        return new SKMatrix
        {
            ScaleX = (float)x4X,
            SkewX = (float)x4Y,
            TransX = 0,
            SkewY = (float)y3X,
            ScaleY = (float)y3Y,
            TransY = 0,
            Persp0 = (float)(z4X / distance),
            Persp1 = (float)(z4Y / distance),
            Persp2 = 1f,
        };
    }

    /// <summary>
    /// Resolves the anchor point the alignment refers to and the top-left corner of the text block.
    /// </summary>
    private void Place(
        EventLayout layout,
        AssScriptInfo info,
        int layer,
        double elapsed,
        out float anchorX,
        out float anchorY,
        out float blockX,
        out float blockY)
    {
        int horizontal = layout.HorizontalAlignment;
        int vertical = layout.VerticalAlignment;

        if (layout.HasMove)
        {
            anchorX = (float)Interpolate(layout.MoveX1, layout.MoveX2, elapsed, layout.MoveStart, layout.MoveEnd);
            anchorY = (float)Interpolate(layout.MoveY1, layout.MoveY2, elapsed, layout.MoveStart, layout.MoveEnd);
        }
        else if (layout.HasPosition)
        {
            anchorX = layout.PositionX;
            anchorY = layout.PositionY;
        }
        else
        {
            anchorX = horizontal switch
            {
                0 => layout.MarginL,
                1 => layout.MarginL + ((info.PlayResX - layout.MarginL - layout.MarginR) / 2f),
                _ => info.PlayResX - layout.MarginR,
            };

            anchorY = vertical switch
            {
                0 => info.PlayResY - layout.MarginV,
                1 => info.PlayResY / 2f,
                _ => layout.MarginV,
            };
        }

        blockX = horizontal switch
        {
            0 => anchorX,
            1 => anchorX - (layout.Width / 2f),
            _ => anchorX - layout.Width,
        };

        blockY = vertical switch
        {
            0 => anchorY - layout.Height,
            1 => anchorY - (layout.Height / 2f),
            _ => anchorY,
        };

        if (layout.HasPosition)
            return;

        float shift = Deconflict(layout, layer, blockX, blockY, vertical);
        anchorY += shift;
        blockY += shift;
    }

    /// <summary>
    /// Moves an event clear of the ones already placed, away from the edge it is anchored to.
    /// </summary>
    /// <remarks>
    /// Two rules from VSFilter matter here. Events only collide with others on the same layer, which
    /// is how scripts put a translation and its original on screen together -- they differ in layer,
    /// so neither is nudged even where their boxes overlap. And an event is only moved when the boxes
    /// really do intersect, so the far more common case of two lines separated by their own
    /// <c>MarginV</c> is left exactly where the script put it.
    /// </remarks>
    private float Deconflict(EventLayout layout, int layer, float blockX, float blockY, int vertical)
    {
        var box = new SKRect(blockX, blockY, blockX + layout.Width, blockY + layout.Height);

        float shift = 0;
        if (vertical != 1)
        {
            // Each pass clears the worst conflict; a later obstacle can create a new one.
            for (int pass = 0; pass < occupied.Count; pass++)
            {
                float step = shift;
                foreach ((int otherLayer, SKRect other) in occupied)
                {
                    if (otherLayer != layer || !Intersects(box, other, step))
                        continue;

                    step += vertical == 0 ? other.Top - (box.Bottom + step) : other.Bottom - (box.Top + step);
                }

                if (step == shift)
                    break;
                shift = step;
            }
        }

        box.Offset(0, shift);
        occupied.Add((layer, box));
        return shift;
    }

    private static bool Intersects(SKRect rect, SKRect other, float shift)
        => rect.Left < other.Right && other.Left < rect.Right &&
           rect.Top + shift < other.Bottom && other.Top < rect.Bottom + shift;

    /// <summary>
    /// Decides how far an event's outlines, blur and shadow may be nudged so that consecutive
    /// frames, and the shadow and outline passes, can share glyph images.
    /// </summary>
    /// <remarks>
    /// Skia rasterizes a glyph anew for every outline width, blur radius and subpixel position. A
    /// static event pays that once, so only animated ones are nudged: outline width and blur radius
    /// move in steps of at least an eighth of a device pixel, and the shadow moves by whole device
    /// pixels so its glyphs land on the same subpixel phase as the outline's.
    /// </remarks>
    private void PrepareEvent(SKMatrix matrix, bool animated, float scaleY)
    {
        eventMatrix = matrix;
        eventPerspective = matrix.Persp0 != 0 || matrix.Persp1 != 0;
        strokeStep = animated ? 1f / (8 * scaleY) : 0;
        snapShadows = animated && !eventPerspective && matrix.TryInvert(out eventInverse);
    }

    private void SnapToPixels(ref float x, ref float y)
    {
        SKPoint device = eventMatrix.MapVector(x, y);
        float snappedX = MathF.Round(device.X, MidpointRounding.AwayFromZero);
        float snappedY = MathF.Round(device.Y, MidpointRounding.AwayFromZero);

        // A shadow shorter than half a pixel would vanish behind the outline, so it stays exact.
        if (snappedX == 0 && snappedY == 0)
            return;

        SKPoint snapped = eventInverse.MapVector(snappedX, snappedY);
        x = snapped.X;
        y = snapped.Y;
    }

    /// <summary>Shadow, then outline, then fill -- the order ASS renderers agree on.</summary>
    private void DrawEvent(SKCanvas canvas, EventLayout layout, float blockX, float blockY, float borderScale, float layoutScale, float opacity, float colorScale, double elapsed)
    {
        foreach (LayoutLine line in layout.Lines)
            DrawBox(canvas, line, blockX, blockY, borderScale, layoutScale, opacity, colorScale);

        foreach (LayoutLine line in layout.Lines)
            DrawLine(canvas, line, blockX, blockY, borderScale, layoutScale, opacity, colorScale, elapsed, Pass.Shadow);

        foreach (LayoutLine line in layout.Lines)
            DrawLine(canvas, line, blockX, blockY, borderScale, layoutScale, opacity, colorScale, elapsed, Pass.Outline);

        foreach (LayoutLine line in layout.Lines)
            DrawLine(canvas, line, blockX, blockY, borderScale, layoutScale, opacity, colorScale, elapsed, Pass.Fill);
    }

    /// <summary>
    /// Border style 3 paints a slab behind the whole line, drawn in one piece: per-run rectangles
    /// would overlap and a translucent box would come out blotchy.
    /// </summary>
    private void DrawBox(SKCanvas canvas, LayoutLine line, float blockX, float blockY, float borderScale, float layoutScale, float opacity, float colorScale)
    {
        if (line.Runs.Count == 0 || line.Runs[0].Style.BorderStyle != 3)
            return;

        SpanStyle style = line.Runs[0].Style;
        SKMaskFilter? blur = GetBlur(style, layoutScale);
        float border = 0;
        foreach (LayoutRun run in line.Runs)
            border = Math.Max(border, (float)((run.Style.BorderX + run.Style.BorderY) / 2) * borderScale);

        float left = blockX + line.OffsetX;
        float baseline = blockY + line.OffsetY + line.Ascent;
        var box = new SKRect(
            left - border,
            baseline - line.Ascent - border,
            left + line.Width + border,
            baseline + line.Descent + border);

        float shadowX = (float)style.ShadowX * borderScale;
        float shadowY = (float)style.ShadowY * borderScale;
        if ((shadowX != 0 || shadowY != 0) && TryPrepare(style.ShadowColour, opacity, colorScale, blur))
        {
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawRect(
                new SKRect(box.Left + shadowX, box.Top + shadowY, box.Right + shadowX, box.Bottom + shadowY),
                paint);
        }

        if (!TryPrepare(style.OutlineColour, opacity, colorScale, blur))
            return;

        paint.Style = SKPaintStyle.Fill;
        canvas.DrawRect(box, paint);
    }

    private void DrawLine(SKCanvas canvas, LayoutLine line, float blockX, float blockY, float borderScale, float layoutScale, float opacity, float colorScale, double elapsed, Pass pass)
    {
        float baseline = blockY + line.OffsetY + line.Ascent;

        foreach (LayoutRun run in line.Runs)
        {
            SpanStyle style = run.Style;
            float x = blockX + line.OffsetX + run.OffsetX;
            float border = (float)((style.BorderX + style.BorderY) / 2) * borderScale;
            if (strokeStep > 0 && border > 0)
                border = MathF.Max(strokeStep, MathF.Round(border / strokeStep) * strokeStep);
            float shadowX = (float)style.ShadowX * borderScale;
            float shadowY = (float)style.ShadowY * borderScale;
            if (snapShadows)
                SnapToPixels(ref shadowX, ref shadowY);
            bool box = style.BorderStyle == 3;
            SKMaskFilter? blur = GetBlur(style, layoutScale);

            // A border shields the fill from the blur, which is what makes \blur a glow rather than
            // a smear; without one there is nothing to hide behind and the fill is blurred too.
            SKMaskFilter? fillBlur = border > 0 && !box ? null : blur;

            switch (pass)
            {
                case Pass.Shadow:
                    if (!box && (shadowX != 0 || shadowY != 0) && TryPrepare(style.ShadowColour, opacity, colorScale, blur))
                    {
                        // The drop shadow is a copy of the glyph including its border.
                        paint.Style = border > 0 ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Fill;
                        paint.StrokeWidth = border * 2;
                        DrawRun(canvas, run, x + shadowX, baseline + shadowY);
                        DrawDecorations(canvas, run, x + shadowX, baseline + shadowY, border);
                    }

                    break;

                case Pass.Outline:
                    // \ko hides the border until the syllable has been sung.
                    if (style.Karaoke == KaraokeMode.Outline && elapsed < style.KaraokeStart)
                        break;

                    if (!box && border > 0 && TryPrepare(style.OutlineColour, opacity, colorScale, blur))
                    {
                        // Skia centers the stroke on the contour, so half of it is hidden by the fill.
                        // Under an opaque fill the glyph's inside never shows, so the outline can be
                        // the same filled-and-stroked shape as the shadow and share its glyph images;
                        // perspective keeps the plain stroke, which Skia can still draw from distance fields.
                        paint.Style = !eventPerspective && HasOpaqueFill(style, opacity)
                            ? SKPaintStyle.StrokeAndFill
                            : SKPaintStyle.Stroke;
                        paint.StrokeWidth = border * 2;
                        DrawRun(canvas, run, x, baseline);
                        DrawDecorations(canvas, run, x, baseline, border);
                    }

                    break;

                case Pass.Fill:
                    DrawFill(canvas, run, x, baseline, opacity, colorScale, elapsed, fillBlur);
                    break;
            }
        }
    }

    /// <summary>The fill pass, which is where the karaoke tags do their work.</summary>
    private void DrawFill(SKCanvas canvas, LayoutRun run, float x, float baseline, float opacity, float colorScale, double elapsed, SKMaskFilter? blur)
    {
        SpanStyle style = run.Style;

        if (style.Karaoke == KaraokeMode.Sweep)
        {
            double sung = Interpolate(0, 1, elapsed, style.KaraokeStart, style.KaraokeEnd);

            // The syllable may have been cut across several runs, so the split is measured from its
            // own start rather than from this run's.
            float split = x - run.KaraokeOffset + ((float)sung * run.KaraokeWidth);
            DrawHalf(canvas, run, x, baseline, opacity, colorScale, float.NegativeInfinity, split, style.PrimaryColour, blur);
            DrawHalf(canvas, run, x, baseline, opacity, colorScale, split, float.PositiveInfinity, style.SecondaryColour, blur);
            return;
        }

        AssColor color = style.Karaoke != KaraokeMode.None && elapsed < style.KaraokeStart
            ? style.SecondaryColour
            : style.PrimaryColour;

        if (!TryPrepare(color, opacity, colorScale, blur))
            return;

        paint.Style = SKPaintStyle.Fill;
        DrawRun(canvas, run, x, baseline);
        DrawDecorations(canvas, run, x, baseline, 0);
    }

    private void DrawHalf(SKCanvas canvas, LayoutRun run, float x, float baseline, float opacity, float colorScale, float left, float right, AssColor color, SKMaskFilter? blur)
    {
        if (right <= left || !TryPrepare(color, opacity, colorScale, blur))
            return;

        int restore = canvas.Save();
        canvas.ClipRect(new SKRect(
            float.IsInfinity(left) ? x - run.KaraokeOffset - 1 : left,
            baseline - (float)run.Style.FontSize * 4,
            float.IsInfinity(right) ? x + run.Width + 1 : right,
            baseline + ((float)run.Style.FontSize * 4)));

        paint.Style = SKPaintStyle.Fill;
        DrawRun(canvas, run, x, baseline);
        DrawDecorations(canvas, run, x, baseline, 0);
        canvas.RestoreToCount(restore);
    }

    private void DrawRun(SKCanvas canvas, LayoutRun run, float x, float baseline)
    {
        SKPath? path = run.Path;
        if (path is null && drawTextAsPath)
            path = run.GetTextPath();

        if (path is not null)
        {
            int restore = canvas.Save();
            canvas.Translate(x, baseline);
            canvas.DrawPath(path, paint);
            canvas.RestoreToCount(restore);
        }
        else if (run.GetBlob() is { } blob)
        {
            canvas.DrawText(blob, x, baseline, paint);
        }
    }

    /// <summary>Underline and strike-out, drawn with whatever color the current pass set up.</summary>
    private void DrawDecorations(SKCanvas canvas, LayoutRun run, float x, float baseline, float inflate)
    {
        SpanStyle style = run.Style;
        if (run.Font is null || (!style.Underline && !style.StrikeOut))
            return;

        SKFontMetrics metrics = run.Font.Metrics;
        float size = run.Font.Size;
        SKPaintStyle previous = paint.Style;
        paint.Style = SKPaintStyle.Fill;

        if (style.Underline)
        {
            float position = metrics.UnderlinePosition ?? (metrics.Descent * 0.5f);
            float thickness = metrics.UnderlineThickness ?? (size / 18f);
            canvas.DrawRect(Decoration(x, baseline + position, run.Width, thickness, inflate), paint);
        }

        if (style.StrikeOut)
        {
            float position = metrics.StrikeoutPosition ?? (metrics.Ascent * 0.35f);
            float thickness = metrics.StrikeoutThickness ?? (size / 18f);
            canvas.DrawRect(Decoration(x, baseline + position, run.Width, thickness, inflate), paint);
        }

        paint.Style = previous;
    }

    private static SKRect Decoration(float x, float top, float width, float thickness, float inflate)
    {
        var rect = new SKRect(x, top, x + width, top + Math.Max(1f, thickness));
        rect.Inflate(inflate, inflate);
        return rect;
    }

    /// <summary>Whether the fill pass hides the glyph's inside completely, in any karaoke state.</summary>
    private static bool HasOpaqueFill(SpanStyle style, float opacity)
        => opacity >= 1f && style.PrimaryColour.Alpha == 255 &&
           (style.Karaoke == KaraokeMode.None || style.SecondaryColour.Alpha == 255);

    private bool TryPrepare(AssColor color, float opacity, float colorScale, SKMaskFilter? blur)
    {
        byte alpha = (byte)Math.Clamp(color.Alpha * opacity, 0, 255);
        if (alpha == 0)
            return false;

        if (colorScale == 1f)
        {
            paint.Color = new SKColor(color.Red, color.Green, color.Blue, alpha);
        }
        else
        {
            paint.ColorF = new SKColorF(
                ScaleSrgb(color.Red, colorScale),
                ScaleSrgb(color.Green, colorScale),
                ScaleSrgb(color.Blue, colorScale),
                alpha / 255f);
        }

        paint.MaskFilter = blur;
        return true;
    }

    internal static float ScaleSrgb(byte component, float scale)
    {
        double encoded = component / 255.0;
        double linear = encoded <= 0.04045
            ? encoded / 12.92
            : Math.Pow((encoded + 0.055) / 1.055, 2.4);
        linear *= scale;
        return (float)(linear <= 0.0031308
            ? linear * 12.92
            : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055);
    }

    /// <summary>
    /// The mask filter for <c>\blur</c> and <c>\be</c> combined, or <c>null</c> when neither is set.
    /// </summary>
    /// <remarks>
    /// <c>\be</c> is <em>n</em> passes of a [1,2,1] kernel, whose variance is a half each time, so
    /// the two effects compose by adding variances rather than radii.
    /// </remarks>
    private SKMaskFilter? GetBlur(SpanStyle style, float layoutScale)
    {
        if (style.Blur <= 0 && style.BlurEdges <= 0)
            return null;

        double gaussian = style.Blur * BlurSigmaScale * layoutScale;
        double sigma = Math.Sqrt((style.BlurEdges / 2.0) + (gaussian * gaussian));
        if (sigma < 0.05)
            return null;

        if (strokeStep > 0)
        {
            // The step grows with the radius, the same form libass uses: 3% of sigma plus the pixel fraction.
            const double Relative = 0.03;
            double index = Math.Round(Math.Log(1 + (sigma * Relative / strokeStep)) / Relative);
            sigma = Math.Max(0.05, (Math.Exp(Relative * index) - 1) * strokeStep / Relative);
        }

        int key = (int)Math.Round(sigma * 16);
        if (blurs.TryGetValue(key, out SKMaskFilter? cached))
            return cached;

        var filter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, key / 16f);
        blurs[key] = filter;
        return filter;
    }

    /// <summary>
    /// Returns the layout for an event, rebuilding it as an animation advances.
    /// </summary>
    /// <remarks>
    /// Only events carrying a <c>\t</c> are time dependent, and they are re-segmented in steps rather
    /// than every frame, and only while a transition is in progress: <c>\t</c> can animate the font
    /// size, so there is no way to keep the layout and repaint alone, and stepping bounds the cost of
    /// a script that animates everything at once.
    /// </remarks>
    private EventLayout GetLayout(AssDocument document, AssScriptInfo info, AssDialogue dialogue, double elapsed)
    {
        if (layouts.TryGetValue(dialogue, out EventLayout? cached))
        {
            if (!cached.IsAnimated || cached.AnimationSlot == AnimationSlot(cached, elapsed))
                return cached;
        }

        EventLayout layout = engine.Layout(segmenter.Segment(document, dialogue, elapsed), info);
        layout.AnimationSlot = AnimationSlot(layout, elapsed);
        if (cached is not null)
        {
            layout.AdoptResources(cached);
            cached.Dispose();
        }

        layouts[dialogue] = layout;
        return layout;
    }

    /// <summary>Before the first transition starts and after the last one ends, nothing moves.</summary>
    private static int AnimationSlot(EventLayout layout, double elapsed)
    {
        if (elapsed < layout.AnimationStart)
            return int.MinValue;
        if (elapsed >= layout.AnimationEnd)
            return int.MaxValue;
        return (int)Math.Floor(elapsed * AnimationStepsPerSecond);
    }

    private void TrimCache()
    {
        if (layouts.Count <= MaxCachedLayouts)
            return;

        foreach (KeyValuePair<AssDialogue, EventLayout> entry in layouts)
        {
            if (active.Contains(entry.Key))
                continue;
            entry.Value.Dispose();
        }

        var survivors = new List<KeyValuePair<AssDialogue, EventLayout>>(active.Count);
        foreach (AssDialogue dialogue in active)
        {
            if (layouts.TryGetValue(dialogue, out EventLayout? layout))
                survivors.Add(new KeyValuePair<AssDialogue, EventLayout>(dialogue, layout));
        }

        layouts.Clear();
        foreach (KeyValuePair<AssDialogue, EventLayout> entry in survivors)
            layouts[entry.Key] = entry.Value;
    }

    private void DropLayouts()
    {
        foreach (EventLayout layout in layouts.Values)
            layout.Dispose();
        layouts.Clear();
    }

    private enum Pass
    {
        Shadow,
        Outline,
        Fill,
    }
}
