#if MACOS || IOS
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Metal;
using Avalonia.Platform;
using LightMediaRenderer.Avalonia.Rendering;
using LightMediaRenderer.Contracts;
using LightStudio.Logging;
using SkiaSharp;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>Imports IOSurface-backed CoreVideo planes into the renderer's Metal device.</summary>
internal sealed class VideoToolboxSurfaceImporter : IVideoSurfaceImporter
{
    private const uint CvNv12VideoRange = 0x34323076;
    private const uint CvNv12FullRange = 0x34323066;
    private const uint CvP010VideoRange = 0x78343230;
    private const uint CvP010FullRange = 0x78663230;
    private const uint CvYuv420VideoRange = 0x79343230;
    private const uint CvYuv420FullRange = 0x66343230;
    private const uint CvBgra = 0x42475241;

    private const nuint MetalR8Unorm = 10;
    private const nuint MetalR16Unorm = 20;
    private const nuint MetalRg8Unorm = 30;
    private const nuint MetalRg16Unorm = 60;
    private const nuint MetalBgra8Unorm = 80;

    private readonly ConditionalWeakTable<IPlatformGraphicsContext, TextureCache> caches = new();

    public static void Register() => VideoSurfaceImporterFactory.Register(new VideoToolboxSurfaceImporter());

    public VideoSurfaceKind Kind => VideoSurfaceKind.VideoToolbox;
    public IntPtr Device => IntPtr.Zero;
    public bool CacheSurfaces => false;

    public bool IsSupportedBy(IPlatformGraphicsContext context) =>
        context is IMetalDevice { Device: not 0 } && !context.IsLost &&
        caches.GetValue(context, static owner => new TextureCache((IMetalDevice)owner)).Handle != IntPtr.Zero;

    /// <summary>FFmpeg completes VideoToolbox decode and pixel transfer before delivering a frame.</summary>
    public void WaitForDecode(VideoFrame frame)
    {
    }

    public IVideoSurfaceTextures? Import(IPlatformGraphicsContext context, VideoFrame frame)
    {
        if (context is not IMetalDevice || context.IsLost || frame.Surface.Kind != Kind ||
            !frame.Surface.IsValid || !caches.TryGetValue(context, out TextureCache? cache) ||
            cache.Handle == IntPtr.Zero)
            return null;

        IntPtr buffer = (IntPtr)frame.Surface.Id;
        uint format = Native.CVPixelBufferGetPixelFormatType(buffer);
        nuint planeCount = Native.CVPixelBufferGetPlaneCount(buffer);
        bool planar = VideoPlanarLayout.IsPlanar(frame.Format);
        if (!Matches(format, frame.Format) || Native.CVPixelBufferGetIOSurface(buffer) == IntPtr.Zero ||
            (planar ? planeCount != (nuint)frame.PlaneCount : planeCount != 0))
        {
            AppLog.Warning("Video.Surface", $"Cannot import CoreVideo format 0x{format:X8} as {frame.Format} with {planeCount} planes.");
            return null;
        }

        cache.FlushExpired();
        var imported = new ImportedSurface(frame);
        bool succeeded = false;
        try
        {
            for (int plane = 0; plane < frame.PlaneCount; plane++)
            {
                nuint width = planar ? Native.CVPixelBufferGetWidthOfPlane(buffer, (nuint)plane)
                    : Native.CVPixelBufferGetWidth(buffer);
                nuint height = planar ? Native.CVPixelBufferGetHeightOfPlane(buffer, (nuint)plane)
                    : Native.CVPixelBufferGetHeight(buffer);
                VideoPlaneTexture description = VideoPlaneTexture.For(frame, plane);
                if (width < (nuint)description.Width || height < (nuint)description.Height ||
                    width > int.MaxValue || height > int.MaxValue)
                {
                    AppLog.Warning("Video.Surface", $"CoreVideo plane {plane} has invalid dimensions {width}x{height} for {description.Width}x{description.Height}.");
                    return null;
                }

                nuint metalFormat = !planar ? MetalBgra8Unorm : description.ColorType switch
                {
                    SKColorType.Alpha8 => MetalR8Unorm,
                    SKColorType.Alpha16 => MetalR16Unorm,
                    SKColorType.Rg88 => MetalRg8Unorm,
                    SKColorType.Rg1616 => MetalRg16Unorm,
                    _ => 0,
                };
                if (metalFormat == 0)
                    return null;

                int result = Native.CVMetalTextureCacheCreateTextureFromImage(IntPtr.Zero, cache.Handle,
                    buffer, IntPtr.Zero, metalFormat, width, height, (nuint)plane, out IntPtr texture);
                imported.Textures[plane] = texture;
                if (result != 0 || texture == IntPtr.Zero || Native.CVMetalTextureGetTexture(texture) == IntPtr.Zero)
                {
                    AppLog.Warning("Video.Surface", $"Could not import CoreVideo plane {plane} ({width}x{height}, format 0x{format:X8}) into Metal: CVReturn {result}.");
                    return null;
                }

                imported.Sizes[plane] = ((int)width, (int)height);
            }

            succeeded = true;
            return imported;
        }
        finally
        {
            if (!succeeded)
                imported.Release();
            GC.KeepAlive(cache);
        }
    }

    private static bool Matches(uint format, VideoPixelFormat layout) => layout switch
    {
        VideoPixelFormat.Nv12 => format is CvNv12VideoRange or CvNv12FullRange,
        VideoPixelFormat.P010 => format is CvP010VideoRange or CvP010FullRange,
        VideoPixelFormat.Yuv420p => format is CvYuv420VideoRange or CvYuv420FullRange,
        VideoPixelFormat.Bgra32 => format == CvBgra,
        _ => false,
    };

    private sealed class TextureCache
    {
        private long lastFlush = Environment.TickCount64;

        public TextureCache(IMetalDevice device)
        {
            int result = Native.CVMetalTextureCacheCreate(IntPtr.Zero, IntPtr.Zero,
                device.Device, IntPtr.Zero, out IntPtr cache);
            if (result != 0)
            {
                if (cache != IntPtr.Zero)
                    Native.CFRelease(cache);
                AppLog.Warning("Video.Surface", $"Could not create a CoreVideo Metal texture cache: CVReturn {result}.");
                return;
            }
            Handle = cache;
        }

        public IntPtr Handle { get; }

        public void FlushExpired()
        {
            long now = Environment.TickCount64;
            if (now - lastFlush < 1000)
                return;
            Native.CVMetalTextureCacheFlush(Handle, 0);
            lastFlush = now;
        }

        ~TextureCache()
        {
            if (Handle != IntPtr.Zero)
                Native.CFRelease(Handle);
        }
    }

    private sealed unsafe class ImportedSurface(VideoFrame frame) : IVideoSurfaceTextures
    {
        private VideoFrame? owner = frame.AddRef();

        public IntPtr[] Textures { get; } = new IntPtr[frame.PlaneCount];
        public (int Width, int Height)[] Sizes { get; } = new (int, int)[frame.PlaneCount];

        // Reads are fenced in Destroy, on the queue Skia submits to.
        public bool RequiresReadFence => false;

        public (int Width, int Height) PlaneSize(int index) => Sizes[index];

        public SKImage? CreateImage(GRContext context, VideoFrame frame, int index, SKImageInfo info)
        {
            if (owner is null || (uint)index >= (uint)Textures.Length || context.Backend != GRBackend.Metal)
                return null;
            IntPtr texture = Textures[index];
            VideoPlaneTexture description = VideoPlaneTexture.For(frame, index);
            (int width, int height) = Sizes[index];
            if (texture == IntPtr.Zero || info.ColorType != description.ColorType ||
                width < info.Width || height < info.Height)
                return null;

            using var backend = new GRBackendTexture(width, height, false,
                new GRMtlTextureInfo(Native.CVMetalTextureGetTexture(texture)));
            if (!backend.IsValid)
                return null;

            var reference = new ImageReference(texture, owner);
            try
            {
                return SKImage.FromTexture(context, backend, GRSurfaceOrigin.TopLeft,
                    frame.Format == VideoPixelFormat.Bgra32 ? SKColorType.Bgra8888 : info.ColorType,
                    info.AlphaType, info.ColorSpace, static retained => ((ImageReference)retained).Dispose(), reference);
            }
            catch
            {
                reference.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Keeps the textures and the frame until the GPU has finished the draws already submitted.
        /// Skia lets go of a wrapped texture once its image is disposed, and the pixel buffer then
        /// returns to VideoToolbox, which would decode a later picture into it before those draws sample it.
        /// </summary>
        public void Destroy(IPlatformGraphicsContext context)
        {
            if (context is not IMetalDevice { CommandQueue: not 0 } device || context.IsLost ||
                !ReleaseAfterSubmittedWork(device.CommandQueue))
                Release();
        }

        public void Abandon(IPlatformGraphicsContext context) => Release();

        public void Release()
        {
            for (int plane = 0; plane < Textures.Length; plane++)
            {
                if (Textures[plane] == IntPtr.Zero)
                    continue;
                Native.CFRelease(Textures[plane]);
                Textures[plane] = IntPtr.Zero;
            }
            Interlocked.Exchange(ref owner, null)?.Release();
        }

        /// <summary>
        /// Commits an empty command buffer after Skia's, which completes only once every command
        /// buffer committed before it on the queue has.
        /// </summary>
        private bool ReleaseAfterSubmittedWork(IntPtr queue)
        {
            IntPtr pool = ObjC.PushAutoreleasePool();
            try
            {
                IntPtr marker = ObjC.Send(queue, ObjC.Sel("commandBuffer"));
                if (marker == IntPtr.Zero)
                    return false;

                GCHandle handle = GCHandle.Alloc(this);
                IntPtr block = ObjC.CreateContextBlock(
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnSubmittedWorkCompleted,
                    GCHandle.ToIntPtr(handle));
                try
                {
                    ObjC.Send(marker, ObjC.Sel("addCompletedHandler:"), block);
                }
                finally
                {
                    ObjC.FreeContextBlock(block);
                }

                ObjC.Send(marker, ObjC.Sel("commit"));
                return true;
            }
            finally
            {
                ObjC.PopAutoreleasePool(pool);
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnSubmittedWorkCompleted(IntPtr block, IntPtr commandBuffer)
        {
            GCHandle handle = GCHandle.FromIntPtr(ObjC.GetBlockContext(block));
            var surface = (ImportedSurface)handle.Target!;
            handle.Free();
            try
            {
                surface.Release();
            }
            catch (Exception exception)
            {
                AppLog.Error("Video.Surface", "Could not release a CoreVideo surface after its GPU reads.", exception);
            }
        }
    }

    private sealed class ImageReference : IDisposable
    {
        private IntPtr texture;
        private VideoFrame? frame;

        public ImageReference(IntPtr texture, VideoFrame frame)
        {
            this.texture = Native.CFRetain(texture);
            this.frame = frame.AddRef();
        }

        public void Dispose()
        {
            IntPtr retained = Interlocked.Exchange(ref texture, IntPtr.Zero);
            if (retained != IntPtr.Zero)
                Native.CFRelease(retained);
            Interlocked.Exchange(ref frame, null)?.Release();
        }
    }

    private static class Native
    {
        private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(CoreVideo)]
        public static extern int CVMetalTextureCacheCreate(IntPtr allocator, IntPtr cacheAttributes,
            IntPtr device, IntPtr textureAttributes, out IntPtr cache);

        [DllImport(CoreVideo)]
        public static extern int CVMetalTextureCacheCreateTextureFromImage(IntPtr allocator, IntPtr cache,
            IntPtr buffer, IntPtr attributes, nuint pixelFormat, nuint width, nuint height, nuint plane, out IntPtr texture);

        [DllImport(CoreVideo)]
        public static extern void CVMetalTextureCacheFlush(IntPtr cache, ulong options);

        [DllImport(CoreVideo)]
        public static extern IntPtr CVMetalTextureGetTexture(IntPtr texture);

        [DllImport(CoreVideo)]
        public static extern uint CVPixelBufferGetPixelFormatType(IntPtr buffer);

        [DllImport(CoreVideo)]
        public static extern IntPtr CVPixelBufferGetIOSurface(IntPtr buffer);

        [DllImport(CoreVideo)]
        public static extern nuint CVPixelBufferGetPlaneCount(IntPtr buffer);

        [DllImport(CoreVideo)]
        public static extern nuint CVPixelBufferGetWidth(IntPtr buffer);

        [DllImport(CoreVideo)]
        public static extern nuint CVPixelBufferGetHeight(IntPtr buffer);

        [DllImport(CoreVideo)]
        public static extern nuint CVPixelBufferGetWidthOfPlane(IntPtr buffer, nuint plane);

        [DllImport(CoreVideo)]
        public static extern nuint CVPixelBufferGetHeightOfPlane(IntPtr buffer, nuint plane);

        [DllImport(CoreFoundation)]
        public static extern IntPtr CFRetain(IntPtr value);

        [DllImport(CoreFoundation)]
        public static extern void CFRelease(IntPtr value);
    }
}
#endif