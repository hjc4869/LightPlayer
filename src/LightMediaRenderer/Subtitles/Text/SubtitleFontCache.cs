using System;
using System.Collections.Generic;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Subtitles;
using SkiaSharp;

namespace LightMediaRenderer.Subtitles.Text;

/// <summary>
/// Resolves ASS font names to typefaces and keeps the expensive pieces alive between frames.
/// </summary>
/// <remarks>
/// Three things are cached, each for a different reason: the <see cref="SubtitleFont"/> because
/// building it reads the entire font file, the <see cref="SKFont"/> because a subtitle line asks for
/// the same size dozens of times, and the fallback chain because scripts routinely name a font that
/// does not exist on this machine, or one that lacks the script they are written in.
///
/// The chain is what keeps a line looking like one line. Asking the platform for a face per
/// character, which is the obvious thing to do, gives back whichever family the system ranks first
/// for that codepoint on its own -- so a Chinese sentence comes out partly in a Japanese face and
/// partly in another, changing every few characters, and any codepoint the winner happens to lack
/// turns into a box. Instead every request builds one ordered list of candidates, led by the families
/// that belong to the line's <see cref="SubtitleScript"/>, and a character is drawn with the first
/// candidate that covers it. Whatever the platform matcher still has to be asked for is appended to
/// that same list, so the next character reuses it rather than starting the search again.
/// </remarks>
internal sealed class SubtitleFontCache : IDisposable
{
    private readonly SKFontManager manager = SKFontManager.Default;
    private readonly Dictionary<FontRequest, SubtitleFont?> byRequest = [];
    private readonly Dictionary<FontRequest, FallbackChain> chains = [];
    private readonly Dictionary<(FontRequest Request, int Codepoint), SubtitleFont?> byCharacter = [];
    private readonly Dictionary<string, SubtitleFont?> byTypeface = [];
    private readonly Dictionary<SizedFontKey, SKFont> sizedFonts = [];

    /// <summary>
    /// A leading <c>@</c> asks for the family's vertical variant, as it does in GDI. The face itself
    /// is the ordinary one; what changes is that its glyphs get turned a quarter turn when drawn.
    /// </summary>
    public static bool IsVertical(string family) => family.AsSpan().TrimStart().StartsWith("@");

    /// <summary>
    /// Returns a font able to draw <paramref name="sample"/>, falling back through the families that
    /// suit <paramref name="script"/> when the requested one is missing or does not cover it.
    /// </summary>
    public SubtitleFont? Resolve(string family, bool bold, bool italic, SubtitleScript script, int sample)
    {
        var request = new FontRequest(family, bold, italic, script);
        SubtitleFont? primary = Primary(request);

        if (sample <= 0)
            return primary ?? Fallback(request, ' ');

        if (primary is not null && primary.ContainsGlyph(sample))
            return primary;

        return Fallback(request, sample) ?? primary;
    }

    /// <summary>Builds the drawing font; <c>\fscx</c> and <c>\fscy</c> are baked into it.</summary>
    /// <param name="emSize">Em size in script pixels, already converted from the ASS font size.</param>
    public SKFont GetSizedFont(SubtitleFont font, double emSize, double scaleX, double scaleY, bool italic)
    {
        // Skia scales horizontally after sizing, so vertical scale goes into the size and the
        // horizontal scale becomes their ratio. Both end up exact.
        float height = (float)Math.Max(1.0, emSize * scaleY);
        float horizontal = scaleY > 0 ? (float)(scaleX / scaleY) : 1f;
        float skew = italic && font.SyntheticItalic ? -0.25f : 0f;

        var key = new SizedFontKey(font, height, horizontal, skew);
        if (sizedFonts.TryGetValue(key, out SKFont? cached))
            return cached;

        var created = new SKFont(font.Typeface, height, horizontal, skew)
        {
            // Shaping already produced unhinted design-unit advances; hinting here would make the
            // drawn glyphs disagree with the positions the layout computed.
            Hinting = SKFontHinting.None,
            LinearMetrics = true,
            Subpixel = true,
            Edging = SKFontEdging.Antialias,
        };

        sizedFonts[key] = created;
        return created;
    }

    public void Dispose()
    {
        foreach (SKFont font in sizedFonts.Values)
            font.Dispose();
        sizedFonts.Clear();

        foreach (SubtitleFont? font in byTypeface.Values)
            font?.Dispose();
        byTypeface.Clear();
        byRequest.Clear();
        byCharacter.Clear();
        chains.Clear();
    }

    /// <summary>The face the script actually named, or <c>null</c> when this machine has no such family.</summary>
    private SubtitleFont? Primary(FontRequest request)
    {
        if (byRequest.TryGetValue(request, out SubtitleFont? cached))
            return cached;

        SubtitleFont? font = null;
        if (!string.IsNullOrWhiteSpace(request.Family))
        {
            string family = request.Family.Trim().TrimStart('@');
            SKFontStyle style = ToFontStyle(request.Bold, request.Italic);

            // Left deliberately lenient: a font manager that answers "Arial" with a metric-compatible
            // stand-in is doing what the script wanted. Only the fallback list below needs the family
            // it asked for to be the family it gets.
            SKTypeface? typeface = manager.MatchFamily(family, style)
                ?? SubtitleFontFallbacks.MatchFamily(family, style);

            if (typeface is not null)
                font = Adopt(typeface, request.Italic);
        }

        byRequest[request] = font;
        return font;
    }

    /// <summary>Walks the request's fallback chain, extending it if nothing on it covers the character.</summary>
    private SubtitleFont? Fallback(FontRequest request, int codepoint)
    {
        var key = (request, codepoint);
        if (byCharacter.TryGetValue(key, out SubtitleFont? cached))
            return cached;

        FallbackChain chain = Chain(request);
        SubtitleFont? found = chain.Find(codepoint) ?? Extend(request, chain, codepoint);
        byCharacter[key] = found;
        return found;
    }

    private FallbackChain Chain(FontRequest request)
    {
        if (chains.TryGetValue(request, out FallbackChain? cached))
            return cached;

        // The script's own families first, so that Han characters keep the stroke shapes of the
        // country the line is written for, then the Latin faces for the words that are not CJK.
        string[] preferred = SubtitleScripts.PreferredFamilies(request.Script);
        var candidates = new string[preferred.Length + SubtitleScripts.LatinFamilies.Length];
        preferred.CopyTo(candidates, 0);
        SubtitleScripts.LatinFamilies.CopyTo(candidates, preferred.Length);

        var chain = new FallbackChain(candidates, family => OpenExact(request, family));
        chains[request] = chain;
        return chain;
    }

    /// <summary>
    /// Opens one candidate family, but only if the font manager really has it.
    /// </summary>
    /// <remarks>
    /// Fontconfig answers every request with its best guess rather than with nothing, so an unchecked
    /// lookup for, say, <c>Noto Sans CJK SC</c> on a machine without it would quietly return an
    /// unrelated face and stop the search before the families further down the list were ever tried.
    /// A stand-in is not thrown away, though: it becomes the chain's last resort, which is also what
    /// rescues the platforms that report a family under its localized name.
    /// </remarks>
    private (SubtitleFont? Exact, SubtitleFont? Substitute) OpenExact(FontRequest request, string family)
    {
        SKFontStyle style = ToFontStyle(request.Bold, request.Italic);
        SKTypeface? typeface = manager.MatchFamily(family, style)
            ?? SubtitleFontFallbacks.MatchFamily(family, style);

        if (typeface is null)
            return (null, null);

        SubtitleFont? font = Adopt(typeface, request.Italic);
        return IsFamily(typeface.FamilyName, family) ? (font, null) : (null, font);
    }

    /// <summary>Asks the platform for a face for one character, and pins it to the chain for the rest.</summary>
    private SubtitleFont? Extend(FontRequest request, FallbackChain chain, int codepoint)
    {
        SKFontStyle style = ToFontStyle(request.Bold, request.Italic);
        string? language = SubtitleScripts.Bcp47(request.Script);

        // The hint is passed for the platforms that honor it; fontconfig, which is what most Linux
        // builds see, ignores it -- which is exactly why the curated list above runs first.
        SKTypeface? match = manager.MatchCharacter(
            request.Family,
            style,
            language is null ? null : [language],
            codepoint);

        using SKFont? matchFont = match?.ToFont();
        if (matchFont is null || !matchFont.ContainsGlyph(codepoint))
            match = SubtitleFontFallbacks.MatchCharacter(codepoint, style, request.Script) ?? match;

        SubtitleFont? font = match is null ? null : Adopt(match, request.Italic);
        if (font is not null && font.ContainsGlyph(codepoint))
        {
            chain.Pin(font);
            return font;
        }

        if (chain.Substitute is not null)
            return chain.Substitute;

        // Nothing on this machine has the character. Something still has to come back, or the whole
        // run it belongs to would vanish instead of showing the boxes that say a glyph is missing.
        return font
            ?? (SubtitleFontFallbacks.MatchAny(style) is { } any ? Adopt(any, request.Italic) : null)
            ?? Adopt(SKTypeface.Default, request.Italic);
    }

    /// <summary>Shares one <see cref="SubtitleFont"/> per physical face, however it was found.</summary>
    private SubtitleFont? Adopt(SKTypeface typeface, bool italicRequested)
    {
        string key = $"{typeface.FamilyName}\u0000{typeface.FontWeight}\u0000{typeface.FontWidth}\u0000{typeface.FontSlant}\u0000{italicRequested}";
        if (byTypeface.TryGetValue(key, out SubtitleFont? cached))
            return cached;

        SubtitleFont? font = SubtitleFont.TryCreate(typeface, italicRequested);
        byTypeface[key] = font;
        return font;
    }

    /// <summary>Whether a font manager answered with the family that was asked for.</summary>
    /// <remarks>
    /// The trailing-space case covers the managers that name each face rather than its family, so
    /// that <c>Noto Sans CJK JP Medium</c> still counts as <c>Noto Sans CJK JP</c>.
    /// </remarks>
    private static bool IsFamily(string? reported, string requested)
        => reported is not null
            && (reported.Equals(requested, StringComparison.OrdinalIgnoreCase)
                || (reported.Length > requested.Length
                    && reported[requested.Length] == ' '
                    && reported.AsSpan(0, requested.Length).Equals(requested, StringComparison.OrdinalIgnoreCase)));

    private static SKFontStyle ToFontStyle(bool bold, bool italic) => new(
        bold ? (int)SKFontStyleWeight.Bold : (int)SKFontStyleWeight.Normal,
        (int)SKFontStyleWidth.Normal,
        italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    private readonly record struct FontRequest(string Family, bool Bold, bool Italic, SubtitleScript Script);

    private readonly record struct SizedFontKey(SubtitleFont Font, float Size, float ScaleX, float SkewX);

    /// <summary>
    /// An ordered list of fallback candidates, opened only as far as the text actually needs.
    /// </summary>
    /// <remarks>
    /// Opening a candidate reads the whole font file, and a CJK family runs to tens of megabytes, so
    /// the list holds names until a character forces the next one open. In practice a CJK line opens
    /// exactly one, because the first family of its own script covers everything that follows.
    /// </remarks>
    private sealed class FallbackChain(string[] families, Func<string, (SubtitleFont? Exact, SubtitleFont? Substitute)> open)
    {
        private readonly List<SubtitleFont> fonts = [];
        private int opened;

        /// <summary>A face the manager offered in place of a family it did not actually have.</summary>
        public SubtitleFont? Substitute { get; private set; }

        public SubtitleFont? Find(int codepoint)
        {
            foreach (SubtitleFont font in fonts)
            {
                if (font.ContainsGlyph(codepoint))
                    return font;
            }

            while (opened < families.Length)
            {
                (SubtitleFont? exact, SubtitleFont? substitute) = open(families[opened++]);
                Substitute ??= substitute;

                if (exact is null)
                    continue;

                fonts.Add(exact);
                if (exact.ContainsGlyph(codepoint))
                    return exact;
            }

            return null;
        }

        /// <summary>Keeps a face the platform matcher found, so that later characters reuse it.</summary>
        public void Pin(SubtitleFont font)
        {
            if (!fonts.Contains(font))
                fonts.Add(font);
        }
    }
}
