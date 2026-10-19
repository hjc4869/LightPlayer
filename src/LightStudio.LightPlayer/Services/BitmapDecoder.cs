using System;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Skia;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services;

/// <summary>Decodes display-sized bitmaps, returning null for unreadable image data.</summary>
internal static class BitmapDecoder
{
    public static Bitmap? DecodeToWidth(byte[]? content, int width, BitmapInterpolationMode interpolationMode)
    {
        using var pixels = DecodePixelsToWidth(content, width, interpolationMode);
        return pixels is null ? null : CreateBitmap(pixels);
    }

    /// <summary>
    /// Decodes <paramref name="content"/> <paramref name="width"/> pixels across and applies a Gaussian blur, with
    /// the picture's edges extended outward.
    /// </summary>
    public static Bitmap? DecodeBackdrop(byte[]? content, int width, float blurSigma)
    {
        using var decoded = DecodePixelsToWidth(content, width, BitmapInterpolationMode.HighQuality);
        if (decoded is null)
            return null;

        var info = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var source = SKImage.FromPixels(info, decoded.GetPixels(), decoded.RowBytes);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Could not create the backdrop surface.");
        using var filter = SKImageFilter.CreateBlur(blurSigma, blurSigma, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = filter };
        surface.Canvas.DrawImage(source, 0, 0, SKSamplingOptions.Default, paint);
        using var blurred = surface.PeekPixels();
        return new Bitmap(
            PixelFormat.Bgra8888,
            AlphaFormat.Premul,
            blurred.GetPixels(),
            new PixelSize(decoded.Width, decoded.Height),
            new Vector(96, 96),
            blurred.RowBytes);
    }

    private static SKBitmap? DecodePixelsToWidth(byte[]? content, int width, BitmapInterpolationMode interpolationMode)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        if (content is not { Length: > 0 })
            return null;

        using var stream = new MemoryStream(content, writable: false);
        using var managed = new SKManagedStream(stream);
        using var codec = SKCodec.Create(managed);
        if (codec is null)
            return null;

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
            return null;

        var size = codec.GetScaledDimensions((float)width / info.Width);
        if (size.Width <= 0 || size.Height <= 0)
            return null;

        var decodedInfo = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var decoded = SKBitmap.Decode(codec, decodedInfo);
        if (decoded is null)
            return null;

        var height = Math.Max(1, checked((int)((double)info.Height / info.Width * width)));
        if (decoded.Width == width && decoded.Height == height)
            return decoded;

        using (decoded)
        {
            var upscaling = width > decoded.Width || height > decoded.Height;
            var sampling = interpolationMode == BitmapInterpolationMode.HighQuality && !upscaling
                ? BitmapInterpolationMode.MediumQuality.ToSKSamplingOptions()
                : interpolationMode.ToSKSamplingOptions();
            return decoded.Resize(decodedInfo.WithSize(width, height), sampling)
                ?? throw new InvalidDataException("Image resizing failed.");
        }
    }

    private static Bitmap CreateBitmap(SKBitmap pixels) => new(
        PixelFormat.Bgra8888,
        AlphaFormat.Premul,
        pixels.GetPixels(),
        new PixelSize(pixels.Width, pixels.Height),
        new Vector(96, 96),
        pixels.RowBytes);
}