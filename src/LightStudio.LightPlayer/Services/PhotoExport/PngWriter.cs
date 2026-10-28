using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace LightStudio.LightPlayer.Services.PhotoExport;

/// <summary>Fills one row of a PNG image with its big-endian samples, before filtering.</summary>
internal delegate void PngRowSource(int y, Span<byte> row);

/// <summary>
/// Writes truecolor PNG images with adaptive row filtering. Ancillary chunks, such as the cICP chunk
/// (PNG third edition) identifying primaries and transfer function, precede the image data.
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="bitDepth">8 or 16 bits per sample.</param>
    /// <param name="channels">3 for RGB, 4 for RGB with alpha.</param>
    /// <param name="chunks">Chunks written between the header and the image data, in order.</param>
    /// <param name="level">zlib effort from 0 to 9.</param>
    public static void Write(Stream output, int width, int height, int bitDepth, int channels, PngRowSource rows,
        IEnumerable<(byte[] Type, byte[] Data)> chunks, int level, CancellationToken cancellationToken)
    {
        if (bitDepth is not (8 or 16) || channels is not (3 or 4))
            throw new ArgumentException("Only 8- and 16-bit RGB and RGBA images are written.");
        int bytesPerPixel = channels * bitDepth / 8;
        int rowBytes = checked(width * bytesPerPixel);
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)(channels == 4 ? 6 : 2);
        header[10..].Clear();
        WriteChunk(output, "IHDR"u8, header);
        foreach ((byte[] type, byte[] data) in chunks)
            WriteChunk(output, type, data);

        using (var idat = new ChunkStream(output, "IDAT"u8.ToArray()))
        using (var zlib = new ZLibStream(idat, level >= 9 ? CompressionLevel.SmallestSize
                   : level <= 3 ? CompressionLevel.Fastest : CompressionLevel.Optimal, leaveOpen: true))
            WriteFilteredRows(zlib, height, rowBytes, bytesPerPixel, rows, cancellationToken);

        WriteChunk(output, "IEND"u8, []);
    }

    /// <summary>A cICP chunk's data for full-range RGB with the given ITU-T H.273 code points.</summary>
    public static byte[] Cicp(int primaries, int transfer) => [(byte)primaries, (byte)transfer, 0, 1];

    /// <summary>A cLLI chunk's data for content light levels in nits.</summary>
    public static byte[] ContentLightLevels(float maxContentLightLevel, float maxFrameAverageLightLevel)
    {
        var light = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(light, (uint)Math.Round(maxContentLightLevel * 10000));
        BinaryPrimitives.WriteUInt32BigEndian(light.AsSpan(4), (uint)Math.Round(maxFrameAverageLightLevel * 10000));
        return light;
    }

    /// <summary>A pHYs chunk's data giving only the shape of a pixel, as width over height.</summary>
    public static byte[] PixelAspectRatio(double aspect)
    {
        const double scale = 100000;
        var physical = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(physical, (uint)Math.Clamp(Math.Round(scale / aspect), 1, int.MaxValue));
        BinaryPrimitives.WriteUInt32BigEndian(physical.AsSpan(4), (uint)scale);
        return physical;
    }

    public static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(length, crc);
        output.Write(length);
    }

    private static void WriteFilteredRows(Stream output, int height, int rowBytes, int bytesPerPixel, PngRowSource rows,
        CancellationToken cancellationToken)
    {
        var scratch = new byte[checked(rowBytes * 4 + 1)];
        Span<byte> current = scratch.AsSpan(0, rowBytes);
        Span<byte> previous = scratch.AsSpan(rowBytes, rowBytes);
        Span<byte> candidate = scratch.AsSpan(rowBytes * 2, rowBytes);
        Span<byte> bestRow = scratch.AsSpan(rowBytes * 3, rowBytes + 1);
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows(y, current);

            long best = long.MaxValue;
            for (byte filter = 0; filter <= 4; filter++)
            {
                long cost = 0;
                for (int i = 0; i < rowBytes; i++)
                {
                    int left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                    int up = previous[i];
                    int upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                    int value = filter switch
                    {
                        1 => current[i] - left,
                        2 => current[i] - up,
                        3 => current[i] - ((left + up) >> 1),
                        4 => current[i] - Paeth(left, up, upLeft),
                        _ => current[i],
                    };
                    candidate[i] = (byte)value;
                    cost += Math.Abs((int)(sbyte)(byte)value);
                }

                if (cost < best)
                {
                    best = cost;
                    bestRow[0] = filter;
                    candidate.CopyTo(bestRow[1..]);
                }
            }

            output.Write(bestRow);
            Span<byte> swap = previous;
            previous = current;
            current = swap;
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    /// <summary>Buffers written data into PNG chunks of a fixed type.</summary>
    private sealed class ChunkStream(Stream output, byte[] chunkType) : Stream
    {
        private const int ChunkSize = 1 << 16;
        private readonly MemoryStream buffer = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] data, int offset, int count) => Write(data.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            buffer.Write(data);
            if (buffer.Length >= ChunkSize)
                Emit();
        }

        public override void Flush()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && buffer.Length > 0)
                Emit();
            if (disposing)
                buffer.Dispose();
            base.Dispose(disposing);
        }

        private void Emit()
        {
            WriteChunk(output, chunkType, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            buffer.SetLength(0);
        }

        public override int Read(byte[] data, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
