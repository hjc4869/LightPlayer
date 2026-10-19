using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Net.Codecrete.QrCodeGenerator;

namespace LightStudio.LightPlayer.Services;

internal static class PairingQrCode
{
    private const int BitmapSize = 300;
    private const int QuietZoneModules = 4;
    private const int BytesPerPixel = 4;

    public static WriteableBitmap Create(string payload)
    {
        var code = QrCode.EncodeText(payload, QrCode.Ecc.Medium);
        var totalModules = code.Size + (QuietZoneModules * 2);
        var moduleSize = BitmapSize / totalModules;
        var renderedSize = totalModules * moduleSize;
        var contentOffset = ((BitmapSize - renderedSize) / 2) + (QuietZoneModules * moduleSize);
        var pixels = new byte[BitmapSize * BitmapSize * BytesPerPixel];
        Array.Fill(pixels, byte.MaxValue);

        foreach (var rectangle in code.ToRectangles())
        {
            var left = contentOffset + (rectangle.X * moduleSize);
            var top = contentOffset + (rectangle.Y * moduleSize);
            var right = left + (rectangle.Width * moduleSize);
            var bottom = top + (rectangle.Height * moduleSize);
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++)
                {
                    var offset = ((y * BitmapSize) + x) * BytesPerPixel;
                    pixels[offset] = 0;
                    pixels[offset + 1] = 0;
                    pixels[offset + 2] = 0;
                }
            }
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(BitmapSize, BitmapSize),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        var rowBytes = BitmapSize * BytesPerPixel;
        for (var y = 0; y < BitmapSize; y++)
        {
            Marshal.Copy(pixels, y * rowBytes, buffer.Address + (y * buffer.RowBytes), rowBytes);
        }

        return bitmap;
    }
}