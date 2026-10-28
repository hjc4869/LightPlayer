using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using TextEncoding = System.Text.Encoding;

namespace LightStudio.LightPlayer.Services.PhotoExport;

/// <summary>A JPEG split into its header marker segments and the scan data that follows them.</summary>
internal sealed class JpegSegments
{
    private JpegSegments(List<(byte Marker, byte[] Segment)> header, byte[] scan)
    {
        Header = header;
        Scan = scan;
    }

    /// <summary>Complete marker segments (marker bytes included) between SOI and the first SOS.</summary>
    public List<(byte Marker, byte[] Segment)> Header { get; }

    /// <summary>Everything from the first SOS marker to the end of the image.</summary>
    public byte[] Scan { get; }

    public static JpegSegments Parse(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("The encoder did not produce a JPEG stream.");

        var header = new List<(byte, byte[])>();
        int position = 2;
        while (position + 4 <= jpeg.Length)
        {
            if (jpeg[position] != 0xFF)
                throw new InvalidDataException("Malformed JPEG marker sequence.");
            byte marker = jpeg[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            if (marker == 0xDA)
                return new JpegSegments(header, jpeg[position..].ToArray());

            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(position + 2)..]);
            if (length < 2 || position + 2 + length > jpeg.Length)
                throw new InvalidDataException("Truncated JPEG marker segment.");
            header.Add((marker, jpeg.Slice(position, 2 + length).ToArray()));
            position += 2 + length;
        }

        throw new InvalidDataException("The JPEG stream has no scan.");
    }

    public static byte[] Segment(byte marker, ReadOnlySpan<byte> identifier, ReadOnlySpan<byte> payload)
    {
        int length = 2 + identifier.Length + payload.Length;
        if (length > ushort.MaxValue)
            throw new InvalidDataException("A JPEG marker segment cannot exceed 64 KiB.");

        var segment = new byte[2 + length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)length);
        identifier.CopyTo(segment.AsSpan(4));
        payload.CopyTo(segment.AsSpan(4 + identifier.Length));
        return segment;
    }

    public static bool IsApp(byte marker) => marker is >= 0xE0 and <= 0xEF;

    public static bool StartsWith(byte[] segment, ReadOnlySpan<byte> identifier) =>
        segment.Length >= 4 + identifier.Length && segment.AsSpan(4, identifier.Length).SequenceEqual(identifier);
}

/// <summary>
/// Writes an Ultra HDR / ISO 21496-1 JPEG: the SDR primary image followed by a gain map JPEG,
/// associated through the CIPA Multi-Picture Format.
/// </summary>
/// <remarks>
/// Both the ISO 21496-1 binary metadata and the Adobe <c>hdrgm</c> XMP are written, so readers of
/// either generation find the gain map. Marker order follows libultrahdr: JFIF, EXIF, XMP, ICC and the
/// ISO version block ahead of the tables, and the MPF segment directly before the scan. The primary
/// image's XMP packet carries the source's XMP alongside the gain map description.
/// </remarks>
internal static class UltraHdrJpegWriter
{
    private static readonly byte[] XmpIdentifier = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
    private static readonly byte[] IsoIdentifier = TextEncoding.ASCII.GetBytes(GainMapMetadata.IsoNamespace + "\0");
    private static readonly byte[] MpfIdentifier = "MPF\0"u8.ToArray();
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const int MpfPayloadLength = 8 + 2 + 3 * 12 + 4 + 2 * 16;

    public static void Write(Stream output, byte[] primary, byte[] gainJpeg, GainMapMetadata metadata,
        PhotoExportMetadataBlocks? sourceMetadata)
    {
        byte[] secondary = BuildSecondary(gainJpeg, metadata);
        WritePrimary(output, JpegSegments.Parse(primary), secondary, sourceMetadata);
        output.Write(secondary);
    }

    private static byte[] BuildSecondary(byte[] gainJpeg, GainMapMetadata metadata)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        stream.Write(JpegSegments.Segment(0xE1, XmpIdentifier, TextEncoding.UTF8.GetBytes(metadata.ToGainMapXmp())));
        stream.Write(JpegSegments.Segment(0xE2, IsoIdentifier, metadata.ToIsoBinary()));
        stream.Write(gainJpeg.AsSpan(2));
        return stream.ToArray();
    }

    private static void WritePrimary(Stream output, JpegSegments primary, byte[] secondary, PhotoExportMetadataBlocks? sourceMetadata)
    {
        XElement description = XElement.Parse(PrimaryXmp(secondary.Length)).Descendants(Rdf + "Description").First();
        byte[] iso = JpegSegments.Segment(0xE2, IsoIdentifier, [0, 0, 0, 0]);

        var ordered = new List<byte[]>();
        byte[]? jfif = primary.Header.FirstOrDefault(s => s.Marker == 0xE0 && JpegSegments.StartsWith(s.Segment, "JFIF\0"u8)).Segment;
        if (jfif is not null)
            ordered.Add(jfif);
        ordered.AddRange(PhotoExportMetadata.JpegMetadataSegments(sourceMetadata, description));
        ordered.AddRange(primary.Header
            .Where(s => s.Marker == 0xE2 && JpegSegments.StartsWith(s.Segment, "ICC_PROFILE\0"u8))
            .Select(s => s.Segment));
        ordered.Add(iso);
        ordered.AddRange(primary.Header.Where(s => !JpegSegments.IsApp(s.Marker)).Select(s => s.Segment));

        long headerLength = 2 + ordered.Sum(segment => (long)segment.Length);
        long mpfSegmentLength = 4 + MpfIdentifier.Length + MpfPayloadLength;
        long primaryLength = headerLength + mpfSegmentLength + primary.Scan.Length;
        long tiffHeader = headerLength + 4 + MpfIdentifier.Length;
        if (primaryLength + secondary.Length > uint.MaxValue)
            throw new InvalidDataException("The image is too large for the Multi-Picture Format.");

        output.Write([0xFF, 0xD8]);
        foreach (byte[] segment in ordered)
            output.Write(segment);
        output.Write(JpegSegments.Segment(0xE2, MpfIdentifier,
            MpfIndex((uint)primaryLength, (uint)secondary.Length, (uint)(primaryLength - tiffHeader))));
        output.Write(primary.Scan);
    }

    /// <summary>A big-endian MP Index IFD describing the primary image and one gain map image.</summary>
    private static byte[] MpfIndex(uint primaryLength, uint secondaryLength, uint secondaryOffset)
    {
        var payload = new byte[MpfPayloadLength];
        Span<byte> span = payload;
        "MM\0*"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], 8);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], 3);
        int entry = 10;
        WriteEntry(span, ref entry, 0xB000, 7, 4, BinaryPrimitives.ReadUInt32BigEndian("0100"u8));
        WriteEntry(span, ref entry, 0xB001, 4, 1, 2);
        const uint entriesOffset = 8 + 2 + 3 * 12 + 4;
        WriteEntry(span, ref entry, 0xB002, 7, 32, entriesOffset);
        BinaryPrimitives.WriteUInt32BigEndian(span[entry..], 0);

        int images = (int)entriesOffset;
        BinaryPrimitives.WriteUInt32BigEndian(span[images..], 0x030000);
        BinaryPrimitives.WriteUInt32BigEndian(span[(images + 4)..], primaryLength);
        BinaryPrimitives.WriteUInt32BigEndian(span[(images + 8)..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(span[(images + 16)..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(span[(images + 20)..], secondaryLength);
        BinaryPrimitives.WriteUInt32BigEndian(span[(images + 24)..], secondaryOffset);
        return payload;

        static void WriteEntry(Span<byte> target, ref int offset, ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(target[offset..], tag);
            BinaryPrimitives.WriteUInt16BigEndian(target[(offset + 2)..], type);
            BinaryPrimitives.WriteUInt32BigEndian(target[(offset + 4)..], count);
            BinaryPrimitives.WriteUInt32BigEndian(target[(offset + 8)..], value);
            offset += 12;
        }
    }

    private static string PrimaryXmp(int gainMapLength) =>
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"Light Player\">" +
        "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\"" +
        " xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\"" +
        " xmlns:Container=\"http://ns.google.com/photos/1.0/container/\"" +
        " xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\"" +
        " hdrgm:Version=\"1.0\">" +
        "<Container:Directory><rdf:Seq>" +
        "<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Semantic=\"Primary\" Item:Mime=\"image/jpeg\"/></rdf:li>" +
        "<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\"" +
        string.Create(CultureInfo.InvariantCulture, $" Item:Length=\"{gainMapLength}\"/></rdf:li>") +
        "</rdf:Seq></Container:Directory>" +
        "</rdf:Description></rdf:RDF></x:xmpmeta>";
}
