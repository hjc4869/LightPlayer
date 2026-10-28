using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightStudio.FfmpegShim.Media;
using LightMediaRenderer.Avalonia.Rendering;
using LightStudio.Logging;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Shaders;

/// <summary>Keeps decoded image views and bounded scene mip levels for one immutable frame.</summary>
/// <remarks>
/// Stable image identities retain uploads across draws. Large scenes can read padded source
/// windows, and scene mip levels share a viewport-sized budget capped at 512 MiB and keyed by
/// their output color settings.
/// References held by in-flight operations keep the images and decoded frame buffer alive.
/// </remarks>
internal sealed class VideoFrameImageCache : IVideoFrameImageCache
{
    private const long MaximumSceneBytes = 512 * 1024 * 1024;
    private static readonly ConditionalWeakTable<Compositor, CompositionCustomVisual> disposalTargets = new();

    private readonly VideoFrame frame;
    private readonly SKImage?[] rasterPlanes;
    private readonly SKImage?[] texturePlanes;
    private readonly SKImageInfo[] planeInfos;
    private readonly SKImage?[] regionPlanes;
    private readonly SKImageInfo[] regionInfos;
    private readonly SKRectI[] regionBounds;
    private SKImage? packedRaster;
    private SKImage? packedTexture;
    private SKImage? gainMapRaster;
    private SKImage? gainMapTexture;
    private VideoFrameImageCache? gainSurface;
    private VideoFrameImageCache? softwarePhoto;
    private bool photoImportFailed;
    private int softwarePhotoStarted;
    private VideoFrame? downloadedPhoto;
    private bool photoDownloadAttempted;
    private PhotoTileCache? photoTiles;
    private PhotoTileCache? gainTiles;
    private int version;
    private SKImageInfo packedInfo;
    private GRContext? textureContext;
    private IPlatformGraphicsContext? platformContext;
    private CompositionCustomVisual? disposalTarget;
    private readonly List<SceneImage> scenes = new(2);
    private long sceneBytes;
    private int references = 1;

    public VideoFrameImageCache(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        this.frame = frame.AddRef();
        rasterPlanes = new SKImage?[frame.PlaneCount];
        texturePlanes = new SKImage?[frame.PlaneCount];
        planeInfos = new SKImageInfo[frame.PlaneCount];
        regionPlanes = new SKImage?[frame.PlaneCount];
        regionInfos = new SKImageInfo[frame.PlaneCount];
        regionBounds = new SKRectI[frame.PlaneCount];
    }

    public VideoFrame Frame => frame;

    public int Version => Volatile.Read(ref version);

    public event Action? Invalidated;

    public event Action<VideoFrame, Exception>? Failed;

    public void RenderPhotoTiles(in VideoRenderRequest request, ISkiaSharpApiLease lease, bool draw = true)
    {
        photoTiles ??= new PhotoTileCache(frame, Invalidate, ReportFailure);
        try { photoTiles.Render(request, lease, draw); }
        catch (Exception exception)
        {
            AppLog.Error("Photo.Rendering", "Could not render the photo viewport.", exception);
            ReportFailure(exception);
        }
    }

    public bool PrepareGainMap(in VideoRenderRequest request, ISkiaSharpApiLease lease)
    {
        if (frame.GainMap?.Samples is not { } samples)
            return true;
        if (!frame.GainMap.BaseIsHdr && VideoFramePipeline.GainMapWeight(frame.GainMap, request.Target, request.HdrPassthrough, request.ColorVolume) <= 0)
            return true;
        if (samples.PlaneSource is not null)
        {
            gainSurface ??= new VideoFrameImageCache(samples);
            gainSurface.SetRenderContext(lease);
            if (gainMapTexture is not null)
                return true;
            SKImage? packed = VideoPlanarLayout.IsPlanar(samples.Format) ? null : gainSurface.GetPackedImage(
                new SKImageInfo(samples.Width, samples.Height, samples.Format switch
                {
                    VideoPixelFormat.Rgba64 => SKColorType.Rgba16161616,
                    VideoPixelFormat.RgbaF16 => SKColorType.RgbaF16,
                    _ => SKColorType.Bgra8888,
                }, SKAlphaType.Unpremul), lease.GrContext);
            gainMapTexture = VideoFramePipeline.RenderPhotoLinear(request with
            {
                Frame = samples, ImageCache = gainSurface, PlaneSource = gainSurface, PackedImage = packed,
                SceneBounds = null, SamplingBounds = null,
            }, out string failure) ?? throw new InvalidOperationException($"Could not process gain-map samples: {failure}.");
            return true;
        }
        if (samples.PhotoSurface is not null)
        {
            gainSurface ??= new VideoFrameImageCache(samples);
            gainSurface.SetRenderContext(lease);
            return gainSurface.GetPackedImage(default, lease.GrContext) is not null;
        }
        if (samples.PhotoTiles is null)
            return true;
        gainTiles ??= new PhotoTileCache(samples, Invalidate, ReportFailure, samplesOnly: true);
        try { gainTiles.Render(request with { Frame = samples }, lease); }
        catch (Exception exception)
        {
            AppLog.Error("Photo.Rendering", "Could not prepare the photo gain map.", exception);
            ReportFailure(exception);
            return false;
        }
        return gainTiles.GetSampleImage(out _) is not null;
    }

    public void RenderPlatformPhoto(in VideoRenderRequest request, ISkiaSharpApiLease lease)
    {
        SetRenderContext(lease);
        if (Volatile.Read(ref softwarePhoto) is { } fallback)
        {
            fallback.SetRenderContext(lease);
            VideoRenderRequest software = request with { Frame = fallback.frame, ImageCache = fallback, PlaneSource = fallback };
            bool gainReady = fallback.PrepareGainMap(software, lease);
            if (fallback.frame.PhotoTiles is not null)
                fallback.RenderPhotoTiles(software, lease, gainReady);
            else if (gainReady)
            {
                SKImage? packed = VideoPlanarLayout.IsPlanar(fallback.frame.Format) ? null : fallback.GetPackedImage(
                    new SKImageInfo(fallback.frame.Width, fallback.frame.Height, fallback.frame.Format switch
                    {
                        VideoPixelFormat.Rgba64 => SKColorType.Rgba16161616,
                        VideoPixelFormat.RgbaF16 => SKColorType.RgbaF16,
                        _ => SKColorType.Bgra8888,
                    }, SKAlphaType.Unpremul), lease.GrContext);
                if (!VideoFramePipeline.Render(software with { PackedImage = packed }, out string failure))
                    ReportFailure(new InvalidOperationException($"Could not render software photo: {failure}."));
            }
            return;
        }

        try
        {
            SKImage? image = GetPackedImage(default, lease.GrContext);
            if (image is not null && PrepareGainMap(request, lease) &&
                VideoFramePipeline.Render(request with { PackedImage = image, ImageCache = this, PlaneSource = null }, out _))
            {
                lease.GrContext?.Flush(submit: true);
                return;
            }
        }
        catch (Exception exception)
        {
            AppLog.Warning("Photo.Rendering", "Accelerated photo rendering failed; loading software pixels.", exception);
            lease.GrContext?.ResetContext();
        }
        StartSoftwarePhoto();
    }

    private void StartSoftwarePhoto()
    {
        if (Interlocked.Exchange(ref softwarePhotoStarted, 1) != 0)
            return;
        AddRef();
        _ = DecodeAsync();

        async Task DecodeAsync()
        {
            VideoFrame? decoded = null;
            try
            {
                decoded = await frame.DownloadPhotoAsync().ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The accelerated photo could not be decoded in software.");
                var fallback = new VideoFrameImageCache(decoded) { disposalTarget = disposalTarget };
                fallback.Invalidated += Invalidate;
                fallback.Failed += (_, exception) => ReportFailure(exception);
                Volatile.Write(ref softwarePhoto, fallback);
                Invalidate();
            }
            catch (Exception exception)
            {
                AppLog.Error("Photo.Rendering", "Could not load the software photo fallback.", exception);
                ReportFailure(exception);
            }
            finally
            {
                decoded?.Release();
                Release();
            }
        }
    }

    private void Invalidate()
    {
        Interlocked.Increment(ref version);
        Invalidated?.Invoke();
    }

    private void ReportFailure(Exception exception) => Failed?.Invoke(frame, exception);

    public bool ImagesAreBorrowed => true;

    public bool UseBytePairPlanes => frame.PlaneSource?.UseBytePairPlanes ?? VideoPlaneFormatProbe.UseBytePairPlanes;

    public void SetDisposalCompositor(Compositor? compositor)
    {
        if (disposalTarget is null && compositor is not null)
            disposalTarget = disposalTargets.GetValue(compositor,
                static owner => owner.CreateCustomVisual(new ImageDisposer()));
    }

    public void SetRenderContext(ISkiaSharpApiLease lease)
    {
        EnsureTextureContext(lease.GrContext);
        if (textureContext is null || platformContext is not null)
            return;

        using ISkiaSharpPlatformGraphicsApiLease? graphics = lease.TryLeasePlatformGraphicsApi();
        platformContext = graphics?.Context;
    }

    public bool HasFullPlaneTextures(GRContext? context) =>
        ReferenceEquals(textureContext, context) && texturePlanes.Length > 0 && texturePlanes[0] is not null;

    public bool CanRetainPlanes(GRContext? context)
    {
        if (context is null || OperatingSystem.IsBrowser() ||
            frame.Width > context.MaxTextureSize || frame.Height > context.MaxTextureSize)
            return false;

        long bytes = 0;
        for (int index = 0; index < frame.PlaneCount; index++)
            bytes += (long)frame.GetRowBytes(index) * frame.GetPlaneHeight(index);
        return bytes <= 384 * 1024 * 1024;
    }

    public VideoFrameImageCache AddRef()
    {
        int current = Volatile.Read(ref references);
        while (current > 0)
        {
            int observed = Interlocked.CompareExchange(ref references, current + 1, current);
            if (observed == current)
                return this;
            current = observed;
        }

        throw new ObjectDisposedException(nameof(VideoFrameImageCache));
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref references) != 0)
            return;

        // GPU image destruction mutates Skia's context-wide resource cache, even when no draw
        // still references this photo. Compositor messages serialize it with other rendering.
        if (textureContext is not null && disposalTarget is { } target)
        {
            if (Dispatcher.UIThread.CheckAccess())
                target.SendHandlerMessage(this);
            else
                Dispatcher.UIThread.Post(() => target.SendHandlerMessage(this));
            return;
        }

        DisposeImages();
    }

    private void DisposeImages()
    {
        DisposeTextures(purgeUnlockedResources: photoTiles is not null || gainTiles is not null);
        packedRaster?.Dispose();
        gainMapRaster?.Dispose();
        foreach (SKImage? plane in rasterPlanes)
            plane?.Dispose();
        downloadedPhoto?.Release();
        softwarePhoto?.Release();
        frame.Release();
    }

    /// <summary>Requests software pixels once and invalidates the image when they become available.</summary>
    public VideoFrame? GetDownloadedPhoto()
    {
        if (!photoDownloadAttempted)
        {
            photoDownloadAttempted = true;
            AddRef();
            _ = DownloadAsync();
        }
        return Volatile.Read(ref downloadedPhoto);

        async Task DownloadAsync()
        {
            try
            {
                VideoFrame? pixels = await frame.DownloadPhotoAsync().ConfigureAwait(false);
                Volatile.Write(ref downloadedPhoto, pixels);
                Invalidate();
            }
            catch (Exception exception)
            {
                AppLog.Error("Photo.Rendering", "Could not download photo pixels.", exception);
                ReportFailure(exception);
            }
            finally
            {
                Release();
            }
        }
    }

    public SKImage? GetGainMapImage(out SKRect? bounds)
    {
        bounds = null;
        if (frame.GainMap?.Samples?.PhotoTiles is not null)
            return gainTiles?.GetSampleImage(out bounds);
        if (frame.GainMap?.Samples?.PlaneSource is not null)
            return gainMapTexture;
        if (frame.GainMap?.Samples?.PhotoSurface is not null)
            return gainSurface?.GetPackedImage(default, textureContext);
        return gainMapRaster ??= frame.GainMap is { } gainMap ? VideoFramePipeline.CreateGainMapImage(gainMap) : null;
    }

    public SKImage? CreatePlane(int index, SKImageInfo info) => CreatePlane(index, info, context: null);

    public SKImage? CreatePlane(int index, SKImageInfo info, GRContext? context)
    {
        if (frame.PlaneSource is { } source)
            return source.CreatePlane(index, info, context);
        if ((uint)index >= (uint)rasterPlanes.Length || frame.GetPlane(index) == IntPtr.Zero)
            return null;

        EnsureTextureContext(context);

        SKImage? raster = rasterPlanes[index];
        if (raster is null || !planeInfos[index].Equals(info))
        {
            texturePlanes[index]?.Dispose();
            texturePlanes[index] = null;
            raster?.Dispose();
            raster = SKImage.FromPixels(info, frame.GetPlane(index), frame.GetStride(index));
            rasterPlanes[index] = raster;
            planeInfos[index] = info;
        }

        // Browser planes retain stable raster identities and use Skia's upload cache.
        if (raster is null || context is null || OperatingSystem.IsBrowser())
            return raster;

        if (texturePlanes[index] is { } texture)
            return texture;

        // A 200 MP luma plane alone can exceed Skia's resource-cache budget. This page-scoped,
        // unbudgeted image stays resident until the photo changes instead of being re-uploaded on
        // every viewport change.
        SKImage? promoted = raster.ToTextureImage(context, mipmapped: false, budgeted: false);
        if (promoted is null || ReferenceEquals(promoted, raster))
            return raster;

        texturePlanes[index] = promoted;
        return promoted;
    }

    public SKImage? CreatePlaneRegion(int index, SKImageInfo info, GRContext? context, SKRectI bounds)
    {
        if (bounds == SKRectI.Create(info.Width, info.Height))
            return CreatePlane(index, info, context);
        if (frame.PlaneSource is not null)
        {
            EnsureTextureContext(context);
            if (regionPlanes[index] is { } imported && regionInfos[index].Equals(info) && regionBounds[index] == bounds)
                return imported;
            regionPlanes[index]?.Dispose();
            regionInfos[index] = info;
            regionBounds[index] = bounds;
            return regionPlanes[index] = CreatePlane(index, info, context)?.Subset(context, bounds);
        }
        if ((uint)index >= (uint)regionPlanes.Length || frame.GetPlane(index) == IntPtr.Zero || bounds.IsEmpty)
            return null;

        EnsureTextureContext(context);
        if (regionPlanes[index] is { } existing && regionInfos[index].Equals(info) && regionBounds[index] == bounds)
            return existing;

        regionPlanes[index]?.Dispose();
        regionPlanes[index] = null;
        nint pixels = frame.GetPlane(index) + checked((nint)(
            ((long)bounds.Top * frame.GetStride(index)) + ((long)bounds.Left * info.BytesPerPixel)));
        SKImage? image = SKImage.FromPixels(info.WithSize(bounds.Width, bounds.Height), pixels, frame.GetStride(index));
        if (image is not null && context is not null && !OperatingSystem.IsBrowser())
        {
            SKImage? texture = image.ToTextureImage(context, mipmapped: false, budgeted: false);
            if (texture is not null && !ReferenceEquals(texture, image))
            {
                image.Dispose();
                image = texture;
            }
        }

        regionPlanes[index] = image;
        regionInfos[index] = info;
        regionBounds[index] = bounds;
        return image;
    }

    public SKImage? GetPackedImage(SKImageInfo info, GRContext? context, bool promote = true)
    {
        if (frame.PlaneSource is { } source)
            return source.CreatePlane(0, info, context);
        if (frame.PhotoSurface is { } photo)
        {
            EnsureTextureContext(context);
            if (packedTexture is not null || photoImportFailed)
                return packedTexture;
            try
            {
                Func<IDisposable>? makeCurrent = platformContext is null ? null : platformContext.EnsureCurrent;
                packedTexture = context is null ? null : photo.TryImport(context, makeCurrent);
                photoImportFailed = packedTexture is null;
                if (photoImportFailed)
                    AppLog.Warning("Photo.Rendering", "The graphics backend could not import the platform photo.");
            }
            catch (Exception exception)
            {
                photoImportFailed = true;
                AppLog.Warning("Photo.Rendering", "Could not import the platform photo.", exception);
            }
            finally { context?.ResetContext(); }
            return packedTexture;
        }
        if (frame.Data == IntPtr.Zero)
            return null;

        EnsureTextureContext(context);

        if (packedRaster is null || !packedInfo.Equals(info))
        {
            packedTexture?.Dispose();
            packedTexture = null;
            packedRaster?.Dispose();
            packedRaster = SKImage.FromPixels(info, frame.Data, frame.Stride);
            packedInfo = info;
        }

        if (!promote)
        {
            packedTexture?.Dispose();
            packedTexture = null;
        }

        if (packedRaster is null || context is null || OperatingSystem.IsBrowser() || !promote)
            return packedRaster;

        if (packedTexture is not null)
            return packedTexture;

        SKImage? promoted = packedRaster.ToTextureImage(context, mipmapped: false, budgeted: false);
        if (promoted is null || ReferenceEquals(promoted, packedRaster))
            return packedRaster;

        packedTexture = promoted;
        return promoted;
    }

    public bool TryGetScene(
        in VideoRenderRequest request,
        SKRectI visible,
        int reduction,
        out SKImage? image,
        out SKRectI bounds)
    {
        EnsureTextureContext(request.GrContext);
        SceneKey key = SceneKey.For(request);
        for (int index = 0; index < scenes.Count; index++)
        {
            SceneImage entry = scenes[index];
            if (entry.Key != key || entry.Reduction != reduction || !entry.Bounds.Contains(visible))
                continue;

            scenes.RemoveAt(index);
            scenes.Add(entry);
            image = entry.Image;
            bounds = entry.Bounds;
            return true;
        }

        image = null;
        bounds = default;
        return false;
    }

    public bool StoreScene(in VideoRenderRequest request, SKRectI bounds, int reduction, SKImage image)
    {
        long bytes = (long)image.Width * image.Height * 8 * 4 / 3;
        long budget = SceneBudget(request);
        if (OperatingSystem.IsBrowser() || bytes > budget)
            return false;

        while (scenes.Count > 0 && (scenes.Count >= 2 || sceneBytes + bytes > budget))
        {
            SceneImage outgoing = scenes[0];
            scenes.RemoveAt(0);
            sceneBytes -= outgoing.Bytes;
            outgoing.Image.Dispose();
        }

        scenes.Add(new SceneImage(SceneKey.For(request), bounds, reduction, image, bytes));
        sceneBytes += bytes;
        return true;
    }

    public long GetSceneBudget(in VideoRenderRequest request) => SceneBudget(request);

    public static long SceneBudget(in VideoRenderRequest request)
    {
        SKRectI clip = request.Canvas.DeviceClipBounds;
        return Math.Clamp(((4L * clip.Width) + 256) * ((4L * clip.Height) + 256) * 8 * 4 / 3,
            128 * 1024 * 1024, MaximumSceneBytes);
    }

    private void EnsureTextureContext(GRContext? context)
    {
        if (ReferenceEquals(textureContext, context))
            return;

        DisposeTextures();
        textureContext = context;
        platformContext = null;
        photoImportFailed = false;
    }

    private void DisposeTextures(bool purgeUnlockedResources = false)
    {
        using IDisposable? current = MakeTextureContextCurrent();
        photoTiles?.Dispose();
        photoTiles = null;
        gainTiles?.Dispose();
        gainTiles = null;
        gainMapTexture?.Dispose();
        gainMapTexture = null;
        gainSurface?.Release();
        gainSurface = null;
        foreach (SceneImage scene in scenes)
            scene.Image.Dispose();
        scenes.Clear();
        sceneBytes = 0;
        packedTexture?.Dispose();
        packedTexture = null;
        foreach (ref SKImage? plane in texturePlanes.AsSpan())
        {
            plane?.Dispose();
            plane = null;
        }
        foreach (ref SKImage? plane in regionPlanes.AsSpan())
        {
            plane?.Dispose();
            plane = null;
        }
        if (purgeUnlockedResources && textureContext is { Handle: not 0, IsAbandoned: false } context)
            context.PurgeUnlockedResources(scratchResourcesOnly: false);
    }

    private IDisposable? MakeTextureContextCurrent()
    {
        if (textureContext is not { Handle: not 0, IsAbandoned: false } context || platformContext is null)
            return null;

        try
        {
            return platformContext.EnsureCurrent();
        }
        catch (PlatformGraphicsContextLostException)
        {
            context.AbandonContext();
            return null;
        }
    }

    private sealed class ImageDisposer : CompositionCustomVisualHandler
    {
        public override void OnMessage(object message) => ((VideoFrameImageCache)message).DisposeImages();

        public override void OnRender(ImmediateDrawingContext drawingContext) { }
    }

    private readonly record struct SceneKey(
        VideoOutputColorSpace Target,
        VideoSurfaceTransfer SurfaceTransfer,
        VideoSurfaceTransfer? Bt709Transfer,
        bool HdrPassthrough,
        VideoOutputColorVolume? ColorVolume,
        VideoSdrToneMappingOptions? SdrToneMapping,
        int PanelBitDepth,
        VideoDebandingQuality Debanding)
    {
        public static SceneKey For(in VideoRenderRequest request) => new(
            request.Target,
            VideoSurfaceTransfers.For(request.Target, request.ColorVolume),
            VideoSurfaceTransfers.ForBt709(),
            request.HdrPassthrough,
            request.ColorVolume,
            request.SdrToneMapping,
            request.PanelBitDepth,
            request.Debanding);
    }

    private sealed record SceneImage(SceneKey Key, SKRectI Bounds, int Reduction, SKImage Image, long Bytes);
}