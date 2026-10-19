#nullable enable
using System;
using System.Buffers;
using System.IO;
using System.Threading;
using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Software;

/// <summary>Single-threaded, linear-light area reduction into tightly packed, straight-alpha sRGB BGRA8.</summary>
internal static unsafe class VideoCpuPreview
{
    public static void Render(VideoFrame frame, int width, int height, Span<byte> output,
        CancellationToken cancellationToken = default, bool useSimd = true,
        VideoSdrToneMappingOptions? toneMapping = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Width > 65536 || width > 65536 ||
            frame.Data == IntPtr.Zero || frame.Surface.IsValid || frame.Format == VideoPixelFormat.ExternalTexture)
            throw new ArgumentException("Software previews require CPU-accessible pixels with a width up to 65536.", nameof(frame));
        if (output.Length < checked(width * height * 4))
            throw new ArgumentException("The output buffer does not cover the preview.", nameof(output));
        cancellationToken.ThrowIfCancellationRequested();
        var color = new VideoCpuColorPipeline(frame, toneMapping);
        int sourceWidth = frame.Width;
        float[] scratch = ArrayPool<float>.Shared.Rent(checked(sourceWidth * 7 + width * 4));
        try
        {
            Span<float> r = scratch.AsSpan(0, sourceWidth), g = scratch.AsSpan(sourceWidth, sourceWidth),
                b = scratch.AsSpan(sourceWidth * 2, sourceWidth), alpha = scratch.AsSpan(sourceWidth * 3, sourceWidth),
                er = scratch.AsSpan(sourceWidth * 4, sourceWidth), eg = scratch.AsSpan(sourceWidth * 5, sourceWidth),
                eb = scratch.AsSpan(sourceWidth * 6, sourceWidth);
            Span<float> accumulated = scratch.AsSpan(sourceWidth * 7, width * 4);
            Span<float> red = accumulated[..width], green = accumulated.Slice(width, width),
                blue = accumulated.Slice(width * 2, width), opacity = accumulated.Slice(width * 3, width);
            int cachedRow = -1;
            float scaleX = (float)sourceWidth / width, scaleY = (float)frame.Height / height;
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                accumulated.Clear();
                float top = y * scaleY, bottom = MathF.Min((y + 1) * scaleY, frame.Height);
                for (int row = (int)top; row < (int)MathF.Ceiling(bottom); row++)
                {
                    if (row != cachedRow)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ReadRow(frame, row, r, g, b, alpha);
                        if (frame.DolbyVision.EnhancementLayerResidual && frame.EnhancementLayer is { } enhancement)
                        {
                            for (int x = 0; x < sourceWidth; x++)
                                Sample(enhancement, x * MathF.Min((float)enhancement.Width / sourceWidth, 1),
                                    (row + 0.5f) * MathF.Min((float)enhancement.Height / frame.Height, 1) - 0.5f,
                                    out er[x], out eg[x], out eb[x], out _);
                        }
                        color.Decode(r, g, b, er, eg, eb, useSimd);
                        cachedRow = row;
                    }
                    float rowWeight = MathF.Min(row + 1, bottom) - MathF.Max(row, top);
                    for (int x = 0; x < width; x++)
                    {
                        float left = x * scaleX, right = MathF.Min((x + 1) * scaleX, sourceWidth);
                        float sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                        for (int column = (int)left; column < (int)MathF.Ceiling(right); column++)
                        {
                            float weight = (MathF.Min(column + 1, right) - MathF.Max(column, left)) * rowWeight;
                            float premultipliedWeight = weight * alpha[column];
                            sumR += r[column] * premultipliedWeight;
                            sumG += g[column] * premultipliedWeight;
                            sumB += b[column] * premultipliedWeight;
                            sumA += premultipliedWeight;
                        }
                        red[x] += sumR; green[x] += sumG; blue[x] += sumB; opacity[x] += sumA;
                    }
                }
                for (int x = 0; x < width; x++)
                {
                    float reciprocal = opacity[x] > 0 ? 1 / opacity[x] : 0;
                    red[x] *= reciprocal; green[x] *= reciprocal; blue[x] *= reciprocal;
                }
                color.Encode(red, green, blue, useSimd);
                for (int x = 0; x < width; x++)
                {
                    float noise = color.Dither ? Dither(x, y) : 0;
                    int offset = (y * width + x) * 4;
                    output[offset] = Quantize(blue[x] + noise);
                    output[offset + 1] = Quantize(green[x] + noise);
                    output[offset + 2] = Quantize(red[x] + noise);
                    output[offset + 3] = Quantize(opacity[x] / (scaleX * (bottom - top)));
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scratch);
        }
    }

    private static byte Quantize(float value)
    {
        if (!float.IsFinite(value))
            throw new InvalidDataException("Software color conversion produced a non-finite component.");
        return (byte)MathF.Round(Math.Clamp(value, 0, 1) * 255);
    }

    // Shaders/VideoColorStages.cs: gradientNoise and dither, at a fixed preview seed of zero.
    private static float Dither(int x, int y)
    {
        float first = Noise(x, y, 0);
        float second = Noise(y + 37, x + 17, 0.61803399f);
        return (first - second) / 255;

        static float Noise(float x, float y, float seed)
        {
            float value = x * 0.06711056f + y * 0.00583715f + seed;
            value = 52.9829189f * (value - MathF.Floor(value));
            return value - MathF.Floor(value);
        }
    }

    private static void ReadRow(VideoFrame frame, int row, Span<float> r, Span<float> g, Span<float> b, Span<float> alpha)
    {
        for (int x = 0; x < r.Length; x++)
            Sample(frame, x, row, out r[x], out g[x], out b[x], out alpha[x]);
    }

    private static void Sample(VideoFrame frame, float x, float y, out float r, out float g, out float b, out float alpha)
    {
        VideoPlanarLayout layout = frame.Layout;
        if (!VideoPlanarLayout.IsPlanar(frame.Format))
        {
            int column = Math.Clamp((int)MathF.Round(x), 0, frame.Width - 1);
            int row = Math.Clamp((int)MathF.Round(y), 0, frame.Height - 1);
            byte* pixel = (byte*)frame.Data + (long)row * frame.Stride;
            if (frame.Format == VideoPixelFormat.Bgra32)
            {
                pixel += column * 4;
                r = pixel[2] / 255f; g = pixel[1] / 255f; b = pixel[0] / 255f;
                alpha = frame.HasAlpha ? pixel[3] / 255f : 1;
            }
            else if (frame.Format == VideoPixelFormat.Rgba64)
            {
                ushort* rgba = (ushort*)pixel + column * 4;
                r = rgba[0] / 65535f; g = rgba[1] / 65535f; b = rgba[2] / 65535f;
                alpha = frame.HasAlpha ? rgba[3] / 65535f : 1;
            }
            else if (frame.Format == VideoPixelFormat.RgbaF16)
            {
                Half* rgba = (Half*)pixel + column * 4;
                r = (float)rgba[0]; g = (float)rgba[1]; b = (float)rgba[2];
                alpha = frame.HasAlpha ? (float)rgba[3] : 1;
                if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b) || !float.IsFinite(alpha))
                    throw new InvalidDataException("The frame contains a non-finite half-float component.");
            }
            else
                throw new NotSupportedException($"Software previews do not support {frame.Format}.");
            return;
        }
        r = Plane(0, x, y, false, 0);
        float shiftX = 1 << layout.ChromaShiftX, shiftY = 1 << layout.ChromaShiftY;
        float sitingX = frame.ChromaLocation is VideoChromaLocation.Left or VideoChromaLocation.TopLeft or VideoChromaLocation.BottomLeft
            ? (shiftX - 1) / (2 * shiftX) : 0;
        float sitingY = frame.ChromaLocation switch
        {
            VideoChromaLocation.TopLeft or VideoChromaLocation.Top => (shiftY - 1) / (2 * shiftY),
            VideoChromaLocation.BottomLeft or VideoChromaLocation.Bottom => -(shiftY - 1) / (2 * shiftY),
            _ => 0,
        };
        float cx = (x + 0.5f) / shiftX + sitingX - 0.5f;
        float cy = (y + 0.5f) / shiftY + sitingY - 0.5f;
        g = Plane(1, cx, cy, true, layout.SwappedChroma ? 1 : 0);
        b = Plane(layout.InterleavedChroma ? 1 : 2, cx, cy, true, layout.SwappedChroma ? 0 : 1);
        alpha = 1;

        float Plane(int plane, float sx, float sy, bool chroma, int component)
        {
            int width = chroma ? layout.ChromaWidth(frame.Width) : frame.Width;
            int height = chroma ? layout.ChromaHeight(frame.Height) : frame.Height;
            sx = Math.Clamp(sx, 0, width - 1); sy = Math.Clamp(sy, 0, height - 1);
            int left = (int)sx, top = (int)sy;
            int right = Math.Min(left + 1, width - 1), bottom = Math.Min(top + 1, height - 1);
            float a = Read(left, top), b = Read(right, top), c = Read(left, bottom), d = Read(right, bottom);
            float first = a + (b - a) * (sx - left), second = c + (d - c) * (sx - left);
            return first + (second - first) * (sy - top);

            float Read(int column, int row)
            {
                int index = chroma && layout.InterleavedChroma ? column * 2 + component : column;
                byte* address = (byte*)frame.GetPlane(plane) + (long)row * frame.GetStride(plane);
                if (layout.BytesPerSample == 1)
                    return address[index] / 255f;
                int value = ((ushort*)address)[index];
                if (layout.MsbAligned)
                    value >>= 16 - layout.BitDepth;
                return value / (float)((1 << layout.BitDepth) - 1);
            }
        }
    }
}
