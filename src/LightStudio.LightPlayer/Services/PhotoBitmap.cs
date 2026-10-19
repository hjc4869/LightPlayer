using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Turns decoded still pixels into a toolkit bitmap, which is what everything outside the video
/// renderer -- a library tile, the clipboard -- can actually take.
/// </summary>
internal static class PhotoBitmap
{
    private const int BytesPerPixel = 4;

    public static StillImageThumbnail? DecodeThumbnail(Stream stream, int maxDimension, bool leaveOpen = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDimension, 0);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!stream.CanSeek)
            {
                using var buffered = new MemoryStream();
                byte[] buffer = new byte[65536];
                int read;
                while ((read = stream.Read(buffer)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    buffered.Write(buffer, 0, read);
                }
                buffered.Position = 0;
                return DecodeThumbnail(buffered, maxDimension, true, cancellationToken);
            }
            if (DecodeJpegThumbnail(stream, maxDimension, cancellationToken) is { } jpeg)
                return jpeg;
            using (NativeImageThumbnail? raster = StillImage.DecodeRasterThumbnail(stream, maxDimension, cancellationToken))
            {
                if (raster is not null)
                    return new StillImageThumbnail
                    {
                        Width = raster.Width, Height = raster.Height, Pixels = raster.Pixels.GetSpan().ToArray(),
                    };
            }
            VideoFrame? frame = PhotoDecoder.DecodeJxr(stream, cancellationToken);
            if (frame is null)
                return StillImage.DecodeThumbnail(stream, maxDimension, true, cancellationToken);
            try
            {
                using NativeImageThumbnail thumbnail = StillImage.RenderThumbnail(frame, maxDimension, cancellationToken);
                return new StillImageThumbnail
                {
                    Width = thumbnail.Width, Height = thumbnail.Height, Pixels = thumbnail.Pixels.GetSpan().ToArray(),
                };
            }
            finally { frame.Release(); }
        }
        finally
        {
            if (!leaveOpen)
                stream.Dispose();
        }
    }

    private static StillImageThumbnail? DecodeJpegThumbnail(Stream stream, int maxDimension, CancellationToken cancellationToken)
    {
        long origin = stream.Position;
        try
        {
            bool jpeg = stream.ReadByte() == 0xff && stream.ReadByte() == 0xd8;
            stream.Position = origin;
            if (!jpeg)
                return null;
            double pixelAspectRatio = StillImage.ReadPixelAspectRatio(stream);
            using var managed = new SKManagedStream(stream, false);
            using SKCodec? codec = SKCodec.Create(managed);
            if (codec is null)
                return null;
            if (codec.Info.ColorSpace is { IsSrgb: false })
                return null;
            double displayWidth = codec.Info.Width * pixelAspectRatio;
            double scale = Math.Min(1, maxDimension / Math.Max(displayWidth, codec.Info.Height));
            SKSizeI size = codec.GetScaledDimensions((float)Math.Min(1, scale * Math.Max(1, pixelAspectRatio)));
            using SKColorSpace srgb = SKColorSpace.CreateSrgb();
            using var pixels = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Opaque, srgb));
            if (codec.GetPixels(pixels.Info, pixels.GetPixels(), pixels.RowBytes, SKCodecOptions.Default) != SKCodecResult.Success)
                return null;
            cancellationToken.ThrowIfCancellationRequested();
            int width = Math.Max(1, (int)Math.Round(displayWidth * scale));
            int height = Math.Max(1, (int)Math.Round(codec.Info.Height * scale));
            int rotation = codec.EncodedOrigin switch
            {
                SKEncodedOrigin.BottomRight or SKEncodedOrigin.BottomLeft => 180,
                SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom => 90,
                SKEncodedOrigin.LeftTop or SKEncodedOrigin.LeftBottom => 270,
                _ => 0,
            };
            bool mirror = codec.EncodedOrigin is SKEncodedOrigin.TopRight or SKEncodedOrigin.BottomLeft or
                SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightBottom;
            bool quarterTurn = rotation is 90 or 270;
            int outputWidth = quarterTurn ? height : width;
            int outputHeight = quarterTurn ? width : height;
            using SKSurface? surface = SKSurface.Create(new SKImageInfo(outputWidth, outputHeight,
                SKColorType.Bgra8888, SKAlphaType.Opaque, srgb));
            if (surface is null)
                return null;
            surface.Canvas.Translate(outputWidth / 2f, outputHeight / 2f);
            surface.Canvas.RotateDegrees(rotation);
            if (mirror)
                surface.Canvas.Scale(-1, 1);
            surface.Canvas.Translate(-width / 2f, -height / 2f);
            using SKImage image = SKImage.FromBitmap(pixels);
            surface.Canvas.DrawImage(image, SKRect.Create(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            using SKPixmap? output = surface.PeekPixels();
            if (output is null)
                return null;
            byte[] result = new byte[checked(outputWidth * outputHeight * BytesPerPixel)];
            for (int row = 0; row < outputHeight; row++)
                System.Runtime.InteropServices.Marshal.Copy(output.GetPixels() + row * output.RowBytes,
                    result, row * outputWidth * BytesPerPixel, outputWidth * BytesPerPixel);
            return new StillImageThumbnail { Width = outputWidth, Height = outputHeight, Pixels = result };
        }
        finally { stream.Position = origin; }
    }

    /// <summary>
    /// The picture as a bitmap, turned a further <paramref name="rotation"/> degrees clockwise.
    /// The pixels arrive already upright as the file declares them, so the turn asked for here is
    /// only ever the one the user made while looking at it.
    /// </summary>
    public static Bitmap Create(StillImageThumbnail thumbnail, int rotation = 0)
    {
        int turn = ((rotation % 360) + 360) % 360;
        byte[] pixels = thumbnail.Pixels;
        int width = thumbnail.Width;
        int height = thumbnail.Height;

        if (turn != 0)
        {
            (pixels, width, height) = Rotate(pixels, width, height, turn);
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using ILockedFramebuffer buffer = bitmap.Lock();
        int stride = width * BytesPerPixel;
        for (int y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                pixels,
                y * stride,
                buffer.Address + (y * buffer.RowBytes),
                stride);
        }

        return bitmap;
    }

    private static (byte[] Pixels, int Width, int Height) Rotate(
        byte[] source,
        int width,
        int height,
        int rotation)
    {
        bool quarterTurn = rotation is 90 or 270;
        int destinationWidth = quarterTurn ? height : width;
        int destinationHeight = quarterTurn ? width : height;
        var destination = new byte[destinationWidth * destinationHeight * BytesPerPixel];

        for (int y = 0; y < destinationHeight; y++)
        {
            for (int x = 0; x < destinationWidth; x++)
            {
                (int sourceX, int sourceY) = rotation switch
                {
                    90 => (y, height - 1 - x),
                    180 => (width - 1 - x, height - 1 - y),
                    _ => (width - 1 - y, x),
                };

                Buffer.BlockCopy(
                    source,
                    ((sourceY * width) + sourceX) * BytesPerPixel,
                    destination,
                    (((y * destinationWidth) + x) * BytesPerPixel),
                    BytesPerPixel);
            }
        }

        return (destination, destinationWidth, destinationHeight);
    }
}
