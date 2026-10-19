using System;
using System.Buffers.Binary;
using System.Numerics;

namespace LightStudio.FfmpegShim.Media;

public sealed partial record JxrImage
{
    public void CopyRgbaRow(int row, Span<Vector4> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Height);
        if (destination.Length < Width)
            throw new ArgumentException("The destination must hold a complete row.", nameof(destination));
        if (PixelFormat.ColorModel is JxrColorModel.Multichannel && PixelFormat.Channels - (PixelFormat.HasAlpha ? 1 : 0) != 3)
            throw new NotSupportedException("Multichannel JPEG XR pixels require a color interpretation.");
        if (PixelFormat.ColorModel is JxrColorModel.Yuv420 or JxrColorModel.Yuv422 or JxrColorModel.Yuv444)
            throw new NotSupportedException("Packed YUV JPEG XR pixels require a YUV color conversion.");

        ReadOnlySpan<byte> source = PixelBytes.Slice(row * Stride, Stride);
        int pixelBytes = (PixelFormat.BitsPerUnit + 7) / 8;
        for (int column = 0; column < Width; column++)
        {
            if (PixelFormat.SampleType == JxrSampleType.Bit1)
            {
                float value = (source[column / 8] >> (7 - column % 8)) & 1;
                destination[column] = new Vector4(value, value, value, 1);
                continue;
            }
            ReadOnlySpan<byte> pixel = source.Slice(column * pixelBytes, pixelBytes);
            if (PixelFormat.SampleType is JxrSampleType.Packed555 or JxrSampleType.Packed565 or JxrSampleType.Packed101010)
            {
                uint packed = pixelBytes == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(pixel) : BinaryPrimitives.ReadUInt16LittleEndian(pixel);
                destination[column] = PixelFormat.SampleType switch
                {
                    JxrSampleType.Packed101010 => new Vector4((packed >> 20 & 1023) / 1023f, (packed >> 10 & 1023) / 1023f, (packed & 1023) / 1023f, 1),
                    JxrSampleType.Packed565 => new Vector4((packed >> 11 & 31) / 31f, (packed >> 5 & 63) / 63f, (packed & 31) / 31f, 1),
                    _ => new Vector4((packed >> 10 & 31) / 31f, (packed >> 5 & 31) / 31f, (packed & 31) / 31f, 1),
                };
                continue;
            }
            if (PixelFormat.ColorModel == JxrColorModel.Rgbe)
            {
                float scale = pixel[3] == 0 ? 0 : MathF.ScaleB(1, pixel[3] - 136);
                destination[column] = new Vector4(pixel[0] * scale, pixel[1] * scale, pixel[2] * scale, 1);
                continue;
            }
            int componentBytes = PixelFormat.SampleType == JxrSampleType.Unsigned8 ? 1 :
                PixelFormat.SampleType is JxrSampleType.Unsigned16 or JxrSampleType.Fixed16 or JxrSampleType.Float16 ? 2 : 4;
            float first = ReadSample(pixel);
            float second = PixelFormat.ColorModel == JxrColorModel.Gray ? first : ReadSample(pixel[componentBytes..]);
            float third = PixelFormat.ColorModel == JxrColorModel.Gray ? first : ReadSample(pixel[(componentBytes * 2)..]);
            float alpha = PixelFormat.HasAlpha ? ReadSample(pixel[(componentBytes * (PixelFormat.Channels - 1))..]) : 1;
            alpha = float.IsFinite(alpha) ? Math.Clamp(alpha, 0, 1) : 0;
            if (PixelFormat.ColorModel == JxrColorModel.Cmyk)
            {
                float black = ReadSample(pixel[(componentBytes * 3)..]);
                first = (1 - first) * (1 - black);
                second = (1 - second) * (1 - black);
                third = (1 - third) * (1 - black);
            }
            Vector3 rgb = PixelFormat.IsBgr ? new Vector3(third, second, first) : new Vector3(first, second, third);
            if (PixelFormat.IsPremultiplied)
                rgb = alpha > 0 ? rgb / alpha : Vector3.Zero;
            destination[column] = new Vector4(rgb, alpha);
        }
    }

    private float ReadSample(ReadOnlySpan<byte> value) => PixelFormat.SampleType switch
    {
        JxrSampleType.Unsigned8 => value[0] / 255f,
        JxrSampleType.Unsigned16 => BinaryPrimitives.ReadUInt16LittleEndian(value) / 65535f,
        JxrSampleType.Fixed16 => BinaryPrimitives.ReadInt16LittleEndian(value) / 8192f,
        JxrSampleType.Float16 => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(value)),
        JxrSampleType.Unsigned32 => BinaryPrimitives.ReadUInt32LittleEndian(value) / (float)uint.MaxValue,
        JxrSampleType.Fixed32 => BinaryPrimitives.ReadInt32LittleEndian(value) / 16777216f,
        JxrSampleType.Float32 => BinaryPrimitives.ReadSingleLittleEndian(value),
        _ => throw new NotSupportedException("Unsupported JPEG XR component representation."),
    };
}