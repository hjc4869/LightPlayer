using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace LightStudio.FfmpegShim.Media;

public sealed record PhotoPreviewRequest(int ViewportWidth, int ViewportHeight, int ExistingWidth = 0, int ExistingHeight = 0)
{
    public int ExistingMaxDimension { get; init; }

    public double ResolutionDistance(double width, double height) =>
        ViewportWidth > 0 && ViewportHeight > 0 && width > 0 && height > 0
            ? Math.Abs(Math.Log2(Math.Min(ViewportWidth / width, ViewportHeight / height)))
            : double.PositiveInfinity;

    public bool IsBetter(double width, double height)
    {
        double scale = ExistingMaxDimension / Math.Max(width, height);
        double existing = ExistingWidth > 0 && ExistingHeight > 0
            ? ResolutionDistance(ExistingWidth, ExistingHeight)
            : ResolutionDistance(width * scale, height * scale);
        return ResolutionDistance(width, height) < existing;
    }
}

public static unsafe partial class StillImage
{
    public static byte[] ReadExifThumbnail(Stream stream, out int rotation, out bool mirror,
        CancellationToken cancellationToken = default)
    {
        rotation = 0;
        mirror = false;
        cancellationToken.ThrowIfCancellationRequested();
        byte[] tiff = ExifBlock.Read(stream, out bool standalone);
        if (tiff.Length < 8)
            return null;
        bool bigEndian = tiff[0] == 'M';
        if (ReadShort(tiff.AsSpan(2)) != 42)
            return null;
        uint directory = ReadLong(tiff.AsSpan(4));
        if (directory > tiff.Length - 2)
            return null;
        int count = ReadShort(tiff.AsSpan((int)directory));
        long next = directory + 2L + count * 12L;
        if (next > tiff.Length - 4)
            return null;
        directory = ReadLong(tiff.AsSpan((int)next));
        if (directory == 0 || directory > tiff.Length - 2)
            return null;
        count = ReadShort(tiff.AsSpan((int)directory));
        if (directory + 2L + count * 12L > tiff.Length)
            return null;

        ExifOrientation.TryRead(tiff, out rotation, out mirror);
        uint offset = 0;
        uint length = 0;
        uint compression = 6;
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = tiff.AsSpan((int)directory + 2 + index * 12, 12);
            ushort type = ReadShort(entry[2..]);
            if (ReadLong(entry[4..]) != 1 || type is not (3 or 4))
                continue;
            uint value = type == 3 ? ReadShort(entry[8..]) : ReadLong(entry[8..]);
            switch (ReadShort(entry))
            {
                case 259: compression = value; break;
                case 274:
                    if (value is >= 1 and <= 8)
                        ExifOrientation.TryDecode((int)value, out rotation, out mirror);
                    break;
                case 513: offset = value; break;
                case 514: length = value; break;
            }
        }
        if (compression == 1 && !standalone && directory + 2L + count * 12L <= tiff.Length - 4)
        {
            if (bigEndian)
                BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), directory);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), directory);
            tiff.AsSpan((int)directory + 2 + count * 12, 4).Clear();
            return tiff;
        }
        if (compression != 6 || offset == 0 || length is < 4 or > 16 * 1024 * 1024)
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        if (!standalone)
            return offset <= tiff.Length && length <= tiff.Length - offset
                ? tiff.AsSpan((int)offset, (int)length).ToArray() : null;
        if (offset > stream.Length || length > stream.Length - offset)
            return null;
        long origin = stream.Position;
        try
        {
            stream.Position = offset;
            byte[] data = new byte[length];
            stream.ReadExactly(data);
            cancellationToken.ThrowIfCancellationRequested();
            return data;
        }
        finally { stream.Position = origin; }

        ushort ReadShort(ReadOnlySpan<byte> value) => bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(value) : BinaryPrimitives.ReadUInt16LittleEndian(value);
        uint ReadLong(ReadOnlySpan<byte> value) => bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(value) : BinaryPrimitives.ReadUInt32LittleEndian(value);
    }
}