using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LightStudio.FfmpegShim.ImageEncoding;
using LightStudio.NativeBindings.Types;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services.PhotoExport;

/// <summary>Writes a rendered picture in the chosen format, with its adapted metadata.</summary>
internal static unsafe class PhotoExportEncoders
{
    public static void Write(Stream output, PhotoExportPicture picture, PhotoExportOptions options,
        PhotoExportMetadataBlocks? metadata, CancellationToken cancellationToken)
    {
        switch (options.Format)
        {
            case PhotoExportFormat.Jpeg:
                WriteJpeg(output, picture, options, metadata, cancellationToken);
                break;
            case PhotoExportFormat.Png when picture.IsPq:
                WritePqPng(output, picture, ZlibLevel(options.Speed), PngMetadataChunks(metadata), cancellationToken);
                break;
            case PhotoExportFormat.Png:
            {
                using SKBitmap bitmap = CreateBitmap(picture, options.PixelFormat, options.Dither, cancellationToken);
                WritePng(output, EncodeBitmap(bitmap, pixmap => pixmap.Encode(
                    new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, ZlibLevel(options.Speed)))), metadata);
                break;
            }

            case PhotoExportFormat.WebP:
            {
                using SKBitmap bitmap = CreateBitmap(picture, PhotoExportPixelFormat.Rgba8, options.Dither, cancellationToken);
                bool lossless = PhotoExportFormats.IsLosslessWebP(options.PixelFormat);
                WriteWebP(output, EncodeBitmap(bitmap, pixmap => pixmap.Encode(new SKWebpEncoderOptions(
                    lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy,
                    lossless ? 75 : Math.Clamp(options.Quality, 1, 100)))), picture, metadata);
                break;
            }

            case PhotoExportFormat.Avif or PhotoExportFormat.Heic:
                WriteHeif(output, picture, options, metadata, cancellationToken);
                break;
            default:
                WriteJxl(output, picture, options, metadata, cancellationToken);
                break;
        }
    }

    private static void WriteJpeg(Stream output, PhotoExportPicture picture, PhotoExportOptions options,
        PhotoExportMetadataBlocks? metadata, CancellationToken cancellationToken)
    {
        bool gray = options.PixelFormat == PhotoExportPixelFormat.Gray8;
        SKJpegEncoderDownsample downsample = options.PixelFormat switch
        {
            PhotoExportPixelFormat.Yuv444 => SKJpegEncoderDownsample.Downsample444,
            PhotoExportPixelFormat.Yuv422 => SKJpegEncoderDownsample.Downsample422,
            _ => SKJpegEncoderDownsample.Downsample420,
        };
        int quality = Math.Clamp(options.Quality, 1, 100);
        byte[] primary;
        using (SKBitmap bitmap = CreateBitmap(picture, gray ? PhotoExportPixelFormat.Gray8 : PhotoExportPixelFormat.Rgb8,
                   options.Dither, cancellationToken))
            primary = EncodeBitmap(bitmap, pixmap => pixmap.Encode(
                new SKJpegEncoderOptions(quality, downsample, SKJpegEncoderAlphaOption.Ignore)));

        if (picture.GainMap is not { } gainMap)
        {
            WriteJpegMetadata(output, primary, metadata);
            return;
        }

        byte[] gainJpeg;
        using (var gainBitmap = new SKBitmap(new SKImageInfo(gainMap.Width, gainMap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque)))
        {
            byte* source = (byte*)gainMap.Pixels.Address;
            byte* target = (byte*)gainBitmap.GetPixels();
            for (int y = 0; y < gainMap.Height; y++)
            {
                byte* row = target + (long)y * gainBitmap.RowBytes;
                for (int x = 0; x < gainMap.Width; x++)
                {
                    long index = ((long)y * gainMap.Width + x) * 3;
                    row[x * 4] = source[index];
                    row[x * 4 + 1] = source[index + 1];
                    row[x * 4 + 2] = source[index + 2];
                    row[x * 4 + 3] = 255;
                }
            }

            gainJpeg = EncodeBitmap(gainBitmap, pixmap => pixmap.Encode(
                new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore)));
        }

        UltraHdrJpegWriter.Write(output, primary, gainJpeg, gainMap.Metadata, metadata);
    }

    /// <summary>Writes a JPEG with the metadata segments after its JFIF header, where EXIF readers expect them.</summary>
    private static void WriteJpegMetadata(Stream output, byte[] jpeg, PhotoExportMetadataBlocks? metadata)
    {
        List<byte[]> segments = PhotoExportMetadata.JpegMetadataSegments(metadata, null);
        if (segments.Count == 0)
        {
            output.Write(jpeg);
            return;
        }

        JpegSegments parsed = JpegSegments.Parse(jpeg);
        byte[]? jfif = parsed.Header.FirstOrDefault(s => s.Marker == 0xE0 && JpegSegments.StartsWith(s.Segment, "JFIF\0"u8)).Segment;
        output.Write([0xFF, 0xD8]);
        if (jfif is not null)
            output.Write(jfif);
        foreach (byte[] segment in segments)
            output.Write(segment);
        foreach ((byte _, byte[] segment) in parsed.Header)
        {
            if (!ReferenceEquals(segment, jfif))
                output.Write(segment);
        }

        output.Write(parsed.Scan);
    }

    private static List<(byte[] Type, byte[] Data)> PngMetadataChunks(PhotoExportMetadataBlocks? metadata)
    {
        var chunks = new List<(byte[], byte[])>();
        if (metadata?.Exif is { } exif)
            chunks.Add(("eXIf"u8.ToArray(), exif));
        if (metadata?.XmpPacket is { } xmp)
            // Keyword, then no compression, method, language or translated keyword.
            chunks.Add(("iTXt"u8.ToArray(), [.. "XML:com.adobe.xmp"u8, 0, 0, 0, 0, 0, .. xmp]));
        return chunks;
    }

    /// <summary>Writes a PNG with the metadata chunks before its first IDAT chunk.</summary>
    private static void WritePng(Stream output, byte[] png, PhotoExportMetadataBlocks? metadata)
    {
        List<(byte[] Type, byte[] Data)> chunks = PngMetadataChunks(metadata);
        int position = 8;
        while (chunks.Count > 0 && position <= png.Length - 12 && !png.AsSpan(position + 4, 4).SequenceEqual("IDAT"u8))
            position += 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
        if (chunks.Count == 0 || position > png.Length - 12)
        {
            output.Write(png);
            return;
        }

        output.Write(png.AsSpan(0, position));
        foreach ((byte[] type, byte[] data) in chunks)
            PngWriter.WriteChunk(output, type, data);
        output.Write(png.AsSpan(position));
    }

    /// <summary>
    /// Writes a WebP with EXIF and XMP chunks after the image, as the extended format orders them,
    /// converting a simple-format file to the extended format.
    /// </summary>
    private static void WriteWebP(Stream output, byte[] webp, PhotoExportPicture picture, PhotoExportMetadataBlocks? metadata)
    {
        byte[]? exif = metadata?.Exif, xmp = metadata?.XmpPacket;
        if (exif is null && xmp is null || webp.Length < 20 || !webp.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            output.Write(webp);
            return;
        }

        ReadOnlySpan<byte> body = webp.AsSpan(12, Math.Min(webp.Length - 12, (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(4)) - 4));
        Span<byte> extended = stackalloc byte[18];
        ReadOnlySpan<byte> rest;
        if (body.StartsWith("VP8X"u8) && body.Length >= 18)
        {
            body[..18].CopyTo(extended);
            rest = body[18..];
        }
        else
        {
            "VP8X"u8.CopyTo(extended);
            BinaryPrimitives.WriteUInt32LittleEndian(extended[4..], 10);
            extended[8] = (byte)(picture.HasAlpha ? 0x10 : 0);
            WriteUInt24(extended[12..], picture.Width - 1);
            WriteUInt24(extended[15..], picture.Height - 1);
            rest = body;
        }

        extended[8] |= (byte)((exif is null ? 0 : 0x08) | (xmp is null ? 0 : 0x04));
        long size = 4 + extended.Length + rest.Length + ChunkSize(exif) + ChunkSize(xmp);
        if (size > uint.MaxValue)
            throw new InvalidDataException("The image is too large for a WebP file.");
        Span<byte> header = stackalloc byte[8];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)size);
        output.Write(header);
        output.Write("WEBP"u8);
        output.Write(extended);
        output.Write(rest);
        WriteRiffChunk("EXIF"u8, exif);
        WriteRiffChunk("XMP "u8, xmp);

        static long ChunkSize(byte[]? data) => data is null ? 0 : 8 + data.Length + (data.Length & 1);

        static void WriteUInt24(Span<byte> target, int value)
        {
            target[0] = (byte)value;
            target[1] = (byte)(value >> 8);
            target[2] = (byte)(value >> 16);
        }

        void WriteRiffChunk(ReadOnlySpan<byte> type, byte[]? data)
        {
            if (data is null)
                return;
            Span<byte> chunk = stackalloc byte[8];
            type.CopyTo(chunk);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[4..], (uint)data.Length);
            output.Write(chunk);
            output.Write(data);
            if ((data.Length & 1) != 0)
                output.WriteByte(0);
        }
    }

    private static void WriteHeif(Stream output, PhotoExportPicture picture, PhotoExportOptions options,
        PhotoExportMetadataBlocks? metadata, CancellationToken cancellationToken)
    {
        StillImageEncoderInfo encoder = options.Encoder ?? throw new InvalidOperationException("No HEIF encoder was chosen.");
        AVPixelFormat format = PhotoExportFormats.EncoderFormat(options.PixelFormat, encoder)
            ?? throw new NotSupportedException($"{encoder.Name} does not take {options.PixelFormat}.");
        bool fullRange = !encoder.IsHardware;
        bool gray = PhotoExportFormats.IsGray(options.PixelFormat);
        int matrix = picture.Primaries == PhotoExportPrimaries.Bt2020 ? PhotoExportColor.CicpMatrixBt2020 : PhotoExportColor.CicpMatrixBt709;
        int depth = PhotoExportFormats.BitDepth(options.PixelFormat);
        var settings = new StillEncodeSettings(options.Quality, options.Speed);
        using StillPicture primary = PhotoExportPlanes.Create(picture, format, matrix, fullRange, options.Dither, cancellationToken);
        var layer = new HeifImageLayer
        {
            Picture = primary,
            BitDepth = depth,
            Channels = gray ? 1 : 3,
            Primaries = PhotoExportColor.Cicp(picture.Primaries),
            Transfer = picture.IsPq ? PhotoExportColor.CicpTransferPq : PhotoExportColor.CicpTransferSrgb,
            Matrix = matrix,
            FullRange = fullRange,
        };

        StillPicture? gainPicture = null;
        try
        {
            HeifGainMapLayer? gainLayer = null;
            if (picture.GainMap is { } gainMap)
            {
                (AVPixelFormat gainFormat, int gainMatrix, bool gainFullRange) =
                    encoder.Supports(AVPixelFormat.AV_PIX_FMT_GBRP) ? (AVPixelFormat.AV_PIX_FMT_GBRP, PhotoExportColor.CicpMatrixIdentity, true)
                    : encoder.Supports(AVPixelFormat.AV_PIX_FMT_YUV444P) ? (AVPixelFormat.AV_PIX_FMT_YUV444P, PhotoExportColor.CicpMatrixBt601, true)
                    : (PhotoExportFormats.EncoderFormat(PhotoExportPixelFormat.Yuv420, encoder)
                        ?? throw new NotSupportedException($"{encoder.Name} takes no 8-bit layout for a gain map."),
                        PhotoExportColor.CicpMatrixBt601, fullRange);
                gainPicture = PhotoExportPlanes.CreateGainMap(gainMap, gainFormat, gainMatrix, gainFullRange, cancellationToken);
                gainLayer = new HeifGainMapLayer
                {
                    Image = new HeifImageLayer
                    {
                        Picture = gainPicture,
                        BitDepth = 8,
                        Channels = 3,
                        Primaries = 2,
                        Transfer = 2,
                        Matrix = gainMatrix,
                        FullRange = gainFullRange,
                    },
                    Metadata = gainMap.Metadata.ToIsoBinary(),
                    AlternatePrimaries = PhotoExportColor.Cicp(picture.Primaries),
                    AlternateTransfer = PhotoExportColor.CicpTransferPq,
                    AlternateBitDepth = 10,
                    AlternateChannels = 3,
                    MaxContentLightLevel = (int)MathF.Ceiling(gainMap.Peak * PhotoExportColor.ReferenceWhiteNits),
                };
            }

            HeifImageEncoder.Write(output, encoder, settings, layer, gainLayer,
                picture.IsPq ? ((int)MathF.Ceiling(picture.MaxContentLightLevel), (int)MathF.Ceiling(picture.MaxFrameAverageLightLevel)) : null,
                options.TileSize,
                metadata is null ? null : new HeifMetadata { Exif = metadata.Exif, Xmp = metadata.XmpPacket });
        }
        finally
        {
            gainPicture?.Dispose();
        }
    }

    private static void WriteJxl(Stream output, PhotoExportPicture picture, PhotoExportOptions options,
        PhotoExportMetadataBlocks? metadata, CancellationToken cancellationToken)
    {
        StillImageEncoderInfo encoder = options.Encoder ?? throw new InvalidOperationException("No JPEG XL encoder was chosen.");
        AVPixelFormat format = PhotoExportFormats.EncoderFormat(options.PixelFormat, encoder)
            ?? throw new NotSupportedException($"{encoder.Name} does not take {options.PixelFormat}.");
        byte[] encoded;
        using (StillPicture still = PhotoExportPlanes.Create(picture, format, PhotoExportColor.CicpMatrixBt709, true, options.Dither,
                   cancellationToken))
            encoded = StillImageEncoder.Encode(encoder, still, new StillEncodeSettings(options.Quality, options.Speed));
        WriteJxlMetadata(output, encoded, metadata);
    }

    /// <summary>
    /// Writes a JPEG XL file with <c>Exif</c> and <c>xml </c> boxes ahead of the codestream, wrapping a
    /// bare codestream in the ISO base media container.
    /// </summary>
    private static void WriteJxlMetadata(Stream output, byte[] encoded, PhotoExportMetadataBlocks? metadata)
    {
        byte[]? exif = metadata?.Exif, xmp = metadata?.XmpPacket;
        ReadOnlySpan<byte> signature = [0, 0, 0, 12, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A];
        if (exif is null && xmp is null)
        {
            output.Write(encoded);
            return;
        }

        int codestream;
        if (encoded.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0x0A]))
        {
            output.Write(signature);
            WriteBox(output, "ftyp"u8, [], [(byte)'j', (byte)'x', (byte)'l', (byte)' ', 0, 0, 0, 0, (byte)'j', (byte)'x', (byte)'l', (byte)' ']);
            codestream = -1;
        }
        else if (encoded.AsSpan().StartsWith(signature))
        {
            codestream = 0;
            while (codestream <= encoded.Length - 8)
            {
                ReadOnlySpan<byte> type = encoded.AsSpan(codestream + 4, 4);
                if (type.SequenceEqual("jxlc"u8) || type.SequenceEqual("jxlp"u8))
                    break;
                long size = BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(codestream));
                if (size == 1)
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(encoded.AsSpan(codestream + 8));
                if (size < 8 || size > encoded.Length - codestream)
                    throw new InvalidDataException("The JPEG XL encoder produced a malformed container.");
                codestream += (int)size;
            }

            output.Write(encoded.AsSpan(0, codestream));
        }
        else
        {
            throw new InvalidDataException("The JPEG XL encoder produced neither a codestream nor a container.");
        }

        if (exif is not null)
            WriteBox(output, "Exif"u8, [0, 0, 0, 0], exif);
        if (xmp is not null)
            WriteBox(output, "xml "u8, [], xmp);
        if (codestream < 0)
            WriteBox(output, "jxlc"u8, [], encoded);
        else
            output.Write(encoded.AsSpan(codestream));

        static void WriteBox(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> data)
        {
            long size = 8L + prefix.Length + data.Length;
            Span<byte> header = stackalloc byte[16];
            if (size <= uint.MaxValue)
            {
                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size);
                type.CopyTo(header[4..]);
                output.Write(header[..8]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(header, 1);
                type.CopyTo(header[4..]);
                BinaryPrimitives.WriteUInt64BigEndian(header[8..], (ulong)(size + 8));
                output.Write(header);
            }

            output.Write(prefix);
            output.Write(data);
        }
    }

    /// <summary>A Skia bitmap of the samples in the color space they are encoded in.</summary>
    private static SKBitmap CreateBitmap(PhotoExportPicture picture, PhotoExportPixelFormat pixelFormat, bool dither,
        CancellationToken cancellationToken)
    {
        bool wide = PhotoExportFormats.BitDepth(pixelFormat) == 16;
        bool gray = pixelFormat == PhotoExportPixelFormat.Gray8;
        bool alpha = picture.HasAlpha && !gray;
        SKColorType type = gray ? SKColorType.Gray8 : wide ? SKColorType.Rgba16161616 : SKColorType.Rgba8888;
        using SKColorSpace space = picture.Primaries switch
        {
            PhotoExportPrimaries.DisplayP3 => SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3),
            PhotoExportPrimaries.Bt2020 => SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.Rec2020),
            _ => SKColorSpace.CreateSrgb(),
        };
        var bitmap = new SKBitmap(new SKImageInfo(picture.Width, picture.Height, type,
            alpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque, space));
        try
        {
            (float r, float g, float b) luma = PhotoExportColor.Luma(picture.Primaries);
            byte* pixels = (byte*)bitmap.GetPixels();
            int rowBytes = bitmap.RowBytes;
            ushort* source = picture.Pointer;
            int width = picture.Width;
            PhotoExportColor.ForRows(picture.Height, width, cancellationToken, (first, last) =>
            {
                for (int y = first; y < last; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ushort* input = source + (long)y * width * 4;
                    byte* row = pixels + (long)y * rowBytes;
                    for (int x = 0; x < width; x++)
                    {
                        float noise = dither ? PhotoExportColor.Dither(x, y) : 0;
                        if (gray)
                        {
                            float value = luma.r * input[x * 4] + luma.g * input[x * 4 + 1] + luma.b * input[x * 4 + 2];
                            row[x] = PhotoExportPlanes.To8((ushort)Math.Clamp(value + 0.5f, 0, 65535), noise);
                        }
                        else if (wide)
                        {
                            ushort* target = (ushort*)row + x * 4;
                            target[0] = input[x * 4];
                            target[1] = input[x * 4 + 1];
                            target[2] = input[x * 4 + 2];
                            target[3] = alpha ? input[x * 4 + 3] : ushort.MaxValue;
                        }
                        else
                        {
                            row[x * 4] = PhotoExportPlanes.To8(input[x * 4], noise);
                            row[x * 4 + 1] = PhotoExportPlanes.To8(input[x * 4 + 1], noise);
                            row[x * 4 + 2] = PhotoExportPlanes.To8(input[x * 4 + 2], noise);
                            row[x * 4 + 3] = alpha ? PhotoExportPlanes.To8(input[x * 4 + 3], 0) : (byte)255;
                        }
                    }
                }
            });
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static byte[] EncodeBitmap(SKBitmap bitmap, Func<SKPixmap, SKData?> encode)
    {
        using SKPixmap pixmap = bitmap.PeekPixels();
        using SKData? data = encode(pixmap);
        return data?.ToArray() ?? throw new InvalidDataException("The image encoder produced no data.");
    }

    private static int ZlibLevel(StillEncodeSpeed speed) => speed switch
    {
        StillEncodeSpeed.Slower => 9,
        StillEncodeSpeed.Faster => 3,
        _ => 6,
    };

    /// <summary>
    /// Writes 16-bit BT.2100 PQ PNG. The cICP chunk identifies the primaries and transfer function, and
    /// cLLI carries the content light levels.
    /// </summary>
    private static void WritePqPng(Stream output, PhotoExportPicture picture, int level, List<(byte[] Type, byte[] Data)> metadata,
        CancellationToken cancellationToken)
    {
        int channels = picture.HasAlpha ? 4 : 3;
        List<(byte[] Type, byte[] Data)> chunks =
        [
            ("cICP"u8.ToArray(), PngWriter.Cicp(PhotoExportColor.Cicp(picture.Primaries), PhotoExportColor.CicpTransferPq)),
            ("cLLI"u8.ToArray(), PngWriter.ContentLightLevels(picture.MaxContentLightLevel, picture.MaxFrameAverageLightLevel)),
            .. metadata,
        ];
        PngWriter.Write(output, picture.Width, picture.Height, 16, channels, (y, row) =>
        {
            ushort* source = picture.Pointer + (long)y * picture.Width * 4;
            for (int x = 0, target = 0; x < picture.Width; x++)
            {
                for (int c = 0; c < channels; c++, target += 2)
                    BinaryPrimitives.WriteUInt16BigEndian(row[target..], source[x * 4 + c]);
            }
        }, chunks, level, cancellationToken);
    }
}
