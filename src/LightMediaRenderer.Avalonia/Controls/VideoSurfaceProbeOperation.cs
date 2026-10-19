using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Media;
using LightMediaRenderer.Avalonia.Rendering;
using LightMediaRenderer.Avalonia.Shaders;

namespace LightMediaRenderer.Avalonia.Controls;

/// <summary>
/// Draws nothing, and only asks the graphics backend what it is: whether it can import decoder
/// surfaces, and what color the surface it draws into is read in. It also releases the imports of
/// surface pools decoders have retired.
/// </summary>
/// <remarks>
/// Decoders have to know before they open, because a device that produces surfaces the renderer
/// cannot import is not one they can change their mind about later. Establishing it takes a
/// graphics context, which only a draw has, so an empty video view issues this to have an answer
/// ready by the time something is played.
/// </remarks>
internal sealed class VideoSurfaceProbeOperation(
    Rect bounds,
    VideoScalingQuality scaling = VideoScalingQuality.Bilinear,
    bool hdrPassthrough = true,
    Action<bool, double>? outputChanged = null)
    : ICustomDrawOperation
{
    public Rect Bounds { get; } = bounds;

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
            return;

        using ISkiaSharpApiLease lease = feature.Lease();
        try
        {
            Probe(lease);
        }
        catch (Exception exception)
        {
            DrawOperationFailures.Report(nameof(VideoSurfaceProbeOperation), exception);
        }
    }

    private void Probe(ISkiaSharpApiLease lease)
    {
        // This is the only guaranteed paint before the decoder produces its first frame.
        RenderThreadScheduler.EnterRenderPass();
        VideoSurfaceImport.Evaluate(lease);
        VideoSurfaceImport.ReleaseRetired(lease);

        // WebCodecs must preserve high-bit HDR samples for the production tone mapper. Establish
        // whether this backend can display their 16-bit containers before a decoder chooses that
        // route, rather than rejecting its first frame after playback has already started. A lease
        // without a context would answer for the rasterizer rather than for the backend.
        if (lease.GrContext is not null)
            VideoPlaneFormatProbe.Supports(lease.GrContext, VideoPixelFormat.Yuv420p10);

        // Reported from here too, so the color chain can be read off an idle view rather than
        // only once something HDR happens to be playing.
        VideoOutputColorSpace target = lease.GetOutputColorSpace();
        VideoHdrOutputMapping hdrOutput = target.ResolveHdrOutput(
            sourcePeakLuminance: 1000,
            hdrPassthrough: false,
            componentGain: 1,
            colorVolume: lease.GetOutputColorVolume());
        outputChanged?.Invoke(target.IsExtendedRange(), 1);
        VideoOutputDiagnostics.RecordSurface(
            lease,
            target,
            VideoColorTransfer.Bt709,
            hdrOutput,
            scaling,
            hdrPassthrough,
            lease.GetOutputColorVolume(),
            isProbe: true);
    }

    public void Dispose()
    {
    }
}
