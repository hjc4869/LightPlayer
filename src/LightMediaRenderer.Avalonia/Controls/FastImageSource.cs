using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Controls;

/// <summary>
/// Immutable artwork pixels drawn through Skia by <see cref="FastImage"/>. Each instance is one
/// reference to the shared pixels: <see cref="Clone"/> adds a reference and <see cref="Dispose"/>
/// releases it.
/// </summary>
public sealed class FastImageSource : IImage, IDisposable
{
    private FastImageContent? content;

    /// <summary>Takes ownership of a raster Skia image.</summary>
    public FastImageSource(SKImage image)
        : this(new SkiaFastImageContent(image ?? throw new ArgumentNullException(nameof(image))))
    {
    }

    internal FastImageSource(FastImageContent content)
    {
        this.content = content;
        PixelSize = content.PixelSize;
    }

    public PixelSize PixelSize { get; }

    public Size Size => new(PixelSize.Width, PixelSize.Height);

    internal FastImageContent? Content => content;

    public FastImageSource Clone() =>
        new(content?.TryAddRef() ?? throw new ObjectDisposedException(nameof(FastImageSource)));

    public void Dispose() => Interlocked.Exchange(ref content, null)?.Release();

    void IImage.Draw(DrawingContext context, Rect sourceRect, Rect destRect)
    {
        if (content?.TryAddRef() is { } reference)
            context.Custom(new FastImageDrawOperation(reference, sourceRect, destRect, null));
    }
}

/// <summary>Pixels shared by <see cref="FastImageSource"/> handles and in-flight draw operations.</summary>
internal abstract class FastImageContent(PixelSize pixelSize, long sizeInBytes)
{
    private int references = 1;
    private volatile bool isLost;

    public PixelSize PixelSize { get; } = pixelSize;

    public long SizeInBytes { get; } = sizeInBytes;

    /// <summary>
    /// The pixels existed only in a texture of a graphics context that has since been replaced, so
    /// the artwork has to be loaded again.
    /// </summary>
    public bool IsLost
    {
        get => isLost;
        protected set => isLost = value;
    }

    public FastImageContent? TryAddRef()
    {
        int current = Volatile.Read(ref references);
        while (current > 0)
        {
            int observed = Interlocked.CompareExchange(ref references, current + 1, current);
            if (observed == current)
                return this;
            current = observed;
        }

        return null;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref references) == 0)
            Free();
    }

    /// <summary>Draws on the render thread. Returns false when nothing could be drawn.</summary>
    public abstract bool Draw(ISkiaSharpApiLease lease, SKRect source, SKRect destination, SKPaint paint);

    protected abstract void Free();
}

/// <summary>
/// Decoded pixels in native memory. A GPU renderer uploads them on first draw into a texture owned
/// by this content, and the raster copy is then released.
/// </summary>
internal sealed class SkiaFastImageContent(SKImage raster) : FastImageContent(
    new PixelSize(raster.Width, raster.Height), (long)raster.Width * raster.Height * raster.Info.BytesPerPixel)
{
    private SKImage? raster = raster;
    private SKImage? texture;
    private GRContext? textureContext;

    public override bool Draw(ISkiaSharpApiLease lease, SKRect source, SKRect destination, SKPaint paint)
    {
        // A raster canvas can only draw the raster copy.
        SKImage? image = raster;
        if (lease.GrContext is { } context)
        {
            if (texture is not null && !ReferenceEquals(textureContext, context))
            {
                FastImageGpu.Dispose(texture, textureContext);
                texture = null;
                textureContext = null;
                IsLost = raster is null;
            }

            if (texture is null && raster is { } pixels &&
                pixels.ToTextureImage(context, mipmapped: false, budgeted: true) is { } uploaded &&
                !ReferenceEquals(uploaded, pixels))
            {
                texture = uploaded;
                textureContext = context;
                pixels.Dispose();
                raster = null;
            }

            image = texture ?? raster;
        }

        if (image is null)
        {
            IsLost = true;
            return false;
        }

        lease.SkCanvas.DrawImage(image, source, destination, FastImageGpu.Sampling, paint);
        return true;
    }

    protected override void Free()
    {
        raster?.Dispose();
        raster = null;
        if (texture is { } gpu)
        {
            texture = null;
            FastImageGpu.Dispose(gpu, textureContext);
        }
    }
}

/// <summary>
/// Render-thread bookkeeping for FastImage textures, which are disposed with their owning graphics
/// context current.
/// </summary>
internal static class FastImageGpu
{
    public static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private static readonly ConcurrentQueue<(SKImage Image, GRContext Context)> pending = new();
    private static readonly ConditionalWeakTable<Compositor, CompositionCustomVisual> disposers = new();
    private static readonly ConditionalWeakTable<GRContext, IPlatformGraphicsContext?> platforms = new();
    private static CompositionCustomVisual? disposer;
    private static int renderThread = -1;

    public static bool IsRenderThread => Environment.CurrentManagedThreadId == Volatile.Read(ref renderThread);

    /// <summary>Called by draw operations on entry so later releases can tell the render thread apart.</summary>
    public static void EnterRender(ISkiaSharpApiLease lease)
    {
        Volatile.Write(ref renderThread, Environment.CurrentManagedThreadId);
        if (lease.GrContext is { } context && !platforms.TryGetValue(context, out _))
        {
            // Leasing the platform API flushes the context, so it happens once per context.
            using ISkiaSharpPlatformGraphicsApiLease? platform = lease.TryLeasePlatformGraphicsApi();
            platforms.AddOrUpdate(context, platform?.Context);
        }

        Drain();
    }

    /// <summary>Registers the compositor that releases textures freed outside the render thread.</summary>
    public static void UseCompositor(Compositor? compositor)
    {
        if (compositor is null || disposer is not null && ReferenceEquals(disposer.Compositor, compositor))
            return;
        disposer = disposers.GetValue(compositor, static owner => owner.CreateCustomVisual(new Disposer()));
    }

    public static void Dispose(SKImage image, GRContext? context)
    {
        if (context is null || context.IsAbandoned)
        {
            image.Dispose();
            return;
        }

        pending.Enqueue((image, context));
        if (IsRenderThread)
        {
            Drain();
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
            disposer?.SendHandlerMessage(Disposer.Drain);
        else
            Dispatcher.UIThread.Post(static () => disposer?.SendHandlerMessage(Disposer.Drain));
    }

    private static void Drain()
    {
        while (pending.TryDequeue(out var entry))
        {
            if (entry.Context.IsAbandoned || !platforms.TryGetValue(entry.Context, out IPlatformGraphicsContext? platform) ||
                platform is null)
            {
                entry.Image.Dispose();
                continue;
            }

            IDisposable? current = null;
            try
            {
                current = platform.EnsureCurrent();
            }
            catch (PlatformGraphicsContextLostException)
            {
                entry.Context.AbandonContext();
            }

            using (current)
                entry.Image.Dispose();
        }
    }

    private sealed class Disposer : CompositionCustomVisualHandler
    {
        public static readonly object Drain = new();

        public override void OnMessage(object message)
        {
            Volatile.Write(ref renderThread, Environment.CurrentManagedThreadId);
            FastImageGpu.Drain();
        }

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
        }
    }
}
