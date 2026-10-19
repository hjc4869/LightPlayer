using SkiaSharp;

namespace LightMediaRenderer.Contracts;

/// <summary>Host-owned image views and scene buffers borrowed for a render call.</summary>
public interface IVideoFrameImageCache : IVideoPlaneSource
{
    bool HasFullPlaneTextures(GRContext? context);

    bool CanRetainPlanes(GRContext? context);

    SKImage? CreatePlaneRegion(int index, SKImageInfo info, GRContext? context, SKRectI bounds);

    SKImage? GetGainMapImage(out SKRect? bounds);

    bool TryGetScene(in VideoRenderRequest request, SKRectI visible, int reduction,
        out SKImage? image, out SKRectI bounds);

    bool StoreScene(in VideoRenderRequest request, SKRectI bounds, int reduction, SKImage image);

    long GetSceneBudget(in VideoRenderRequest request);
}