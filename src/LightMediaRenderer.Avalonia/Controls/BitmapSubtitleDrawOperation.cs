using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Subtitles;
using LightStudio.FfmpegShim.Subtitles;
using LightMediaRenderer.Avalonia.Rendering;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Controls;

/// <summary>Paints bitmap subtitle compositions through Avalonia's Skia lease.</summary>
internal sealed class BitmapSubtitleDrawOperation : ICustomDrawOperation
{
    private readonly BitmapSubtitleRenderer renderer;
    private readonly BitmapSubtitleDocument document;
    private readonly TimeSpan time;
    private readonly Rect video;
    private readonly SKSize storage;
    private readonly SubtitleBrightnessMode brightnessMode;
    private readonly VideoOutputLuminanceState outputLuminance;

    public BitmapSubtitleDrawOperation(
        BitmapSubtitleRenderer renderer,
        BitmapSubtitleDocument document,
        TimeSpan time,
        Rect bounds,
        Rect video,
        SKSize storage,
        SubtitleBrightnessMode brightnessMode,
        VideoOutputLuminanceState outputLuminance)
    {
        this.renderer = renderer;
        this.document = document;
        this.time = time;
        this.video = video;
        this.storage = storage;
        this.brightnessMode = brightnessMode;
        this.outputLuminance = outputLuminance;
        Bounds = bounds;
    }

    public Rect Bounds { get; }

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
            return;

        using ISkiaSharpApiLease lease = feature.Lease();
        SKCanvas canvas = lease.SkCanvas;
        int canvasState = canvas.SaveCount;
        try
        {
            var target = new SKRect(
                (float)video.X,
                (float)video.Y,
                (float)video.Right,
                (float)video.Bottom);

            VideoOutputColorSpace output = lease.GetOutputColorSpace();
            float colorScale = SubtitleDrawOperation.ResolveColorScale(
                brightnessMode,
                output.IsExtendedRange(),
                outputLuminance.PeakScale);

            renderer.Render(
                canvas,
                document,
                time,
                target,
                (float)lease.CurrentOpacity,
                storage,
                colorScale);
        }
        catch (Exception exception)
        {
            DrawOperationFailures.Report(nameof(BitmapSubtitleDrawOperation), exception);
        }
        finally
        {
            canvas.RestoreToCount(canvasState);
        }
    }

    public void Dispose()
    {
    }
}