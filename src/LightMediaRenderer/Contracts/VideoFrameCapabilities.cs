using System;
using System.Collections.Concurrent;
using System.Threading;

namespace LightMediaRenderer.Contracts;

/// <summary>
/// What the renderer has been found able to display. A decoder hands frames over in whatever layout
/// it decoded them in, which is only worth doing if the graphics backend can sample that layout;
/// where it cannot, the decoder has to convert instead.
/// </summary>
/// <remarks>
/// This is process-wide because it describes the graphics device, which every decoder in the
/// process shares. The renderer establishes it by trying the layout rather than by naming
/// platforms, so a backend missing a texture format is found rather than assumed.
/// </remarks>
public static class VideoFrameCapabilities
{
    private static int maxPlanarBytesPerSample = 2;
    private static int surfaceKind = (int)VideoSurfaceKind.None;
    private static IntPtr surfaceDevice;
    private static int externalTextureGeneration;
    private static readonly ConcurrentQueue<long> retiredSurfacePools = new();

    /// <summary>
    /// Raised on whichever thread retired a surface pool, so a renderer that is not drawing knows it
    /// has imports to release.
    /// </summary>
    public static event Action? SurfacePoolRetired;

    /// <summary>Whether surface pools have been retired that the renderer has not taken yet.</summary>
    public static bool HasRetiredSurfacePools => !retiredSurfacePools.IsEmpty;

    /// <summary>
    /// Widest sample container the renderer can sample planes of. 0 means planar output is unusable
    /// and everything has to be converted before it is handed over.
    /// </summary>
    public static int MaxPlanarBytesPerSample => Volatile.Read(ref maxPlanarBytesPerSample);

    /// <summary>Whether a layout with this container width is worth handing to the renderer.</summary>
    public static bool SupportsPlanarSamples(int bytesPerSample) =>
        bytesPerSample <= Volatile.Read(ref maxPlanarBytesPerSample);

    /// <summary>
    /// Surface kind the renderer has been found able to import, or <see cref="VideoSurfaceKind.None"/>
    /// while nothing has established one. Decoders producing anything else must hand over pixels.
    /// </summary>
    public static VideoSurfaceKind SupportedSurfaceKind => (VideoSurfaceKind)Volatile.Read(ref surfaceKind);

    /// <summary>
    /// The graphics device the renderer imports surfaces into, where it only accepts surfaces that
    /// device produced itself, or zero where the producer is free to pick its own. Reading this is
    /// only meaningful together with <see cref="SupportedSurfaceKind"/>.
    /// </summary>
    public static IntPtr SupportedSurfaceDevice => Volatile.Read(ref surfaceDevice);

    /// <summary>Generation of the external-texture namespace the renderer currently samples.</summary>
    public static int ExternalTextureGeneration => Volatile.Read(ref externalTextureGeneration);

    /// <summary>Changes the external-texture namespace after moving to another graphics context.</summary>
    public static void SetExternalTextureGeneration(int generation) =>
        Volatile.Write(ref externalTextureGeneration, generation);

    /// <summary>Whether an external texture still belongs to the renderer's active context.</summary>
    public static bool IsExternalTextureCurrent(VideoFrame frame) =>
        frame.Format != VideoPixelFormat.ExternalTexture ||
        frame.ExternalHandleGeneration == ExternalTextureGeneration;

    /// <summary>Whether handing this kind of surface to the renderer is worth attempting.</summary>
    public static bool SupportsSurfaces(VideoSurfaceKind kind) =>
        kind != VideoSurfaceKind.None && SupportedSurfaceKind == kind;

    /// <summary>
    /// Records that the renderer's graphics backend can import this kind of surface.
    /// </summary>
    /// <param name="device">
    /// The device surfaces must have been produced by, where the backend can only import its own,
    /// or zero where anything of the right kind will do.
    /// </param>
    public static void SetSupportedSurfaceKind(VideoSurfaceKind kind, IntPtr device = default)
    {
        // The device is written first so that nothing sees a kind it cannot yet act on.
        Volatile.Write(ref surfaceDevice, device);
        Volatile.Write(ref surfaceKind, (int)kind);
    }

    /// <summary>Clears capabilities learned from a graphics context that is no longer active.</summary>
    public static void ClearSupportedSurfaceKind()
    {
        Volatile.Write(ref surfaceKind, (int)VideoSurfaceKind.None);
        Volatile.Write(ref surfaceDevice, IntPtr.Zero);
    }

    /// <summary>
    /// Records that no frame refers to the surfaces of the pool <see cref="VideoSurfaceHandle.Pool"/>
    /// names anymore, so whatever the renderer imported from them can be released.
    /// </summary>
    public static void RetireSurfacePool(long pool)
    {
        retiredSurfacePools.Enqueue(pool);
        SurfacePoolRetired?.Invoke();
    }

    /// <summary>Takes one surface pool retired since the renderer last looked.</summary>
    public static bool TryTakeRetiredSurfacePool(out long pool) => retiredSurfacePools.TryDequeue(out pool);

    /// <summary>
    /// Records that the renderer could not display planes of this container width. Only ever
    /// narrows what is attempted, so a backend cannot be talked back into a layout it has failed.
    /// </summary>
    public static void RejectPlanarSamples(int bytesPerSample)
    {
        int limit = Math.Max(bytesPerSample - 1, 0);
        int current;
        do
        {
            current = Volatile.Read(ref maxPlanarBytesPerSample);
            if (current <= limit)
                return;
        }
        while (Interlocked.CompareExchange(ref maxPlanarBytesPerSample, limit, current) != current);
    }
}
