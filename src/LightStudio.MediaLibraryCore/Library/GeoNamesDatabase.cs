#nullable enable
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.Library;

internal sealed record PhotoLocation(string Country, string? State, string City);

/// <summary>
/// Reads the embedded LightStudio.GeoNames database (binary format version 1) and finds the
/// populated place nearest to a coordinate, along with its first-level region and country.
/// </summary>
internal sealed class GeoNamesDatabase
{
    internal const string ResourceName = "LightStudio.MediaLibraryCore.GeoNames.geonames.bin.gz";

    /// <summary>Places farther than this from the nearest populated place have no location.</summary>
    internal const double MaxDistanceKm = 50;

    private const double EarthRadiusKm = 6371.0088;
    private const double CoordinateScale = 100_000;
    private const int HeaderSize = 64;
    private const int EntitySize = 24;
    private const int CitySize = 12;
    private const int TranslationSize = 5;
    private const int ChunkSize = 64 * 1024;
    private const uint NoParent = uint.MaxValue;
    private const byte CountryKind = 0;
    private const byte Admin1Kind = 1;
    private const byte PlaceKind = 3;

    private readonly byte[] data;
    private readonly int entityCount;
    private readonly int cityCount;
    private readonly int translationCount;
    private readonly int entityOffset;
    private readonly int cityOffset;
    private readonly int translationOffset;
    private readonly int stringOffset;
    private readonly int stringSize;
    private readonly string[] languages;

    private GeoNamesDatabase(byte[] data)
    {
        this.data = data;
        var header = data.AsSpan(0, HeaderSize);
        if (!header[..8].SequenceEqual("LPGEO\0\0\0"u8) || U32(8) != 1 || U32(12) != HeaderSize || U32(56) != 0 || U32(60) != 0)
            throw new InvalidDataException("The GeoNames database header is not supported.");
        entityCount = Count(16);
        cityCount = Count(20);
        translationCount = Count(24);
        var languageCount = Count(28);
        entityOffset = Count(32);
        cityOffset = Count(36);
        translationOffset = Count(40);
        var languageOffset = Count(44);
        stringOffset = Count(48);
        stringSize = Count(52);
        if (entityOffset != HeaderSize ||
            cityOffset != entityOffset + (long)entityCount * EntitySize ||
            translationOffset != cityOffset + (long)cityCount * CitySize ||
            languageOffset != translationOffset + (long)translationCount * TranslationSize ||
            stringOffset != languageOffset + (long)languageCount * 4 ||
            (long)stringOffset + stringSize != data.Length ||
            stringSize == 0 || data[^1] != 0 || data[stringOffset] != 0)
        {
            throw new InvalidDataException("The GeoNames database sections are inconsistent.");
        }

        languages = new string[languageCount];
        for (var index = 0; index < languageCount; index++)
            languages[index] = Text(U32(languageOffset + index * 4));
    }

    /// <summary>
    /// The GeoNames name language for an interface culture. Unsupported languages use English.
    /// </summary>
    internal static string LanguageFor(CultureInfo culture)
    {
        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            switch (current.Name.ToLowerInvariant())
            {
                case "zh-hant" or "zh-tw" or "zh-hk" or "zh-mo":
                    return "zh-Hant";
                case "zh-hans" or "zh-cn" or "zh-sg":
                    return "zh-Hans";
            }
            if (ReferenceEquals(current, current.Parent))
                break;
        }
        return culture.TwoLetterISOLanguageName switch
        {
            "de" => "de",
            "es" => "es",
            "fr" => "fr",
            "ja" => "ja",
            "zh" => "zh-Hans",
            _ => "en",
        };
    }

    /// <summary>
    /// Decompresses the embedded database in bounded chunks, calling <paramref name="yieldAsync"/>
    /// between chunks so single-threaded runtimes keep processing other work.
    /// </summary>
    internal static async Task<GeoNamesDatabase> LoadAsync(Func<ValueTask> yieldAsync, CancellationToken cancellationToken)
    {
        await using var resource = typeof(GeoNamesDatabase).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The embedded GeoNames database is missing.");
        await using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        var header = new byte[HeaderSize];
        await gzip.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = (long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(48)) +
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(52));
        if (length < HeaderSize || length > Array.MaxLength)
            throw new InvalidDataException("The GeoNames database size is invalid.");

        var data = GC.AllocateUninitializedArray<byte>((int)length);
        header.CopyTo(data, 0);
        var position = HeaderSize;
        while (position < data.Length)
        {
            var read = await gzip.ReadAsync(data.AsMemory(position, Math.Min(ChunkSize, data.Length - position)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The GeoNames database is truncated.");
            position += read;
            await yieldAsync().ConfigureAwait(false);
        }
        if (await gzip.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            throw new InvalidDataException("The GeoNames database has trailing data.");
        return new GeoNamesDatabase(data);
    }

    /// <summary>The name slot for a language tag, or -1 to use primary names.</summary>
    internal int LanguageIndex(string language) => Array.IndexOf(languages, language);

    /// <summary>
    /// The nearest populated place within <see cref="MaxDistanceKm"/>, or null when there is none
    /// or the coordinate is out of range.
    /// </summary>
    internal PhotoLocation? Lookup(double latitude, double longitude, int language)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180 || cityCount == 0)
        {
            return null;
        }

        var query = Project(latitude, longitude);
        var best = (Distance: double.PositiveInfinity, Id: uint.MaxValue, Node: -1);
        Search(0, cityCount, 0, query, ref best);
        if (best.Node < 0 ||
            2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(best.Distance) / 2)) > MaxDistanceKm)
        {
            return null;
        }

        var place = EntityIndex(U32(cityOffset + best.Node * CitySize));
        if (Kind(place) != PlaceKind)
            throw new InvalidDataException("A GeoNames city record does not reference a populated place.");
        string? state = null;
        string? country = null;
        var parent = Parent(place);
        for (var depth = 0; parent >= 0 && depth < 3; depth++)
        {
            switch (Kind(parent))
            {
                case Admin1Kind:
                    state = Name(parent, language);
                    break;
                case CountryKind:
                    country = Name(parent, language);
                    break;
            }
            parent = Parent(parent);
        }
        return country is null ? null : new PhotoLocation(country, state, Name(place, language));
    }

    private void Search(int low, int high, int depth, (double X, double Y, double Z) query,
        ref (double Distance, uint Id, int Node) best)
    {
        if (low >= high)
            return;
        var node = low + (high - low) / 2;
        var record = cityOffset + node * CitySize;
        var point = Project(I32(record + 4) / CoordinateScale, I32(record + 8) / CoordinateScale);
        var dx = query.X - point.X;
        var dy = query.Y - point.Y;
        var dz = query.Z - point.Z;
        var distance = dx * dx + dy * dy + dz * dz;
        if (distance <= best.Distance)
        {
            var id = U32(entityOffset + EntityIndex(U32(record)) * EntitySize);
            if (distance < best.Distance || id < best.Id)
                best = (distance, id, node);
        }

        var delta = (depth % 3) switch { 0 => dx, 1 => dy, _ => dz };
        if (delta < 0)
        {
            Search(low, node, depth + 1, query, ref best);
            if (delta * delta <= best.Distance)
                Search(node + 1, high, depth + 1, query, ref best);
        }
        else
        {
            Search(node + 1, high, depth + 1, query, ref best);
            if (delta * delta <= best.Distance)
                Search(low, node, depth + 1, query, ref best);
        }
    }

    private static (double X, double Y, double Z) Project(double latitude, double longitude)
    {
        var lat = latitude * Math.PI / 180;
        var lon = longitude * Math.PI / 180;
        var cosLat = Math.Cos(lat);
        return (cosLat * Math.Cos(lon), cosLat * Math.Sin(lon), Math.Sin(lat));
    }

    private string Name(int entity, int language)
    {
        var record = entityOffset + entity * EntitySize;
        if (language >= 0)
        {
            var first = U32(record + 16);
            var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(record + 20));
            if (first + (ulong)count > (ulong)translationCount)
                throw new InvalidDataException("A GeoNames translation range is out of bounds.");
            for (var index = 0; index < count; index++)
            {
                var translation = translationOffset + (int)(first + (uint)index) * TranslationSize;
                if (data[translation] == language)
                    return Text(U32(translation + 1));
            }
        }
        return Text(U32(record + 4));
    }

    private byte Kind(int entity) => data[entityOffset + entity * EntitySize + 22];

    private int Parent(int entity)
    {
        var parent = U32(entityOffset + entity * EntitySize + 12);
        return parent == NoParent ? -1 : EntityIndex(parent);
    }

    private int EntityIndex(uint value) => value < (uint)entityCount
        ? (int)value
        : throw new InvalidDataException("A GeoNames entity reference is out of bounds.");

    private string Text(uint offset)
    {
        if (offset >= (uint)stringSize)
            throw new InvalidDataException("A GeoNames string reference is out of bounds.");
        var value = data.AsSpan(stringOffset + (int)offset);
        return Encoding.UTF8.GetString(value[..value.IndexOf((byte)0)]);
    }

    private int Count(int offset) => U32(offset) <= int.MaxValue
        ? (int)U32(offset)
        : throw new InvalidDataException("The GeoNames database is too large.");

    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
}
