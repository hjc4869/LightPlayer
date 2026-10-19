using System;
using System.Buffers.Binary;
using System.IO;
using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;

namespace LightStudio.LightPlayer.Services;

internal static unsafe partial class PhotoDecoder
{
    private static bool TryReadXmp(ReadOnlySpan<byte> data, out GainMap metadata, float? appleHeadroom = null)
    {
        metadata = default;
        if (data.Length is 0 or > 1024 * 1024)
            return false;

        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1024 * 1024,
            });
            foreach (XElement element in XDocument.Load(reader).Descendants(RdfNamespace + "Description"))
            {
                if ((Value(element, "Version") == "1.0" && GainMap.TryRead(element, out metadata)) ||
                    TryReadApple(element, appleHeadroom, out metadata))
                    return true;
            }

            return false;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool TryReadApple(XElement element, float? headroom, out GainMap metadata)
    {
        metadata = default;
        XNamespace apple = "http://ns.apple.com/HDRGainMap/1.0/";
        XNamespace pixels = "http://ns.apple.com/pixeldatainfo/1.0/";
        if ((element.Attribute(pixels + "AuxiliaryImageType")?.Value ?? element.Element(pixels + "AuxiliaryImageType")?.Value) !=
                "urn:com:apple:photo:2020:aux:hdrgainmap" ||
            string.IsNullOrWhiteSpace(element.Attribute(apple + "HDRGainMapVersion")?.Value ?? element.Element(apple + "HDRGainMapVersion")?.Value))
            return false;
        if (float.TryParse(element.Attribute(apple + "HDRGainMapHeadroom")?.Value ?? element.Element(apple + "HDRGainMapHeadroom")?.Value,
            NumberStyles.Float, CultureInfo.InvariantCulture, out float declared))
            headroom = declared;
        if (headroom is not > 1 || !float.IsFinite(headroom.Value))
            return false;
        metadata = AppleMetadata(headroom.Value);
        return metadata.IsValid;
    }

    private static GainMap AppleMetadata(float headroom) => new(Vector3.Zero, new Vector3(MathF.Log2(headroom)),
        Vector3.One, Vector3.Zero, Vector3.Zero, 0, MathF.Log2(headroom), IsApple: true);

    private static bool TryReadIso(ReadOnlySpan<byte> data, out GainMap metadata)
    {
        metadata = default;
        var reader = new MetadataReader(data);
        if (reader.UInt16() != 0)
            return false;
        reader.UInt16();
        byte flags = reader.Byte();
        int channels = (flags & 0x80) != 0 ? 3 : 1;
        float baseHeadroom = reader.Rational(false);
        float alternateHeadroom = reader.Rational(false);
        Vector3 minimum = default, maximum = default, gamma = default, baseOffset = default, alternateOffset = default;
        for (int channel = 0; channel < channels; channel++)
        {
            minimum[channel] = reader.Rational(true);
            maximum[channel] = reader.Rational(true);
            gamma[channel] = reader.Rational(false);
            baseOffset[channel] = reader.Rational(true);
            alternateOffset[channel] = reader.Rational(true);
        }

        if (channels == 1)
        {
            minimum = new Vector3(minimum.X);
            maximum = new Vector3(maximum.X);
            gamma = new Vector3(gamma.X);
            baseOffset = new Vector3(baseOffset.X);
            alternateOffset = new Vector3(alternateOffset.X);
        }

        bool baseIsHdr = baseHeadroom > alternateHeadroom;
        metadata = new GainMap(minimum, maximum, gamma,
            baseIsHdr ? alternateOffset : baseOffset, baseIsHdr ? baseOffset : alternateOffset,
            Math.Min(baseHeadroom, alternateHeadroom), Math.Max(baseHeadroom, alternateHeadroom),
            baseIsHdr, (flags & 0x40) != 0);
        return reader.Valid && metadata.IsValid;
    }

    private ref struct MetadataReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> remaining = data;
        public bool Valid { get; private set; } = true;
        public int Remaining => remaining.Length;

        public ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || length > remaining.Length)
            {
                Valid = false;
                return default;
            }

            ReadOnlySpan<byte> result = remaining[..length];
            remaining = remaining[length..];
            return result;
        }

        public byte Byte() => Take(1) is { Length: 1 } value ? value[0] : (byte)0;
        public ushort UInt16() => Take(2) is { Length: 2 } value ? BinaryPrimitives.ReadUInt16BigEndian(value) : (ushort)0;
        public uint UInt32() => Take(4) is { Length: 4 } value ? BinaryPrimitives.ReadUInt32BigEndian(value) : 0;

        public ulong Unsigned(int length)
        {
            if (length is < 0 or > 8)
            {
                Valid = false;
                return 0;
            }

            ulong result = 0;
            foreach (byte value in Take(length))
                result = (result << 8) | value;
            return result;
        }

        public float Rational(bool signed)
        {
            uint numerator = UInt32();
            uint denominator = UInt32();
            if (denominator == 0)
            {
                Valid = false;
                return float.NaN;
            }

            return (float)((signed ? (double)unchecked((int)numerator) : numerator) / denominator);
        }
    }
}