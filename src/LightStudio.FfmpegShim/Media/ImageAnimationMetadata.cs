using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace LightStudio.FfmpegShim.Media;

internal sealed record ImageAnimationMetadata(long PlayCount, int? TrackId = null, int? AlphaTrackId = null,
    bool PremultipliedAlpha = false)
{
    public static ImageAnimationMetadata Read(Stream stream, CancellationToken cancellationToken)
    {
        if (!stream.CanSeek)
            return null;
        long origin = stream.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<byte> header = stackalloc byte[32];
            int count = stream.ReadAtLeast(header, header.Length, false);
            if (count < 12)
                return null;
            stream.Position = origin;
            if (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))
                return ReadGif(stream, cancellationToken);
            if (header[..8].SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]))
                return ReadPng(stream, cancellationToken);
            if (header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8))
                return ReadWebp(stream, cancellationToken);
            if (header[..2].SequenceEqual((ReadOnlySpan<byte>)[255, 10]) ||
                header[..12].SequenceEqual((ReadOnlySpan<byte>)[0, 0, 0, 12, 74, 88, 76, 32, 13, 10, 135, 10]))
                return ReadJxl(stream, cancellationToken);
            if (header.Slice(4, 4).SequenceEqual("ftyp"u8))
                return ReadIso(stream, cancellationToken);
            return null;
        }
        finally { stream.Position = origin; }
    }

    private static ImageAnimationMetadata ReadPng(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position += 8;
        Span<byte> chunk = stackalloc byte[8];
        while (stream.Position <= stream.Length - 12)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(chunk);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(chunk);
            if (length > stream.Length - stream.Position - 4)
                return null;
            if (chunk[4..].SequenceEqual("acTL"u8) && length == 8)
            {
                stream.ReadExactly(chunk);
                return BinaryPrimitives.ReadUInt32BigEndian(chunk) > 1
                    ? new ImageAnimationMetadata(BinaryPrimitives.ReadUInt32BigEndian(chunk[4..])) : null;
            }
            if (chunk[4..].SequenceEqual("IDAT"u8) || chunk[4..].SequenceEqual("IEND"u8))
                return null;
            stream.Position += length + 4L;
        }
        return null;
    }

    private static ImageAnimationMetadata ReadWebp(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position += 12;
        Span<byte> chunk = stackalloc byte[8];
        long plays = 1;
        int frames = 0;
        while (stream.Position <= stream.Length - 8)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(chunk);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            long end = stream.Position + length + (length & 1);
            if (end > stream.Length)
                return null;
            if (chunk[..4].SequenceEqual("ANIM"u8) && length == 6)
            {
                stream.ReadExactly(chunk[..6]);
                plays = BinaryPrimitives.ReadUInt16LittleEndian(chunk[4..]);
            }
            else if (chunk[..4].SequenceEqual("ANMF"u8) && ++frames > 1)
                return new ImageAnimationMetadata(plays);
            stream.Position = end;
        }
        return null;
    }

    private static ImageAnimationMetadata ReadGif(Stream stream, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[13];
        stream.ReadExactly(header);
        if ((header[10] & 128) != 0)
            stream.Position += 3L << ((header[10] & 7) + 1);
        Span<byte> block = stackalloc byte[255];
        int frames = 0;
        long plays = 1;
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int kind = stream.ReadByte();
            if (kind == 0x3b)
                return frames > 1 ? new ImageAnimationMetadata(plays) : null;
            if (kind == 0x2c)
            {
                stream.ReadExactly(block[..9]);
                if ((block[8] & 128) != 0)
                    stream.Position += 3L << ((block[8] & 7) + 1);
                if (stream.ReadByte() < 0)
                    return null;
                frames++;
                SkipGifBlocks(stream, block, cancellationToken);
            }
            else if (kind == 0x21)
            {
                int label = stream.ReadByte();
                int length = stream.ReadByte();
                if (length < 0)
                    return null;
                stream.ReadExactly(block[..length]);
                bool loop = label == 0xff && length == 11 &&
                    (block[..11].SequenceEqual("NETSCAPE2.0"u8) || block[..11].SequenceEqual("ANIMEXTS1.0"u8));
                if (loop)
                {
                    length = stream.ReadByte();
                    if (length < 0)
                        return null;
                    stream.ReadExactly(block[..length]);
                    if (length == 3 && block[0] == 1)
                    {
                        int repeats = BinaryPrimitives.ReadUInt16LittleEndian(block[1..]);
                        plays = repeats == 0 ? 0 : repeats + 1L;
                    }
                }
                if (length != 0)
                    SkipGifBlocks(stream, block, cancellationToken);
            }
            else
                return null;
        }
        return null;
    }

    private static void SkipGifBlocks(Stream stream, Span<byte> buffer, CancellationToken cancellationToken)
    {
        int length;
        while ((length = stream.ReadByte()) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(buffer[..length]);
        }
        if (length < 0)
            throw new EndOfStreamException("Incomplete GIF data block.");
    }

    private static ImageAnimationMetadata ReadJxl(Stream stream, CancellationToken cancellationToken)
    {
        long origin = stream.Position;
        Span<byte> prefix = stackalloc byte[256];
        stream.ReadExactly(prefix[..2]);
        stream.Position = origin;
        int count = 0;
        if (prefix[0] == 255 && prefix[1] == 10)
            count = stream.ReadAtLeast(prefix, prefix.Length, false);
        else
        {
            while (count < prefix.Length && ExifBlock.TryReadBoxHeader(stream, stream.Length,
                out uint type, out long start, out long end))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (type == Code("jxlc"u8) || type == Code("jxlp"u8))
                {
                    if (type == Code("jxlp"u8))
                    {
                        if (end - start < 4)
                            return null;
                        stream.Position += 4;
                    }
                    int take = (int)Math.Min(prefix.Length - count, end - stream.Position);
                    stream.ReadExactly(prefix.Slice(count, take));
                    count += take;
                }
                stream.Position = end;
            }
        }
        var bits = new JxlBits(prefix[..count]);
        if (bits.Read(16) != 0x0aff)
            return null;
        bits.SkipSize();
        if (bits.Read(1) != 0 || bits.Read(1) == 0)
            return null;
        bits.Read(3);
        if (bits.Read(1) != 0)
            bits.SkipSize();
        if (bits.Read(1) != 0)
            bits.SkipPreview();
        if (bits.Read(1) == 0)
            return null;
        bits.Number([100, 1000, 1, 1], [0, 0, 10, 30]);
        bits.Number([1, 1001, 1, 1], [0, 0, 8, 10]);
        return new ImageAnimationMetadata(bits.Number([0, 0, 0, 0], [0, 3, 16, 32]));
    }

    private static ImageAnimationMetadata ReadIso(Stream stream, CancellationToken cancellationToken)
    {
        while (ExifBlock.TryReadBoxHeader(stream, stream.Length, out uint type, out long start, out long end))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (type == Code("moov"u8) && end - start is > 0 and <= 8 * 1024 * 1024)
            {
                byte[] movie = new byte[(int)(end - start)];
                stream.ReadExactly(movie);
                int offset = 0;
                while (ExifBlock.TryReadBox(movie, ref offset, out type, out ReadOnlySpan<byte> track))
                {
                    if (type != Code("trak"u8) || !Find(Find(track, "tref"u8), "auxl"u8).IsEmpty)
                        continue;
                    ReadOnlySpan<byte> samples = Find(Find(Find(track, "mdia"u8), "minf"u8), "stbl"u8);
                    ReadOnlySpan<byte> sizes = Find(samples, "stsz"u8);
                    if (sizes.Length < 12)
                        sizes = Find(samples, "stz2"u8);
                    if (sizes.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(sizes[8..]) <= 1)
                        continue;
                    ReadOnlySpan<byte> header = Find(track, "tkhd"u8);
                    if (header.Length < 24 || header[0] > 1 || header[0] == 1 && header.Length < 36)
                        continue;
                    uint id = BinaryPrimitives.ReadUInt32BigEndian(header[(header[0] == 1 ? 20 : 12)..]);
                    if (id > int.MaxValue)
                        continue;
                    ulong duration = header[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(header[28..]) :
                        BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
                    ReadOnlySpan<byte> edits = Find(Find(track, "edts"u8), "elst"u8);
                    long plays = 0;
                    if (edits.Length >= 8)
                    {
                        plays = 1;
                        if ((edits[3] & 1) != 0 && BinaryPrimitives.ReadUInt32BigEndian(edits[4..]) == 1 &&
                            (edits[0] == 0 && edits.Length >= 20 || edits[0] == 1 && edits.Length >= 28))
                        {
                            ulong segment = edits[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(edits[8..]) :
                                BinaryPrimitives.ReadUInt32BigEndian(edits[8..]);
                            if (segment == 0)
                                throw new InvalidDataException("The animated image has a zero-length repeat segment.");
                            plays = duration == ulong.MaxValue || header[0] == 0 && duration == uint.MaxValue ? 0 :
                                checked((long)(duration / segment + (duration % segment == 0 ? 0UL : 1UL)));
                        }
                    }
                    int? alphaTrack = FindAlphaTrack(movie, id);
                    ReadOnlySpan<byte> premultiplied = Find(Find(track, "tref"u8), "prem"u8);
                    return new ImageAnimationMetadata(plays, (int)id, alphaTrack,
                        alphaTrack is { } alphaId && References(premultiplied, (uint)alphaId));
                }
            }
            stream.Position = end;
        }
        return null;
    }

    private static ReadOnlySpan<byte> Find(ReadOnlySpan<byte> parent, ReadOnlySpan<byte> name)
    {
        int offset = 0;
        while (ExifBlock.TryReadBox(parent, ref offset, out uint type, out ReadOnlySpan<byte> data))
            if (type == Code(name))
                return data;
        return default;
    }

    private static int? FindAlphaTrack(ReadOnlySpan<byte> movie, uint colorId)
    {
        int offset = 0;
        while (ExifBlock.TryReadBox(movie, ref offset, out uint type, out ReadOnlySpan<byte> track))
        {
            if (type != Code("trak"u8) || !References(Find(Find(track, "tref"u8), "auxl"u8), colorId))
                continue;
            ReadOnlySpan<byte> header = Find(track, "tkhd"u8);
            if (header.Length < 24 || header[0] > 1 || header[0] == 1 && header.Length < 36)
                continue;
            uint id = BinaryPrimitives.ReadUInt32BigEndian(header[(header[0] == 1 ? 20 : 12)..]);
            ReadOnlySpan<byte> description = Find(Find(Find(Find(track, "mdia"u8), "minf"u8), "stbl"u8), "stsd"u8);
            if (description.Length < 8)
                continue;
            int entryOffset = 8;
            if (!ExifBlock.TryReadBox(description, ref entryOffset, out _, out ReadOnlySpan<byte> entry) || entry.Length < 78)
                continue;
            ReadOnlySpan<byte> auxiliary = Find(entry[78..], "auxi"u8);
            if (auxiliary.Length > 4)
            {
                ReadOnlySpan<byte> name = auxiliary[4..];
                int terminator = name.IndexOf((byte)0);
                if (terminator >= 0)
                    name = name[..terminator];
                if (!name.SequenceEqual("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha"u8) &&
                    !name.SequenceEqual("urn:mpeg:hevc:2015:auxid:1"u8))
                    continue;
            }
            if (id <= int.MaxValue)
                return (int)id;
        }
        return null;
    }

    private static bool References(ReadOnlySpan<byte> references, uint id)
    {
        for (int offset = 0; offset <= references.Length - 4; offset += 4)
            if (BinaryPrimitives.ReadUInt32BigEndian(references[offset..]) == id)
                return true;
        return false;
    }

    private static uint Code(ReadOnlySpan<byte> name) => BinaryPrimitives.ReadUInt32BigEndian(name);

    private ref struct JxlBits(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int position;

        public uint Read(int count)
        {
            if (count > data.Length * 8 - position)
                throw new InvalidDataException("Incomplete JPEG XL animation header.");
            uint value = 0;
            for (int index = 0; index < count; index++, position++)
                value |= (uint)((data[position / 8] >> (position % 8)) & 1) << index;
            return value;
        }

        public uint Number(ReadOnlySpan<uint> constants, ReadOnlySpan<int> widths)
        {
            int choice = (int)Read(2);
            return checked(constants[choice] + Read(widths[choice]));
        }

        public void SkipSize()
        {
            bool small = Read(1) != 0;
            Dimension(small);
            if (Read(3) == 0)
                Dimension(small);
        }

        private void Dimension(bool small)
        {
            if (small)
                Read(5);
            else
                Number([0, 0, 0, 0], [9, 13, 18, 30]);
        }

        public void SkipPreview()
        {
            bool divided = Read(1) != 0;
            PreviewDimension(divided);
            if (Read(3) == 0)
                PreviewDimension(divided);
        }

        private void PreviewDimension(bool divided)
        {
            if (divided)
                Number([16, 32, 1, 33], [0, 0, 5, 9]);
            else
                Number([1, 65, 321, 1345], [6, 8, 10, 12]);
        }
    }
}