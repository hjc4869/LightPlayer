#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.FfmpegShim.Media;

/// <summary>One EXIF tag, named as the specification names it and formatted for reading.</summary>
public sealed record ExifEntry(string Name, string Value);

public sealed record ExifPhotoMetadata
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string? DateTaken { get; init; }
    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public string? Lens { get; init; }
    public double? ExposureSeconds { get; init; }
    public double? Aperture { get; init; }
    public double? FocalLength { get; init; }
    public int? Iso { get; init; }
    public int? Orientation { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? Altitude { get; init; }
    public string? Description { get; init; }
    public string? Artist { get; init; }
    public string? Copyright { get; init; }
}

/// <summary>
/// Reads the EXIF block of a still picture: what camera took it, how it was exposed, and where.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg surfaces none of this. A JPEG's EXIF lives in an APP1 segment the decoder skips over, and
/// even where a container has somewhere of its own to put it, the demuxer does not turn it into
/// stream tags. So the file's head is parsed here, the same way <see cref="ExifOrientation"/>
/// parses it for the one tag the renderer needs.
/// </para>
/// </remarks>
public static class ExifMetadata
{
    /// <summary>
    /// Most components of a tag that are kept. Nothing read here wants more than the three a GPS
    /// coordinate is written as, and a color profile tag would otherwise be decoded in full.
    /// </summary>
    private const int MaxComponents = 4;

    private const ushort ExifIfdPointer = 0x8769;
    private const ushort GpsIfdPointer = 0x8825;

    /// <summary>Everything the picture declares, in the order the tags are listed below.</summary>
    public static IReadOnlyList<ExifEntry> Read(Stream stream)
    {
        byte[] tiff = ExifBlock.Read(stream, out bool standaloneTiff);
        return Parse(tiff, standaloneTiff ? stream : null);
    }

    /// <summary>
    /// <inheritdoc cref="Read(Stream)"/> Used where there is no stream to seek in, which cannot
    /// answer for a container that keeps its metadata past the bytes in hand.
    /// </summary>
    public static IReadOnlyList<ExifEntry> Read(ReadOnlySpan<byte> header) => Parse(ExifBlock.Find(header));

    public static async Task<IReadOnlyList<ExifEntry>> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var (camera, capture, gps) = await ReadFieldsAsync(stream, cancellationToken).ConfigureAwait(false);
        var entries = new List<ExifEntry>();
        AppendImage(entries, camera);
        AppendCapture(entries, capture);
        AppendPosition(entries, gps);
        return entries;
    }

    public static async Task<ExifPhotoMetadata> ReadPhotoAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var (camera, capture, gps) = await ReadFieldsAsync(stream, cancellationToken).ConfigureAwait(false);
        return CreatePhoto(camera, capture, gps);
    }

    private static async Task<(Fields Camera, Fields Capture, Fields Gps)> ReadFieldsAsync(Stream stream, CancellationToken cancellationToken)
    {
        long origin = stream.Position;
        try
        {
            var (tiff, standalone) = await ExifBlock.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (tiff.Length < 8)
                return (new(), new(), new());
            bool littleEndian = tiff[0] == 0x49;
            if (ReadUInt16(tiff.AsSpan(2), littleEndian) is not (42 or 0x01BC))
                return (new(), new(), new());
            var ranges = new AsyncFields(tiff, standalone ? stream : null, littleEndian, cancellationToken);
            Fields camera = await ranges.ReadDirectoryAsync(ReadUInt32(tiff.AsSpan(4), littleEndian)).ConfigureAwait(false);
            Fields capture = Number(camera, ExifIfdPointer) is { } exifOffset
                ? await ranges.ReadDirectoryAsync((uint)exifOffset).ConfigureAwait(false) : new();
            Fields gps = Number(camera, GpsIfdPointer) is { } gpsOffset
                ? await ranges.ReadDirectoryAsync((uint)gpsOffset).ConfigureAwait(false) : new();
            return (camera, capture, gps);
        }
        finally
        {
            stream.Position = origin;
        }
    }

    public static ExifPhotoMetadata ReadPhoto(Stream stream)
    {
        ReadOnlySpan<byte> tiff = ExifBlock.Read(stream, out bool standaloneTiff);
        Stream? tiffStream = standaloneTiff ? stream : null;
        if (tiff.Length < 8)
            return new();

        bool littleEndian = tiff[0] == 0x49;
        if (ReadUInt16(tiff[2..], littleEndian) is not (42 or 0x01BC))
            return new();

        Fields camera = ReadDirectory(tiff, ReadUInt32(tiff[4..], littleEndian), littleEndian, tiffStream);
        Fields capture = Number(camera, ExifIfdPointer) is { } exifOffset
            ? ReadDirectory(tiff, (uint)exifOffset, littleEndian, tiffStream) : new();
        Fields gps = Number(camera, GpsIfdPointer) is { } gpsOffset
            ? ReadDirectory(tiff, (uint)gpsOffset, littleEndian, tiffStream) : new();

        return CreatePhoto(camera, capture, gps);
    }

    private static ExifPhotoMetadata CreatePhoto(Fields camera, Fields capture, Fields gps)
    {
        string? dateTaken = CaptureDate(capture, 0x9003, Text(capture, 0x9011))
            ?? CaptureDate(capture, 0x9004, Text(capture, 0x9012))
            ?? CaptureDate(camera, 0x0132, Text(capture, 0x9010));

        double? latitude = Coordinate(gps, 0x0002, Text(gps, 0x0001), 'S');
        double? longitude = Coordinate(gps, 0x0004, Text(gps, 0x0003), 'W');
        bool hasLocation = Text(gps, 0x0001) is not null && Text(gps, 0x0003) is not null &&
            latitude is { } lat && double.IsFinite(lat) && longitude is { } lon && double.IsFinite(lon);
        double? altitude = Number(gps, 0x0006);

        return new()
        {
            Width = Integer(capture, 0xA002) ?? Integer(camera, 0x0100) ?? 0,
            Height = Integer(capture, 0xA003) ?? Integer(camera, 0x0101) ?? 0,
            DateTaken = dateTaken,
            CameraMake = Text(camera, 0x010F),
            CameraModel = Text(camera, 0x0110),
            Lens = Text(capture, 0xA434),
            ExposureSeconds = Number(capture, 0x829A),
            Aperture = Number(capture, 0x829D),
            FocalLength = Number(capture, 0x920A),
            Iso = Integer(capture, 0x8827),
            Orientation = Integer(camera, 0x0112),
            Latitude = hasLocation ? latitude : null,
            Longitude = hasLocation ? longitude : null,
            Altitude = Number(gps, 0x0005) == 1 ? -altitude : altitude,
            Description = Text(camera, 0x010E),
            Artist = Text(camera, 0x013B),
            Copyright = Text(camera, 0x8298),
        };
    }

    private static string? CaptureDate(Fields fields, ushort tag, string? offset) =>
        DateTime.TryParseExact(Text(fields, tag), "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var captured)
            ? captured.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + offset
            : null;

    private static int? Integer(Fields fields, ushort tag) =>
        Number(fields, tag) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

    private static IReadOnlyList<ExifEntry> Parse(ReadOnlySpan<byte> tiff, Stream? tiffStream = null)
    {
        if (tiff.Length < 8)
            return [];

        bool littleEndian = tiff[0] == 0x49;
        if (ReadUInt16(tiff[2..], littleEndian) is not (42 or 0x01BC))
            return [];

        // Only IFD0 is walked: IFD1 is the embedded thumbnail, which describes itself rather than
        // the picture, and every tag worth showing hangs off IFD0 or one of the two it points at.
        Fields ifd0 = ReadDirectory(tiff, ReadUInt32(tiff[4..], littleEndian), littleEndian, tiffStream);
        if (ifd0.Count == 0)
            return [];

        var entries = new List<ExifEntry>();
        AppendImage(entries, ifd0);

        if (Number(ifd0, ExifIfdPointer) is { } exifOffset)
            AppendCapture(entries, ReadDirectory(tiff, (uint)exifOffset, littleEndian, tiffStream));

        if (Number(ifd0, GpsIfdPointer) is { } gpsOffset)
            AppendPosition(entries, ReadDirectory(tiff, (uint)gpsOffset, littleEndian, tiffStream));

        return entries;
    }

    private static void AppendImage(List<ExifEntry> entries, Fields ifd)
    {
        Add(entries, "Make", Text(ifd, 0x010F));
        Add(entries, "Model", Text(ifd, 0x0110));
        Add(entries, "Software", Text(ifd, 0x0131));
        Add(entries, "Artist", Text(ifd, 0x013B));
        Add(entries, "Copyright", Text(ifd, 0x8298));
        Add(entries, "ImageDescription", Text(ifd, 0x010E));
        Add(entries, "DateTime", Text(ifd, 0x0132));

        if (Number(ifd, 0x0112) is { } orientation)
            Add(entries, "Orientation", DescribeOrientation((int)orientation));

        if (Number(ifd, 0x011A) is { } x && Number(ifd, 0x011B) is { } y)
        {
            string unit = Number(ifd, 0x0128) == 3 ? "dpcm" : "dpi";
            Add(entries, "Resolution", $"{Format(x)} \u00d7 {Format(y)} {unit}");
        }
    }

    private static void AppendCapture(List<ExifEntry> entries, Fields ifd)
    {
        Add(entries, "DateTimeOriginal", Text(ifd, 0x9003));
        Add(entries, "DateTimeDigitized", Text(ifd, 0x9004));
        Add(entries, "LensModel", Text(ifd, 0xA434));

        if (Number(ifd, 0x829A) is { } exposure)
            Add(entries, "ExposureTime", DescribeExposureTime(exposure));

        if (Number(ifd, 0x829D) is { } aperture)
            Add(entries, "FNumber", $"f/{Format(aperture)}");

        if (Number(ifd, 0x8827) is { } iso)
            Add(entries, "ISO", Format(iso));

        if (Number(ifd, 0x9204) is { } bias)
            Add(entries, "ExposureBias", $"{Format(bias, signed: true)} EV");

        if (Number(ifd, 0x920A) is { } focal)
            Add(entries, "FocalLength", $"{Format(focal)} mm");

        if (Number(ifd, 0xA405) is { } focal35 && focal35 > 0)
            Add(entries, "FocalLengthIn35mmFilm", $"{Format(focal35)} mm");

        if (Number(ifd, 0x8822) is { } program)
            Add(entries, "ExposureProgram", Describe(ExposurePrograms, (int)program));

        if (Number(ifd, 0x9207) is { } metering)
            Add(entries, "MeteringMode", Describe(MeteringModes, (int)metering));

        if (Number(ifd, 0x9209) is { } flash)
            Add(entries, "Flash", ((int)flash & 1) == 1 ? "Fired" : "Did not fire");

        if (Number(ifd, 0xA403) is { } whiteBalance)
            Add(entries, "WhiteBalance", whiteBalance == 0 ? "Auto" : "Manual");

        if (Number(ifd, 0xA001) is { } colorSpace)
            Add(entries, "ColorSpace", colorSpace == 1 ? "sRGB" : "Uncalibrated");
    }

    /// <summary>
    /// Where the picture was taken, as one decimal coordinate rather than the three rationals and
    /// two hemisphere letters the file spells it with.
    /// </summary>
    private static void AppendPosition(List<ExifEntry> entries, Fields ifd)
    {
        double? latitude = Coordinate(ifd, 0x0002, Text(ifd, 0x0001), 'S');
        double? longitude = Coordinate(ifd, 0x0004, Text(ifd, 0x0003), 'W');
        if (latitude is { } lat && longitude is { } lon)
        {
            Add(entries, "GPSPosition", string.Create(
                CultureInfo.InvariantCulture,
                $"{lat:0.######}, {lon:0.######}"));
        }

        if (Number(ifd, 0x0006) is { } altitude)
        {
            // The reference byte says which side of sea level, not which unit.
            double meters = Number(ifd, 0x0005) == 1 ? -altitude : altitude;
            Add(entries, "GPSAltitude", string.Create(CultureInfo.InvariantCulture, $"{meters:0.#} m"));
        }
    }

    /// <summary>
    /// Degrees, minutes and seconds folded into one signed number, negative in the southern and
    /// western hemispheres.
    /// </summary>
    private static double? Coordinate(Fields ifd, ushort tag, string? reference, char negativeHemisphere)
    {
        if (!ifd.TryGetValue(tag, out ExifField? field) || field.Components.Length < 3)
            return null;

        double coordinate = field.Components[0] + (field.Components[1] / 60) + (field.Components[2] / 3600);
        if (!double.IsFinite(coordinate))
            return null;
        bool negative = reference is { Length: > 0 } && char.ToUpperInvariant(reference[0]) == negativeHemisphere;
        return negative ? -coordinate : coordinate;
    }

    private static readonly string[] ExposurePrograms =
    [
        "Not defined", "Manual", "Normal program", "Aperture priority", "Shutter priority",
        "Creative", "Action", "Portrait", "Landscape",
    ];

    private static readonly string[] MeteringModes =
    [
        "Unknown", "Average", "Center-weighted average", "Spot", "Multi-spot", "Pattern", "Partial",
    ];

    private static string Describe(string[] names, int value) =>
        value >= 0 && value < names.Length ? names[value] : value.ToString(CultureInfo.InvariantCulture);

    private static string DescribeOrientation(int value) => value switch
    {
        1 => "Normal",
        2 => "Mirrored",
        3 => "Rotated 180\u00b0",
        4 => "Mirrored, rotated 180\u00b0",
        5 => "Mirrored, rotated 270\u00b0",
        6 => "Rotated 90\u00b0",
        7 => "Mirrored, rotated 90\u00b0",
        8 => "Rotated 270\u00b0",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// A shutter speed reads as the fraction the camera recorded it as, because "1/125" is the
    /// number on the dial and "0.008" is not.
    /// </summary>
    private static string DescribeExposureTime(double seconds) => seconds switch
    {
        <= 0 => string.Empty,
        >= 1 => string.Create(CultureInfo.InvariantCulture, $"{seconds:0.##} s"),
        _ => string.Create(CultureInfo.InvariantCulture, $"1/{Math.Round(1 / seconds):0} s"),
    };

    private static string Format(double value, bool signed = false) => signed && value > 0
        ? string.Create(CultureInfo.InvariantCulture, $"+{value:0.##}")
        : string.Create(CultureInfo.InvariantCulture, $"{value:0.##}");

    private static void Add(List<ExifEntry> entries, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            entries.Add(new ExifEntry(name, value.Trim()));
    }

    private static string? Text(Fields ifd, ushort tag) =>
        ifd.TryGetValue(tag, out ExifField? field) ? field.Text : null;

    private static double? Number(Fields ifd, ushort tag) =>
        ifd.TryGetValue(tag, out ExifField? field) && field.Components.Length > 0 && double.IsFinite(field.Components[0])
            ? field.Components[0]
            : null;

    private sealed record ExifField(string? Text, double[] Components);

    private sealed class Fields : Dictionary<ushort, ExifField>;

    private sealed class AsyncFields(byte[] tiff, Stream? stream, bool littleEndian, CancellationToken cancellationToken)
    {
        private int remainingBytes = 256 * 1024;

        internal async Task<Fields> ReadDirectoryAsync(uint offset)
        {
            var fields = new Fields();
            ReadOnlyMemory<byte> header = await ReadAsync(offset, 2).ConfigureAwait(false);
            if (header.Length != 2)
                return fields;
            int count = ReadUInt16(header.Span, littleEndian);
            if (count > 1024)
                throw new InvalidDataException("EXIF directory exceeds the entry budget.");
            ReadOnlyMemory<byte> directory = await ReadAsync(offset, 2 + count * 12).ConfigureAwait(false);
            if (directory.Length != 2 + count * 12)
                return fields;
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlyMemory<byte> entry = directory.Slice(2 + index * 12, 12);
                ushort tag = ReadUInt16(entry.Span, littleEndian);
                ushort type = ReadUInt16(entry.Span[2..], littleEndian);
                uint components = ReadUInt32(entry.Span[4..], littleEndian);
                int size = ElementSize(type);
                if (size == 0 || components == 0 || components > int.MaxValue / size)
                    continue;
                int bytes = (int)components * size;
                int keptBytes = Math.Min(bytes, type == 2 ? 4096 : MaxComponents * size);
                ReadOnlyMemory<byte> data = bytes <= 4 ? entry.Slice(8, bytes)
                    : await ReadAsync(ReadUInt32(entry.Span[8..], littleEndian), keptBytes).ConfigureAwait(false);
                if (data.Length != keptBytes)
                    continue;
                fields[tag] = type == 2 ? new ExifField(ReadText(data.Span), [])
                    : new ExifField(null, ReadComponents(data.Span, type, size, keptBytes / size, littleEndian));
            }
            return fields;
        }

        private async ValueTask<ReadOnlyMemory<byte>> ReadAsync(uint offset, int length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            remainingBytes -= length;
            if (remainingBytes < 0)
                throw new InvalidDataException("EXIF fields exceed the byte budget.");
            if (offset <= (long)tiff.Length - length)
                return tiff.AsMemory((int)offset, length);
            return stream is null ? ReadOnlyMemory<byte>.Empty
                : await ExifBlock.ReadRangeAsync(stream, offset, length, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One TIFF directory, decoded into plain values so nothing downstream has to carry the byte
    /// order or the offsets around with it.
    /// </summary>
    private static Fields ReadDirectory(ReadOnlySpan<byte> tiff, uint offset, bool littleEndian, Stream? tiffStream = null)
    {
        var fields = new Fields();
        ReadOnlySpan<byte> header = FieldData(tiff, offset, 2, tiffStream);
        if (header.Length < 2)
            return fields;

        int count = ReadUInt16(header, littleEndian);
        ReadOnlySpan<byte> ifd = FieldData(tiff, offset, 2 + count * 12, tiffStream);
        if (count <= 0 || ifd.IsEmpty)
            return fields;

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = ifd.Slice(2 + (i * 12), 12);
            ushort tag = ReadUInt16(entry, littleEndian);
            ushort type = ReadUInt16(entry[2..], littleEndian);
            uint components = ReadUInt32(entry[4..], littleEndian);
            int size = ElementSize(type);
            if (size == 0 || components == 0 || components > int.MaxValue / size)
                continue;

            // A value of four bytes or fewer is written into the entry itself; anything larger
            // leaves an offset from the start of the TIFF block in its place.
            int bytes = (int)components * size;
            ReadOnlySpan<byte> data = bytes <= 4
                ? entry.Slice(8, bytes)
                : FieldData(tiff, ReadUInt32(entry[8..], littleEndian), bytes, tiffStream);
            if (data.Length != bytes)
                continue;

            fields[tag] = type == 2
                ? new ExifField(ReadText(data), [])
                : new ExifField(null, ReadComponents(data, type, size, (int)components, littleEndian));
        }

        return fields;
    }

    private static ReadOnlySpan<byte> FieldData(ReadOnlySpan<byte> tiff, uint offset, int length, Stream? stream)
    {
        if (length < 0 || length > 4 * 1024 * 1024)
            return default;
        if (offset <= (long)tiff.Length - length)
            return tiff.Slice((int)offset, length);
        if (stream is null || offset > stream.Length - length)
            return default;

        long origin = stream.Position;
        try
        {
            stream.Position = offset;
            var buffer = new byte[length];
            return stream.ReadAtLeast(buffer, length, false) == length ? buffer : default;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static string? ReadText(ReadOnlySpan<byte> data)
    {
        int terminator = data.IndexOf((byte)0);
        if (terminator >= 0)
            data = data[..terminator];

        return data.IsEmpty ? null : Encoding.Latin1.GetString(data);
    }

    private static double[] ReadComponents(
        ReadOnlySpan<byte> data,
        ushort type,
        int size,
        int components,
        bool littleEndian)
    {
        var values = new double[Math.Min(components, MaxComponents)];
        for (int i = 0; i < values.Length; i++)
        {
            ReadOnlySpan<byte> element = data[(i * size)..];
            values[i] = type switch
            {
                1 or 6 or 7 => element[0],
                3 => ReadUInt16(element, littleEndian),
                8 => (short)ReadUInt16(element, littleEndian),
                4 => ReadUInt32(element, littleEndian),
                9 => (int)ReadUInt32(element, littleEndian),
                5 => Ratio(ReadUInt32(element, littleEndian), ReadUInt32(element[4..], littleEndian)),
                10 => Ratio((int)ReadUInt32(element, littleEndian), (int)ReadUInt32(element[4..], littleEndian)),
                _ => 0,
            };
        }

        return values;
    }

    private static double Ratio(double numerator, double denominator) => numerator / denominator;

    private static int ElementSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    private static ushort ReadUInt16(ReadOnlySpan<byte> value, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(value)
        : BinaryPrimitives.ReadUInt16BigEndian(value);

    private static uint ReadUInt32(ReadOnlySpan<byte> value, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(value)
        : BinaryPrimitives.ReadUInt32BigEndian(value);
}
