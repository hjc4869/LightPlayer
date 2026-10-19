using System;
using System.Globalization;

namespace LightMediaRenderer.Subtitles;

/// <summary>
/// A color as written in an ASS/SSA file.
/// </summary>
/// <remarks>
/// The on-disk form is <c>&amp;HAABBGGRR&amp;</c>: channels are in reverse order and the leading
/// byte is <em>transparency</em>, not opacity, so <c>00</c> means fully visible. This type stores
/// the value already flipped into the conventional RGBA with <see cref="Alpha"/> 255 = opaque,
/// which is what every drawing back-end expects.
/// </remarks>
internal readonly struct AssColor : IEquatable<AssColor>
{
    public AssColor(byte red, byte green, byte blue, byte alpha = 255)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
    }

    public byte Red { get; }

    public byte Green { get; }

    public byte Blue { get; }

    /// <summary>Opacity, 255 = fully opaque (the inverse of the ASS transparency byte).</summary>
    public byte Alpha { get; }

    public static AssColor White => new(255, 255, 255);

    public static AssColor Black => new(0, 0, 0);

    public static AssColor Transparent => new(0, 0, 0, 0);

    public AssColor WithAlpha(byte alpha) => new(Red, Green, Blue, alpha);

    /// <summary>
    /// Parses <c>&amp;HAABBGGRR&amp;</c>, the shorter <c>&amp;HBBGGRR&amp;</c>, or a plain decimal
    /// integer (which some tools emit for style rows).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out AssColor color)
    {
        color = default;
        text = text.Trim();
        if (text.IsEmpty)
            return false;

        bool hex = false;
        if (text[0] == '&')
        {
            text = text[1..];
            hex = true;
        }

        if (!text.IsEmpty && (text[0] == 'H' || text[0] == 'h'))
        {
            text = text[1..];
            hex = true;
        }

        while (!text.IsEmpty && text[^1] == '&')
            text = text[..^1];

        text = text.Trim();
        if (text.IsEmpty)
            return false;

        uint value;
        if (hex)
        {
            // Files in the wild pad well past eight digits; the low 32 bits are the color.
            if (text.Length > 8)
                text = text[^8..];
            if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return false;
        }
        else if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        color = FromPackedBgr(value);
        return true;
    }

    public static AssColor Parse(ReadOnlySpan<char> text, AssColor fallback)
        => TryParse(text, out AssColor color) ? color : fallback;

    /// <summary>Parses the argument of <c>\alpha</c> / <c>\1a</c> and friends into an opacity.</summary>
    public static bool TryParseAlpha(ReadOnlySpan<char> text, out byte alpha)
    {
        alpha = 255;
        text = text.Trim().TrimStart('&').TrimStart("Hh").TrimEnd('&').Trim();
        if (text.IsEmpty)
            return false;

        if (text.Length > 2)
            text = text[..2];

        if (!byte.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte transparency))
            return false;

        alpha = (byte)(255 - transparency);
        return true;
    }

    private static AssColor FromPackedBgr(uint value)
    {
        byte red = (byte)(value & 0xFF);
        byte green = (byte)((value >> 8) & 0xFF);
        byte blue = (byte)((value >> 16) & 0xFF);
        byte transparency = (byte)((value >> 24) & 0xFF);
        return new AssColor(red, green, blue, (byte)(255 - transparency));
    }

    public bool Equals(AssColor other)
        => Red == other.Red && Green == other.Green && Blue == other.Blue && Alpha == other.Alpha;

    public override bool Equals(object? obj) => obj is AssColor other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Red, Green, Blue, Alpha);

    public override string ToString()
        => $"&H{255 - Alpha:X2}{Blue:X2}{Green:X2}{Red:X2}";

    public static bool operator ==(AssColor left, AssColor right) => left.Equals(right);

    public static bool operator !=(AssColor left, AssColor right) => !left.Equals(right);
}
