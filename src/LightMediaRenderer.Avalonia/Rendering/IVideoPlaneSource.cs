using System;
using Avalonia.Platform;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Rendering;

/// <summary>
/// The planes of a decoder surface imported into backend textures and wrapped for Skia.
/// </summary>
/// <remarks>
/// Cached textures are borrowed for one draw. Other imports are released when the draw ends;
/// their images retain the resources needed by outstanding GPU reads.
/// </remarks>
internal sealed class VideoSurfacePlanes(GRContext context, VideoFrame frame,
    IVideoSurfaceTextures imported, IPlatformGraphicsContext? releaseContext)
    : IVideoPlaneSource, IDisposable
{
    public bool UseBytePairPlanes => false;
    public bool RequiresReadFence => imported.RequiresReadFence;

    public SKImage? CreatePlane(int index, SKImageInfo info) => imported.CreateImage(context, frame, index, info);

    public void Dispose()
    {
        if (releaseContext is not { } owner)
            return;
        releaseContext = null;
        imported.Destroy(owner);
    }
}
