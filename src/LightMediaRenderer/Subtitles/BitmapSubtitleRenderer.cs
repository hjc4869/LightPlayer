using System;
using LightMediaRenderer.Contracts;
using SkiaSharp;

namespace LightMediaRenderer.Subtitles;

/// <summary>Draws the bitmap subtitle composition visible at a given video timestamp.</summary>
public sealed unsafe class BitmapSubtitleRenderer : IDisposable
{
    private static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();
    private static readonly SKSamplingOptions Sampling =
        new(SKFilterMode.Linear, SKMipmapMode.None);

    private readonly object sync = new();
    private readonly SKPaint paint = new() { IsAntialias = false };
    private BitmapSubtitleDocument? cachedDocument;
    private BitmapSubtitleCue? cachedCue;
    private SKBitmap[] bitmaps = [];
    private SKImage[] images = [];
    private float cachedColorScale;
    private bool disposed;

    public void Render(
        SKCanvas canvas,
        BitmapSubtitleDocument document,
        TimeSpan time,
        SKRect target,
        float opacity = 1f,
        SKSize storage = default,
        float colorScale = 1f)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(document);

        if (target.Width <= 0 || target.Height <= 0 || opacity <= 0 ||
            !float.IsFinite(colorScale) || colorScale <= 0)
        {
            return;
        }

        lock (sync)
        {
            if (disposed)
                return;

            BitmapSubtitleCue? cue = document.CueAt(time);
            if (cue is null)
            {
                DropImages();
                cachedDocument = document;
                cachedCue = null;
                return;
            }

            if (!ReferenceEquals(document, cachedDocument) ||
                !ReferenceEquals(cue, cachedCue) ||
                cachedColorScale != colorScale)
            {
                LoadImages(cue, colorScale);
                cachedDocument = document;
                cachedCue = cue;
                cachedColorScale = colorScale;
            }

            float canvasWidth = document.CanvasWidth > 0
                ? document.CanvasWidth
                : storage.Width > 0 ? storage.Width : target.Width;
            float canvasHeight = document.CanvasHeight > 0
                ? document.CanvasHeight
                : storage.Height > 0 ? storage.Height : target.Height;
            if (canvasWidth <= 0 || canvasHeight <= 0)
                return;

            paint.Color = new SKColor(255, 255, 255, (byte)Math.Clamp(opacity * 255, 0, 255));

            int restore = canvas.Save();
            canvas.ClipRect(target);
            canvas.Translate(target.Left, target.Top);
            canvas.Scale(target.Width / canvasWidth, target.Height / canvasHeight);
            for (int index = 0; index < images.Length; index++)
            {
                BitmapSubtitleRectangle rectangle = cue.Rectangles[index];
                var destination = new SKRect(
                    rectangle.X,
                    rectangle.Y,
                    rectangle.X + rectangle.Width,
                    rectangle.Y + rectangle.Height);
                canvas.DrawImage(images[index], destination, Sampling, paint);
            }
            canvas.RestoreToCount(restore);
        }
    }

    private void LoadImages(BitmapSubtitleCue cue, float colorScale)
    {
        DropImages();

        var nextBitmaps = new SKBitmap[cue.Rectangles.Length];
        var nextImages = new SKImage[cue.Rectangles.Length];
        try
        {
            for (int index = 0; index < cue.Rectangles.Length; index++)
            {
                SKBitmap bitmap = CreateBitmap(cue.Rectangles[index], colorScale);
                nextBitmaps[index] = bitmap;
                nextImages[index] = SKImage.FromBitmap(bitmap)
                    ?? throw new InvalidOperationException("Skia could not create a subtitle image.");
            }

            bitmaps = nextBitmaps;
            images = nextImages;
        }
        catch
        {
            DisposeImages(nextImages, nextBitmaps);
            throw;
        }
    }

    private static SKBitmap CreateBitmap(BitmapSubtitleRectangle rectangle, float colorScale)
    {
        bool extended = colorScale != 1f;
        var info = new SKImageInfo(
            rectangle.Width,
            rectangle.Height,
            extended ? SKColorType.RgbaF16 : SKColorType.Bgra8888,
            SKAlphaType.Unpremul,
            Srgb);
        var bitmap = new SKBitmap(info);
        if (bitmap.GetPixels() == IntPtr.Zero)
        {
            bitmap.Dispose();
            throw new InvalidOperationException("Skia could not allocate a subtitle bitmap.");
        }

        if (extended)
            FillExtended(bitmap, rectangle, colorScale);
        else
            FillBgra(bitmap, rectangle);
        return bitmap;
    }

    private static void FillBgra(SKBitmap bitmap, BitmapSubtitleRectangle rectangle)
    {
        Span<uint> target = new((void*)bitmap.GetPixels(), rectangle.Pixels.Length);
        for (int index = 0; index < rectangle.Pixels.Length; index++)
        {
            int paletteIndex = rectangle.Pixels[index];
            target[index] = paletteIndex < rectangle.Palette.Length
                ? rectangle.Palette[paletteIndex]
                : 0;
        }
    }

    private static void FillExtended(
        SKBitmap bitmap,
        BitmapSubtitleRectangle rectangle,
        float colorScale)
    {
        Span<Half> target = new((void*)bitmap.GetPixels(), checked(rectangle.Pixels.Length * 4));
        for (int index = 0; index < rectangle.Pixels.Length; index++)
        {
            int paletteIndex = rectangle.Pixels[index];
            uint color = paletteIndex < rectangle.Palette.Length
                ? rectangle.Palette[paletteIndex]
                : 0;
            int offset = index * 4;
            target[offset] = (Half)AssSubtitleRenderer.ScaleSrgb((byte)(color >> 16), colorScale);
            target[offset + 1] = (Half)AssSubtitleRenderer.ScaleSrgb((byte)(color >> 8), colorScale);
            target[offset + 2] = (Half)AssSubtitleRenderer.ScaleSrgb((byte)color, colorScale);
            target[offset + 3] = (Half)((color >> 24) / 255f);
        }
    }

    private void DropImages()
    {
        DisposeImages(images, bitmaps);
        images = [];
        bitmaps = [];
    }

    private static void DisposeImages(SKImage[] imagesToDispose, SKBitmap[] bitmapsToDispose)
    {
        foreach (SKImage? image in imagesToDispose)
            image?.Dispose();
        foreach (SKBitmap? bitmap in bitmapsToDispose)
            bitmap?.Dispose();
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;
            DropImages();
            paint.Dispose();
        }
    }
}