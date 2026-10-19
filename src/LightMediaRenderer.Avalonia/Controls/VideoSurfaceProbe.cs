using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using LightMediaRenderer.Avalonia.Rendering;

namespace LightMediaRenderer.Avalonia.Controls;

/// <summary>
/// An invisible control that asks the graphics backend, as the shell first draws, whether it can
/// import decoder surfaces, and draws again whenever a decoder retires a surface pool so the
/// imports of it are released.
/// </summary>
/// <remarks>
/// A decoder has to choose between producing surfaces and reading its frames back as it opens, and
/// cannot change its mind afterwards, so the answer has to exist before the first file is played.
/// <see cref="VideoView"/> asks the same question while it is empty, but it only exists once the
/// player page is on screen, which is the same moment the decoder for that file opens -- the first
/// video of a session would lose the race and be decoded to readback. Living in the shell instead
/// means the question is settled while the library is still being looked at.
/// </remarks>
public sealed class VideoSurfaceProbe : Control
{
    public VideoSurfaceProbe()
    {
        Width = 1;
        Height = 1;
        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        bool evaluated = VideoSurfaceImport.IsEvaluated;
        if (evaluated && !VideoFrameCapabilities.HasRetiredSurfacePools)
            return;

        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        context.Custom(new VideoSurfaceProbeOperation(bounds));

        // The first draws of a window can be composited without a GPU context, which answers
        // nothing; keep asking until one of them does.
        if (!evaluated)
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        VideoFrameCapabilities.SurfacePoolRetired += OnSurfacePoolRetired;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        VideoFrameCapabilities.SurfacePoolRetired -= OnSurfacePoolRetired;
        base.OnDetachedFromVisualTree(e);
    }

    // A decoder retires its pool after playback has left the screen, when nothing else draws.
    private void OnSurfacePoolRetired() => Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
}
