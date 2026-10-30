using System;
using System.IO;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services;

internal static partial class PhotoDecoder
{
    /// <summary>
    /// Decodes a picture for thumbnail encoding with its color, HDR transfer and gain map, or returns null for
    /// pictures the sRGB thumbnail path covers: RAW files and sRGB pictures without HDR.
    /// </summary>
    /// <param name="stream">A seekable stream positioned at the picture.</param>
    public static VideoFrame? DecodeThumbnailSource(Stream stream, int maxDimension, CancellationToken cancellationToken)
    {
        long origin = stream.Position;
        try
        {
            if (IsDng(stream, cancellationToken) || IsSrgbWithoutHdr(stream, cancellationToken))
                return null;

            if (FindLibraryThumbnail(stream, maxDimension, cancellationToken) is { } item)
            {
                GainMapSource? gain = ReadContainer(stream, cancellationToken, item, inheritPrimaryGainMap: true);
                stream.Position = origin;
                VideoFrame? frame = gain is not null
                    ? DecodeContainerGainMap(stream, gain, cancellationToken, useTiles: false, gainMaxDimension: maxDimension)
                    : DecodeGpu(stream, cancellationToken, item, useTiles: false);
                if (frame is not null)
                    return frame;
            }

            stream.Position = origin;
            return DecodeSoftware(stream, cancellationToken, useTiles: false);
        }
        finally
        {
            stream.Position = origin;
        }
    }

    /// <summary>
    /// Decodes an encoded thumbnail into memory with its color, HDR transfer and gain map, at least
    /// <paramref name="width"/> pixels across where the codec can reduce it. Returns null for sRGB thumbnails
    /// without HDR, which a bitmap shows as they are.
    /// </summary>
    public static VideoFrame? DecodeThumbnailFrame(byte[] content, int width, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);
        if (IsJpegXl(content))
            return DecodeSoftware(stream, cancellationToken, useTiles: false);
        if (content is not [0xff, 0xd8, ..])
            return null;

        if (TryFindJpegGainMap(stream, out ImageRange primaryImage, out ImageRange gainImage, out GainMap metadata))
        {
            using SKData primaryData = ReadData(stream, primaryImage);
            using SKData gainData = ReadData(stream, gainImage);
            using SKCodec? primary = SKCodec.Create(primaryData);
            using SKCodec? gain = SKCodec.Create(gainData);
            if (primary is null || gain is null)
                return null;
            using SKColorSpace baseSpace = primary.Info.ColorSpace ?? SKColorSpace.CreateSrgb();
            using SKColorSpace? alternate = metadata.UseBaseColorSpace ? null : gain.Info.ColorSpace ?? SKColorSpace.CreateSrgb();
            using SKColorSpace mathSpace = (alternate ?? baseSpace).ToLinearGamma();
            if (!mathSpace.ToColorSpaceXyz(out SKColorSpaceXyz gamut) ||
                CreateGpuProfile(baseSpace, relativeHdr: true, primary.Info.AlphaType != SKAlphaType.Opaque) is not { } profile)
                return null;

            VideoFrame frame = DecodeCodecFrame(primary, primaryData, profile, hasGainMap: true, width, cancellationToken);
            try
            {
                using var pixels = new SKBitmap(new SKImageInfo(gain.Info.Width, gain.Info.Height, SKColorType.Rgba8888,
                    SKAlphaType.Opaque));
                if (!DecodeGainPixels(gain, pixels))
                {
                    frame.Release();
                    return null;
                }

                AttachGainMap(frame, pixels, gamut, metadata, 8, cancellationToken);
                return frame;
            }
            catch
            {
                frame.Release();
                throw;
            }
        }

        using SKData data = SKData.CreateCopy(content);
        using SKCodec? codec = SKCodec.Create(data);
        return codec?.Info.ColorSpace is { IsSrgb: false } space &&
            CreateGpuProfile(space, relativeHdr: false, codec.Info.AlphaType != SKAlphaType.Opaque) is { } colorProfile
            ? DecodeCodecFrame(codec, data, colorProfile, hasGainMap: false, width, cancellationToken)
            : null;
    }

    internal static bool IsJpegXl(ReadOnlySpan<byte> content) =>
        content.StartsWith((ReadOnlySpan<byte>)[0xff, 0x0a]) ||
        content.StartsWith((ReadOnlySpan<byte>)[0, 0, 0, 12, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0d, 0x0a, 0x87, 0x0a]);

    /// <summary>Whether Skia decodes the picture as sRGB, and it carries no gain map or coded HDR transfer.</summary>
    private static bool IsSrgbWithoutHdr(Stream stream, CancellationToken cancellationToken)
    {
        long origin = stream.Position;
        try
        {
            Span<byte> signature = stackalloc byte[8];
            int read = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
            stream.Position = origin;
            using (var managed = new SKManagedStream(stream, false))
            using (SKCodec? codec = SKCodec.Create(managed))
            {
                if (codec is null || codec.Info.ColorSpace is { IsSrgb: false })
                    return false;
            }

            stream.Position = origin;
            if (read >= 2 && signature[0] == 0xff && signature[1] == 0xd8)
                return !TryFindJpegGainMap(stream, out _, out _, out _);
            if (ReadContainer(stream, cancellationToken) is not null)
                return false;
            stream.Position = origin;
            if (read == 8 && signature.SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]))
            {
                using SKData data = ReadData(stream);
                return ReadPngColorProfile(data, hasAlpha: false, out _) is null;
            }

            return true;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static bool IsDng(Stream stream, CancellationToken cancellationToken)
    {
        long origin = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[8];
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length ||
                (!header.StartsWith("II*\0"u8) && !header.StartsWith("MM\0*"u8)))
                return false;
            bool bigEndian = header[0] == 'M';
            return new DngDirectories(stream, origin, bigEndian, cancellationToken).Read(ReadUInt32(header[4..], bigEndian))
                is [{ IsDng: true }, ..];
        }
        catch (EndOfStreamException)
        {
            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    /// <summary>Decodes a codec's samples, reduced towards <paramref name="width"/>, without color conversion.</summary>
    private static VideoFrame DecodeCodecFrame(SKCodec codec, SKData data, PhotoColorProfile profile, bool hasGainMap, int width,
        CancellationToken cancellationToken)
    {
        float scale = Math.Min(1f, width / (float)codec.Info.Width);
        SKSizeI size = codec.GetScaledDimensions(scale);
        // JPEG rounds to the nearest eighth; round up instead so the result is never narrower than asked.
        if (size.Width < Math.Min(width, codec.Info.Width))
            size = codec.GetScaledDimensions(Math.Min(1f, MathF.Ceiling(scale * 8) / 8));
        if (size.Width < Math.Min(width, codec.Info.Width))
            size = codec.Info.Size;
        bool opaque = codec.Info.AlphaType == SKAlphaType.Opaque;
        VideoFrame frame = VideoFrame.CreateStandalone(size.Width, size.Height, VideoPixelFormat.Bgra32);
        try
        {
            SKCodecResult result = codec.GetPixels(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888,
                opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul), frame.Data, frame.Stride, SKCodecOptions.Default);
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidDataException($"Could not decode thumbnail pixels: {result}.");
            cancellationToken.ThrowIfCancellationRequested();
            using Stream orientationStream = data.AsStream();
            ReadCodecOrientation(codec, orientationStream, out int rotation, out bool mirror);
            frame.Rotation = rotation;
            frame.MirrorHorizontally = mirror;
            frame.PixelAspectRatio = StillImage.ReadPixelAspectRatio(orientationStream);
            ApplyGpuProfile(frame, profile, hasGainMap);
            return frame;
        }
        catch
        {
            frame.Release();
            throw;
        }
    }
}
