using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Skia;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Rendering;

/// <summary>
/// Hands decoded surfaces to whichever <see cref="IVideoSurfaceImporter"/> the graphics backend
/// turned out to support, and keeps what they become for as long as the decoder keeps the surface.
/// </summary>
/// <remarks>
/// <para>
/// The importers themselves belong to the application head, which registers the ones its platform
/// has through <see cref="VideoSurfaceImporterFactory"/>. Which of them works is a property of the
/// machine rather than of the build, so it is established by trying them, and a backend where none
/// of them does leaves the decoders reading their frames back as before.
/// </para>
/// <para>
/// Every member touches the graphics context and must be called on the render thread from inside a
/// draw.
/// </para>
/// </remarks>
internal static class VideoSurfaceImport
{
    // Comfortably more than the surfaces a decoder's pool holds, so a steady stream of playback
    // imports each surface once and then only looks it up.
    private const int MaxCachedSurfaces = 48;

    private static readonly Dictionary<VideoSurfaceHandle, CachedSurface> cache = [];
    private static IVideoSurfaceImporter? importer;
    private static IPlatformGraphicsContext? cachedFor;
    private static GRContext? evaluatedFor;
    private static bool evaluated;
    private static long clock;

    /// <summary>
    /// Whether a graphics context has been inspected yet. False while the backend has not drawn
    /// anything with one, which is when a decoder opening would still have to assume the worst.
    /// </summary>
    public static bool IsEvaluated => evaluated;

    /// <summary>
    /// Establishes whether this backend can import decoder surfaces, so decoders know whether
    /// producing them is worth it. Cheap to call on every draw; it only looks once per context.
    /// </summary>
    public static void Evaluate(ISkiaSharpApiLease lease)
    {
        if (evaluated && ReferenceEquals(evaluatedFor, lease.GrContext))
            return;

        evaluatedFor = lease.GrContext;
        importer = null;
        VideoFrameCapabilities.ClearSupportedSurfaceKind();

        // A lease without a context says nothing about the backend, so leave the question open
        // rather than answering it with the absence of one.
        if (lease.GrContext is null)
            return;

        evaluated = true;

        using ISkiaSharpPlatformGraphicsApiLease? api = lease.TryLeasePlatformGraphicsApi();
        if (api?.Context is not { } context)
        {
            Debug.WriteLine("Decoder surfaces need a platform graphics context.");
            return;
        }

        foreach (IVideoSurfaceImporter candidate in VideoSurfaceImporterFactory.Importers)
        {
            if (!candidate.IsSupportedBy(context))
                continue;

            importer = candidate;
            Debug.WriteLine(
                $"Decoder surfaces can be imported here as {candidate.Kind}; " +
                "hardware frames will skip the readback.");
            VideoFrameCapabilities.SetSupportedSurfaceKind(candidate.Kind, candidate.Device);
            return;
        }
    }

    /// <summary>
    /// Wraps a frame's surface as plane images for <see cref="Controls.VideoFrameShader"/>, or
    /// returns null when it cannot be imported, which the caller should treat as this backend not
    /// supporting surfaces at all.
    /// </summary>
    public static VideoSurfacePlanes? TryImport(ISkiaSharpApiLease lease, VideoFrame frame)
    {
        if (lease.GrContext is not { } grContext || importer is not { } active || frame.Surface.Kind != active.Kind)
            return null;

        IVideoSurfaceTextures? imported;
        IPlatformGraphicsContext context;
        using (ISkiaSharpPlatformGraphicsApiLease? api = lease.TryLeasePlatformGraphicsApi())
        {
            if (api?.Context is not { } graphics)
                return null;

            context = graphics;
            UseContext(context);

            // Before the lookup rather than inside the import: the texture is a view of the
            // decoder's memory, so a surface already imported is one whose next decode nothing else
            // would ever wait for.
            active.WaitForDecode(frame);
            imported = active.CacheSurfaces ? Acquire(active, context, frame) : active.Import(context, frame);
        }

        if (imported is null)
            return null;

        // The textures were filled behind Skia's back, so its cached idea of the GL state is stale.
        if (context is EglContext)
            grContext.ResetContext();
        return new VideoSurfacePlanes(grContext, frame, imported, active.CacheSurfaces ? null : context);
    }

    /// <summary>
    /// Releases the imports of surfaces whose pools have been retired, since each one keeps the
    /// decoder's memory alive for as long as it exists. Cheap to call on every draw.
    /// </summary>
    public static void ReleaseRetired(ISkiaSharpApiLease lease)
    {
        if (!VideoFrameCapabilities.HasRetiredSurfacePools)
            return;

        if (cache.Count == 0)
        {
            while (VideoFrameCapabilities.TryTakeRetiredSurfacePool(out _))
            {
            }
            return;
        }

        // Left queued until a draw has the context the imports were made in.
        if (lease.GrContext is null)
            return;

        using ISkiaSharpPlatformGraphicsApiLease? api = lease.TryLeasePlatformGraphicsApi();
        if (api?.Context is not { } context)
            return;

        UseContext(context);
        var retired = new HashSet<long>();
        while (VideoFrameCapabilities.TryTakeRetiredSurfacePool(out long pool))
            retired.Add(pool);

        foreach ((VideoSurfaceHandle key, CachedSurface cached) in cache)
        {
            if (!retired.Contains(key.Pool))
                continue;

            cache.Remove(key);
            cached.Destroy(context);
        }
    }

    /// <summary>Moves the cache to the context drawing now, abandoning what an earlier one made.</summary>
    private static void UseContext(IPlatformGraphicsContext context)
    {
        if (ReferenceEquals(cachedFor, context))
            return;

        // The textures went with the old context, but the images belong to the display, which
        // releases them itself only when it is terminated.
        if (cachedFor is EglContext previous && context is EglContext egl &&
            ReferenceEquals(previous.Display, egl.Display))
        {
            foreach (CachedSurface cached in cache.Values)
                cached.Imported.Abandon(egl);
        }

        cache.Clear();
        cachedFor = context;
    }

    private static IVideoSurfaceTextures? Acquire(IVideoSurfaceImporter active, IPlatformGraphicsContext context, VideoFrame frame)
    {
        VideoSurfaceHandle key = frame.Surface;
        long now = ++clock;

        if (cache.TryGetValue(key, out CachedSurface? cached) && cached.Matches(frame))
        {
            // The same surface holding new pixels needs no new import: the texture is a view of the
            // decoder's memory, so it already shows them.
            cached.LastUsed = now;
            return cached.Imported;
        }

        cached?.Destroy(context);
        cache.Remove(key);

        IVideoSurfaceTextures? imported = active.Import(context, frame);
        if (imported is null)
            return null;

        cache[key] = new CachedSurface(frame.Width, frame.Height, frame.Format, imported) { LastUsed = now };
        Evict(context);
        return imported;
    }

    private static void Evict(IPlatformGraphicsContext context)
    {
        while (cache.Count > MaxCachedSurfaces)
        {
            VideoSurfaceHandle oldestKey = default;
            long oldest = long.MaxValue;
            foreach (KeyValuePair<VideoSurfaceHandle, CachedSurface> entry in cache)
            {
                if (entry.Value.LastUsed < oldest)
                {
                    oldest = entry.Value.LastUsed;
                    oldestKey = entry.Key;
                }
            }

            if (!cache.Remove(oldestKey, out CachedSurface? evicted))
                return;

            evicted.Destroy(context);
        }
    }

    private sealed class CachedSurface(
        int width,
        int height,
        VideoPixelFormat format,
        IVideoSurfaceTextures imported)
    {
        public IVideoSurfaceTextures Imported { get; } = imported;

        public long LastUsed { get; set; }

        /// <summary>
        /// Whether the import still describes the frame. A surface identifier reused for different
        /// geometry after a resolution change would otherwise be sampled with the old one.
        /// </summary>
        public bool Matches(VideoFrame frame) =>
            frame.Width == width && frame.Height == height && frame.Format == format;

        public void Destroy(IPlatformGraphicsContext context) => Imported.Destroy(context);
    }
}

/// <summary>How one plane of a surface is described to EGL, GL and Skia.</summary>
/// <param name="DrmFormat">The DRM format a VAAPI export is expected to name the plane with.</param>
/// <param name="GlFormat">Sized GL internal format of the resulting texture.</param>
/// <param name="ColorType">How Skia is to read the texture.</param>
/// <param name="Width">Texels across, which is samples across for single-component planes.</param>
/// <param name="Height">Rows, which is half the frame's for subsampled chroma.</param>
public readonly record struct VideoPlaneTexture(
    uint DrmFormat,
    int GlFormat,
    SKColorType ColorType,
    int Width,
    int Height)
{
    private static readonly uint DrmR8 = Fourcc('R', '8', ' ', ' ');
    private static readonly uint DrmGr88 = Fourcc('G', 'R', '8', '8');
    private static readonly uint DrmR16 = Fourcc('R', '1', '6', ' ');
    private static readonly uint DrmGr1616 = Fourcc('G', 'R', '3', '2');

    // Bytes B,G,R,A in memory, which is what AV_PIX_FMT_BGRA means and what a device scaler asked
    // for RGB hands back. A surface with no alpha channel is the same bytes with the top eight
    // ignored, so both names describe a texture that samples the same.
    private static readonly uint DrmArgb8888 = Fourcc('A', 'R', '2', '4');
    private static readonly uint DrmXrgb8888 = Fourcc('X', 'R', '2', '4');

    private const int GlR8 = 0x8229;
    private const int GlRg8 = 0x822B;
    private const int GlR16 = 0x822A;
    private const int GlRg16 = 0x822C;
    private const int GlRgba8 = 0x8058;

    public static VideoPlaneTexture For(VideoFrame frame, int plane)
    {
        VideoPlanarLayout layout = frame.Layout;

        // EGL knows what the DRM name means, so the texture samples as red, green, blue and alpha
        // whatever order the bytes are in.
        if (!VideoPlanarLayout.IsPlanar(frame.Format))
            return new(DrmArgb8888, GlRgba8, SKColorType.Rgba8888, frame.Width, frame.Height);

        bool interleaved = plane > 0 && layout.InterleavedChroma;
        int components = interleaved ? 2 : 1;
        int width = frame.GetRowBytes(plane) / (layout.BytesPerSample * components);

        return (interleaved, layout.BytesPerSample == 2) switch
        {
            (false, false) => new(DrmR8, GlR8, SKColorType.Alpha8, width, frame.GetPlaneHeight(plane)),
            (true, false) => new(DrmGr88, GlRg8, SKColorType.Rg88, width, frame.GetPlaneHeight(plane)),
            (false, true) => new(DrmR16, GlR16, SKColorType.Alpha16, width, frame.GetPlaneHeight(plane)),
            (true, true) => new(DrmGr1616, GlRg16, SKColorType.Rg1616, width, frame.GetPlaneHeight(plane)),
        };
    }

    /// <summary>Whether a plane an export named this way is the one this describes.</summary>
    public bool Accepts(uint drmFormat) =>
        drmFormat == DrmFormat ||
        (DrmFormat == DrmArgb8888 && drmFormat == DrmXrgb8888);

    private static uint Fourcc(char a, char b, char c, char d) =>
        (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);
}
