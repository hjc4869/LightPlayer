using System;
using System.Buffers.Binary;
using System.IO;

namespace LightStudio.FfmpegShim.Media;

public static class PhotoHdrMetadata
{
    public static float? ReadAppleHeadroom(Stream stream)
    {
        ReadOnlySpan<byte> tiff = ExifBlock.Read(stream);
        if (tiff.Length < 8 || (!tiff.StartsWith("II*\0"u8) && !tiff.StartsWith("MM\0*"u8)))
            return null;
        bool bigEndian = tiff[0] == 'M';
        uint root = UInt32(tiff[4..], bigEndian);
        ReadOnlySpan<byte> exifOffset = Field(tiff, root, 0x8769, 4, 4, bigEndian);
        uint exif = exifOffset.Length == 4 ? UInt32(exifOffset, bigEndian) : root;
        ReadOnlySpan<byte> maker = Field(tiff, exif, 0x927c, 7, 1, bigEndian);
        if (!maker.StartsWith("Apple iOS\0\0\x01MM"u8))
            return null;
        float? headroom = SignedRational(Field(maker, 14, 33, 10, 8, true));
        float gain = SignedRational(Field(maker, 14, 48, 10, 8, true)) ?? 0;
        if (headroom is null)
            return null;
        float stops = headroom < 1
            ? gain <= 0.01f ? -20 * gain + 1.8f : -0.101f * gain + 1.601f
            : gain <= 0.01f ? -70 * gain + 3 : -0.303f * gain + 2.303f;
        float result = MathF.Pow(2, Math.Max(stops, 0));
        return float.IsFinite(result) ? result : null;
    }

    private static ReadOnlySpan<byte> Field(ReadOnlySpan<byte> data, uint directory, ushort tag, ushort type, int unitSize, bool bigEndian)
    {
        if (data.Length < 2 || directory > data.Length - 2)
            return default;
        int count = UInt16(data[(int)directory..], bigEndian);
        int start = (int)directory + 2;
        count = Math.Min(count, (data.Length - start) / 12);
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = data.Slice(start + index * 12, 12);
            if (UInt16(entry, bigEndian) != tag || UInt16(entry[2..], bigEndian) != type)
                continue;
            uint elements = UInt32(entry[4..], bigEndian);
            ulong length = (ulong)elements * (uint)unitSize;
            if (length <= 4)
                return entry.Slice(8, (int)length);
            uint offset = UInt32(entry[8..], bigEndian);
            if (offset > data.Length || length > (ulong)data.Length - offset)
                return default;
            return data.Slice((int)offset, (int)length);
        }
        return default;
    }

    private static float? SignedRational(ReadOnlySpan<byte> data)
    {
        if (data.Length != 8)
            return null;
        int denominator = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        return denominator == 0 ? null : (float)BinaryPrimitives.ReadInt32BigEndian(data) / denominator;
    }

    private static ushort UInt16(ReadOnlySpan<byte> data, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadUInt16BigEndian(data) : BinaryPrimitives.ReadUInt16LittleEndian(data);

    private static uint UInt32(ReadOnlySpan<byte> data, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadUInt32BigEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data);
}