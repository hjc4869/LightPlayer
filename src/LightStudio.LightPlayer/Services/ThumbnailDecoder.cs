using LightMediaRenderer.Avalonia.Controls;
using System;
using System.IO;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services;

/// <summary>Decodes encoded artwork into display-sized <see cref="FastImageSource"/> pixels.</summary>
internal static class ThumbnailDecoder
{
    private static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();

    /// <summary>
    /// Decodes so that the shorter side covers <paramref name="pixelSize"/>, without enlarging the
    /// source. JPEG and WebP decode at reduced scale; the result is downsampled only when that scale
    /// still exceeds the target noticeably.
    /// </summary>
    public static unsafe FastImageSource Decode(ReadOnlySpan<byte> encoded, int pixelSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelSize);
        fixed (byte* address = encoded)
        {
            // Wraps the pinned bytes without a copy; the codec is released before the pin ends.
            using SKData data = SKData.Create((IntPtr)address, encoded.Length);
            using SKCodec codec = SKCodec.Create(data) ?? throw new InvalidDataException("Unsupported artwork format.");
            SKImageInfo encodedInfo = codec.Info;
            int shorter = Math.Min(encodedInfo.Width, encodedInfo.Height);
            int target = Math.Min(pixelSize, shorter);
            float scale = Math.Min(1f, (float)target / shorter);

            // JPEG rounds to the nearest eighth; round up instead so the result is never enlarged.
            SKSizeI size = codec.GetScaledDimensions(scale);
            if (Math.Min(size.Width, size.Height) < target)
                size = codec.GetScaledDimensions(Math.Min(1f, MathF.Ceiling(scale * 8) / 8));

            var info = new SKImageInfo(size.Width, size.Height, SKImageInfo.PlatformColorType,
                encodedInfo.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul, Srgb);
            var bitmap = new SKBitmap(info);
            try
            {
                SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                    throw new InvalidDataException($"Artwork decoding failed: {result}.");

                double reduction = (double)target / Math.Min(size.Width, size.Height);
                if (reduction < 1 / 1.15)
                {
                    SKImageInfo reduced = info.WithSize(
                        Math.Max(1, (int)Math.Round(size.Width * reduction)),
                        Math.Max(1, (int)Math.Round(size.Height * reduction)));
                    SKBitmap resized = bitmap.Resize(reduced, reduction < 0.5
                            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                            : new SKSamplingOptions(SKFilterMode.Linear))
                        ?? throw new InvalidDataException("Artwork resizing failed.");
                    bitmap.Dispose();
                    bitmap = resized;
                }

                bitmap.SetImmutable();
                SKImage image = SKImage.FromBitmap(bitmap) ?? throw new InvalidDataException("Artwork pixels are unusable.");
                return new FastImageSource(image);
            }
            finally
            {
                bitmap.Dispose();
            }
        }
    }
}
