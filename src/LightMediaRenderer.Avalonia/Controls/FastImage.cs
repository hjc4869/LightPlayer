using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using LightMediaRenderer.Avalonia.Controls;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Controls;

/// <summary>
/// Draws an image uniformly filling its bounds, cropped around the center. The parent supplies the
/// size: the control has no desired size of its own and a new source only invalidates rendering.
/// <see cref="FastImageSource"/> artwork is drawn directly through Skia; other images use the
/// standard image drawing path.
/// </summary>
public sealed class FastImage : Control
{
    private FastImageContent? reportedLostContent;

    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<FastImage, IImage?>(nameof(Source));

    public static readonly AttachedProperty<FastImageCache?> CachePoolProperty =
        AvaloniaProperty.RegisterAttached<FastImage, Control, FastImageCache?>(nameof(CachePool), inherits: true);

    /// <summary>
    /// Raised when the current <see cref="FastImageSource"/> can no longer be drawn because its
    /// pixels went with a replaced graphics context. The owner of the artwork loads it again.
    /// </summary>
    public static readonly RoutedEvent<RoutedEventArgs> SourceLostEvent =
        RoutedEvent.Register<FastImage, RoutedEventArgs>(nameof(SourceLost), RoutingStrategies.Bubble);

    static FastImage()
    {
        AffectsRender<FastImage>(SourceProperty);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public FastImageCache? CachePool
    {
        get => GetValue(CachePoolProperty);
        set => SetValue(CachePoolProperty, value);
    }

    public static FastImageCache? GetCachePool(Control control) => control.GetValue(CachePoolProperty);

    public static void SetCachePool(Control control, FastImageCache? value) => control.SetValue(CachePoolProperty, value);

    public event EventHandler<RoutedEventArgs>? SourceLost
    {
        add => AddHandler(SourceLostEvent, value);
        remove => RemoveHandler(SourceLostEvent, value);
    }

    internal void OnContentLost(FastImageContent content)
    {
        if (Source is FastImageSource { Content: { } current } && ReferenceEquals(current, content) &&
            !ReferenceEquals(reportedLostContent, content))
        {
            reportedLostContent = content;
            RaiseEvent(new RoutedEventArgs(SourceLostEvent));
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
            reportedLostContent = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FastImageGpu.UseCompositor(ElementComposition.GetElementVisual(this)?.Compositor);
    }

    public override void Render(DrawingContext context)
    {
        if (Source is not { } source || source.Size is not { Width: > 0, Height: > 0 } size ||
            Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var destination = new Rect(Bounds.Size);
        double scale = Math.Max(destination.Width / size.Width, destination.Height / size.Height);
        var visible = new Size(destination.Width / scale, destination.Height / scale);
        var sourceRect = new Rect(
            new Point((size.Width - visible.Width) / 2, (size.Height - visible.Height) / 2), visible);

        if (source is FastImageSource { Content: { } content } && content.TryAddRef() is { } reference)
            context.Custom(new FastImageDrawOperation(reference, sourceRect, destination, new WeakReference<FastImage>(this)));
        else
            context.DrawImage(source, sourceRect, destination);
    }
}

/// <summary>Draws <see cref="FastImageContent"/> through the Skia lease and owns one reference to it.</summary>
internal sealed class FastImageDrawOperation(
    FastImageContent content,
    Rect source,
    Rect destination,
    WeakReference<FastImage>? owner) : ICustomDrawOperation
{
    [ThreadStatic]
    private static SKPaint? paint;
    private FastImageContent? content = content;

    public Rect Bounds => destination;

    public bool HitTest(Point p) => destination.Contains(p);

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Render(ImmediateDrawingContext context)
    {
        if (content is not { } current ||
            context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
        {
            return;
        }

        try
        {
            using ISkiaSharpApiLease lease = feature.Lease();
            FastImageGpu.EnterRender(lease);
            SKPaint drawing = paint ??= new SKPaint();
            drawing.Color = new SKColor(255, 255, 255, (byte)(255 * Math.Clamp(lease.CurrentOpacity, 0, 1)));
            if (!current.Draw(lease, source.ToSKRect(), destination.ToSKRect(), drawing) &&
                current.IsLost && owner?.TryGetTarget(out FastImage? image) == true)
            {
                Dispatcher.UIThread.Post(() => image.OnContentLost(current), DispatcherPriority.Background);
            }
        }
        catch (Exception exception)
        {
            DrawOperationFailures.Report(nameof(FastImageDrawOperation), exception);
        }
    }

    public void Dispose()
    {
        content?.Release();
        content = null;
    }
}
