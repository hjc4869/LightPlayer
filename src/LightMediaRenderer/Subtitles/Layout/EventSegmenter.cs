using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Subtitles.Overrides;
using LightMediaRenderer.Subtitles.Text;
using LightMediaRenderer.Subtitles;

namespace LightMediaRenderer.Subtitles.Layout;

/// <summary>
/// Walks the token stream of one event and turns it into styled spans.
/// </summary>
/// <remarks>
/// The state machine keeps one mutable set of values; each tag mutates only what it names and
/// everything else inherits. A <see cref="SpanStyle"/> snapshot is taken lazily, so consecutive tags
/// collapse into a single span and a span object is shared by every run it covers -- which in turn
/// makes reference equality a valid font-cache key downstream.
///
/// Segmentation takes the time since the event appeared, because <c>\t</c> is resolved here: an
/// animated tag is applied with a weight, the way VSFilter and libass do it, so nothing further down
/// the pipeline ever has to know that a value came from an animation.
/// </remarks>
internal sealed class EventSegmenter
{
    /// <summary>Nesting <c>\t</c> is pointless but legal; this only stops a pathological file.</summary>
    private const int MaxTransitionDepth = 4;

    private readonly List<OverrideToken> tokens = [];
    private readonly StringBuilder drawing = new();

    /// <param name="elapsed">Seconds since the event became visible.</param>
    public SegmentedEvent Segment(AssDocument document, AssDialogue dialogue, double elapsed)
    {
        AssStyle baseStyle = document.GetStyle(dialogue.StyleName);
        var state = new StyleState(document, baseStyle);
        double duration = Math.Max(0, (dialogue.End - dialogue.Start).TotalSeconds);
        var result = new SegmentedEvent
        {
            Alignment = baseStyle.Alignment,
            Angle = baseStyle.Angle,
            MarginL = dialogue.MarginL != 0 ? dialogue.MarginL : baseStyle.MarginL,
            MarginR = dialogue.MarginR != 0 ? dialogue.MarginR : baseStyle.MarginR,
            MarginV = dialogue.MarginV != 0 ? dialogue.MarginV : baseStyle.MarginV,
            WrapStyle = document.ScriptInfo.WrapStyle,
            MoveEnd = duration,
        };

        var context = new TagContext(document, baseStyle, state, result, elapsed, duration);
        OverrideParser.Parse(dialogue.Text, tokens);
        drawing.Clear();

        foreach (OverrideToken token in tokens)
        {
            switch (token.Kind)
            {
                case OverrideTokenKind.Text:
                    if (state.DrawingScale >= 1)
                        drawing.Append(token.Value);
                    else if (token.Value.Length > 0)
                        result.Spans.Add(SubtitleSpan.FromText(token.Value, state.Freeze()));
                    break;

                case OverrideTokenKind.LineBreak:
                    if (state.DrawingScale < 1)
                        result.Spans.Add(SubtitleSpan.LineBreak(token.IsHardBreak));
                    break;

                case OverrideTokenKind.Tag:
                    // A drawing ends the moment \p changes, and keeps the style it was drawn with.
                    if (token.Name == "p")
                        FlushDrawing(result, state);
                    ApplyTag(context, token, 1.0, 0);
                    break;
            }
        }

        FlushDrawing(result, state);
        return result;
    }

    private void FlushDrawing(SegmentedEvent result, StyleState state)
    {
        if (drawing.Length == 0)
            return;

        result.Spans.Add(SubtitleSpan.FromDrawing(drawing.ToString(), state.Freeze()));
        drawing.Clear();
    }

    /// <param name="weight">1 outside an animation, the interpolation factor inside one.</param>
    private static void ApplyTag(TagContext context, OverrideToken token, double weight, int depth)
    {
        AssStyle baseStyle = context.BaseStyle;
        StyleState state = context.State;
        SegmentedEvent result = context.Result;
        string value = token.Value;

        switch (token.Name)
        {
            case "fn":
                state.FontName = value.Length > 0 ? value : baseStyle.FontName;
                break;

            case "fe":
                state.Charset = TryNumber(value, out double charset) ? (int)charset : baseStyle.Encoding;
                break;

            case "fs":
                if (value.Length > 0 && (value[0] == '+' || value[0] == '-') && TryNumber(value, out double delta))
                    state.FontSize = Math.Max(1, state.FontSize + delta);
                else
                    state.FontSize = Mix(state.FontSize, TryNumber(value, out double size) && size > 0 ? size : baseStyle.FontSize, weight);
                break;

            case "fsp":
                state.Spacing = Mix(state.Spacing, TryNumber(value, out double spacing) ? spacing : baseStyle.Spacing, weight);
                break;

            case "b":
                state.Bold = TryNumber(value, out double bold) ? bold >= 1 : baseStyle.Bold;
                break;

            case "i":
                state.Italic = TryNumber(value, out double italic) ? italic != 0 : baseStyle.Italic;
                break;

            case "u":
                state.Underline = TryNumber(value, out double underline) ? underline != 0 : baseStyle.Underline;
                break;

            case "s":
                state.StrikeOut = TryNumber(value, out double strike) ? strike != 0 : baseStyle.StrikeOut;
                break;

            case "fscx":
                state.ScaleX = Mix(state.ScaleX, (TryNumber(value, out double scaleX) ? scaleX : baseStyle.ScaleX) / 100.0, weight);
                break;

            case "fscy":
                state.ScaleY = Mix(state.ScaleY, (TryNumber(value, out double scaleY) ? scaleY : baseStyle.ScaleY) / 100.0, weight);
                break;

            case "fr":
            case "frz":
                result.Angle = Mix(result.Angle, TryNumber(value, out double angle) ? angle : baseStyle.Angle, weight);
                break;

            case "frx":
                result.AngleX = Mix(result.AngleX, TryNumber(value, out double angleX) ? angleX : 0, weight);
                break;

            case "fry":
                result.AngleY = Mix(result.AngleY, TryNumber(value, out double angleY) ? angleY : 0, weight);
                break;

            case "fax":
                result.ShearX = Mix(result.ShearX, TryNumber(value, out double shearX) ? shearX : 0, weight);
                break;

            case "fay":
                result.ShearY = Mix(result.ShearY, TryNumber(value, out double shearY) ? shearY : 0, weight);
                break;

            case "org":
                if (TryPoint(value, out double originX, out double originY))
                {
                    result.HasOrigin = true;
                    result.OriginX = Mix(result.OriginX, originX, weight);
                    result.OriginY = Mix(result.OriginY, originY, weight);
                }

                break;

            case "c":
            case "1c":
                state.Primary = Recolor(state.Primary, value, baseStyle.PrimaryColour, weight);
                break;

            case "2c":
                state.Secondary = Recolor(state.Secondary, value, baseStyle.SecondaryColour, weight);
                break;

            case "3c":
                state.Outline = Recolor(state.Outline, value, baseStyle.OutlineColour, weight);
                break;

            case "4c":
                state.Shadow = Recolor(state.Shadow, value, baseStyle.BackColour, weight);
                break;

            case "alpha":
                if (AssColor.TryParseAlpha(value, out byte alpha))
                {
                    state.Primary = state.Primary.WithAlpha(Mix(state.Primary.Alpha, alpha, weight));
                    state.Secondary = state.Secondary.WithAlpha(Mix(state.Secondary.Alpha, alpha, weight));
                    state.Outline = state.Outline.WithAlpha(Mix(state.Outline.Alpha, alpha, weight));
                    state.Shadow = state.Shadow.WithAlpha(Mix(state.Shadow.Alpha, alpha, weight));
                }
                else
                {
                    state.Primary = state.Primary.WithAlpha(baseStyle.PrimaryColour.Alpha);
                    state.Secondary = state.Secondary.WithAlpha(baseStyle.SecondaryColour.Alpha);
                    state.Outline = state.Outline.WithAlpha(baseStyle.OutlineColour.Alpha);
                    state.Shadow = state.Shadow.WithAlpha(baseStyle.BackColour.Alpha);
                }

                break;

            case "1a":
                state.Primary = state.Primary.WithAlpha(Mix(state.Primary.Alpha, ParseAlpha(value, baseStyle.PrimaryColour.Alpha), weight));
                break;

            case "2a":
                state.Secondary = state.Secondary.WithAlpha(Mix(state.Secondary.Alpha, ParseAlpha(value, baseStyle.SecondaryColour.Alpha), weight));
                break;

            case "3a":
                state.Outline = state.Outline.WithAlpha(Mix(state.Outline.Alpha, ParseAlpha(value, baseStyle.OutlineColour.Alpha), weight));
                break;

            case "4a":
                state.Shadow = state.Shadow.WithAlpha(Mix(state.Shadow.Alpha, ParseAlpha(value, baseStyle.BackColour.Alpha), weight));
                break;

            case "bord":
                state.BorderX = Mix(state.BorderX, Border(value, baseStyle.Outline), weight);
                state.BorderY = Mix(state.BorderY, Border(value, baseStyle.Outline), weight);
                break;

            case "xbord":
                state.BorderX = Mix(state.BorderX, Border(value, baseStyle.Outline), weight);
                break;

            case "ybord":
                state.BorderY = Mix(state.BorderY, Border(value, baseStyle.Outline), weight);
                break;

            case "shad":
                state.ShadowX = Mix(state.ShadowX, TryNumber(value, out double shadow) ? shadow : baseStyle.Shadow, weight);
                state.ShadowY = Mix(state.ShadowY, TryNumber(value, out double shadowBoth) ? shadowBoth : baseStyle.Shadow, weight);
                break;

            case "xshad":
                state.ShadowX = Mix(state.ShadowX, TryNumber(value, out double shadowX) ? shadowX : baseStyle.Shadow, weight);
                break;

            case "yshad":
                state.ShadowY = Mix(state.ShadowY, TryNumber(value, out double shadowY) ? shadowY : baseStyle.Shadow, weight);
                break;

            case "blur":
                state.Blur = Mix(state.Blur, TryNumber(value, out double blur) ? Math.Max(0, blur) : 0, weight);
                break;

            case "be":
                state.BlurEdges = Mix(state.BlurEdges, TryNumber(value, out double edges) ? Math.Max(0, edges) : 0, weight);
                break;

            case "an":
                if (TryNumber(value, out double an) && an is >= 1 and <= 9)
                    result.Alignment = (int)an;
                break;

            case "a":
                if (TryNumber(value, out double legacy))
                    result.Alignment = ConvertLegacyAlignment((int)legacy);
                break;

            case "q":
                if (TryNumber(value, out double wrap) && wrap is >= 0 and <= 3)
                    result.WrapStyle = (int)wrap;
                break;

            case "pos":
                if (TryPoint(value, out double x, out double y))
                {
                    result.HasPosition = true;
                    result.PositionX = x;
                    result.PositionY = y;
                }

                break;

            case "move":
                ApplyMove(result, value, context.Duration);
                break;

            case "fad":
                ApplyFad(result, value, context.Duration);
                break;

            case "fade":
                ApplyFade(result, value);
                break;

            case "clip":
            case "iclip":
                ApplyClip(result, value, token.Name == "iclip", weight);
                break;

            case "r":
                state.Reset(value.Length > 0 ? context.Document.GetStyle(value) : baseStyle);
                break;

            case "p":
                state.DrawingScale = TryNumber(value, out double scale) && scale >= 1 ? (int)scale : 0;
                break;

            case "pbo":
                state.DrawingBaselineOffset = TryNumber(value, out double pbo) ? pbo : 0;
                break;

            case "k":
                Karaoke(state, value, KaraokeMode.Fill);
                break;

            case "kf":
                Karaoke(state, value, KaraokeMode.Sweep);
                break;

            case "ko":
                Karaoke(state, value, KaraokeMode.Outline);
                break;

            case "kt":
                state.KaraokeCursor = TryNumber(value, out double cursor) ? cursor / 100.0 : 0;
                break;

            case "t":
                ApplyTransition(context, value, weight, depth);
                break;
        }
    }

    /// <summary>
    /// <c>\t([t1,t2,][accel,]tags)</c> -- works out the animation factor and replays the nested tags.
    /// </summary>
    private static void ApplyTransition(TagContext context, string value, double weight, int depth)
    {
        // Even an animation that has already finished has to be re-evaluated after a seek.
        context.Result.IsAnimated = true;
        if (depth >= MaxTransitionDepth)
            return;

        ReadOnlySpan<char> span = Unwrap(value);
        int backslash = span.IndexOf('\\');
        if (backslash < 0)
            return;

        string nested = span[backslash..].ToString();
        Span<double> head = stackalloc double[3];
        int count = ReadNumbers(span[..backslash], head);

        double start = 0;
        double end = context.Duration;
        double acceleration = 1;
        switch (count)
        {
            case 1:
                acceleration = head[0];
                break;
            case 2:
                start = head[0] / 1000.0;
                end = head[1] / 1000.0;
                break;
            case >= 3:
                start = head[0] / 1000.0;
                end = head[1] / 1000.0;
                acceleration = head[2];
                break;
        }

        SegmentedEvent result = context.Result;
        result.AnimationStart = Math.Min(result.AnimationStart, Math.Min(start, end));
        result.AnimationEnd = Math.Max(result.AnimationEnd, Math.Max(start, end));

        double factor = end <= start
            ? (context.Elapsed >= end ? 1 : 0)
            : Math.Clamp((context.Elapsed - start) / (end - start), 0, 1);

        if (acceleration is > 0 and not 1)
            factor = Math.Pow(factor, acceleration);

        var inner = new List<OverrideToken>();
        OverrideParser.ParseTags(nested, inner);
        foreach (OverrideToken token in inner)
        {
            if (token.Kind == OverrideTokenKind.Tag)
                ApplyTag(context, token, weight * factor, depth + 1);
        }
    }

    private static void ApplyMove(SegmentedEvent result, string value, double duration)
    {
        Span<double> numbers = stackalloc double[6];
        int count = ReadNumbers(Unwrap(value), numbers);
        if (count < 4)
            return;

        result.HasMove = true;
        result.HasPosition = true;
        result.MoveX1 = numbers[0];
        result.MoveY1 = numbers[1];
        result.MoveX2 = numbers[2];
        result.MoveY2 = numbers[3];
        result.MoveStart = count >= 6 ? numbers[4] / 1000.0 : 0;
        result.MoveEnd = count >= 6 ? numbers[5] / 1000.0 : duration;
    }

    private static void ApplyFad(SegmentedEvent result, string value, double duration)
    {
        Span<double> numbers = stackalloc double[2];
        if (ReadNumbers(Unwrap(value), numbers) < 2)
            return;

        double fadeIn = numbers[0] / 1000.0;
        double fadeOut = numbers[1] / 1000.0;
        result.Fade = new SubtitleFade
        {
            Alpha1 = 0,
            Alpha2 = 255,
            Alpha3 = 0,
            Time1 = 0,
            Time2 = fadeIn,
            Time3 = Math.Max(fadeIn, duration - fadeOut),
            Time4 = duration,
        };
    }

    private static void ApplyFade(SegmentedEvent result, string value)
    {
        Span<double> numbers = stackalloc double[7];
        if (ReadNumbers(Unwrap(value), numbers) < 7)
            return;

        // The file stores transparency; everything downstream works in opacity.
        result.Fade = new SubtitleFade
        {
            Alpha1 = 255 - Math.Clamp(numbers[0], 0, 255),
            Alpha2 = 255 - Math.Clamp(numbers[1], 0, 255),
            Alpha3 = 255 - Math.Clamp(numbers[2], 0, 255),
            Time1 = numbers[3] / 1000.0,
            Time2 = numbers[4] / 1000.0,
            Time3 = numbers[5] / 1000.0,
            Time4 = numbers[6] / 1000.0,
        };
    }

    /// <summary>
    /// <c>\clip(x1,y1,x2,y2)</c> or <c>\clip([scale,]drawing)</c>, and the same for <c>\iclip</c>.
    /// </summary>
    /// <remarks>
    /// Only the rectangle form is animatable, which is exactly what the format allows, so the
    /// existing rectangle is kept and interpolated into rather than replaced.
    /// </remarks>
    private static void ApplyClip(SegmentedEvent result, string value, bool inverse, double weight)
    {
        ReadOnlySpan<char> span = Unwrap(value);
        Span<double> numbers = stackalloc double[4];
        if (ReadNumbers(span, numbers) == 4)
        {
            SubtitleClip clip = result.Clip is { IsRectangle: true } previous
                ? previous
                : new SubtitleClip { IsRectangle = true };

            clip.Inverse = inverse;
            clip.IsRectangle = true;
            clip.Drawing = null;
            clip.Left = Mix(clip.Left, numbers[0], weight);
            clip.Top = Mix(clip.Top, numbers[1], weight);
            clip.Right = Mix(clip.Right, numbers[2], weight);
            clip.Bottom = Mix(clip.Bottom, numbers[3], weight);
            result.Clip = clip;
            return;
        }

        int comma = span.IndexOf(',');
        int scale = 1;
        ReadOnlySpan<char> commands = span;
        if (comma > 0 && int.TryParse(span[..comma].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            scale = Math.Max(1, parsed);
            commands = span[(comma + 1)..];
        }

        if (commands.IsWhiteSpace())
            return;

        result.Clip = new SubtitleClip
        {
            Inverse = inverse,
            IsRectangle = false,
            Drawing = commands.ToString(),
            DrawingScale = scale,
        };
    }

    private static void Karaoke(StyleState state, string value, KaraokeMode mode)
    {
        double seconds = TryNumber(value, out double centiseconds) ? Math.Max(0, centiseconds) / 100.0 : 0;
        state.Karaoke = mode;
        state.KaraokeStart = state.KaraokeCursor;
        state.KaraokeEnd = state.KaraokeCursor + seconds;
        state.KaraokeCursor += seconds;
    }

    private static double Border(string value, double fallback)
        => TryNumber(value, out double border) ? Math.Max(0, border) : fallback;

    private static AssColor Recolor(AssColor current, string value, AssColor fallback, double weight)
    {
        AssColor target = AssColor.TryParse(value, out AssColor parsed) ? parsed : fallback;

        // Color tags carry no transparency; the alpha in force stays in force.
        return new AssColor(
            Mix(current.Red, target.Red, weight),
            Mix(current.Green, target.Green, weight),
            Mix(current.Blue, target.Blue, weight),
            current.Alpha);
    }

    private static byte ParseAlpha(string value, byte fallback)
        => AssColor.TryParseAlpha(value, out byte alpha) ? alpha : fallback;

    private static double Mix(double from, double to, double weight)
        => weight >= 1 ? to : from + ((to - from) * weight);

    private static byte Mix(byte from, byte to, double weight)
        => (byte)Math.Clamp(Math.Round(Mix((double)from, to, weight)), 0, 255);

    private static bool TryNumber(string value, out double result)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    /// <summary>Strips the parentheses a bracketed tag argument arrives wrapped in.</summary>
    private static ReadOnlySpan<char> Unwrap(string value)
    {
        ReadOnlySpan<char> span = value.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '(')
            span = span[1..];
        if (span.Length > 0 && span[^1] == ')')
            span = span[..^1];
        return span.Trim();
    }

    /// <summary>Reads up to <paramref name="destination"/>.Length comma separated numbers.</summary>
    private static int ReadNumbers(ReadOnlySpan<char> span, Span<double> destination)
    {
        int count = 0;
        while (count < destination.Length && !span.IsEmpty)
        {
            int comma = span.IndexOf(',');
            ReadOnlySpan<char> field = comma < 0 ? span : span[..comma];
            if (!double.TryParse(field.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                break;

            destination[count++] = number;
            if (comma < 0)
                break;
            span = span[(comma + 1)..];
        }

        return count;
    }

    private static bool TryPoint(string value, out double x, out double y)
    {
        Span<double> numbers = stackalloc double[2];
        int count = ReadNumbers(Unwrap(value), numbers);
        x = count > 0 ? numbers[0] : 0;
        y = count > 1 ? numbers[1] : 0;
        return count >= 2;
    }

    private static int ConvertLegacyAlignment(int value)
    {
        int horizontal = value & 0x3;
        if (horizontal is < 1 or > 3)
            horizontal = 2;

        if ((value & 0x8) != 0)
            return horizontal + 3;
        if ((value & 0x4) != 0)
            return horizontal + 6;
        return horizontal;
    }

    private sealed record TagContext(
        AssDocument Document,
        AssStyle BaseStyle,
        StyleState State,
        SegmentedEvent Result,
        double Elapsed,
        double Duration);

    /// <summary>Mutable rendering state; every tag edits one field and the rest carries over.</summary>
    private sealed class StyleState
    {
        private readonly AssDocument document;
        private string baseFontName = string.Empty;
        private int baseCharset;
        private SubtitleScript styleScript;
        private string fontName = string.Empty;
        private int charset;
        private double fontSize;
        private bool bold;
        private bool italic;
        private bool underline;
        private bool strikeOut;
        private AssColor primary;
        private AssColor secondary;
        private AssColor outline;
        private AssColor shadow;
        private double scaleX;
        private double scaleY;
        private double spacing;
        private double borderX;
        private double borderY;
        private double shadowX;
        private double shadowY;
        private double blur;
        private double blurEdges;
        private int drawingScale;
        private double drawingBaselineOffset;
        private KaraokeMode karaoke;
        private double karaokeStart;
        private double karaokeEnd;
        private int borderStyle;
        private SpanStyle? frozen;

        public StyleState(AssDocument document, AssStyle style)
        {
            this.document = document;
            Reset(style);
        }

        public string FontName { get => fontName; set => Set(ref fontName, value); }

        /// <summary><c>\fe</c>, the GDI charset; only ever read as a hint at the line's language.</summary>
        public int Charset { get => charset; set => Set(ref charset, value); }

        public double FontSize { get => fontSize; set => Set(ref fontSize, value); }

        public bool Bold { get => bold; set => Set(ref bold, value); }

        public bool Italic { get => italic; set => Set(ref italic, value); }

        public bool Underline { get => underline; set => Set(ref underline, value); }

        public bool StrikeOut { get => strikeOut; set => Set(ref strikeOut, value); }

        public AssColor Primary { get => primary; set => Set(ref primary, value); }

        public AssColor Secondary { get => secondary; set => Set(ref secondary, value); }

        public AssColor Outline { get => outline; set => Set(ref outline, value); }

        public AssColor Shadow { get => shadow; set => Set(ref shadow, value); }

        public double ScaleX { get => scaleX; set => Set(ref scaleX, value); }

        public double ScaleY { get => scaleY; set => Set(ref scaleY, value); }

        public double Spacing { get => spacing; set => Set(ref spacing, value); }

        public double BorderX { get => borderX; set => Set(ref borderX, value); }

        public double BorderY { get => borderY; set => Set(ref borderY, value); }

        public double ShadowX { get => shadowX; set => Set(ref shadowX, value); }

        public double ShadowY { get => shadowY; set => Set(ref shadowY, value); }

        public double Blur { get => blur; set => Set(ref blur, value); }

        public double BlurEdges { get => blurEdges; set => Set(ref blurEdges, value); }

        public int DrawingScale { get => drawingScale; set => Set(ref drawingScale, value); }

        public double DrawingBaselineOffset { get => drawingBaselineOffset; set => Set(ref drawingBaselineOffset, value); }

        public KaraokeMode Karaoke { get => karaoke; set => Set(ref karaoke, value); }

        public double KaraokeStart { get => karaokeStart; set => Set(ref karaokeStart, value); }

        public double KaraokeEnd { get => karaokeEnd; set => Set(ref karaokeEnd, value); }

        /// <summary>Where the next syllable starts; not part of the frozen appearance.</summary>
        public double KaraokeCursor { get; set; }

        public void Reset(AssStyle style)
        {
            baseFontName = style.FontName;
            baseCharset = style.Encoding;
            styleScript = document.GetScript(style);
            fontName = style.FontName;
            charset = style.Encoding;
            fontSize = style.FontSize;
            bold = style.Bold;
            italic = style.Italic;
            underline = style.Underline;
            strikeOut = style.StrikeOut;
            primary = style.PrimaryColour;
            secondary = style.SecondaryColour;
            outline = style.OutlineColour;
            shadow = style.BackColour;
            scaleX = style.ScaleX / 100.0;
            scaleY = style.ScaleY / 100.0;
            spacing = style.Spacing;
            borderX = borderY = style.Outline;
            shadowX = shadowY = style.Shadow;
            blur = 0;
            blurEdges = 0;
            borderStyle = style.BorderStyle;
            frozen = null;
        }

        public SpanStyle Freeze() => frozen ??= new SpanStyle
        {
            FontName = fontName,
            Script = ResolveScript(),
            FontSize = fontSize,
            Bold = bold,
            Italic = italic,
            Underline = underline,
            StrikeOut = strikeOut,
            PrimaryColour = primary,
            SecondaryColour = secondary,
            OutlineColour = outline,
            ShadowColour = shadow,
            ScaleX = scaleX,
            ScaleY = scaleY,
            Spacing = spacing,
            BorderX = borderX,
            BorderY = borderY,
            ShadowX = shadowX,
            ShadowY = shadowY,
            BorderStyle = borderStyle,
            Blur = blur,
            BlurEdges = blurEdges,
            DrawingScale = drawingScale,
            DrawingBaselineOffset = drawingBaselineOffset,
            Karaoke = karaoke,
            KaraokeStart = karaokeStart,
            KaraokeEnd = karaokeEnd,
        };

        private void Set<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;

            field = value;
            frozen = null;
        }

        /// <summary>
        /// Decides which country's Han forms this run wants.
        /// </summary>
        /// <remarks>
        /// A tag that names a font or a charset outright is taken at its word, because that is a
        /// deliberate switch mid-line. Failing that the style's own lines decide it, which is by far
        /// the most reliable signal: they are read as a whole file, whereas <c>Encoding</c> is left at
        /// its default in most scripts and copied unchanged into styles of every language in the rest.
        /// </remarks>
        private SubtitleScript ResolveScript()
        {
            SubtitleScript byCharset = SubtitleScripts.FromCharset(charset);
            if (charset != baseCharset && byCharset != SubtitleScript.Unknown)
                return byCharset;

            SubtitleScript byFamily = SubtitleScripts.FromFamilyName(fontName);
            if (byFamily != SubtitleScript.Unknown
                && !string.Equals(fontName, baseFontName, StringComparison.Ordinal))
                return byFamily;

            if (styleScript != SubtitleScript.Unknown)
                return styleScript;

            if (byFamily != SubtitleScript.Unknown)
                return byFamily;

            return byCharset != SubtitleScript.Unknown ? byCharset : document.DocumentScript;
        }
    }
}
