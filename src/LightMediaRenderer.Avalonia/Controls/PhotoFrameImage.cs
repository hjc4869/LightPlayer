using System;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using LightMediaRenderer.Avalonia.Shaders;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;

namespace LightMediaRenderer.Avalonia.Controls;

public sealed class PhotoFrameImage : IImage, IDisposable
{
    private int disposed;

    public PhotoFrameImage(VideoFrame frame)
    {
        Cache = new VideoFrameImageCache(frame);
        double width = frame.Width * (frame.PixelAspectRatio > 0 ? frame.PixelAspectRatio : 1);
        Size = frame.Rotation % 180 == 0 ? new Size(width, frame.Height) : new Size(frame.Height, width);
    }

    internal VideoFrameImageCache Cache { get; }
    public VideoFrame Frame => Cache.Frame;
    public Size Size { get; }

    public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (sourceRect.Width <= 0 || sourceRect.Height <= 0 || destRect.Width <= 0 || destRect.Height <= 0)
            return;
        double scaleX = destRect.Width / sourceRect.Width;
        double scaleY = destRect.Height / sourceRect.Height;
        var destination = new Rect(destRect.X - sourceRect.X * scaleX, destRect.Y - sourceRect.Y * scaleY,
            Size.Width * scaleX, Size.Height * scaleY);
        using var clip = context.PushClip(destRect);
        context.Custom(new VideoDrawOperation(Frame.AddRef(), destRect, destination,
            VideoDebandingQuality.Off, VideoScalingQuality.Bilinear, VideoScalingQuality.Bilinear,
            VideoScalingQuality.Bilinear, VideoShaderPackageCatalog.DisabledOverride, false, imageCache: Cache.AddRef()));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            Cache.Release();
    }
}