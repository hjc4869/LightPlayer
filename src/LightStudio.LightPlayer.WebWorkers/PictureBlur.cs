using System;

namespace LightStudio.LightPlayer.WebWorkers;

/// <summary>
/// Gaussian blur of a tightly packed BGRA picture, approximated by three box blurs along each axis as the SVG
/// and CSS filter specifications define it, with the picture's edges extended outward. The picture is
/// premultiplied first, so the result is premultiplied.
/// </summary>
internal static class PictureBlur
{
    public static void Apply(Span<byte> pixels, int width, int height, double sigma)
    {
        if (width <= 0 || height <= 0 || (long)width * height * 4 != pixels.Length)
            throw new ArgumentException("The picture does not match its size.", nameof(pixels));

        Premultiply(pixels);
        int size = (int)Math.Floor(sigma * 3 * Math.Sqrt(2 * Math.PI) / 4 + 0.5);
        if (size < 2)
            return;

        byte[] line = new byte[Math.Max(width, height) * 4];
        byte[] scratch = new byte[line.Length];
        for (int row = 0; row < height; row++)
        {
            pixels.Slice(row * width * 4, width * 4).CopyTo(line);
            BlurLine(line, scratch, width, size);
            line.AsSpan(0, width * 4).CopyTo(pixels.Slice(row * width * 4, width * 4));
        }

        for (int column = 0; column < width; column++)
        {
            for (int row = 0, source = column * 4; row < height; row++, source += width * 4)
                CopyPixel(pixels, source, line, row * 4);
            BlurLine(line, scratch, height, size);
            for (int row = 0, target = column * 4; row < height; row++, target += width * 4)
                CopyPixel(line, row * 4, pixels, target);
        }
    }

    // An odd size is applied three times around each pixel. An even size is applied once centered on the
    // boundary to the pixel's left, once on the boundary to its right, and then once more one pixel wider.
    private static void BlurLine(byte[] line, byte[] scratch, int count, int size)
    {
        int half = size / 2;
        if ((size & 1) == 1)
        {
            Box(line, scratch, count, half, half);
            Box(scratch, line, count, half, half);
            Box(line, scratch, count, half, half);
        }
        else
        {
            Box(line, scratch, count, half, half - 1);
            Box(scratch, line, count, half - 1, half);
            Box(line, scratch, count, half, half);
        }

        Buffer.BlockCopy(scratch, 0, line, 0, count * 4);
    }

    // Each output is the mean of the inputs from `before` pixels to its left to `after` pixels to its right,
    // reading past either end as the pixel at that end.
    private static void Box(byte[] source, byte[] target, int count, int before, int after)
    {
        int size = before + after + 1;
        int rounding = size / 2;
        int last = count - 1;
        int blue = 0, green = 0, red = 0, alpha = 0;
        for (int offset = -before; offset <= after; offset++)
        {
            int index = Math.Clamp(offset, 0, last) * 4;
            blue += source[index];
            green += source[index + 1];
            red += source[index + 2];
            alpha += source[index + 3];
        }

        for (int position = 0; position < count; position++)
        {
            int output = position * 4;
            target[output] = (byte)((blue + rounding) / size);
            target[output + 1] = (byte)((green + rounding) / size);
            target[output + 2] = (byte)((red + rounding) / size);
            target[output + 3] = (byte)((alpha + rounding) / size);
            int entering = Math.Min(position + after + 1, last) * 4;
            int leaving = Math.Max(position - before, 0) * 4;
            blue += source[entering] - source[leaving];
            green += source[entering + 1] - source[leaving + 1];
            red += source[entering + 2] - source[leaving + 2];
            alpha += source[entering + 3] - source[leaving + 3];
        }
    }

    private static void CopyPixel(ReadOnlySpan<byte> source, int sourceIndex, Span<byte> target, int targetIndex)
    {
        target[targetIndex] = source[sourceIndex];
        target[targetIndex + 1] = source[sourceIndex + 1];
        target[targetIndex + 2] = source[sourceIndex + 2];
        target[targetIndex + 3] = source[sourceIndex + 3];
    }

    private static void Premultiply(Span<byte> pixels)
    {
        for (int index = 0; index < pixels.Length; index += 4)
        {
            int alpha = pixels[index + 3];
            if (alpha == 255)
                continue;
            pixels[index] = (byte)((pixels[index] * alpha + 127) / 255);
            pixels[index + 1] = (byte)((pixels[index + 1] * alpha + 127) / 255);
            pixels[index + 2] = (byte)((pixels[index + 2] * alpha + 127) / 255);
        }
    }
}
