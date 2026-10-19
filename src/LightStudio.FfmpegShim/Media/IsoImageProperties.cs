using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.Logging;

namespace LightStudio.FfmpegShim.Media;

internal sealed class IsoImageProperties
{
    private const int MaxMetadataBytes = 8 * 1024 * 1024;
    private readonly Dictionary<uint, Item> items = new();

    public uint? PrimaryItemId { get; private set; }

    public Item Get(uint itemId) => items.GetValueOrDefault(itemId);

    public uint? FindAlpha(uint itemId)
    {
        foreach (var entry in items)
            if (entry.Key != itemId && entry.Value.IsAlpha && entry.Value.AlphaFor.Contains(itemId))
                return entry.Key;
        return null;
    }

    public bool IsPremultiplied(uint itemId, uint alphaId) => Get(itemId)?.PremultipliedWith.Contains(alphaId) == true;

    /// <summary>The primary item's cropped pixel size, or zero when it declares none.</summary>
    public (int Width, int Height) PrimaryPixelSize
    {
        get
        {
            Item primary = PrimaryItemId is { } itemId ? Get(itemId) : null;
            if (primary is not { Width: > 0, Height: > 0 })
                return default;
            int width = primary.Width;
            int height = primary.Height;
            if (primary.TryGetCrop(width, height, out var crop))
            {
                width -= (int)(crop.Left + crop.Right);
                height -= (int)(crop.Top + crop.Bottom);
            }
            return (width, height);
        }
    }

    internal static async Task<(int Width, int Height)> ReadPrimaryPixelSizeAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        if (!stream.CanRead || !stream.CanSeek)
            return default;
        long origin = stream.Position;
        try
        {
            byte[] signature = await ExifBlock.ReadRangeAsync(stream, origin, 8, cancellationToken).ConfigureAwait(false);
            if (signature.Length != 8 || !signature.AsSpan(4).SequenceEqual("ftyp"u8))
                return default;
            long offset = origin;
            long end = stream.Length;
            for (int count = 0; offset <= end - 8 && count < 4096; count++)
            {
                byte[] header = await ExifBlock.ReadRangeAsync(stream, offset, (int)Math.Min(16, end - offset),
                    cancellationToken).ConfigureAwait(false);
                if (header.Length < 8)
                    return default;
                ulong length = BinaryPrimitives.ReadUInt32BigEndian(header);
                int headerSize = 8;
                if (length == 1)
                {
                    if (header.Length < 16)
                        return default;
                    length = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                    headerSize = 16;
                }
                else if (length == 0)
                    length = (ulong)(end - offset);
                if (length < (ulong)headerSize || length > (ulong)(end - offset))
                    return default;
                long payloadLength = (long)length - headerSize;
                if (header.AsSpan(4, 4).SequenceEqual("meta"u8) && payloadLength is > 4 and <= MaxMetadataBytes)
                {
                    byte[] metadata = await ExifBlock.ReadRangeAsync(stream, offset + headerSize, (int)payloadLength,
                        cancellationToken).ConfigureAwait(false);
                    IsoImageProperties properties = metadata.Length > 4 && metadata[0] == 0 ? Parse(metadata) : null;
                    return properties?.PrimaryPixelSize ?? default;
                }
                offset += (long)length;
            }
            return default;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    public static IsoImageProperties Read(Stream stream)
    {
        if (!stream.CanSeek)
            return null;
        long origin = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[12];
            if (stream.ReadAtLeast(header, header.Length, false) != header.Length || !header.Slice(4, 4).SequenceEqual("ftyp"u8))
                return null;
            stream.Position = origin;
            while (ExifBlock.TryReadBoxHeader(stream, stream.Length, out uint type, out long start, out long end))
            {
                if (type == FourCc("meta"u8) && end - start is > 4 and <= MaxMetadataBytes)
                {
                    var metadata = new byte[(int)(end - start)];
                    if (stream.ReadAtLeast(metadata, metadata.Length, false) != metadata.Length)
                        return null;
                    return Parse(metadata);
                }
                stream.Position = end;
            }
            return null;
        }
        catch (IOException exception)
        {
            AppLog.Warning("Photo.Metadata", "Could not read image item properties.", exception);
            return null;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static IsoImageProperties Parse(ReadOnlySpan<byte> metadata)
    {
        var result = new IsoImageProperties();
        ReadOnlySpan<byte> properties = default;
        ReadOnlySpan<byte> references = default;
        int offset = 4;
        while (ExifBlock.TryReadBox(metadata, ref offset, out uint type, out ReadOnlySpan<byte> data))
        {
            if (type == FourCc("pitm"u8) && data.Length >= 6 && data[0] <= 1)
            {
                if (data[0] == 1 && data.Length < 8)
                    return null;
                result.PrimaryItemId = data[0] == 0 ? BinaryPrimitives.ReadUInt16BigEndian(data[4..])
                    : BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
            }
            else if (type == FourCc("iprp"u8))
                properties = data;
            else if (type == FourCc("iref"u8))
                references = data;
        }

        var entries = new List<(uint Type, byte[] Data)> { default };
        offset = 0;
        while (ExifBlock.TryReadBox(properties, ref offset, out uint type, out ReadOnlySpan<byte> data))
        {
            if (type != FourCc("ipco"u8))
                continue;
            int propertyOffset = 0;
            while (ExifBlock.TryReadBox(data, ref propertyOffset, out uint propertyType, out ReadOnlySpan<byte> property))
            {
                if (entries.Count > 32767)
                    return null;
                bool supported = propertyType == FourCc("clap"u8) && property.Length == 32 ||
                    propertyType == FourCc("pasp"u8) && property.Length == 8 ||
                    propertyType == FourCc("auxC"u8) && property.Length is > 4 and <= 256 && property[0] == 0 ||
                    propertyType == FourCc("ispe"u8) && property.Length == 12 && property[0] == 0;
                entries.Add(supported ? (propertyType, property.ToArray()) : default);
            }
        }

        offset = 0;
        while (ExifBlock.TryReadBox(properties, ref offset, out uint type, out ReadOnlySpan<byte> data))
        {
            if (type != FourCc("ipma"u8))
                continue;
            if (data.Length < 8 || data[0] > 1)
                return null;
            int idBytes = data[0] == 0 ? 2 : 4;
            int associationBytes = (data[3] & 1) == 0 ? 1 : 2;
            uint count = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
            int cursor = 8;
            if (count > (data.Length - cursor) / (idBytes + 1))
                return null;
            for (uint entry = 0; entry < count; entry++)
            {
                if (cursor > data.Length - idBytes - 1)
                    return null;
                uint itemId = idBytes == 2 ? BinaryPrimitives.ReadUInt16BigEndian(data[cursor..])
                    : BinaryPrimitives.ReadUInt32BigEndian(data[cursor..]);
                cursor += idBytes;
                int associations = data[cursor++];
                if (cursor > data.Length - associations * associationBytes)
                    return null;
                if (!result.items.TryGetValue(itemId, out Item item))
                {
                    if (result.items.Count >= 65536)
                        return null;
                    result.items[itemId] = item = new Item();
                }
                for (int association = 0; association < associations; association++)
                {
                    int propertyIndex = associationBytes == 1 ? data[cursor] & 0x7f
                        : BinaryPrimitives.ReadUInt16BigEndian(data[cursor..]) & 0x7fff;
                    cursor += associationBytes;
                    if (propertyIndex >= entries.Count)
                        return null;
                    var property = entries[propertyIndex];
                    if (property.Type == FourCc("clap"u8))
                        item.aperture = property.Data;
                    else if (property.Type == FourCc("ispe"u8))
                    {
                        uint width = BinaryPrimitives.ReadUInt32BigEndian(property.Data.AsSpan(4));
                        uint height = BinaryPrimitives.ReadUInt32BigEndian(property.Data.AsSpan(8));
                        if (width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue)
                        {
                            item.Width = (int)width;
                            item.Height = (int)height;
                        }
                    }
                    else if (property.Type == FourCc("pasp"u8))
                    {
                        uint horizontal = BinaryPrimitives.ReadUInt32BigEndian(property.Data);
                        uint vertical = BinaryPrimitives.ReadUInt32BigEndian(property.Data.AsSpan(4));
                        if (horizontal > 0 && vertical > 0)
                            item.PixelAspectRatio = horizontal / (double)vertical;
                    }
                    else if (property.Type == FourCc("auxC"u8))
                    {
                        ReadOnlySpan<byte> name = property.Data.AsSpan(4);
                        int terminator = name.IndexOf((byte)0);
                        if (terminator >= 0)
                            name = name[..terminator];
                        item.IsAlpha = name.SequenceEqual("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha"u8) ||
                            name.SequenceEqual("urn:mpeg:hevc:2015:auxid:1"u8);
                    }
                }
            }
        }
        if (references.Length >= 4 && references[0] <= 1)
        {
            int idBytes = references[0] == 0 ? 2 : 4;
            offset = 4;
            while (ExifBlock.TryReadBox(references, ref offset, out uint type, out ReadOnlySpan<byte> data))
            {
                if (data.Length < idBytes + 2 || type != FourCc("auxl"u8) && type != FourCc("prem"u8))
                    continue;
                uint from = idBytes == 2 ? BinaryPrimitives.ReadUInt16BigEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);
                int count = BinaryPrimitives.ReadUInt16BigEndian(data[idBytes..]);
                if (count > (data.Length - idBytes - 2) / idBytes || !result.items.TryGetValue(from, out Item item))
                    continue;
                for (int index = 0; index < count; index++)
                {
                    ReadOnlySpan<byte> target = data[(idBytes + 2 + index * idBytes)..];
                    uint to = idBytes == 2 ? BinaryPrimitives.ReadUInt16BigEndian(target) : BinaryPrimitives.ReadUInt32BigEndian(target);
                    if (type == FourCc("auxl"u8))
                        item.AlphaFor.Add(to);
                    else
                        item.PremultipliedWith.Add(to);
                }
            }
        }
        return result.items.Count == 0 ? null : result;
    }

    private static uint FourCc(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32BigEndian(value);

    internal sealed class Item
    {
        internal byte[] aperture;
        internal bool IsAlpha;
        internal readonly HashSet<uint> AlphaFor = [];
        internal readonly HashSet<uint> PremultipliedWith = [];
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public double PixelAspectRatio { get; internal set; }

        public bool TryGetCrop(int width, int height, out (uint Top, uint Bottom, uint Left, uint Right) crop)
        {
            crop = default;
            if (aperture is null || width <= 0 || height <= 0)
                return false;
            ReadOnlySpan<byte> data = aperture;
            uint widthDenominator = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
            uint heightDenominator = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
            uint horizontalDenominator = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
            uint verticalDenominator = BinaryPrimitives.ReadUInt32BigEndian(data[28..]);
            if (widthDenominator == 0 || heightDenominator == 0 || horizontalDenominator == 0 || verticalDenominator == 0)
                return false;
            double cleanWidth = BinaryPrimitives.ReadUInt32BigEndian(data) / (double)widthDenominator;
            double cleanHeight = BinaryPrimitives.ReadUInt32BigEndian(data[8..]) / (double)heightDenominator;
            double horizontal = BinaryPrimitives.ReadInt32BigEndian(data[16..]) / (double)horizontalDenominator;
            double vertical = BinaryPrimitives.ReadInt32BigEndian(data[24..]) / (double)verticalDenominator;
            double left = Math.Floor((width - cleanWidth) / 2 + horizontal + 0.5);
            double top = Math.Floor((height - cleanHeight) / 2 + vertical + 0.5);
            cleanWidth = Math.Floor(cleanWidth + 0.5);
            cleanHeight = Math.Floor(cleanHeight + 0.5);
            if (cleanWidth <= 0 || cleanHeight <= 0 || left < 0 || top < 0 ||
                left + cleanWidth > width || top + cleanHeight > height)
                return false;
            crop = ((uint)top, (uint)(height - top - cleanHeight), (uint)left, (uint)(width - left - cleanWidth));
            return (crop.Top | crop.Bottom | crop.Left | crop.Right) != 0;
        }
    }
}