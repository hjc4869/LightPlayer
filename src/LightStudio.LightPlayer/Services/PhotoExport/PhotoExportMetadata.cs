using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Services.PhotoExport;

/// <summary>A TIFF field as stored: its tag, type, count, and value bytes in the block's byte order.</summary>
/// <param name="Offset">Where an out-of-line value was stored in the source block, or zero.</param>
internal sealed record PhotoExportExifField(ushort Tag, ushort Type, uint Count, byte[] Value, long Offset = 0);

/// <summary>The EXIF directories of a source photo.</summary>
internal sealed class PhotoExportExif
{
    public required bool LittleEndian { get; init; }

    /// <summary>IFD0.</summary>
    public required IReadOnlyList<PhotoExportExifField> Image { get; init; }

    /// <summary>The Exif IFD.</summary>
    public required IReadOnlyList<PhotoExportExifField> Photo { get; init; }

    /// <summary>The GPS IFD.</summary>
    public required IReadOnlyList<PhotoExportExifField> Location { get; init; }
}

/// <summary>The EXIF and XMP metadata of a source photo.</summary>
internal sealed class PhotoExportSourceMetadata
{
    public PhotoExportExif? Exif { get; init; }

    /// <summary>The <c>x:xmpmeta</c> element, including any extended XMP.</summary>
    public XElement? Xmp { get; init; }
}

/// <summary>Source metadata adapted to a saved picture.</summary>
internal sealed class PhotoExportMetadataBlocks
{
    /// <summary>A TIFF block.</summary>
    public byte[]? Exif { get; init; }

    /// <summary><see cref="Exif"/> without its maker note, for containers with little room.</summary>
    public byte[]? CompactExif { get; init; }

    /// <summary>The <c>x:xmpmeta</c> element.</summary>
    public XElement? Xmp { get; init; }

    public byte[]? XmpPacket => Xmp is null ? null : PhotoExportMetadata.Serialize(Xmp, wrapped: true);
}

/// <summary>
/// Reads EXIF and XMP from the containers photos are opened from, and adapts them to a saved picture: the
/// picture is upright, in the export's color space and size, and has no thumbnail, so tags describing the
/// original encoding are replaced or dropped. Rendering mode keeps only orientation, color space and
/// resolution, which removes location, camera and descriptive details.
/// </summary>
internal static class PhotoExportMetadata
{
    private const int MaxFieldBytes = 4 * 1024 * 1024;
    private const int MaxMetadataBytes = 16 * 1024 * 1024;
    private const int MaxJpegPayload = ushort.MaxValue - 2;
    private const int MaxStandardXmp = 65502;
    private const int ExtendedXmpChunk = 65400;

    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeUndefined = 7;
    private const ushort TypeIfd = 13;

    private const ushort TagOrientation = 0x0112;
    private const ushort TagYCbCrPositioning = 0x0213;
    private const ushort TagXmp = 0x02BC;
    private const ushort TagExifIfd = 0x8769;
    private const ushort TagGpsIfd = 0x8825;
    private const ushort TagExifVersion = 0x9000;
    private const ushort TagComponentsConfiguration = 0x9101;
    private const ushort TagMakerNote = 0x927C;
    private const ushort TagColorSpace = 0xA001;
    private const ushort TagPixelXDimension = 0xA002;
    private const ushort TagPixelYDimension = 0xA003;

    private static readonly byte[] ExifIdentifier = "Exif\0\0"u8.ToArray();
    private static readonly byte[] XmpIdentifier = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
    private static readonly byte[] ExtendedXmpIdentifier = "http://ns.adobe.com/xmp/extension/\0"u8.ToArray();

    private static readonly XNamespace Meta = "adobe:ns:meta/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace XmpNote = "http://ns.adobe.com/xmp/note/";
    private static readonly XNamespace Tiff = "http://ns.adobe.com/tiff/1.0/";
    private static readonly XNamespace Exif = "http://ns.adobe.com/exif/1.0/";
    private static readonly XNamespace ExifEx = "http://cipa.jp/exif/1.0/";
    private static readonly XNamespace Photoshop = "http://ns.adobe.com/photoshop/1.0/";
    private static readonly XNamespace GoogleCamera = "http://ns.google.com/photos/1.0/camera/";

    /// <summary>IFD0 tags describing the photo rather than how its pixels are stored.</summary>
    private static readonly HashSet<ushort> DescriptiveImageTags =
    [
        0x010D, 0x010E, 0x010F, 0x0110, 0x011A, 0x011B, 0x011D, 0x0128, 0x0131, 0x0132, 0x013B, 0x013C,
        0x4746, 0x4749, 0x8298, 0x9C9B, 0x9C9C, 0x9C9D, 0x9C9E, 0x9C9F,
    ];

    /// <summary>IFD0 tags affecting how a picture is shown: its resolution.</summary>
    private static readonly HashSet<ushort> RenderingImageTags = [0x011A, 0x011B, 0x0128];

    /// <summary>
    /// Exif IFD tags describing the original encoding (components, compression, color space, size, gamma)
    /// or pointing at directories that are not carried over (interoperability, Microsoft padding).
    /// </summary>
    private static readonly HashSet<ushort> EncodingPhotoTags =
        [TagComponentsConfiguration, 0x9102, TagColorSpace, TagPixelXDimension, TagPixelYDimension, 0xA005, 0xA500, TagMakerNote,
            0xEA1C, 0xEA1D];

    /// <summary>XMP schemas that only describe the original file: gain maps, embedded images, and depth maps.</summary>
    private static readonly HashSet<XNamespace> FileSchemas =
    [
        "http://ns.adobe.com/hdr-gain-map/1.0/",
        "http://ns.google.com/photos/1.0/container/",
        "http://ns.google.com/photos/1.0/container/item/",
        "http://ns.google.com/photos/1.0/depthmap/",
        "http://ns.google.com/photos/1.0/image/",
        "http://ns.apple.com/HDRGainMap/1.0/",
        "http://ns.apple.com/pixeldatainfo/1.0/",
    ];

    /// <summary>XMP properties describing the original encoding, orientation or appended data.</summary>
    private static readonly HashSet<XName> FileProperties =
    [
        XmpNote + "HasExtendedXMP",
        Tiff + "Orientation", Tiff + "ImageWidth", Tiff + "ImageLength", Tiff + "BitsPerSample", Tiff + "Compression",
        Tiff + "PhotometricInterpretation", Tiff + "SamplesPerPixel", Tiff + "PlanarConfiguration",
        Tiff + "YCbCrSubSampling", Tiff + "YCbCrPositioning", Tiff + "YCbCrCoefficients", Tiff + "TransferFunction",
        Tiff + "WhitePoint", Tiff + "PrimaryChromaticities", Tiff + "ReferenceBlackWhite", Tiff + "NativeDigest",
        Exif + "PixelXDimension", Exif + "PixelYDimension", Exif + "ColorSpace", Exif + "Gamma",
        Exif + "ComponentsConfiguration", Exif + "CompressedBitsPerPixel", Exif + "NativeDigest",
        ExifEx + "Gamma", Photoshop + "ICCProfile",
        GoogleCamera + "MotionPhoto", GoogleCamera + "MotionPhotoVersion", GoogleCamera + "MotionPhotoPresentationTimestampUs",
        GoogleCamera + "MicroVideo", GoogleCamera + "MicroVideoVersion", GoogleCamera + "MicroVideoOffset",
        GoogleCamera + "MicroVideoPresentationTimestampUs",
    ];

    /// <summary>Schemas JPEG readers look for in the standard XMP packet, which stay out of extended XMP.</summary>
    private static readonly HashSet<XNamespace> StandardPacketSchemas =
    [
        "http://ns.adobe.com/hdr-gain-map/1.0/",
        "http://ns.google.com/photos/1.0/container/",
        "http://ns.google.com/photos/1.0/container/item/",
        XmpNote,
    ];

    /// <summary>Reads the source's EXIF and XMP, or nothing for containers without them.</summary>
    /// <exception cref="InvalidDataException">The metadata is malformed.</exception>
    public static PhotoExportSourceMetadata Read(Stream stream, CancellationToken cancellationToken)
    {
        Span<byte> head = stackalloc byte[16];
        long origin = stream.Position;
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = origin;
        head = head[..read];
        byte[]? exif = null;
        List<byte[]> xmp = [];
        PhotoExportExif? tiff = null;
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xD8)
            ReadJpeg(stream, ref exif, xmp, cancellationToken);
        else if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            ReadPng(stream, ref exif, xmp, cancellationToken);
        else if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            ReadRiff(stream, ref exif, xmp, cancellationToken);
        else if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
        {
            (byte[] item, byte[] packet) = PhotoDecoder.ReadIsoMetadata(stream, cancellationToken);
            exif = ExifItem(item);
            if (packet.Length > 0)
                xmp.Add(packet);
        }
        else if (head.Length >= 12 && head[4..8].SequenceEqual("JXL "u8))
            ReadJxl(stream, ref exif, xmp, cancellationToken);
        else if (head.Length >= 8 && (head.StartsWith("II*\0"u8) || head.StartsWith("MM\0*"u8) ||
            head.StartsWith((ReadOnlySpan<byte>)[0x49, 0x49, 0xBC, 0x01])))
        {
            tiff = ReadExif(new TiffReader(stream, null));
            if (tiff?.Image.FirstOrDefault(field => field.Tag == TagXmp) is { } packet)
                xmp.Add(packet.Value);
        }

        if (exif is { Length: > 0 })
            tiff = ReadExif(new TiffReader(null, exif));
        return new PhotoExportSourceMetadata { Exif = tiff, Xmp = ParseXmp(xmp) };
    }

    /// <summary>Adapts source metadata to a saved picture, or returns null when nothing is carried.</summary>
    /// <param name="jpeg">Whether the picture is stored as JPEG, whose EXIF describes its YCbCr components.</param>
    public static PhotoExportMetadataBlocks? Adapt(PhotoExportSourceMetadata? source, PhotoExportMetadataMode mode,
        int width, int height, bool srgb, bool jpeg)
    {
        if (source is null)
            return null;
        byte[]? exif = source.Exif is { } fields ? WriteExif(fields, mode, width, height, srgb, jpeg, makerNote: true) : null;
        byte[]? compact = source.Exif is { } compactFields && mode == PhotoExportMetadataMode.All &&
            compactFields.Photo.Any(field => field.Tag == TagMakerNote)
            ? WriteExif(compactFields, mode, width, height, srgb, jpeg, makerNote: false)
            : exif;
        XElement? xmp = mode == PhotoExportMetadataMode.All && source.Xmp is { } packet ? Clean(packet) : null;
        return exif is null && xmp is null ? null : new PhotoExportMetadataBlocks { Exif = exif, CompactExif = compact, Xmp = xmp };
    }

    /// <summary>
    /// APP1 segments carrying the metadata of a JPEG: EXIF, then XMP with <paramref name="addition"/> merged
    /// in, splitting XMP that exceeds one segment into extended XMP.
    /// </summary>
    /// <param name="addition">An <c>rdf:Description</c> for the standard XMP packet.</param>
    public static List<byte[]> JpegMetadataSegments(PhotoExportMetadataBlocks? blocks, XElement? addition)
    {
        var segments = new List<byte[]>();
        byte[]? exif = blocks?.Exif;
        if (exif is not null && ExifIdentifier.Length + exif.Length > MaxJpegPayload)
            exif = blocks!.CompactExif;
        if (exif is not null && ExifIdentifier.Length + exif.Length > MaxJpegPayload)
        {
            AppLog.Warning("Photo.Export", $"EXIF of {exif.Length} bytes does not fit a JPEG segment; it is not saved.");
            exif = null;
        }

        if (exif is not null)
            segments.Add(JpegSegments.Segment(0xE1, ExifIdentifier, exif));

        XElement? xmp = blocks?.Xmp is { } source ? new XElement(source) : null;
        if (addition is not null)
        {
            xmp ??= new XElement(Meta + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", Meta.NamespaceName),
                new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName)));
            xmp.Element(Rdf + "RDF")!.Add(new XElement(addition));
        }

        if (xmp is not null)
            segments.AddRange(XmpSegments(xmp));
        return segments;
    }

    public static byte[] Serialize(XElement root, bool wrapped)
    {
        string body = root.ToString(SaveOptions.DisableFormatting | SaveOptions.OmitDuplicateNamespaces);
        return Encoding.UTF8.GetBytes(wrapped
            ? "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" + body + "<?xpacket end=\"w\"?>"
            : body);
    }

    private static void ReadJpeg(Stream stream, ref byte[]? exif, List<byte[]> xmp, CancellationToken cancellationToken)
    {
        var extended = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        Span<byte> header = stackalloc byte[4];
        stream.Position += 2;
        while (stream.ReadAtLeast(header, 4, throwOnEndOfStream: false) == 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (header[0] != 0xFF)
                break;
            if (header[1] == 0xFF)
            {
                stream.Position -= 3;
                continue;
            }

            if (header[1] is 0x01 or >= 0xD0 and <= 0xD7)
            {
                stream.Position -= 2;
                continue;
            }

            if (header[1] is 0xD9 or 0xDA)
                break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(header[2..]) - 2;
            if (length < 0)
                break;
            if (header[1] != 0xE1)
            {
                stream.Position += length;
                continue;
            }

            total += length;
            if (total > MaxMetadataBytes)
                throw new InvalidDataException("The photo metadata exceeds 16 MiB.");
            byte[] payload = new byte[length];
            stream.ReadExactly(payload);
            if (exif is null && payload.AsSpan().StartsWith("Exif\0"u8) && payload.Length > 6)
                exif = payload[6..];
            else if (payload.AsSpan().StartsWith(XmpIdentifier))
                xmp.Insert(0, payload[XmpIdentifier.Length..]);
            else if (payload.AsSpan().StartsWith(ExtendedXmpIdentifier) && payload.Length > ExtendedXmpIdentifier.Length + 40)
                AddExtendedXmp(extended, payload.AsSpan(ExtendedXmpIdentifier.Length));
        }

        xmp.AddRange(extended.Values);
    }

    /// <summary>Places one chunk of extended XMP, identified by GUID, at its offset in the full packet.</summary>
    private static void AddExtendedXmp(Dictionary<string, byte[]> packets, ReadOnlySpan<byte> chunk)
    {
        string guid = Encoding.ASCII.GetString(chunk[..32]);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(chunk[32..]);
        uint offset = BinaryPrimitives.ReadUInt32BigEndian(chunk[36..]);
        ReadOnlySpan<byte> data = chunk[40..];
        if (length > MaxMetadataBytes || offset > length || data.Length > length - offset)
            throw new InvalidDataException("Malformed extended XMP chunk.");
        if (!packets.TryGetValue(guid, out byte[]? packet))
            packets.Add(guid, packet = new byte[length]);
        else if (packet.Length != length)
            throw new InvalidDataException("Inconsistent extended XMP length.");
        data.CopyTo(packet.AsSpan((int)offset));
    }

    private static void ReadPng(Stream stream, ref byte[]? exif, List<byte[]> xmp, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[8];
        stream.Position += 8;
        long total = 0;
        while (stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) == 8)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            ReadOnlySpan<byte> type = header[4..];
            if (type.SequenceEqual("IEND"u8) || length > stream.Length - stream.Position)
                break;
            bool text = type.SequenceEqual("iTXt"u8) || type.SequenceEqual("zTXt"u8) || type.SequenceEqual("tEXt"u8);
            if (!type.SequenceEqual("eXIf"u8) && !text)
            {
                stream.Position += length + 4L;
                continue;
            }

            total += length;
            if (total > MaxMetadataBytes)
                throw new InvalidDataException("The photo metadata exceeds 16 MiB.");
            byte[] payload = new byte[length];
            stream.ReadExactly(payload);
            stream.Position += 4;
            if (!text)
            {
                exif ??= payload.AsSpan().StartsWith("Exif\0"u8) && payload.Length > 6 ? payload[6..] : payload;
                continue;
            }

            (string keyword, byte[] value) = ReadPngText(type, payload);
            if (keyword == "XML:com.adobe.xmp")
                xmp.Insert(0, value);
            else if (keyword is "Raw profile type exif" or "Raw profile type APP1" && exif is null &&
                RawProfile(value) is { } profile)
                exif = profile.AsSpan().StartsWith("Exif\0"u8) && profile.Length > 6 ? profile[6..] : profile;
            else if (keyword == "Raw profile type xmp" && xmp.Count == 0 && RawProfile(value) is { } packet)
                xmp.Add(packet);
        }
    }

    /// <summary>The keyword and decompressed text of a PNG text chunk.</summary>
    private static (string Keyword, byte[] Text) ReadPngText(ReadOnlySpan<byte> type, byte[] payload)
    {
        int end = Array.IndexOf(payload, (byte)0);
        if (end <= 0)
            return (string.Empty, []);
        string keyword = Encoding.Latin1.GetString(payload, 0, end);
        if (type.SequenceEqual("tEXt"u8))
            return (keyword, payload[(end + 1)..]);
        if (type.SequenceEqual("zTXt"u8))
            return (keyword, Inflate(payload.AsSpan(Math.Min(end + 2, payload.Length))));
        if (payload.Length < end + 3)
            return (keyword, []);
        bool compressed = payload[end + 1] != 0;
        int language = Array.IndexOf(payload, (byte)0, end + 3);
        int translated = language < 0 ? -1 : Array.IndexOf(payload, (byte)0, language + 1);
        if (translated < 0)
            return (keyword, []);
        ReadOnlySpan<byte> text = payload.AsSpan(translated + 1);
        return (keyword, compressed ? Inflate(text) : text.ToArray());
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = zlib.Read(buffer)) > 0)
        {
            if (output.Length + count > MaxMetadataBytes)
                throw new InvalidDataException("The photo metadata exceeds 16 MiB.");
            output.Write(buffer, 0, count);
        }

        return output.ToArray();
    }

    /// <summary>Decodes ImageMagick's hexadecimal raw profile text: a name line, a length line, then hex digits.</summary>
    private static byte[]? RawProfile(byte[] text)
    {
        string[] lines = Encoding.Latin1.GetString(text).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 3 || !int.TryParse(lines[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int length) ||
            length is <= 0 or > MaxMetadataBytes)
            return null;
        string digits = string.Concat(lines.Skip(2)).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (digits.Length < length * 2)
            return null;
        try
        {
            return Convert.FromHexString(digits.AsSpan(0, length * 2));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Malformed raw PNG metadata profile.", exception);
        }
    }

    private static void ReadRiff(Stream stream, ref byte[]? exif, List<byte[]> xmp, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[8];
        stream.Position += 4;
        stream.ReadExactly(header[..4]);
        long end = Math.Min(stream.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(header));
        stream.Position += 4;
        while (stream.Position <= end - 8 && stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) == 8)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (length > end - stream.Position)
                break;
            bool isExif = header[..4].SequenceEqual("EXIF"u8), isXmp = header[..4].SequenceEqual("XMP "u8);
            if ((isExif || isXmp) && length <= MaxMetadataBytes)
            {
                byte[] payload = new byte[length];
                stream.ReadExactly(payload);
                if (isExif)
                    exif ??= payload.AsSpan().StartsWith("Exif\0"u8) && payload.Length > 6 ? payload[6..] : payload;
                else if (xmp.Count == 0)
                    xmp.Add(payload);
                stream.Position += length & 1;
            }
            else
            {
                stream.Position += length + (length & 1);
            }
        }
    }

    private static void ReadJxl(Stream stream, ref byte[]? exif, List<byte[]> xmp, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[16];
        while (stream.Position <= stream.Length - 8)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long start = stream.Position;
            stream.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            int headerSize = 8;
            if (size == 1)
            {
                stream.ReadExactly(header[8..16]);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0)
                size = stream.Length - start;
            if (size < headerSize || size > stream.Length - start)
                break;
            long length = size - headerSize;
            uint inner = type;
            byte[]? payload = null;
            if (type == BoxType("brob"u8) && length is > 4 and <= MaxMetadataBytes)
            {
                stream.ReadExactly(header[..4]);
                inner = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (inner == BoxType("Exif"u8) || inner == BoxType("xml "u8))
                {
                    byte[] compressed = new byte[length - 4];
                    stream.ReadExactly(compressed);
                    payload = PhotoBrotli.Decode(compressed, MaxMetadataBytes, cancellationToken)
                        ?? throw new InvalidDataException("Malformed compressed JPEG XL metadata box.");
                }
            }
            else if ((type == BoxType("Exif"u8) || type == BoxType("xml "u8)) && length <= MaxMetadataBytes)
            {
                payload = new byte[length];
                stream.ReadExactly(payload);
            }

            if (payload is not null && inner == BoxType("Exif"u8))
                exif ??= ExifItem(payload);
            else if (payload is not null && xmp.Count == 0)
                xmp.Add(payload);
            stream.Position = start + size;
        }
    }

    private static uint BoxType(ReadOnlySpan<byte> name) => BinaryPrimitives.ReadUInt32BigEndian(name);

    /// <summary>The TIFF block of an ISO base media EXIF item, after its header offset.</summary>
    private static byte[]? ExifItem(byte[] item)
    {
        if (item.Length < 4)
            return null;
        uint offset = BinaryPrimitives.ReadUInt32BigEndian(item);
        if (offset > item.Length - 4)
            return null;
        ReadOnlySpan<byte> block = item.AsSpan(4 + (int)offset);
        if (block.StartsWith("Exif\0"u8) && block.Length > 6)
            block = block[6..];
        return block.ToArray();
    }

    private static PhotoExportExif? ReadExif(TiffReader reader)
    {
        if (!reader.IsValid)
            return null;
        List<PhotoExportExifField> image = reader.Directory(reader.First, tag =>
            DescriptiveImageTags.Contains(tag) || tag is TagOrientation or TagXmp or TagExifIfd or TagGpsIfd);
        if (image.Count == 0)
            return null;
        return new PhotoExportExif
        {
            LittleEndian = reader.LittleEndian,
            Image = image,
            Photo = Pointer(TagExifIfd) is { } photo ? reader.Directory(photo) : [],
            Location = Pointer(TagGpsIfd) is { } location ? reader.Directory(location) : [],
        };

        uint? Pointer(ushort tag) => image.FirstOrDefault(field => field.Tag == tag && field.Value.Length == 4 &&
            field.Type is TypeLong or TypeIfd) is { } field ? reader.UInt32(field.Value) : null;
    }

    private static XElement? ParseXmp(List<byte[]> packets)
    {
        XElement? root = null;
        foreach (byte[] packet in packets)
        {
            XElement? parsed = ParseXmpPacket(packet);
            if (parsed?.Element(Rdf + "RDF") is not { } rdf)
                continue;
            if (root is null)
                root = parsed;
            else
                root.Element(Rdf + "RDF")!.Add(rdf.Elements(Rdf + "Description"));
        }

        return root;
    }

    /// <summary>The packet as an <c>x:xmpmeta</c> element.</summary>
    private static XElement? ParseXmpPacket(byte[] packet)
    {
        int end = packet.Length;
        while (end > 0 && packet[end - 1] is 0 or (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t')
            end--;
        if (end == 0)
            return null;
        try
        {
            using var stream = new MemoryStream(packet, 0, end, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxMetadataBytes,
            });
            XElement? root = XDocument.Load(reader).Root;
            XElement? rdf = root?.Name == Rdf + "RDF" ? root : root?.Descendants(Rdf + "RDF").FirstOrDefault();
            if (rdf is null)
                return null;
            return root!.Name == Meta + "xmpmeta" && rdf.Parent == root
                ? root
                : new XElement(Meta + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", Meta.NamespaceName), rdf);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("Malformed XMP packet.", exception);
        }
    }

    /// <summary>A copy without the properties that describe the original file rather than the photo.</summary>
    private static XElement? Clean(XElement source)
    {
        var root = new XElement(source);
        XElement rdf = root.Element(Rdf + "RDF")!;
        foreach (XElement description in rdf.Elements(Rdf + "Description").ToArray())
        {
            foreach (XAttribute attribute in description.Attributes().ToArray())
            {
                if (!attribute.IsNamespaceDeclaration && IsFileProperty(attribute.Name))
                    attribute.Remove();
            }

            foreach (XElement property in description.Elements().ToArray())
            {
                if (IsFileProperty(property.Name))
                    property.Remove();
            }

            if (!description.HasElements && !description.Attributes().Any(attribute =>
                    !attribute.IsNamespaceDeclaration && attribute.Name != Rdf + "about"))
                description.Remove();
        }

        return rdf.HasElements ? root : null;

        static bool IsFileProperty(XName name) => FileSchemas.Contains(name.Namespace) || FileProperties.Contains(name);
    }

    /// <summary>
    /// The standard XMP segment and, when the packet does not fit one, extended XMP segments holding the
    /// largest properties, linked by the MD5 digest of the extended packet.
    /// </summary>
    private static IEnumerable<byte[]> XmpSegments(XElement root)
    {
        byte[] packet = Serialize(root, wrapped: true);
        if (packet.Length <= MaxStandardXmp)
            return [JpegSegments.Segment(0xE1, XmpIdentifier, packet)];

        XElement rdf = root.Element(Rdf + "RDF")!;
        var moved = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", string.Empty));
        var candidates = new List<XObject>();
        foreach (XElement description in rdf.Elements(Rdf + "Description"))
        {
            foreach (XAttribute declaration in new[] { root, rdf, description }.SelectMany(element => element.Attributes())
                .Where(attribute => attribute.IsNamespaceDeclaration))
            {
                if (moved.Attribute(declaration.Name) is null)
                    moved.Add(new XAttribute(declaration));
            }

            candidates.AddRange(description.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration &&
                attribute.Name != Rdf + "about" && !StandardPacketSchemas.Contains(attribute.Name.Namespace)));
            candidates.AddRange(description.Elements().Where(element => !StandardPacketSchemas.Contains(element.Name.Namespace)));
        }

        XElement first = rdf.Element(Rdf + "Description") ?? new XElement(Rdf + "Description", new XAttribute(Rdf + "about", string.Empty));
        if (first.Parent is null)
            rdf.Add(first);
        // Reserves room for the digest, which depends on what moves.
        var digest = new XAttribute(XmpNote + "HasExtendedXMP", new string('0', 32));
        if (first.GetPrefixOfNamespace(XmpNote) is null)
            first.Add(new XAttribute(XNamespace.Xmlns + "xmpNote", XmpNote.NamespaceName));
        first.Add(digest);
        packet = Serialize(root, wrapped: true);
        List<XObject> ordered = candidates.OrderByDescending(Size).ToList();
        int next = 0;
        while (packet.Length > MaxStandardXmp && next < ordered.Count)
        {
            // Moves the largest properties until the estimate fits, then measures again.
            for (long excess = packet.Length - MaxStandardXmp; excess > 0 && next < ordered.Count; next++)
            {
                XObject candidate = ordered[next];
                excess -= Size(candidate);
                if (candidate is XAttribute attribute)
                {
                    attribute.Remove();
                    moved.Add(new XAttribute(attribute.Name, attribute.Value));
                }
                else if (candidate is XElement element)
                {
                    element.Remove();
                    moved.Add(element);
                }
            }

            packet = Serialize(root, wrapped: true);
        }

        if (packet.Length > MaxStandardXmp)
        {
            AppLog.Warning("Photo.Export", $"XMP of {packet.Length} bytes does not fit a JPEG segment; it is not saved.");
            return [];
        }

        byte[] extended = Serialize(new XElement(Meta + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", Meta.NamespaceName),
            new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName), moved)), wrapped: false);
        string guid = Convert.ToHexString(OperatingSystem.IsBrowser() ? Md5(extended) : MD5.HashData(extended));
        digest.Value = guid;
        packet = Serialize(root, wrapped: true);

        var segments = new List<byte[]> { JpegSegments.Segment(0xE1, XmpIdentifier, packet) };
        byte[] guidBytes = Encoding.ASCII.GetBytes(guid);
        for (int offset = 0; offset < extended.Length; offset += ExtendedXmpChunk)
        {
            int length = Math.Min(ExtendedXmpChunk, extended.Length - offset);
            byte[] chunk = new byte[32 + 8 + length];
            guidBytes.CopyTo(chunk, 0);
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(32), (uint)extended.Length);
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(36), (uint)offset);
            extended.AsSpan(offset, length).CopyTo(chunk.AsSpan(40));
            segments.Add(JpegSegments.Segment(0xE1, ExtendedXmpIdentifier, chunk));
        }

        return segments;

        static long Size(XObject value) => value is XElement element
            ? element.ToString(SaveOptions.DisableFormatting).Length
            : value.ToString()!.Length;
    }

    private static readonly int[] Md5Shifts =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    private static readonly uint[] Md5Constants =
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    ];

    /// <summary>The RFC 1321 digest, for the browser, whose .NET runtime offers no MD5.</summary>
    private static byte[] Md5(byte[] data)
    {
        byte[] message = new byte[(data.Length + 8) / 64 * 64 + 64];
        data.CopyTo(message, 0);
        message[data.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(message.Length - 8), (ulong)data.Length * 8);
        uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
        Span<uint> words = stackalloc uint[16];
        for (int block = 0; block < message.Length; block += 64)
        {
            for (int index = 0; index < 16; index++)
                words[index] = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(block + index * 4));
            uint a = a0, b = b0, c = c0, d = d0;
            for (int step = 0; step < 64; step++)
            {
                (uint f, int word) = (step / 16) switch
                {
                    0 => ((b & c) | (~b & d), step),
                    1 => ((d & b) | (~d & c), (5 * step + 1) % 16),
                    2 => (b ^ c ^ d, (3 * step + 5) % 16),
                    _ => (c ^ (b | ~d), 7 * step % 16),
                };
                f += a + Md5Constants[step] + words[word];
                (a, d, c) = (d, c, b);
                b += BitOperations.RotateLeft(f, Md5Shifts[step]);
            }

            a0 += a;
            b0 += b;
            c0 += c;
            d0 += d;
        }

        byte[] digest = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(digest, a0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4), b0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8), c0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12), d0);
        return digest;
    }

    /// <summary>
    /// Builds a TIFF block in the source's byte order. The picture is upright, so orientation is written
    /// as normal; the color space, pixel size and JPEG components are those of the saved picture. A maker
    /// note is placed at its original offset when possible, since some keep offsets relative to the TIFF header.
    /// </summary>
    private static byte[] WriteExif(PhotoExportExif exif, PhotoExportMetadataMode mode, int width, int height, bool srgb,
        bool jpeg, bool makerNote)
    {
        bool all = mode == PhotoExportMetadataMode.All;
        bool little = exif.LittleEndian;
        HashSet<ushort> imageTags = all ? DescriptiveImageTags : RenderingImageTags;
        var image = exif.Image.Where(field => imageTags.Contains(field.Tag) && field.Type != TypeIfd).ToList();
        image.Add(Short(TagOrientation, 1));

        var photo = all
            ? exif.Photo.Where(field => !EncodingPhotoTags.Contains(field.Tag) && field.Type != TypeIfd).ToList()
            : exif.Photo.Where(field => field.Tag == TagExifVersion).ToList();
        if (!photo.Any(field => field.Tag == TagExifVersion))
            photo.Add(new PhotoExportExifField(TagExifVersion, TypeUndefined, 4, "0232"u8.ToArray()));
        if (jpeg)
        {
            image.Add(Short(TagYCbCrPositioning, 1));
            photo.Add(new PhotoExportExifField(TagComponentsConfiguration, TypeUndefined, 4, [1, 2, 3, 0]));
        }
        photo.Add(Short(TagColorSpace, srgb ? 1 : 0xFFFF));
        photo.Add(Long(TagPixelXDimension, (uint)width));
        photo.Add(Long(TagPixelYDimension, (uint)height));
        PhotoExportExifField? note = all && makerNote ? exif.Photo.FirstOrDefault(field => field.Tag == TagMakerNote) : null;
        List<PhotoExportExifField>? location = all && exif.Location.Count > 0
            ? exif.Location.Where(field => field.Type != TypeIfd).ToList()
            : null;

        image.Add(Long(TagExifIfd, 0));
        if (location is not null)
            image.Add(Long(TagGpsIfd, 0));
        if (note is not null)
            photo.Add(note);
        image = Sorted(image);
        photo = Sorted(photo);
        location = location is null ? null : Sorted(location);

        long imageOffset = 8;
        long photoOffset = Align(imageOffset + DirectorySize(image, note));
        long locationOffset = Align(photoOffset + DirectorySize(photo, note));
        long end = location is null ? locationOffset : Align(locationOffset + DirectorySize(location, note));
        long noteOffset = note is null ? end
            : note.Offset >= end && note.Offset - end <= 65536 && note.Offset % 2 == 0 ? note.Offset : end;
        long total = note is null ? end : noteOffset + note.Value.Length;
        if (total > int.MaxValue)
            throw new InvalidDataException("The EXIF block is too large.");

        var block = new byte[total];
        Span<byte> span = block;
        (little ? "II*\0"u8 : "MM\0*"u8).CopyTo(span);
        WriteUInt32(span[4..], (uint)imageOffset, little);
        WriteDirectory(image, imageOffset);
        WriteDirectory(photo, photoOffset);
        if (location is not null)
            WriteDirectory(location, locationOffset);
        note?.Value.CopyTo(span[(int)noteOffset..]);
        return block;

        void WriteDirectory(List<PhotoExportExifField> fields, long offset)
        {
            Span<byte> directory = block.AsSpan((int)offset);
            WriteUInt16(directory, (ushort)fields.Count, little);
            long data = offset + 2 + 12L * fields.Count + 4;
            for (int index = 0; index < fields.Count; index++)
            {
                PhotoExportExifField field = fields[index];
                Span<byte> entry = directory.Slice(2 + index * 12, 12);
                WriteUInt16(entry, field.Tag, little);
                WriteUInt16(entry[2..], field.Type, little);
                WriteUInt32(entry[4..], field.Count, little);
                if (ReferenceEquals(field, note))
                    WriteUInt32(entry[8..], (uint)noteOffset, little);
                else if (field.Tag == TagExifIfd && fields == image)
                    WriteUInt32(entry[8..], (uint)photoOffset, little);
                else if (field.Tag == TagGpsIfd && fields == image)
                    WriteUInt32(entry[8..], (uint)locationOffset, little);
                else if (field.Value.Length <= 4)
                    field.Value.CopyTo(entry[8..]);
                else
                {
                    WriteUInt32(entry[8..], (uint)data, little);
                    field.Value.CopyTo(block.AsSpan((int)data));
                    data = Align(data + field.Value.Length);
                }
            }
        }

        PhotoExportExifField Short(ushort tag, int value)
        {
            byte[] bytes = new byte[2];
            WriteUInt16(bytes, (ushort)value, little);
            return new PhotoExportExifField(tag, TypeShort, 1, bytes);
        }

        PhotoExportExifField Long(ushort tag, uint value)
        {
            byte[] bytes = new byte[4];
            WriteUInt32(bytes, value, little);
            return new PhotoExportExifField(tag, TypeLong, 1, bytes);
        }
    }

    /// <summary>Fields in ascending tag order, keeping the first of any repeated tag.</summary>
    private static List<PhotoExportExifField> Sorted(List<PhotoExportExifField> fields) =>
        fields.GroupBy(field => field.Tag).Select(group => group.First()).OrderBy(field => field.Tag).ToList();

    /// <summary>A directory and the values stored after it, except a maker note placed separately.</summary>
    private static long DirectorySize(List<PhotoExportExifField> fields, PhotoExportExifField? note) =>
        2 + 12L * fields.Count + 4 + fields.Where(field => field.Value.Length > 4 && !ReferenceEquals(field, note))
            .Sum(field => Align(field.Value.Length));

    private static long Align(long value) => value + (value & 1);

    private static void WriteUInt16(Span<byte> target, ushort value, bool little)
    {
        if (little)
            BinaryPrimitives.WriteUInt16LittleEndian(target, value);
        else
            BinaryPrimitives.WriteUInt16BigEndian(target, value);
    }

    private static void WriteUInt32(Span<byte> target, uint value, bool little)
    {
        if (little)
            BinaryPrimitives.WriteUInt32LittleEndian(target, value);
        else
            BinaryPrimitives.WriteUInt32BigEndian(target, value);
    }

    /// <summary>Reads TIFF directories from a block in memory or from a TIFF-structured file.</summary>
    private sealed class TiffReader
    {
        private const int MaxEntries = 1024;
        private readonly Stream? stream;
        private readonly byte[]? block;
        private readonly long length;
        private long budget = MaxMetadataBytes;

        public TiffReader(Stream? stream, byte[]? block)
        {
            this.stream = stream;
            this.block = block;
            length = block?.Length ?? stream!.Length;
            byte[]? header = Read(0, 8);
            if (header is null || header[0] != header[1] || header[0] is not ((byte)'I' or (byte)'M'))
                return;
            LittleEndian = header[0] == (byte)'I';
            ushort magic = UInt16(header.AsSpan(2));
            if (magic != 42 && !(LittleEndian && magic == 0x01BC))
                return;
            First = UInt32(header.AsSpan(4));
            IsValid = true;
        }

        public bool IsValid { get; }

        public bool LittleEndian { get; }

        public uint First { get; }

        public ushort UInt16(ReadOnlySpan<byte> value) =>
            LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(value) : BinaryPrimitives.ReadUInt16BigEndian(value);

        public uint UInt32(ReadOnlySpan<byte> value) =>
            LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(value) : BinaryPrimitives.ReadUInt32BigEndian(value);

        /// <summary>The fields of a directory with known types and readable values.</summary>
        /// <param name="include">Selects the tags whose values are read, or null for all.</param>
        public List<PhotoExportExifField> Directory(uint offset, Func<ushort, bool>? include = null)
        {
            var fields = new List<PhotoExportExifField>();
            if (Read(offset, 2) is not { } count)
                return fields;
            int entries = Math.Min((int)UInt16(count), MaxEntries);
            if (Read(offset + 2L, entries * 12) is not { } table)
                return fields;
            for (int index = 0; index < entries; index++)
            {
                ReadOnlySpan<byte> entry = table.AsSpan(index * 12, 12);
                ushort tag = UInt16(entry), type = UInt16(entry[2..]);
                uint components = UInt32(entry[4..]);
                long size = TypeSize(type) * (long)components;
                if (size <= 0 || size > MaxFieldBytes || include?.Invoke(tag) == false)
                    continue;
                if (size <= 4)
                {
                    fields.Add(new PhotoExportExifField(tag, type, components, entry.Slice(8, (int)size).ToArray()));
                    continue;
                }

                uint position = UInt32(entry[8..]);
                if (Read(position, (int)size) is { } value)
                    fields.Add(new PhotoExportExifField(tag, type, components, value, position));
            }

            return fields;
        }

        private static int TypeSize(ushort type) => type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 or 13 => 4,
            5 or 10 or 12 => 8,
            _ => 0,
        };

        private byte[]? Read(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset > length - count || count > budget)
                return null;
            budget -= count;
            if (block is not null)
                return block.AsSpan((int)offset, count).ToArray();
            stream!.Position = offset;
            byte[] value = new byte[count];
            stream.ReadExactly(value);
            return value;
        }
    }
}
