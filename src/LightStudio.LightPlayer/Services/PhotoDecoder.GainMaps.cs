using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;

namespace LightStudio.LightPlayer.Services;

// Gain-map metadata and JPEG container parsing. Free of rendering dependencies, so the browser's media
// worker compiles this file and its sibling parsers too.
internal static unsafe partial class PhotoDecoder
{
    private static readonly XNamespace GainMapNamespace = "http://ns.adobe.com/hdr-gain-map/1.0/";
    private static readonly XNamespace RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    /// <summary>Finds an Ultra HDR or ISO 21496 gain map stored as a JPEG after the primary JPEG.</summary>
    private static bool TryFindJpegGainMap(Stream stream, out ImageRange primary, out ImageRange gain, out GainMap metadata)
    {
        primary = gain = default;
        metadata = default;
        long origin = stream.Position;
        if (!ReadHeader(stream, stream.Length, out XElement? primaryMetadata, out ImageRange[]? images, out _))
            return false;
        images ??= ReadContainerImages(stream, origin, primaryMetadata);
        if (images is not { Length: >= 2 })
            return false;

        foreach (ImageRange image in images.AsSpan(1))
        {
            stream.Position = image.Offset;
            if (!ReadHeader(stream, image.Offset + image.Length, out XElement? imageMetadata, out _, out byte[]? iso) ||
                !(iso is not null && TryReadIso(iso, out metadata)) &&
                !(imageMetadata is not null && TryReadJpegMetadata(imageMetadata, stream, out metadata)))
            {
                continue;
            }

            primary = images[0];
            gain = image;
            return true;
        }

        return false;
    }

    /// <summary>Extends a JPEG range that stops short of its end-of-image marker to where the marker is.</summary>
    private static ImageRange TrimJpegRange(Stream stream, ImageRange image)
    {
        stream.Position = image.Offset + image.Length - 2;
        if (stream.ReadByte() == 0xff && stream.ReadByte() == 0xd9)
            return image;
        long end = FindJpegEnd(stream, image.Offset);
        return end > image.Offset && end - image.Offset <= int.MaxValue ? new ImageRange(image.Offset, (int)(end - image.Offset)) : image;
    }

    private static bool ReadHeader(
        Stream stream,
        long end,
        out XElement? metadata,
        out ImageRange[]? images,
        out byte[]? iso,
        List<byte[]>? thumbnails = null)
    {
        metadata = null;
        images = null;
        iso = null;
        long origin = stream.Position;
        if (end - origin < 2 || stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8)
            return false;

        while (stream.Position + 1 < end)
        {
            if (stream.ReadByte() != 0xff)
                return false;
            int marker;
            do
            {
                marker = stream.ReadByte();
            }
            while (marker == 0xff && stream.Position < end);

            if (marker == 0xda)
                return true;
            if (marker is <= 0 or 0xd8 or 0xd9)
                return false;
            if (marker is 1 or >= 0xd0 and <= 0xd7)
                continue;
            if (stream.Position + 2 > end)
                return false;

            int length = (stream.ReadByte() << 8) | stream.ReadByte();
            long payloadStart = stream.Position;
            if (length < 2 || length - 2 > end - payloadStart)
                return false;

            if (marker is not (0xe1 or 0xe2) && !(marker == 0xe0 && thumbnails is not null))
            {
                stream.Position += length - 2;
                continue;
            }

            byte[] payload = new byte[length - 2];
            stream.ReadExactly(payload);
            if (marker == 0xe0 && thumbnails is not null && ReadJfifThumbnail(payload) is { } thumbnail)
            {
                if (thumbnails.Count < 16)
                    thumbnails.Add(thumbnail);
            }
            else if (marker == 0xe1 && payload.AsSpan().StartsWith("http://ns.adobe.com/xap/1.0/\0"u8))
            {
                metadata ??= ReadMetadata(payload);
            }
            else if (marker == 0xe2 && payload.AsSpan().StartsWith("MPF\0"u8))
            {
                images ??= ReadImages(payload.AsSpan(4), payloadStart + 4, origin, end);
            }
            else if (marker == 0xe2 && payload.AsSpan().StartsWith("urn:iso:std:iso:ts:21496:-1\0"u8))
            {
                iso ??= payload["urn:iso:std:iso:ts:21496:-1\0"u8.Length..];
            }
        }

        return false;
    }

    private static XElement? ReadMetadata(byte[] payload)
    {
        try
        {
            using var stream = new MemoryStream(payload, 29, payload.Length - 29, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 65536,
            });
            return XDocument.Load(reader).Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static ImageRange[]? ReadImages(ReadOnlySpan<byte> data, long tiffOrigin, long jpegOrigin, long end)
    {
        if (data.Length < 8 || (!data.StartsWith("II*\0"u8) && !data.StartsWith("MM\0*"u8)))
            return null;

        bool bigEndian = data[0] == 'M';
        uint directoryOffset = ReadUInt32(data[4..], bigEndian);
        if (directoryOffset > data.Length - 2)
            return null;
        int directory = (int)directoryOffset;
        int entries = ReadUInt16(data[directory..], bigEndian);
        if (entries > (data.Length - directory - 2) / 12)
            return null;

        for (int index = 0; index < entries; index++)
        {
            ReadOnlySpan<byte> entry = data.Slice(directory + 2 + (index * 12), 12);
            if (ReadUInt16(entry, bigEndian) != 0xb002 || ReadUInt16(entry[2..], bigEndian) != 7)
                continue;

            uint length = ReadUInt32(entry[4..], bigEndian);
            uint offset = ReadUInt32(entry[8..], bigEndian);
            if (length < 32 || length % 16 != 0 || offset > data.Length || length > data.Length - offset)
                return null;

            var images = new ImageRange[length / 16];
            for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
            {
                ReadOnlySpan<byte> image = data.Slice((int)offset + (imageIndex * 16), 16);
                uint imageLength = ReadUInt32(image[4..], bigEndian);
                uint imageOffset = ReadUInt32(image[8..], bigEndian);
                long absolute = imageIndex == 0 ? jpegOrigin : tiffOrigin + imageOffset;
                if ((imageIndex == 0 && imageOffset != 0) || imageLength < 4 || imageLength > int.MaxValue ||
                    absolute < jpegOrigin || absolute > end || imageLength > end - absolute)
                {
                    return null;
                }

                images[imageIndex] = new ImageRange(absolute, (int)imageLength)
                    { Type = ReadUInt32(image, bigEndian) & 0xffffff };
            }

            return images;
        }

        return null;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadUInt16BigEndian(data)
        : BinaryPrimitives.ReadUInt16LittleEndian(data);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadUInt32BigEndian(data)
        : BinaryPrimitives.ReadUInt32LittleEndian(data);

    private static string? Value(XElement metadata, string name) =>
        metadata.Attribute(GainMapNamespace + name)?.Value ?? metadata.Element(GainMapNamespace + name)?.Value;

    private readonly record struct ImageRange(long Offset, int Length)
    {
        public uint Type { get; init; }
    }

    private readonly record struct GainMap(Vector3 Minimum, Vector3 Maximum, Vector3 Gamma,
        Vector3 OffsetSdr, Vector3 OffsetHdr, float CapacityMin, float CapacityMax,
        bool BaseIsHdr = false, bool UseBaseColorSpace = true, bool IsApple = false)
    {
        public static bool TryRead(XElement metadata, out GainMap gainMap)
        {
            gainMap = default;
            if (!bool.TryParse(Value(metadata, "BaseRenditionIsHDR") ?? "False", out bool baseIsHdr) ||
                !ReadVector(metadata, "GainMapMin", 0, out Vector3 minimum) ||
                !ReadVector(metadata, "GainMapMax", null, out Vector3 maximum) ||
                !ReadVector(metadata, "Gamma", 1, out Vector3 gamma) ||
                !ReadVector(metadata, "OffsetSDR", 1f / 64, out Vector3 offsetSdr) ||
                !ReadVector(metadata, "OffsetHDR", 1f / 64, out Vector3 offsetHdr) ||
                !ReadNumber(Value(metadata, "HDRCapacityMin") ?? "0", out float capacityMin) ||
                !ReadNumber(Value(metadata, "HDRCapacityMax"), out float capacityMax) ||
                capacityMin < 0 || capacityMax <= capacityMin)
            {
                return false;
            }

            gainMap = new GainMap(minimum, maximum, gamma, offsetSdr, offsetHdr, capacityMin, capacityMax, baseIsHdr);
            return gainMap.IsValid;
        }

        public bool IsValid
        {
            get
            {
                if (!float.IsFinite(CapacityMin) || !float.IsFinite(CapacityMax) ||
                    CapacityMin < 0 || CapacityMax <= CapacityMin || !float.IsFinite(MathF.Pow(2, CapacityMax)))
                    return false;

                for (int channel = 0; channel < 3; channel++)
                {
                    if (!float.IsFinite(Minimum[channel]) || !float.IsFinite(Maximum[channel]) ||
                        !float.IsFinite(Gamma[channel]) || Gamma[channel] <= 0 ||
                        !float.IsFinite(OffsetSdr[channel]) || !float.IsFinite(OffsetHdr[channel]) ||
                        !float.IsFinite(MathF.Pow(2, Maximum[channel])) || !float.IsFinite(MathF.Pow(2, Minimum[channel])) ||
                        MathF.Pow(2, Minimum[channel]) <= 0 || MathF.Pow(2, Maximum[channel]) <= 0)
                        return false;
                }

                return true;
            }
        }

        private static bool ReadVector(XElement metadata, string name, float? fallback, out Vector3 result)
        {
            result = default;
            XName qualified = GainMapNamespace + name;
            string[] values;
            if (metadata.Attribute(qualified) is { } attribute)
                values = [attribute.Value];
            else if (metadata.Element(qualified) is { } element)
                values = element.Element(RdfNamespace + "Seq") is { } sequence
                    ? sequence.Elements(RdfNamespace + "li").Select(item => item.Value).ToArray()
                    : [element.Value];
            else if (fallback is { } value)
            {
                result = new Vector3(value);
                return true;
            }
            else
                return false;

            if (values.Length is not (1 or 3))
                return false;
            for (int channel = 0; channel < 3; channel++)
            {
                if (!ReadNumber(values[values.Length == 1 ? 0 : channel], out float number))
                    return false;
                result[channel] = number;
            }

            return true;
        }

        private static bool ReadNumber(string? text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
    }
}
