using System;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Subtitles;
using LightMediaRenderer.Avalonia.Rendering;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Controls;

internal sealed class VideoOutputLuminanceState
{
    private double peakScale = 1;

    public double PeakScale
    {
        get => Volatile.Read(ref peakScale);
        set => Volatile.Write(ref peakScale, value);
    }
}

/// <summary>
/// Paints the subtitles for one timestamp over the picture, through Avalonia's Skia lease.
/// </summary>
/// <remarks>
/// The renderer is owned by the control rather than by the operation: it holds the font, shaping and
/// layout caches, and rebuilding those every frame would be far more expensive than drawing.
/// </remarks>
internal sealed class SubtitleDrawOperation : ICustomDrawOperation
{
    private readonly AssSubtitleRenderer renderer;
    private readonly AssDocument document;
    private readonly TimeSpan time;
    private readonly Rect video;
    private readonly SKSize storage;
    private readonly SubtitleBrightnessMode brightnessMode;
    private readonly VideoOutputLuminanceState outputLuminance;

    public SubtitleDrawOperation(
        AssSubtitleRenderer renderer,
        AssDocument document,
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

    // The timestamp moves on every frame, so the scene graph must not reuse the previous operation.
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
            bool extended = output.IsExtendedRange();
            float colorScale = ResolveColorScale(
                brightnessMode,
                extended,
                outputLuminance.PeakScale);
            bool drawTextAsPath = RequiresWideTextPath(
                extended,
                lease.GetOutputColorVolume()?.ReferenceWhiteScale ?? 1,
                colorScale);

            renderer.Render(
                canvas,
                document,
                time,
                target,
                (float)lease.CurrentOpacity,
                storage,
                colorScale,
                drawTextAsPath);
        }
        catch (Exception exception)
        {
            DrawOperationFailures.Report(nameof(SubtitleDrawOperation), exception);
        }
        finally
        {
            canvas.RestoreToCount(canvasState);
        }
    }

    internal static float ResolveColorScale(
        SubtitleBrightnessMode mode,
        bool extended,
        double videoPeakScale)
    {
        if (!extended || mode != SubtitleBrightnessMode.FollowVideo ||
            !double.IsFinite(videoPeakScale) || videoPeakScale <= 0)
        {
            return 1f;
        }

        return (float)Math.Clamp(videoPeakScale, 0.01, 125);
    }

    internal static bool RequiresWideTextPath(
        bool extended,
        double referenceWhiteScale,
        float colorScale)
    {
        if (!extended || !double.IsFinite(referenceWhiteScale) || referenceWhiteScale <= 0 ||
            !float.IsFinite(colorScale) || colorScale <= 0)
        {
            return false;
        }

        // Ganesh packs atlas text colors into 8-bit vertices, so values above 1.0 need paths.
        return colorScale * referenceWhiteScale > 1.0001;
    }

    public void Dispose()
    {
    }
}
