using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.LightPlayer.Services;

internal static unsafe partial class PhotoDecoder
{
    private const int MaxGainMapBytes = 64 * 1024 * 1024;
    private const int MaxContainerMetadataBytes = 8 * 1024 * 1024;

    private sealed record GainMapSource(GainMap Metadata, uint? BaseItem = null, uint? GainItem = null,
        byte[]? EncodedGain = null, byte[]? BaseProfile = null, byte[]? MathProfile = null, int MathPrimaries = 0);

    private static GainMapSource? ReadContainer(Stream stream, CancellationToken cancellationToken, uint? imageItem = null)
    {
        long origin = stream.Position;
        Span<byte> signature = stackalloc byte[12];
        int read = stream.ReadAtLeast(signature, 12, throwOnEndOfStream: false);
        stream.Position = origin;
        if (read < 12)
            return null;
        if (signature[..8].SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]))
            return ReadPngGainMap(stream, cancellationToken);
        if (!signature.Slice(4, 4).SequenceEqual("ftyp"u8) && !signature.Slice(4, 4).SequenceEqual("JXL "u8))
            return null;

        while (ReadBoxHeader(stream, stream.Length, out uint type, out long start, out long end))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long length = end - start;
            if (type == FourCc("meta"u8) && length <= MaxContainerMetadataBytes)
            {
                var items = new IsoImageItems(ReadBytes(stream, (int)length), origin);
                GainMapSource? result = items.FindGainMap(stream, imageItem);
                if (result is not null)
                    return result;
            }
            else if (type == FourCc("jhgm"u8) && length <= MaxGainMapBytes)
            {
                return ReadJxlGainMap(ReadBytes(stream, (int)length));
            }
            else if (type == FourCc("brob"u8) && length > 4 && stream.ReadByte() == 'j' &&
                stream.ReadByte() == 'h' && stream.ReadByte() == 'g' && stream.ReadByte() == 'm' && length <= MaxGainMapBytes)
            {
                byte[]? decoded = PhotoBrotli.Decode(ReadBytes(stream, (int)length - 4), MaxGainMapBytes, cancellationToken);
                return decoded is null ? null : ReadJxlGainMap(decoded);
            }

            stream.Position = end;
        }

        return null;
    }

    private static (uint Item, int Width, int Height)? FindContainerThumbnail(Stream stream,
        PhotoPreviewRequest request, CancellationToken cancellationToken)
    {
        long origin = stream.Position;
        try
        {
            Span<byte> signature = stackalloc byte[12];
            if (stream.ReadAtLeast(signature, signature.Length, false) != signature.Length ||
                !signature.Slice(4, 4).SequenceEqual("ftyp"u8))
                return null;
            stream.Position = origin;
            while (ReadBoxHeader(stream, stream.Length, out uint type, out long start, out long end))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (type == FourCc("meta"u8) && end - start is > 4 and <= MaxContainerMetadataBytes)
                    return new IsoImageItems(ReadBytes(stream, (int)(end - start)), origin).FindThumbnail(request);
                stream.Position = end;
            }
            return null;
        }
        finally { stream.Position = origin; }
    }

    private static GainMapSource? ReadPngGainMap(Stream stream, CancellationToken cancellationToken, bool metadataOnly = false)
    {
        stream.Position += 8;
        GainMap? metadata = null;
        bool declared = false;
        byte[]? gain = null;
        Span<byte> header = stackalloc byte[8];
        Span<byte> checksum = stackalloc byte[4];
        while (stream.Position <= stream.Length - 12)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(header);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint type = FourCc(header[4..]);
            if (length > stream.Length - stream.Position - 4)
                return null;
            if (type == FourCc("IEND"u8))
                break;
            if (type == FourCc("gmAP"u8) || type == FourCc("gdAT"u8))
            {
                if (length > MaxGainMapBytes)
                    return null;
                byte[] payload = ReadBytes(stream, (int)length);
                stream.ReadExactly(checksum);
                uint crc = uint.MaxValue;
                foreach (byte value in header[4..])
                    crc = UpdatePngCrc(crc, value);
                foreach (byte value in payload)
                    crc = UpdatePngCrc(crc, value);
                if (~crc != BinaryPrimitives.ReadUInt32BigEndian(checksum))
                    return null;
                if (type == FourCc("gmAP"u8))
                {
                    if (declared || payload.Length < 4 || BinaryPrimitives.ReadUInt16BigEndian(payload) != 0)
                        return null;
                    declared = true;
                    if (payload.Length > 4)
                    {
                        if (!TryReadIso(payload, out GainMap parsed))
                            return null;
                        metadata = parsed;
                    }
                }
                else
                {
                    if (gain is not null)
                        return null;
                    gain = payload;
                }
            }
            else
                stream.Position += length + 4L;
        }

        if (metadataOnly)
            return metadata is { } nestedInfo ? new GainMapSource(nestedInfo) : null;
        if (!declared || gain is null)
            return null;
        if (metadata is null && gain.AsSpan().StartsWith((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]))
        {
            using var nested = new MemoryStream(gain, writable: false);
            metadata = ReadPngGainMap(nested, cancellationToken, metadataOnly: true)?.Metadata;
        }
        return metadata is { } info ? new GainMapSource(info, EncodedGain: gain) : null;
    }

    private static uint UpdatePngCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        return crc;
    }

    private static GainMapSource? ReadJxlGainMap(ReadOnlySpan<byte> data)
    {
        var reader = new MetadataReader(data);
        if (reader.Byte() != 0 || !TryReadIso(reader.Take(reader.UInt16()), out GainMap metadata))
            return null;
        ReadOnlySpan<byte> colorEncoding = reader.Take(reader.Byte());
        uint profileLength = reader.UInt32();
        if (profileLength > MaxContainerMetadataBytes)
            return null;
        ReadOnlySpan<byte> profile = reader.Take((int)profileLength);
        if (!reader.Valid || reader.Remaining == 0)
            return null;
        if (!metadata.UseBaseColorSpace && (!colorEncoding.IsEmpty || !profile.IsEmpty))
            return null;
        return new GainMapSource(metadata, EncodedGain: reader.Take(reader.Remaining).ToArray());
    }

    private static uint FourCc(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32BigEndian(value);

    private static byte[] ReadBytes(Stream stream, int length)
    {
        byte[] result = new byte[length];
        stream.ReadExactly(result);
        return result;
    }

    private static bool ReadBoxHeader(Stream stream, long limit, out uint type, out long start, out long end)
    {
        type = 0;
        start = end = 0;
        long origin = stream.Position;
        if (origin > limit - 8)
            return false;
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);
        ulong length = BinaryPrimitives.ReadUInt32BigEndian(header);
        type = FourCc(header[4..]);
        if (length == 1)
        {
            if (stream.Position > limit - 8)
                return false;
            stream.ReadExactly(header);
            length = BinaryPrimitives.ReadUInt64BigEndian(header);
        }
        else if (length == 0)
            length = (ulong)(limit - origin);
        if (length > (ulong)(limit - origin) || length < (ulong)(stream.Position - origin))
            return false;
        start = stream.Position;
        end = origin + (long)length;
        return true;
    }

    private static IEnumerable<(uint Type, byte[] Data)> Boxes(byte[] data, int offset = 0)
    {
        using var stream = new MemoryStream(data, writable: false);
        stream.Position = offset;
        while (ReadBoxHeader(stream, stream.Length, out uint type, out long start, out long end))
        {
            yield return (type, ReadBytes(stream, (int)(end - start)));
            stream.Position = end;
        }
    }

    private sealed class IsoImageItems
    {
        private readonly Dictionary<uint, Item> items = [];
        private readonly List<(uint Type, byte[] Data)> properties = [];
        private readonly List<(uint Type, uint From, uint[] To)> references = [];
        private readonly long origin;
        private byte[] locations = [];
        private byte[] itemData = [];
        private uint primary;

        private sealed class Item
        {
            public uint Type;
            public readonly List<int> Properties = [];
        }

        public IsoImageItems(byte[] metadata, long origin)
        {
            this.origin = origin;
            if (metadata.Length < 4 || metadata[0] != 0)
                return;
            foreach ((uint type, byte[] data) in Boxes(metadata, 4))
            {
                if (type == FourCc("iinf"u8))
                    ReadItemInfo(data);
                else if (type == FourCc("iloc"u8))
                    locations = data;
                else if (type == FourCc("idat"u8))
                    itemData = data;
                else if (type == FourCc("pitm"u8) && data.Length >= 6)
                {
                    var reader = new MetadataReader(data.AsSpan(4));
                    primary = data[0] == 0 ? reader.UInt16() : reader.UInt32();
                }
                else if (type == FourCc("iref"u8))
                    ReadReferences(data);
                else if (type == FourCc("iprp"u8))
                    ReadProperties(data);
            }
        }

        private Item GetItem(uint id)
        {
            if (!items.TryGetValue(id, out Item? item))
                items.Add(id, item = new Item());
            return item;
        }

        private void ReadItemInfo(byte[] data)
        {
            if (data.Length < 6)
                return;
            foreach ((uint type, byte[] entry) in Boxes(data, data[0] == 0 ? 6 : 8))
            {
                if (type != FourCc("infe"u8) || entry.Length < 12 || entry[0] is not (2 or 3))
                    continue;
                var reader = new MetadataReader(entry.AsSpan(4));
                uint id = entry[0] == 2 ? reader.UInt16() : reader.UInt32();
                ushort protection = reader.UInt16();
                uint itemType = reader.UInt32();
                if (reader.Valid && protection == 0)
                    GetItem(id).Type = itemType;
            }
        }

        private void ReadReferences(byte[] data)
        {
            if (data.Length < 4 || data[0] > 1)
                return;
            foreach ((uint type, byte[] entry) in Boxes(data, 4))
            {
                var reader = new MetadataReader(entry);
                uint from = data[0] == 0 ? reader.UInt16() : reader.UInt32();
                int count = reader.UInt16();
                if (count > reader.Remaining / (data[0] == 0 ? 2 : 4))
                    continue;
                uint[] targets = new uint[count];
                for (int index = 0; index < count; index++)
                    targets[index] = data[0] == 0 ? reader.UInt16() : reader.UInt32();
                if (reader.Valid)
                    references.Add((type, from, targets));
            }
        }

        private void ReadProperties(byte[] data)
        {
            foreach ((uint type, byte[] entry) in Boxes(data))
            {
                if (type == FourCc("ipco"u8))
                    properties.AddRange(Boxes(entry));
                else if (type == FourCc("ipma"u8) && entry.Length >= 8 && entry[0] <= 1)
                {
                    var reader = new MetadataReader(entry.AsSpan(4));
                    uint count = reader.UInt32();
                    if (count > 65536)
                        continue;
                    for (uint index = 0; index < count && reader.Valid; index++)
                    {
                        uint id = entry[0] == 0 ? reader.UInt16() : reader.UInt32();
                        int associations = reader.Byte();
                        Item item = GetItem(id);
                        for (int association = 0; association < associations && reader.Valid; association++)
                        {
                            int property = (entry[3] & 1) == 0 ? reader.Byte() & 0x7f : reader.UInt16() & 0x7fff;
                            if (property > 0)
                                item.Properties.Add(property - 1);
                        }
                    }
                }
            }
        }

        private IEnumerable<byte[]> Properties(uint id, uint type) => items.TryGetValue(id, out Item? item)
            ? item.Properties.Where(index => index < properties.Count && properties[index].Type == type)
                .Select(index => properties[index].Data)
            : [];

        private byte[]? Profile(uint id) => Properties(id, FourCc("colr"u8))
            .Where(data => data.Length > 4 && (FourCc(data) == FourCc("prof"u8) || FourCc(data) == FourCc("rICC"u8)))
            .Select(data => data[4..]).FirstOrDefault();

        private int Primaries(uint id) => Properties(id, FourCc("colr"u8))
            .Where(data => data.Length >= 11 && FourCc(data) == FourCc("nclx"u8))
            .Select(data => (int)BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4))).FirstOrDefault();

        public (uint Item, int Width, int Height)? FindThumbnail(PhotoPreviewRequest request)
        {
            (uint Item, int Width, int Height)? selected = null;
            double distance = double.PositiveInfinity;
            foreach (var reference in references)
            {
                if (reference.Type != FourCc("thmb"u8) || !reference.To.Contains(primary) || reference.From == primary)
                    continue;
                byte[]? size = Properties(reference.From, FourCc("ispe"u8)).FirstOrDefault();
                if (size is not { Length: >= 12 })
                    continue;
                uint width = BinaryPrimitives.ReadUInt32BigEndian(size.AsSpan(4));
                uint height = BinaryPrimitives.ReadUInt32BigEndian(size.AsSpan(8));
                if (width == 0 || height == 0 || (ulong)width * height > 16 * 1024 * 1024)
                    continue;
                byte[]? rotation = Properties(reference.From, FourCc("irot"u8)).FirstOrDefault();
                if (rotation is { Length: > 0 } && (rotation[0] & 1) != 0)
                    (width, height) = (height, width);
                double candidate = request.ResolutionDistance(width, height);
                if (request.IsBetter(width, height) && candidate < distance)
                {
                    selected = (reference.From, (int)width, (int)height);
                    distance = candidate;
                }
            }
            return selected;
        }

        public GainMapSource? FindGainMap(Stream stream, uint? imageItem = null)
        {
            uint selected = imageItem ?? primary;
            foreach ((uint id, Item item) in items)
            {
                if (item.Type != FourCc("tmap"u8))
                    continue;
                uint[]? inputs = references.FirstOrDefault(reference => reference.Type == FourCc("dimg"u8) && reference.From == id).To;
                if (inputs is not { Length: 2 } || (selected != id && selected != inputs[0]))
                    continue;
                byte[] data = ReadItem(stream, id);
                if (data.Length > 1 && data[0] == 0 && TryReadIso(data.AsSpan(1), out GainMap metadata))
                    return new GainMapSource(metadata, inputs[0], inputs[1], BaseProfile: Profile(inputs[0]),
                        MathProfile: Profile(id), MathPrimaries: Primaries(id));
            }

            foreach (var reference in references)
            {
                if (reference.Type != FourCc("auxl"u8) || !reference.To.Contains(selected))
                    continue;
                string? auxiliary = Properties(reference.From, FourCc("auxC"u8)).Where(data => data.Length > 4)
                    .Select(data => Encoding.ASCII.GetString(data, 4, data.Length - 4).TrimEnd('\0')).FirstOrDefault();
                bool gain = auxiliary is
                        "urn:com:samsung:photo:2024:aux:hdrgainmap" or "urn:com:apple:photo:2020:aux:hdrgainmap" or
                        "urn:iso:std:iso:ts:21496:-1";
                if (!gain)
                    continue;
                float? appleHeadroom = selected == primary && auxiliary == "urn:com:apple:photo:2020:aux:hdrgainmap"
                    ? PhotoHdrMetadata.ReadAppleHeadroom(stream) : null;
                foreach (var description in references)
                {
                    if (description.Type == FourCc("cdsc"u8) && description.To.Contains(reference.From) &&
                        items.TryGetValue(description.From, out Item? item) && item.Type == FourCc("mime"u8) &&
                        TryReadXmp(ReadItem(stream, description.From), out GainMap metadata, appleHeadroom))
                        return new GainMapSource(metadata, selected, reference.From, BaseProfile: Profile(selected));
                }
                if (appleHeadroom is > 1)
                    return new GainMapSource(AppleMetadata(appleHeadroom.Value), selected, reference.From, BaseProfile: Profile(selected));
            }

            return null;
        }

        private byte[] ReadItem(Stream stream, uint requested)
        {
            if (locations.Length < 8 || locations[0] > 2)
                return [];
            byte version = locations[0];
            var reader = new MetadataReader(locations.AsSpan(4));
            byte widths = reader.Byte();
            int offsetSize = widths >> 4, lengthSize = widths & 15;
            widths = reader.Byte();
            int baseSize = widths >> 4, indexSize = version == 0 ? 0 : widths & 15;
            uint count = version < 2 ? reader.UInt16() : reader.UInt32();
            if (count > 65536)
                return [];
            for (uint index = 0; index < count && reader.Valid; index++)
            {
                uint id = version < 2 ? reader.UInt16() : reader.UInt32();
                int method = version == 0 ? 0 : reader.UInt16() & 15;
                int dataReference = reader.UInt16();
                ulong baseOffset = reader.Unsigned(baseSize);
                int extentCount = reader.UInt16();
                using var result = new MemoryStream();
                for (int extent = 0; extent < extentCount && reader.Valid; extent++)
                {
                    reader.Unsigned(indexSize);
                    ulong offset = reader.Unsigned(offsetSize);
                    ulong length = reader.Unsigned(lengthSize);
                    if (id != requested)
                        continue;
                    if (!reader.Valid || dataReference != 0 || method > 1 || offset > ulong.MaxValue - baseOffset ||
                        length > MaxContainerMetadataBytes || result.Length + (long)length > MaxContainerMetadataBytes)
                        return [];
                    offset += baseOffset;
                    if (method == 1)
                    {
                        if (offset > (ulong)itemData.Length || length > (ulong)itemData.Length - offset)
                            return [];
                        result.Write(itemData.AsSpan((int)offset, (int)length));
                    }
                    else
                    {
                        if (offset > (ulong)(stream.Length - origin) || length > (ulong)(stream.Length - origin) - offset)
                            return [];
                        stream.Position = origin + (long)offset;
                        result.Write(ReadBytes(stream, (int)length));
                    }
                }
                if (id == requested)
                    return reader.Valid ? result.ToArray() : [];
            }

            return [];
        }
    }
}