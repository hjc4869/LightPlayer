using System;
using System.Buffers.Binary;
using System.IO;
using LightStudio.Logging;

namespace LightStudio.FfmpegShim.Media;

public static partial class StillImage
{
    private const int MaxHeaderSegments = 4096;

    /// <summary>
    /// Reads the pixel size declared by JPEG, PNG, GIF, WebP, BMP, JPEG XR, JPEG XL, or HEIF/AVIF
    /// headers without decoding the picture.
    /// </summary>
    private static bool TryReadHeaderPixelSize(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!stream.CanSeek)
            return false;

        long origin = stream.Position;
        try
        {
            Span<byte> buffer = stackalloc byte[32];
            ReadOnlySpan<byte> header = buffer[..stream.ReadAtLeast(buffer, buffer.Length, false)];
            if (header is [0xff, 0xd8, ..])
                (width, height) = ReadJpegSize(stream, origin + 2);
            else if (header.Length >= 24 && BinaryPrimitives.ReadUInt64BigEndian(header) == 0x89504e470d0a1a0a &&
                header.Slice(12, 4).SequenceEqual("IHDR"u8))
                (width, height) = (BinaryPrimitives.ReadInt32BigEndian(header[16..]), BinaryPrimitives.ReadInt32BigEndian(header[20..]));
            else if (header.Length >= 10 && (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8)))
                (width, height) = (BinaryPrimitives.ReadUInt16LittleEndian(header[6..]), BinaryPrimitives.ReadUInt16LittleEndian(header[8..]));
            else if (header.Length >= 30 && header.StartsWith("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8))
                (width, height) = ReadWebpSize(header);
            else if (header.Length >= 26 && header.StartsWith("BM"u8))
                (width, height) = ReadBmpSize(header);
            else if (header.Length >= 8 && JxrContainer.HasSignature(header))
                (width, height) = ReadJxrSize(stream, origin, BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));
            else if (header is [0xff, 0x0a, ..])
                (width, height) = ReadJxlSize(header[2..]);
            else if (header.StartsWith((ReadOnlySpan<byte>)[0, 0, 0, 0x0c, 0x4a, 0x58, 0x4c, 0x20, 0x0d, 0x0a, 0x87, 0x0a]))
                (width, height) = ReadJxlContainerSize(stream, origin);
            else if (header.Length >= 12 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
            {
                stream.Position = origin;
                (width, height) = IsoImageProperties.Read(stream)?.PrimaryPixelSize ?? default;
            }
        }
        catch (IOException exception)
        {
            AppLog.Warning("Photo.Metadata", "Could not read image header.", exception);
            width = height = 0;
        }
        finally
        {
            stream.Position = origin;
        }

        if (width > 0 && height > 0)
            return true;
        width = height = 0;
        return false;
    }

    private static (int Width, int Height) ReadJpegSize(Stream stream, long offset)
    {
        Span<byte> segment = stackalloc byte[9];
        for (int count = 0; count < MaxHeaderSegments && offset <= stream.Length - 4; count++)
        {
            stream.Position = offset;
            if (stream.ReadAtLeast(segment[..4], 4, false) != 4 || segment[0] != 0xff)
                return default;
            byte marker = segment[1];
            if (marker == 0xff)
            {
                offset++;
                continue;
            }
            if (marker is 0xd9 or 0xda)
                return default;
            if (marker is 0xd8 or 0x01 or >= 0xd0 and <= 0xd7)
            {
                offset += 2;
                continue;
            }
            int length = BinaryPrimitives.ReadUInt16BigEndian(segment[2..]);
            if (length < 2)
                return default;
            // SOF0-SOF15 except DHT, JPG, and DAC carry the frame's line count and samples per line.
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                return length >= 7 && stream.ReadAtLeast(segment[4..], 5, false) == 5
                    ? (BinaryPrimitives.ReadUInt16BigEndian(segment[7..]), BinaryPrimitives.ReadUInt16BigEndian(segment[5..]))
                    : default;
            }
            offset += length + 2;
        }
        return default;
    }

    private static (int Width, int Height) ReadWebpSize(ReadOnlySpan<byte> header)
    {
        ReadOnlySpan<byte> chunk = header.Slice(12, 4);
        ReadOnlySpan<byte> data = header[20..];
        if (chunk.SequenceEqual("VP8X"u8))
            return (1 + (data[4] | data[5] << 8 | data[6] << 16), 1 + (data[7] | data[8] << 8 | data[9] << 16));
        if (chunk.SequenceEqual("VP8L"u8) && data[0] == 0x2f)
        {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
            return (1 + (int)(bits & 0x3fff), 1 + (int)(bits >> 14 & 0x3fff));
        }
        if (chunk.SequenceEqual("VP8 "u8) && data[3] == 0x9d && data[4] == 0x01 && data[5] == 0x2a)
            return (BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3fff);
        return default;
    }

    private static (int Width, int Height) ReadBmpSize(ReadOnlySpan<byte> header)
    {
        int size = BinaryPrimitives.ReadInt32LittleEndian(header[14..]);
        if (size == 12)
            return (BinaryPrimitives.ReadUInt16LittleEndian(header[18..]), BinaryPrimitives.ReadUInt16LittleEndian(header[20..]));
        if (size < 16)
            return default;
        // A negative height marks rows stored top-down.
        int height = BinaryPrimitives.ReadInt32LittleEndian(header[22..]);
        return (BinaryPrimitives.ReadInt32LittleEndian(header[18..]), height == int.MinValue ? 0 : Math.Abs(height));
    }

    private static (int Width, int Height) ReadJxrSize(Stream stream, long origin, uint directory)
    {
        if (directory < 8 || directory > stream.Length - origin - 2)
            return default;
        stream.Position = origin + directory;
        Span<byte> entry = stackalloc byte[12];
        if (stream.ReadAtLeast(entry[..2], 2, false) != 2)
            return default;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(entry);
        int width = 0;
        int height = 0;
        for (int index = 0; index < count && (width == 0 || height == 0); index++)
        {
            if (stream.ReadAtLeast(entry, entry.Length, false) != entry.Length)
                return default;
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            if (tag is not (0xbc80 or 0xbc81))
                continue;
            uint value = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]) switch
            {
                1 => entry[8],
                3 => BinaryPrimitives.ReadUInt16LittleEndian(entry[8..]),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),
                _ => 0,
            };
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]) != 1 || value is 0 or > int.MaxValue)
                return default;
            if (tag == 0xbc80)
                width = (int)value;
            else
                height = (int)value;
        }
        return (width, height);
    }

    private static (int Width, int Height) ReadJxlContainerSize(Stream stream, long origin)
    {
        stream.Position = origin;
        Span<byte> codestream = stackalloc byte[16];
        int filled = 0;
        for (int count = 0; count < MaxHeaderSegments && filled < codestream.Length &&
            ExifBlock.TryReadBoxHeader(stream, stream.Length, out uint type, out long start, out long end); count++)
        {
            // The codestream is stored whole in jxlc, or split across jxlp boxes after a sequence index.
            bool partial = type == 0x6a786c70;
            if (partial)
                start += 4;
            if ((partial || type == 0x6a786c63) && start < end)
            {
                stream.Position = start;
                Span<byte> part = codestream[filled..];
                part = part[..(int)Math.Min(part.Length, end - start)];
                filled += stream.ReadAtLeast(part, part.Length, false);
            }
            stream.Position = end;
        }
        return codestream[..filled] is [0xff, 0x0a, ..] ? ReadJxlSize(codestream[2..filled]) : default;
    }

    /// <summary>
    /// Reads the JPEG XL <c>SizeHeader</c> that follows the codestream signature, transposed when the
    /// codestream orientation turns the picture a quarter, as its decoder outputs it.
    /// </summary>
    private static (int Width, int Height) ReadJxlSize(ReadOnlySpan<byte> data)
    {
        Span<byte> padded = stackalloc byte[16];
        int available = Math.Min(data.Length, padded.Length) * 8;
        data[..(available / 8)].CopyTo(padded);
        UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(padded);
        int consumed = 0;

        uint height;
        uint width;
        if (Read(1) == 1)
        {
            height = (Read(5) + 1) * 8;
            width = WidthFromRatio(height, Read(3)) ?? (Read(5) + 1) * 8;
        }
        else
        {
            height = Size();
            width = WidthFromRatio(height, Read(3)) ?? Size();
        }
        // ImageMetadata starts with all_default, then extra_fields, then orientation - 1.
        uint orientation = Read(1) == 0 && Read(1) == 1 ? Read(3) + 1 : 1;
        if (consumed > available || height > int.MaxValue || width > int.MaxValue)
            return default;
        return orientation >= 5 ? ((int)height, (int)width) : ((int)width, (int)height);

        uint Read(int count)
        {
            uint value = (uint)(bits >> consumed) & ((1u << count) - 1);
            consumed += count;
            return value;
        }

        uint Size() => 1 + Read(Read(2) switch { 0 => 9, 1 => 13, 2 => 18, _ => 30 });

        static uint? WidthFromRatio(uint height, uint ratio) => ratio switch
        {
            1 => height,
            2 => (uint)(height * 12UL / 10),
            3 => (uint)(height * 4UL / 3),
            4 => (uint)(height * 3UL / 2),
            5 => (uint)(height * 16UL / 9),
            6 => (uint)(height * 5UL / 4),
            7 => (uint)(height * 2UL),
            _ => null,
        };
    }
}
