using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using LightMediaRenderer.Contracts;
using HarfBuzzSharp;
using SkiaSharp;
using HbBlob = HarfBuzzSharp.Blob;
using HbFace = HarfBuzzSharp.Face;
using HbFont = HarfBuzzSharp.Font;

namespace LightMediaRenderer.Subtitles.Text;

/// <summary>
/// A typeface paired with the HarfBuzz font that shapes it.
/// </summary>
/// <remarks>
/// The HarfBuzz font is scaled to the face's units-per-em rather than to a point size, so a single
/// instance serves every size the script asks for: advances come back in design units and the layout
/// multiplies them by <c>size / </c><see cref="UnitsPerEm"/>. Building one of these means reading the
/// whole font file, which is exactly why they are cached.
/// </remarks>
internal sealed class SubtitleFont : IDisposable
{
    private const uint Os2Tag = 0x4F532F32;  // 'OS/2'
    private const uint HheaTag = 0x68686561; // 'hhea'

    private readonly HbBlob blob;
    private readonly HbFace face;
    private readonly SKFont glyphFont;

    private SubtitleFont(SKTypeface typeface, HbBlob blob, HbFace face, HbFont font, bool syntheticItalic)
    {
        Typeface = typeface;
        this.blob = blob;
        this.face = face;
        glyphFont = new SKFont(typeface);
        Shaper = font;
        UnitsPerEm = typeface.UnitsPerEm > 0 ? typeface.UnitsPerEm : 1000;
        SyntheticItalic = syntheticItalic;
        (DesignHeight, AscentRatio) = ReadVerticalMetrics(typeface, UnitsPerEm);
        DescenderRatio = ReadTypoDescender(typeface, UnitsPerEm);
    }

    public SKTypeface Typeface { get; }

    public HbFont Shaper { get; }

    public int UnitsPerEm { get; }

    /// <summary>
    /// How many ems an ASS <c>Fontsize</c> spans, i.e. <c>(usWinAscent + usWinDescent) / unitsPerEm</c>.
    /// </summary>
    /// <remarks>
    /// VSFilter sized text by the OS/2 window metrics rather than by the em square, and libass
    /// reproduces it (<c>FT_SIZE_REQUEST_TYPE_REAL_DIM</c>). It is usually well above 1, so treating
    /// <c>Fontsize</c> as an em size renders everything noticeably too big.
    /// </remarks>
    public double DesignHeight { get; }

    /// <summary>Share of <see cref="DesignHeight"/> that sits above the baseline.</summary>
    public double AscentRatio { get; }

    /// <summary>
    /// OS/2 <c>sTypoDescender</c> as a fraction of the em, so zero or negative. Only the rotation of
    /// <c>@</c> fonts uses it, to place the turned glyph the way VSFilter did.
    /// </summary>
    public double DescenderRatio { get; }

    /// <summary>Set when an italic face was asked for but the family only has an upright one.</summary>
    public bool SyntheticItalic { get; }

    public bool ContainsGlyph(int codepoint) => glyphFont.ContainsGlyph(codepoint);

    /// <summary>
    /// Height a glyph occupies when the text runs downwards, as a fraction of the em.
    /// </summary>
    /// <remarks>
    /// Taken from <c>vmtx</c> when the font has one; HarfBuzz otherwise falls back to the font's own
    /// height, which is what FreeType reports as <c>vertAdvance</c> in the same situation.
    /// </remarks>
    public float GetVerticalAdvance(ushort glyph)
        => Math.Abs(Shaper.GetVerticalGlyphAdvance(glyph)) / (float)UnitsPerEm;

    public static SubtitleFont? TryCreate(SKTypeface typeface, bool italicRequested)
    {
        SKStreamAsset? stream = null;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            stream = typeface.OpenStream(out int index);
            if (stream is null || stream.Length <= 0)
                return null;

            // HarfBuzz reads the table data for as long as the face lives, so it gets its own
            // unmanaged copy rather than a pointer into anything the GC is allowed to move.
            int length = stream.Length;
            buffer = Marshal.AllocHGlobal(length);
            if (stream.Read(buffer, length) != length)
                return null;

            IntPtr owned = buffer;
            var blob = new HbBlob(owned, length, MemoryMode.ReadOnly, () => Marshal.FreeHGlobal(owned));
            buffer = IntPtr.Zero;
            blob.MakeImmutable();

            var face = new HbFace(blob, index)
            {
                Index = index,
                UnitsPerEm = typeface.UnitsPerEm > 0 ? typeface.UnitsPerEm : 1000,
            };

            var font = new HbFont(face);
            font.SetScale(face.UnitsPerEm, face.UnitsPerEm);
            font.SetFunctionsOpenType();

            return new SubtitleFont(typeface, blob, face, font, italicRequested && !typeface.IsItalic);
        }
        catch (Exception)
        {
            // A font the platform cannot hand over as a stream is simply unusable for shaping.
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
            stream?.Dispose();
        }
    }

    public void Dispose()
    {
        // The typeface is not ours to dispose: the font manager hands out shared instances.
        glyphFont.Dispose();
        Shaper.Dispose();
        face.Dispose();
        blob.Dispose();
    }

    private static (double DesignHeight, double AscentRatio) ReadVerticalMetrics(SKTypeface typeface, int unitsPerEm)
    {
        if (TryReadTable(typeface, Os2Tag, 78) is { } os2)
        {
            int ascent = BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(74));
            int descent = BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(76));
            if (ascent + descent > 0)
                return ((double)(ascent + descent) / unitsPerEm, (double)ascent / (ascent + descent));
        }

        if (TryReadTable(typeface, HheaTag, 8) is { } hhea)
        {
            int ascent = BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(4));
            int descent = -BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(6));
            if (ascent + descent > 0)
                return ((double)(ascent + descent) / unitsPerEm, (double)ascent / (ascent + descent));
        }

        return (1.0, 0.8);
    }

    private static double ReadTypoDescender(SKTypeface typeface, int unitsPerEm)
    {
        if (TryReadTable(typeface, Os2Tag, 72) is { } os2)
            return (double)BinaryPrimitives.ReadInt16BigEndian(os2.AsSpan(70)) / unitsPerEm;

        return 0;
    }

    private static byte[]? TryReadTable(SKTypeface typeface, uint tag, int minimumLength)
    {
        try
        {
            byte[]? table = typeface.GetTableData(tag);
            return table is not null && table.Length >= minimumLength ? table : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
