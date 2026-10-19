#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.Logging;

namespace LightStudio.FfmpegShim.Media;

/// <summary>
/// Finds the TIFF block an EXIF profile is written as, wherever the picture's container happens to
/// keep it.
/// </summary>
/// <remarks>
/// <para>
/// Every still format carries the same TIFF-structured block; what differs is only how it is
/// wrapped. A JPEG puts it in an APP1 segment, a PNG in an <c>eXIf</c> chunk, a WebP in a RIFF
/// chunk of its own, and HEIF, AVIF and JPEG XL in an ISO base media item whose bytes are located
/// through the <c>meta</c> box rather than sitting anywhere in particular.
/// </para>
/// <para>
/// Metadata near the front is read from the file's head. Later metadata is located by seeking
/// over container segments, chunks, or boxes without reading the compressed picture data.
/// </para>
/// </remarks>
internal static class ExifBlock
{
    /// <summary>How much of the file is read before falling back to walking its box tree.</summary>
    private const int HeaderBytes = 128 * 1024;

    /// <summary>
    /// Largest metadata box read whole. A <c>meta</c> box is an index of items and a few thousand
    /// bytes even for a picture cut into a hundred tiles; one far larger is not worth trusting.
    /// </summary>
    private const int MaxMetaBytes = 8 * 1024 * 1024;

    /// <summary>Largest EXIF block accepted, which is far more than any camera writes.</summary>
    private const int MaxBlockBytes = 4 * 1024 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Boxes are named by four ASCII bytes read as one big-endian integer.</summary>
    private static uint BoxType(ReadOnlySpan<byte> name) => BinaryPrimitives.ReadUInt32BigEndian(name);

    /// <summary>
    /// The TIFF block, read out of the file itself where the head of it does not hold the answer.
    /// Empty when the picture declares no EXIF, or when the stream cannot be seeked to look.
    /// </summary>
    public static byte[] Read(Stream stream) => Read(stream, out _);

    internal static async Task<(byte[] Tiff, bool Standalone)> ReadAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        if (!stream.CanRead || !stream.CanSeek)
            return ([], false);

        int remainingBytes = 8 * 1024 * 1024;
        async Task<byte[]> ReadBudgetedAsync(long offset, int length)
        {
            remainingBytes -= Math.Max(0, length);
            if (remainingBytes < 0)
                throw new InvalidDataException("EXIF extraction exceeds the byte budget.");
            return await ReadRangeAsync(stream, offset, length, cancellationToken).ConfigureAwait(false);
        }

        long origin = stream.Position;
        try
        {
            byte[] header = await ReadBudgetedAsync(0, (int)Math.Min(stream.Length, HeaderBytes)).ConfigureAwait(false);
            if (IsTiff(header))
                return (header, true);
            byte[] found = Find(header).ToArray();
            if (found.Length > 0)
                return (found, false);

            bool jpeg = header.Length >= 2 && header[0] == 0xFF && header[1] == 0xD8;
            bool png = header.AsSpan().StartsWith(PngSignature);
            bool webp = header.Length >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8);
            bool boxes = IsIsoBaseMedia(header);
            if (!jpeg && !png && !webp && !boxes)
                return ([], false);

            long offset = jpeg ? 2 : png ? 8 : webp ? 12 : 0;
            long end = webp ? Math.Min(stream.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4))) : stream.Length;
            byte[]? meta = null;
            for (int count = 0; offset < end && count < 4096; count++)
            {
                byte[] chunk = await ReadBudgetedAsync(offset, (int)Math.Min(16, end - offset)).ConfigureAwait(false);
                long payload;
                long length;
                long next;
                bool isExif;
                if (jpeg)
                {
                    if (chunk.Length < 4 || chunk[0] != 0xFF || chunk[1] is 0xD9 or 0xDA)
                        break;
                    if (chunk[1] == 0xFF) { offset++; continue; }
                    if (chunk[1] is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) { offset += 2; continue; }
                    length = BinaryPrimitives.ReadUInt16BigEndian(chunk.AsSpan(2)) - 2;
                    payload = offset + 4;
                    next = payload + length;
                    isExif = chunk[1] == 0xE1;
                }
                else if (png || webp)
                {
                    if (chunk.Length < 8 || png && chunk.AsSpan(4, 4).SequenceEqual("IEND"u8))
                        break;
                    length = png ? BinaryPrimitives.ReadUInt32BigEndian(chunk) : BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
                    payload = offset + 8;
                    next = payload + length + (png ? 4 : length & 1);
                    isExif = png ? chunk.AsSpan(4, 4).SequenceEqual("eXIf"u8) : chunk.AsSpan(0, 4).SequenceEqual("EXIF"u8);
                }
                else
                {
                    if (chunk.Length < 8)
                        break;
                    long size = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                    int headerSize = 8;
                    if (size == 1)
                    {
                        if (chunk.Length < 16)
                            break;
                        ulong extended = BinaryPrimitives.ReadUInt64BigEndian(chunk.AsSpan(8));
                        if (extended > (ulong)(end - offset))
                            break;
                        size = (long)extended;
                        headerSize = 16;
                    }
                    if (size == 0) size = end - offset;
                    payload = offset + headerSize;
                    length = size - headerSize;
                    next = offset + size;
                    isExif = chunk.AsSpan(4, 4).SequenceEqual("Exif"u8);
                    if (length is > 4 and <= MaxBlockBytes && chunk.AsSpan(4, 4).SequenceEqual("meta"u8))
                        meta = await ReadBudgetedAsync(payload, (int)length).ConfigureAwait(false);
                }
                if (length < 0 || next <= offset || next > end)
                    break;
                if (isExif && length is > 0 and <= MaxBlockBytes)
                {
                    byte[] block = await ReadBudgetedAsync(payload, (int)length).ConfigureAwait(false);
                    found = (boxes ? ExifItem(block) : AsTiff(block)).ToArray();
                    if (found.Length > 0)
                        return (found, false);
                }
                offset = next;
            }

            if (meta is { Length: >= 4 } && TryFindExifExtent(meta.AsSpan(4), out int construction, out long extent, out int itemLength))
            {
                byte[] item = construction == 0
                    ? await ReadBudgetedAsync(extent, itemLength).ConfigureAwait(false)
                    : construction == 1 ? ItemData(meta.AsSpan(4), extent, itemLength).ToArray() : [];
                return (ExifItem(item).ToArray(), false);
            }
            return ([], false);
        }
        finally
        {
            stream.Position = origin;
        }
    }

    internal static async Task<byte[]> ReadRangeAsync(Stream stream, long offset, int length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0 || length <= 0 || length > MaxBlockBytes || offset > stream.Length - length)
            return [];
        stream.Position = offset;
        byte[] buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(read, Math.Min(65536, length - read)), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                return [];
            read += count;
        }
        return buffer;
    }

    internal static byte[] Read(Stream stream, out bool standaloneTiff)
    {
        standaloneTiff = false;
        if (stream is null || !stream.CanRead || !stream.CanSeek)
            return [];

        long origin = stream.Position;
        try
        {
            stream.Position = 0;
            int headerLength = (int)Math.Min(stream.Length, HeaderBytes);
            if (headerLength <= 0)
                return [];

            var header = new byte[headerLength];
            int read = stream.ReadAtLeast(header, headerLength, throwOnEndOfStream: false);

            standaloneTiff = IsTiff(header.AsSpan(0, read));
            ReadOnlySpan<byte> found = Find(header.AsSpan(0, read));
            if (!found.IsEmpty)
                return found.ToArray();

            if (read >= 2 && header[0] == 0xFF && header[1] == 0xD8)
                return ReadJpeg(stream);
            if (header.AsSpan(0, read).StartsWith(PngSignature))
                return ReadChunks(stream, true, stream.Length);
            if (read >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8))
                return ReadChunks(stream, false, Math.Min(stream.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4))));

            return IsIsoBaseMedia(header.AsSpan(0, read)) ? ReadFromBoxTree(stream) : [];
        }
        catch (IOException exception)
        {
            AppLog.Warning("Photo.Metadata", "Could not read the photo EXIF block.", exception);
            return [];
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static byte[] ReadJpeg(Stream stream)
    {
        long offset = 2;
        Span<byte> header = stackalloc byte[4];
        while (offset <= stream.Length - header.Length)
        {
            stream.Position = offset;
            if (stream.ReadAtLeast(header, header.Length, false) < header.Length || header[0] != 0xFF)
                break;
            byte marker = header[1];
            if (marker == 0xFF)
            {
                offset++;
                continue;
            }
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                offset += 2;
                continue;
            }
            if (marker is 0xD9 or 0xDA)
                break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
            if (length < 2 || offset > stream.Length - 2 - length)
                break;
            if (marker == 0xE1 && AsTiff(ReadRange(stream, offset + 4, length - 2)) is { IsEmpty: false } tiff)
                return tiff.ToArray();
            offset += 2 + length;
        }
        return [];
    }

    private static byte[] ReadChunks(Stream stream, bool png, long end)
    {
        long offset = png ? 8 : 12;
        Span<byte> header = stackalloc byte[8];
        while (offset <= end - header.Length)
        {
            stream.Position = offset;
            if (stream.ReadAtLeast(header, header.Length, false) < header.Length)
                break;
            uint length = png ? BinaryPrimitives.ReadUInt32BigEndian(header)
                : BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            ReadOnlySpan<byte> name = png ? header[4..] : header[..4];
            int padding = png ? 4 : (int)(length & 1);
            if (length > end - offset - header.Length - padding || (png && name.SequenceEqual("IEND"u8)))
                break;
            if (length <= MaxBlockBytes && name.SequenceEqual(png ? "eXIf"u8 : "EXIF"u8) &&
                AsTiff(ReadRange(stream, offset + header.Length, (int)length)) is { IsEmpty: false } tiff)
                return tiff.ToArray();
            offset += header.Length + (long)length + padding;
        }
        return [];
    }

    /// <summary>
    /// The TIFF block, as far as the bytes in hand can say. Used where there is no stream to seek
    /// in, and by <see cref="Read"/> for the formats that keep their metadata at the front.
    /// </summary>
    public static ReadOnlySpan<byte> Find(ReadOnlySpan<byte> file)
    {
        if (IsTiff(file))
            return file;

        if (file.Length >= 4 && file[0] == 0xFF && file[1] == 0xD8)
            return FindInJpeg(file);

        if (file.StartsWith(PngSignature))
            return FindInPng(file);

        if (file.Length >= 12 && file[..4].SequenceEqual("RIFF"u8) && file[8..12].SequenceEqual("WEBP"u8))
            return FindInRiff(file[12..]);

        return FindInIsoBaseMedia(file);
    }

    private static bool IsTiff(ReadOnlySpan<byte> block) =>
        block.Length >= 8 && (block[0] == 0x49 || block[0] == 0x4D) && block[0] == block[1];

    /// <summary>Whether the file opens with a box, which is the only thing worth walking for.</summary>
    private static bool IsIsoBaseMedia(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12)
            return false;

        uint type = BoxType(header[4..]);
        return type == BoxType("ftyp"u8) || type == BoxType("JXL "u8);
    }

    /// <summary>
    /// A block as the container handed it over, with the <c>Exif\0\0</c> marker some writers put in
    /// front of it stripped. Empty when what is there is not a TIFF block at all.
    /// </summary>
    private static ReadOnlySpan<byte> AsTiff(ReadOnlySpan<byte> block)
    {
        if (block.Length >= 6 && block[..4].SequenceEqual("Exif"u8) && block[4] == 0)
            block = block[6..];

        return IsTiff(block) ? block : default;
    }

    /// <summary>JPEG segments are a marker, a big-endian length covering itself, then the payload.</summary>
    private static ReadOnlySpan<byte> FindInJpeg(ReadOnlySpan<byte> file)
    {
        int offset = 2;
        while (offset + 4 <= file.Length)
        {
            if (file[offset] != 0xFF)
                break;

            byte marker = file[offset + 1];

            // Standalone markers carry no length, and SOS is followed by entropy-coded data that
            // is not segmented at all, so there is nothing left to walk.
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                offset += 2;
                continue;
            }

            if (marker == 0xD9 || marker == 0xDA)
                break;

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(file[(offset + 2)..]);
            if (segmentLength < 2 || offset + 2 + segmentLength > file.Length)
                break;

            ReadOnlySpan<byte> payload = file.Slice(offset + 4, segmentLength - 2);
            if (marker == 0xE1 && AsTiff(payload) is { IsEmpty: false } tiff)
                return tiff;

            offset += 2 + segmentLength;
        }

        return default;
    }

    /// <summary>PNG chunks are a big-endian length, a four-byte name, the data, then a checksum.</summary>
    private static ReadOnlySpan<byte> FindInPng(ReadOnlySpan<byte> file)
    {
        int offset = 8;
        while (offset + 12 <= file.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(file[offset..]);
            if (length > int.MaxValue - 12 || offset + 12 + length > file.Length)
                break;

            ReadOnlySpan<byte> name = file.Slice(offset + 4, 4);
            if (name.SequenceEqual("IEND"u8))
                break;

            if (name.SequenceEqual("eXIf"u8) &&
                AsTiff(file.Slice(offset + 8, (int)length)) is { IsEmpty: false } tiff)
            {
                return tiff;
            }

            offset += 12 + (int)length;
        }

        return default;
    }

    /// <summary>RIFF chunks are a four-byte name, a little-endian length, then data padded to even.</summary>
    private static ReadOnlySpan<byte> FindInRiff(ReadOnlySpan<byte> body)
    {
        int offset = 0;
        while (offset + 8 <= body.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 4)..]);
            if (length > int.MaxValue - 9 || offset + 8 + length > body.Length)
                break;

            if (body.Slice(offset, 4).SequenceEqual("EXIF"u8) &&
                AsTiff(body.Slice(offset + 8, (int)length)) is { IsEmpty: false } tiff)
            {
                return tiff;
            }

            offset += 8 + (int)length + ((int)length & 1);
        }

        return default;
    }

    /// <summary>
    /// HEIF, AVIF and JPEG XL, from bytes already in hand. Only answers for a file whose metadata
    /// happens to be near the front; <see cref="ReadFromBoxTree"/> is what handles the rest.
    /// </summary>
    private static ReadOnlySpan<byte> FindInIsoBaseMedia(ReadOnlySpan<byte> file)
    {
        ReadOnlySpan<byte> meta = default;
        int offset = 0;
        while (TryReadBox(file, ref offset, out uint type, out ReadOnlySpan<byte> payload))
        {
            // JPEG XL keeps it in a top-level box rather than as an item.
            if (type == BoxType("Exif"u8))
                return ExifItem(payload);

            if (type == BoxType("meta"u8))
            {
                meta = payload;
            }
        }

        // meta is a full box: a version byte and three flag bytes before its children.
        if (meta.Length < 4)
            return default;

        ReadOnlySpan<byte> children = meta[4..];
        if (!TryFindExifExtent(children, out int construction, out long extentOffset, out int length))
            return default;

        return construction switch
        {
            0 when extentOffset >= 0 && extentOffset <= file.Length - length =>
                ExifItem(file.Slice((int)extentOffset, length)),
            1 => ExifItem(ItemData(children, extentOffset, length)),
            _ => default,
        };
    }

    /// <summary>
    /// Walks the file's boxes over the stream, which is the only way to reach a <c>meta</c> box
    /// written after the picture data and an item stored in the middle of it.
    /// </summary>
    private static byte[] ReadFromBoxTree(Stream stream)
    {
        stream.Position = 0;
        long end = stream.Length;
        byte[]? meta = null;

        while (TryReadBoxHeader(stream, end, out uint type, out long payloadStart, out long payloadEnd))
        {
            long length = payloadEnd - payloadStart;
            if (type == BoxType("Exif"u8) && length is > 0 and <= MaxBlockBytes)
            {
                if (ExifItem(ReadRange(stream, payloadStart, (int)length)) is { IsEmpty: false } block)
                    return block.ToArray();
            }
            else if (type == BoxType("meta"u8) && length is > 4 and <= MaxMetaBytes)
            {
                meta = ReadRange(stream, payloadStart, (int)length);
            }

            stream.Position = payloadEnd;
        }

        if (meta is null || meta.Length < 4)
            return [];

        ReadOnlySpan<byte> children = meta.AsSpan(4);
        if (!TryFindExifExtent(children, out int construction, out long extentOffset, out int itemLength))
            return [];

        return construction switch
        {
            0 => ExifItem(ReadRange(stream, extentOffset, itemLength)).ToArray(),
            1 => ExifItem(ItemData(children, extentOffset, itemLength)).ToArray(),
            _ => [],
        };
    }

    /// <summary>
    /// An EXIF item opens with the number of bytes standing between it and the TIFF block, which is
    /// zero as often as it is the six of an <c>Exif\0\0</c> marker.
    /// </summary>
    private static ReadOnlySpan<byte> ExifItem(ReadOnlySpan<byte> item)
    {
        if (item.Length < 4)
            return default;

        uint headerOffset = BinaryPrimitives.ReadUInt32BigEndian(item);
        return headerOffset > (uint)item.Length - 4 ? default : AsTiff(item[(4 + (int)headerOffset)..]);
    }

    /// <summary>
    /// The bytes of an item the file chose to keep inside its own metadata box rather than beside
    /// the picture, which is what a construction method of 1 means.
    /// </summary>
    private static ReadOnlySpan<byte> ItemData(ReadOnlySpan<byte> children, long offset, int length)
    {
        int cursor = 0;
        while (TryReadBox(children, ref cursor, out uint type, out ReadOnlySpan<byte> payload))
        {
            if (type == BoxType("idat"u8) && offset >= 0 && length > 0 && offset <= payload.Length - length)
                return payload.Slice((int)offset, length);
        }

        return default;
    }

    private static byte[] ReadRange(Stream stream, long offset, int length)
    {
        if (offset < 0 || length <= 0 || length > MaxBlockBytes || offset > stream.Length - length)
            return [];

        stream.Position = offset;
        var buffer = new byte[length];
        int read = stream.ReadAtLeast(buffer, length, throwOnEndOfStream: false);
        return read == length ? buffer : buffer[..read];
    }

    /// <summary>
    /// Reads the header of the box at the stream's position, leaving the stream on its payload.
    /// A box whose declared size runs past the end of the file is taken to end there.
    /// </summary>
    internal static bool TryReadBoxHeader(
        Stream stream,
        long end,
        out uint type,
        out long payloadStart,
        out long payloadEnd)
    {
        type = 0;
        payloadStart = 0;
        payloadEnd = 0;

        long start = stream.Position;
        if (start < 0 || start > end - 8)
            return false;

        Span<byte> head = stackalloc byte[16];
        if (stream.ReadAtLeast(head[..8], 8, throwOnEndOfStream: false) < 8)
            return false;

        long size = BinaryPrimitives.ReadUInt32BigEndian(head);
        type = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
        int header = 8;

        if (size == 1)
        {
            if (stream.ReadAtLeast(head[8..], 8, throwOnEndOfStream: false) < 8)
                return false;

            size = (long)BinaryPrimitives.ReadUInt64BigEndian(head[8..]);
            header = 16;
        }
        else if (size == 0)
        {
            size = end - start;
        }

        if (size < header)
            return false;

        payloadStart = start + header;
        payloadEnd = Math.Min(start + size, end);
        return payloadEnd >= payloadStart;
    }

    /// <summary>
    /// Reads the box at <paramref name="offset"/> and steps past it. A box whose declared size runs
    /// past the data present still yields what there is, and ends the walk.
    /// </summary>
    internal static bool TryReadBox(
        ReadOnlySpan<byte> region,
        scoped ref int offset,
        out uint type,
        out ReadOnlySpan<byte> payload)
    {
        type = 0;
        payload = default;
        if (offset < 0 || offset > region.Length - 8)
            return false;

        long size = BinaryPrimitives.ReadUInt32BigEndian(region[offset..]);
        type = BinaryPrimitives.ReadUInt32BigEndian(region[(offset + 4)..]);
        int header = 8;

        if (size == 1)
        {
            if (offset > region.Length - 16)
                return false;

            size = (long)BinaryPrimitives.ReadUInt64BigEndian(region[(offset + 8)..]);
            header = 16;
        }
        else if (size == 0)
        {
            size = region.Length - offset;
        }

        if (size < header)
            return false;

        if (size > region.Length - offset)
        {
            payload = region[(offset + header)..];
            offset = region.Length;
            return true;
        }

        int end = offset + (int)size;
        payload = region[(offset + header)..end];
        offset = end;
        return true;
    }

    /// <summary>
    /// Asks the metadata box which of its items is the EXIF one and where its bytes were put.
    /// </summary>
    private static bool TryFindExifExtent(
        ReadOnlySpan<byte> children,
        out int construction,
        out long offset,
        out int length)
    {
        construction = 0;
        offset = 0;
        length = 0;

        ReadOnlySpan<byte> itemInfo = default;
        ReadOnlySpan<byte> itemLocation = default;
        int cursor = 0;
        while (TryReadBox(children, ref cursor, out uint type, out ReadOnlySpan<byte> payload))
        {
            if (type == BoxType("iinf"u8))
                itemInfo = payload;
            else if (type == BoxType("iloc"u8))
                itemLocation = payload;
        }

        return TryFindExifItemId(itemInfo, out uint itemId) &&
               TryLocateItem(itemLocation, itemId, out construction, out offset, out length);
    }

    /// <summary>The id of the item declared to hold an EXIF block, out of the item information box.</summary>
    private static bool TryFindExifItemId(ReadOnlySpan<byte> itemInfo, out uint itemId)
    {
        itemId = 0;
        if (itemInfo.Length < 6)
            return false;

        byte version = itemInfo[0];
        int position = version == 0 ? 6 : 8;
        if (position > itemInfo.Length)
            return false;

        ReadOnlySpan<byte> entries = itemInfo[position..];
        int offset = 0;
        while (TryReadBox(entries, ref offset, out uint type, out ReadOnlySpan<byte> entry))
        {
            if (type != BoxType("infe"u8) || entry.Length < 12)
                continue;

            // Only version 2 and later name the item by type; version 0 and 1 predate the item
            // types this is looking for.
            byte entryVersion = entry[0];
            if (entryVersion < 2)
                continue;

            int idSize = entryVersion == 2 ? 2 : 4;
            int cursor = 4;
            uint id = entryVersion == 2
                ? BinaryPrimitives.ReadUInt16BigEndian(entry[cursor..])
                : BinaryPrimitives.ReadUInt32BigEndian(entry[cursor..]);
            cursor += idSize + 2;
            if (cursor > entry.Length - 4)
                continue;

            if (BinaryPrimitives.ReadUInt32BigEndian(entry[cursor..]) == BoxType("Exif"u8))
            {
                itemId = id;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Where the item's bytes were put. Only the first extent is followed: an EXIF block is never
    /// split, and a reader that guessed at the rest would be inventing offsets.
    /// </summary>
    private static bool TryLocateItem(
        ReadOnlySpan<byte> itemLocation,
        uint itemId,
        out int construction,
        out long itemOffset,
        out int itemLength)
    {
        construction = 0;
        itemOffset = 0;
        itemLength = 0;
        if (itemLocation.Length < 8)
            return false;

        byte version = itemLocation[0];
        int cursor = 4;
        int offsetSize = itemLocation[cursor] >> 4;
        int lengthSize = itemLocation[cursor] & 0xF;
        int baseOffsetSize = itemLocation[cursor + 1] >> 4;
        int indexSize = version is 1 or 2 ? itemLocation[cursor + 1] & 0xF : 0;
        cursor += 2;

        int idSize = version < 2 ? 2 : 4;
        if (cursor > itemLocation.Length - idSize)
            return false;

        long count = version < 2
            ? BinaryPrimitives.ReadUInt16BigEndian(itemLocation[cursor..])
            : BinaryPrimitives.ReadUInt32BigEndian(itemLocation[cursor..]);
        cursor += idSize;

        for (long item = 0; item < count; item++)
        {
            if (cursor > itemLocation.Length - idSize)
                return false;

            uint id = version < 2
                ? BinaryPrimitives.ReadUInt16BigEndian(itemLocation[cursor..])
                : BinaryPrimitives.ReadUInt32BigEndian(itemLocation[cursor..]);
            cursor += idSize;

            int method = 0;
            if (version is 1 or 2)
            {
                if (cursor > itemLocation.Length - 2)
                    return false;

                method = BinaryPrimitives.ReadUInt16BigEndian(itemLocation[cursor..]) & 0xF;
                cursor += 2;
            }

            cursor += 2;
            if (!TryReadInteger(itemLocation, ref cursor, baseOffsetSize, out long baseOffset) ||
                cursor > itemLocation.Length - 2)
            {
                return false;
            }

            int extents = BinaryPrimitives.ReadUInt16BigEndian(itemLocation[cursor..]);
            cursor += 2;

            for (int extent = 0; extent < extents; extent++)
            {
                cursor += indexSize;
                if (!TryReadInteger(itemLocation, ref cursor, offsetSize, out long extentOffset) ||
                    !TryReadInteger(itemLocation, ref cursor, lengthSize, out long extentLength))
                {
                    return false;
                }

                long absolute = baseOffset + extentOffset;
                if (id == itemId && extent == 0 &&
                    absolute >= 0 && extentLength is > 0 and <= MaxBlockBytes)
                {
                    construction = method;
                    itemOffset = absolute;
                    itemLength = (int)extentLength;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Reads a big-endian integer of the width the surrounding box declared for it.</summary>
    private static bool TryReadInteger(ReadOnlySpan<byte> data, scoped ref int cursor, int size, out long value)
    {
        value = 0;
        if (size == 0)
            return true;

        if (size is not (4 or 8) || cursor < 0 || cursor > data.Length - size)
            return false;

        value = size == 4
            ? BinaryPrimitives.ReadUInt32BigEndian(data[cursor..])
            : (long)BinaryPrimitives.ReadUInt64BigEndian(data[cursor..]);
        cursor += size;
        return true;
    }
}
