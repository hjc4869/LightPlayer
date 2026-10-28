using System;
using System.Threading;
using LightStudio.FfmpegShim.ImageEncoding;
using LightStudio.NativeBindings.Types;

namespace LightStudio.LightPlayer.Services.PhotoExport;

/// <summary>Converts rendered 16-bit RGBA samples into the picture layouts FFmpeg's encoders take.</summary>
internal static unsafe class PhotoExportPlanes
{
    /// <param name="matrix">ITU-T H.273 matrix_coefficients: 0 (GBR), 1, 6 or 9.</param>
    public static StillPicture Create(PhotoExportPicture source, AVPixelFormat format, int matrix, bool fullRange, bool dither,
        CancellationToken cancellationToken) =>
        Create(source.Pointer, source.Width, source.Height, source.Primaries, source.IsPq, format, matrix, fullRange, dither,
            cancellationToken);

    /// <summary>Converts an 8-bit RGB gain map, which carries no transfer function or primaries of its own.</summary>
    public static StillPicture CreateGainMap(PhotoExportGainImage gainMap, AVPixelFormat format, int matrix, bool fullRange,
        CancellationToken cancellationToken)
    {
        int count = gainMap.Width * gainMap.Height;
        var expanded = new ushort[checked(count * 4)];
        byte* samples = (byte*)gainMap.Pixels.Address;
        for (int index = 0; index < count; index++)
        {
            expanded[index * 4] = (ushort)(samples[index * 3] * 257);
            expanded[index * 4 + 1] = (ushort)(samples[index * 3 + 1] * 257);
            expanded[index * 4 + 2] = (ushort)(samples[index * 3 + 2] * 257);
            expanded[index * 4 + 3] = ushort.MaxValue;
        }

        fixed (ushort* pixels = expanded)
        {
            StillPicture picture = Create(pixels, gainMap.Width, gainMap.Height, PhotoExportPrimaries.Bt709, false, format, matrix,
                fullRange, false, cancellationToken);
            picture.Primaries = AVColorPrimaries.AVCOL_PRI_UNSPECIFIED;
            picture.Transfer = AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED;
            return picture;
        }
    }

    private static StillPicture Create(ushort* source, int width, int height, PhotoExportPrimaries primaries, bool pq,
        AVPixelFormat format, int matrix, bool fullRange, bool dither, CancellationToken cancellationToken)
    {
        var picture = new StillPicture(width, height, format)
        {
            Primaries = (AVColorPrimaries)PhotoExportColor.Cicp(primaries),
            Transfer = pq ? AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 : AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1,
            Matrix = matrix == 0 ? AVColorSpace.AVCOL_SPC_RGB : (AVColorSpace)matrix,
            Range = fullRange ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG,
        };
        try
        {
            switch (format)
            {
                case AVPixelFormat.AV_PIX_FMT_RGB24 or AVPixelFormat.AV_PIX_FMT_RGBA or
                    AVPixelFormat.AV_PIX_FMT_RGB48LE or AVPixelFormat.AV_PIX_FMT_RGBA64LE:
                    picture.Matrix = AVColorSpace.AVCOL_SPC_RGB;
                    picture.Range = AVColorRange.AVCOL_RANGE_JPEG;
                    WritePacked(source, width, height, picture, dither, cancellationToken);
                    break;
                case AVPixelFormat.AV_PIX_FMT_GBRP:
                    WriteGbr(source, width, height, picture, cancellationToken);
                    break;
                default:
                    WriteYuv(source, width, height, picture, primaries, matrix, fullRange, dither, cancellationToken);
                    break;
            }

            return picture;
        }
        catch
        {
            picture.Dispose();
            throw;
        }
    }

    private static void WritePacked(ushort* source, int width, int height, StillPicture picture, bool dither,
        CancellationToken cancellationToken)
    {
        AVPixelFormat format = picture.Format;
        int channels = format is AVPixelFormat.AV_PIX_FMT_RGBA or AVPixelFormat.AV_PIX_FMT_RGBA64LE ? 4 : 3;
        bool wide = format is AVPixelFormat.AV_PIX_FMT_RGB48LE or AVPixelFormat.AV_PIX_FMT_RGBA64LE;
        byte* plane = (byte*)picture.GetPlane(0);
        int stride = picture.GetStride(0);
        PhotoExportColor.ForRows(height, width, cancellationToken, (first, last) =>
        {
            for (int y = first; y < last; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ushort* input = source + (long)y * width * 4;
                byte* row = plane + (long)y * stride;
                for (int x = 0; x < width; x++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        ushort value = input[x * 4 + c];
                        if (wide)
                            ((ushort*)row)[x * channels + c] = value;
                        else
                            row[x * channels + c] = To8(value, c < 3 && dither ? PhotoExportColor.Dither(x, y) : 0);
                    }
                }
            }
        });
    }

    private static void WriteGbr(ushort* source, int width, int height, StillPicture picture, CancellationToken cancellationToken)
    {
        byte* green = (byte*)picture.GetPlane(0), blue = (byte*)picture.GetPlane(1), red = (byte*)picture.GetPlane(2);
        int greenStride = picture.GetStride(0), blueStride = picture.GetStride(1), redStride = picture.GetStride(2);
        PhotoExportColor.ForRows(height, width, cancellationToken, (first, last) =>
        {
            for (int y = first; y < last; y++)
            {
                ushort* input = source + (long)y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    red[(long)y * redStride + x] = To8(input[x * 4], 0);
                    green[(long)y * greenStride + x] = To8(input[x * 4 + 1], 0);
                    blue[(long)y * blueStride + x] = To8(input[x * 4 + 2], 0);
                }
            }
        });
    }

    private static void WriteYuv(ushort* source, int width, int height, StillPicture picture, PhotoExportPrimaries primaries,
        int matrix, bool fullRange, bool dither, CancellationToken cancellationToken)
    {
        AVPixelFormat format = picture.Format;
        (float kr, float kb) = matrix switch
        {
            PhotoExportColor.CicpMatrixBt601 => (0.299f, 0.114f),
            PhotoExportColor.CicpMatrixBt2020 => (0.2627f, 0.0593f),
            PhotoExportColor.CicpMatrixBt709 => (0.2126f, 0.0722f),
            _ => (PhotoExportColor.Luma(primaries).R, PhotoExportColor.Luma(primaries).B),
        };
        float kg = 1 - kr - kb;
        int depth = format switch
        {
            AVPixelFormat.AV_PIX_FMT_YUV420P10LE or AVPixelFormat.AV_PIX_FMT_YUV422P10LE or AVPixelFormat.AV_PIX_FMT_YUV444P10LE or
                AVPixelFormat.AV_PIX_FMT_P010LE or AVPixelFormat.AV_PIX_FMT_GRAY10LE => 10,
            AVPixelFormat.AV_PIX_FMT_YUV420P12LE or AVPixelFormat.AV_PIX_FMT_YUV422P12LE or AVPixelFormat.AV_PIX_FMT_YUV444P12LE or
                AVPixelFormat.AV_PIX_FMT_GRAY12LE => 12,
            AVPixelFormat.AV_PIX_FMT_GRAY16LE => 16,
            _ => 8,
        };
        int maximum = (1 << depth) - 1;
        float unit = 1 << Math.Max(depth - 8, 0);
        (float lumaScale, float lumaOffset, float chromaScale, float chromaOffset) = fullRange
            ? (maximum, 0f, maximum, 1 << (depth - 1))
            : (219 * unit, 16 * unit, 224 * unit, 128 * unit);
        bool gray = picture.PlaneCount == 1;
        bool interleaved = format is AVPixelFormat.AV_PIX_FMT_NV12 or AVPixelFormat.AV_PIX_FMT_P010LE;
        int shift = format == AVPixelFormat.AV_PIX_FMT_P010LE ? 6 : 0;
        bool wide = depth > 8;
        int shiftX = picture.ChromaShiftX, shiftY = picture.ChromaShiftY;
        int chromaWidth = gray ? 0 : picture.GetPlaneWidth(1), chromaHeight = gray ? height : picture.GetPlaneHeight(1);
        byte* lumaPlane = (byte*)picture.GetPlane(0);
        int lumaStride = picture.GetStride(0);
        byte* cbPlane = gray ? null : (byte*)picture.GetPlane(1);
        byte* crPlane = gray || interleaved ? null : (byte*)picture.GetPlane(2);
        int cbStride = gray ? 0 : picture.GetStride(1), crStride = gray || interleaved ? 0 : picture.GetStride(2);
        float cbScale = 1 / (2 * (1 - kb)), crScale = 1 / (2 * (1 - kr));
        bool noise = dither && depth == 8;

        PhotoExportColor.ForRows(gray ? height : chromaHeight, width, cancellationToken, (first, last) =>
        {
            for (int cy = first; cy < last; cy++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int top = gray ? cy : cy << shiftY, bottom = gray ? cy + 1 : Math.Min(height, (cy + 1) << shiftY);
                for (int y = top; y < bottom; y++)
                {
                    ushort* input = source + (long)y * width * 4;
                    byte* lumaRow = lumaPlane + (long)y * lumaStride;
                    for (int x = 0; x < width; x++)
                    {
                        float r = input[x * 4] / 65535f, g = input[x * 4 + 1] / 65535f, b = input[x * 4 + 2] / 65535f;
                        float luma = kr * r + kg * g + kb * b;
                        int code = Code(luma * lumaScale + lumaOffset + (noise ? PhotoExportColor.Dither(x, y) : 0), maximum);
                        if (wide)
                            ((ushort*)lumaRow)[x] = (ushort)(code << shift);
                        else
                            lumaRow[x] = (byte)code;
                    }
                }

                if (gray)
                    continue;
                for (int cx = 0; cx < chromaWidth; cx++)
                {
                    float r = 0, g = 0, b = 0;
                    int count = 0;
                    int right = Math.Min(width, (cx + 1) << shiftX);
                    for (int y = top; y < bottom; y++)
                    {
                        ushort* input = source + (long)y * width * 4;
                        for (int x = cx << shiftX; x < right; x++)
                        {
                            r += input[x * 4];
                            g += input[x * 4 + 1];
                            b += input[x * 4 + 2];
                            count++;
                        }
                    }

                    float inverse = 1f / (count * 65535f);
                    r *= inverse;
                    g *= inverse;
                    b *= inverse;
                    float luma = kr * r + kg * g + kb * b;
                    float d = noise ? PhotoExportColor.Dither(cx + 7, cy + 3) : 0;
                    int cb = Code((b - luma) * cbScale * chromaScale + chromaOffset + d, maximum);
                    int cr = Code((r - luma) * crScale * chromaScale + chromaOffset - d, maximum);
                    if (interleaved)
                    {
                        if (wide)
                        {
                            ushort* pair = (ushort*)(cbPlane + (long)cy * cbStride) + cx * 2;
                            pair[0] = (ushort)(cb << shift);
                            pair[1] = (ushort)(cr << shift);
                        }
                        else
                        {
                            byte* pair = cbPlane + (long)cy * cbStride + cx * 2;
                            pair[0] = (byte)cb;
                            pair[1] = (byte)cr;
                        }
                    }
                    else if (wide)
                    {
                        ((ushort*)(cbPlane + (long)cy * cbStride))[cx] = (ushort)cb;
                        ((ushort*)(crPlane + (long)cy * crStride))[cx] = (ushort)cr;
                    }
                    else
                    {
                        cbPlane[(long)cy * cbStride + cx] = (byte)cb;
                        crPlane[(long)cy * crStride + cx] = (byte)cr;
                    }
                }
            }
        });
    }

    /// <summary>Rounds a 16-bit sample to eight bits, adding the given noise in 8-bit steps.</summary>
    public static byte To8(ushort value, float noise) => (byte)Math.Clamp((int)MathF.Round(value / 257f + noise), 0, 255);

    private static int Code(float value, int maximum) => Math.Clamp((int)MathF.Round(value), 0, maximum);
}
