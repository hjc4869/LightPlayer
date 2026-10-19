using LightMediaRenderer.Avalonia.Controls;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Controls;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Animations;

public sealed class ConnectedArtwork
{
    public static readonly AttachedProperty<object?> KeyProperty =
        AvaloniaProperty.RegisterAttached<ConnectedArtwork, Control, object?>("Key");

    public static object? GetKey(Control control) => control.GetValue(KeyProperty);
    public static void SetKey(Control control, object? value) => control.SetValue(KeyProperty, value);
}

internal sealed class ArtworkPageTransition : ConnectedPageTransition
{
    private readonly PageViewModelBase previous;
    private readonly PageViewModelBase next;
    private readonly object key;
    private readonly ArtworkOverlay image;
    private MediaThumbnail? target;
    private readonly MediaThumbnail source;

    private ArtworkPageTransition(Panel host, PageViewModelBase previous, PageViewModelBase next,
        IPageTransition fallback, bool entering, Endpoint source, IImage snapshot)
        : base(host, new ArtworkOverlay(snapshot), entering, fallback)
    {
        this.previous = previous;
        this.next = next;
        key = ConnectedArtwork.GetKey(source.Control)!;
        this.source = source.Control;
        image = (ArtworkOverlay)Overlay;
        Initialize(source.Control, source.Geometry);
        source.Control.PropertyChanged += OnEndpointChanged;
    }

    internal static ArtworkPageTransition? Create(Panel host, PageViewModelBase? previous,
        PageViewModelBase next, IPageTransition fallback)
    {
        bool entering = previous is GridItemsPageViewModelBase && next is AlbumDetailPageViewModel ||
            previous is HomePageViewModel && next is NowPlayingPageViewModel;
        bool returning = previous is AlbumDetailPageViewModel && next is GridItemsPageViewModelBase ||
            previous is NowPlayingPageViewModel && next is HomePageViewModel;
        if ((!entering && !returning) || previous is null)
            return null;

        object? key = next is AlbumDetailPageViewModel album ? album.ArtworkKey :
            next is NowPlayingPageViewModel playing ? playing.CurrentMedia :
            previous is AlbumDetailPageViewModel leaving ? leaving.ArtworkKey :
            previous is NowPlayingPageViewModel current ? current.ArtworkKey : null;
        if (key is null || Find(host, previous, key) is not { Control.Source: { } artwork } source)
            return null;
        int maximumEdge = returning
            ? (int)Math.Clamp(Math.Ceiling(Math.Max(source.Geometry.Image.Width, source.Geometry.Image.Height) *
                (TopLevel.GetTopLevel(host)?.RenderScaling ?? 1)), 1, 2048)
            : 2048;
        if (Copy(artwork, maximumEdge) is not { } snapshot)
            return null;
        return new ArtworkPageTransition(host, previous, next, fallback, entering, source, snapshot);
    }

    // An owned copy that stays valid after the page holding the original releases it.
    private static IImage? Copy(IImage artwork, int maximumEdge)
    {
        if (artwork is FastImageSource fast)
        {
            try
            {
                return fast.Clone();
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        if (artwork is not Bitmap bitmap)
            return null;
        double reduction = Math.Min(1, (double)maximumEdge / Math.Max(bitmap.PixelSize.Width, bitmap.PixelSize.Height));
        return bitmap.CreateScaledBitmap(new PixelSize(
            Math.Max(1, (int)Math.Round(bitmap.PixelSize.Width * reduction)),
            Math.Max(1, (int)Math.Round(bitmap.PixelSize.Height * reduction))), BitmapInterpolationMode.LowQuality);
    }

    protected override void OnStarting()
    {
        if (Find(Host, previous, key) is { } recreated)
            Hide(recreated.Control);
        if (Find(Host, next, key, image.Source.Size) is { } incoming)
            Hide(incoming.Control);
    }

    protected override async Task<bool> PrepareAsync(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        Endpoint? endpoint;
        do
        {
            endpoint = Find(Host, next, key, image.Source.Size);
            if (endpoint is not null || !Entering || clock.Elapsed.TotalMilliseconds >= 250)
                break;
            await NextFrame(Host, token);
        }
        while (true);
        if (endpoint is null)
            return false;

        Destination = endpoint.Geometry;
        target = endpoint.Control;
        Hide(target);
        target.PropertyChanged += OnEndpointChanged;
        return true;
    }

    private void OnEndpointChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == ConnectedArtwork.KeyProperty && sender is Control control &&
            !Equals(ConnectedArtwork.GetKey(control), key))
            Cancel();
    }

    protected override void SetGeometry(GeometryState geometry)
    {
        image.Geometry = geometry;
        image.InvalidateVisual();
    }

    protected override void OnProgress(double progress)
    {
        double crossfade = target?.Source is not null ? Math.Clamp((progress - 0.85) / 0.15, 0, 1) : 0;
        if (target is not null)
            target.Opacity = Math.Min(1, crossfade * 2);
        image.Opacity = Math.Min(1, (1 - crossfade) * 2);
    }

    protected override void OnRestoring()
    {
        if (IsInteractive && !Landed)
            return;
        if (target is { Source: null } && Equals(ConnectedArtwork.GetKey(target), key))
        {
            if (next is AlbumDetailPageViewModel album)
                album.CoverImage = Copy(image.Source, int.MaxValue);
            else if (next is NowPlayingPageViewModel playing)
                playing.AlbumImage = Copy(image.Source, int.MaxValue);
        }
    }

    protected override void OnFinished()
    {
        source.PropertyChanged -= OnEndpointChanged;
        if (target is not null)
            target.PropertyChanged -= OnEndpointChanged;
        (image.Source as IDisposable)?.Dispose();
    }

    private static Endpoint? Find(Panel host, PageViewModelBase page, object key, Size? fallbackSize = null)
    {
        foreach (MediaThumbnail thumbnail in host.GetVisualDescendants().OfType<MediaThumbnail>())
        {
            if (!thumbnail.IsEffectivelyVisible || !Equals(ConnectedArtwork.GetKey(thumbnail), key) ||
                !thumbnail.GetVisualAncestors().OfType<UserControl>().Any(control => ReferenceEquals(control.DataContext, page)) ||
                Map(thumbnail, host, new Rect(thumbnail.Bounds.Size)) is not { Width: > 0, Height: > 0 } bounds)
                continue;
            Rect viewport = VisibleViewport(thumbnail, host);
            Rect visible = bounds.Intersect(viewport);
            if (visible.Width <= 1 || visible.Height <= 1)
                continue;
            Size size = thumbnail.Source?.Size ?? fallbackSize ?? default;
            if (size.Width <= 0 || size.Height <= 0)
                continue;
            double scale = Math.Max(bounds.Width / size.Width, bounds.Height / size.Height);
            var image = new Rect(bounds.Center.X - size.Width * scale / 2, bounds.Center.Y - size.Height * scale / 2,
                size.Width * scale, size.Height * scale);
            double radius = thumbnail.CornerRadius.TopLeft * bounds.Width / thumbnail.Bounds.Width;
            return new Endpoint(thumbnail, new GeometryState(image, bounds, viewport, radius));
        }
        return null;
    }

    private sealed record Endpoint(MediaThumbnail Control, GeometryState Geometry);

    private sealed class ArtworkOverlay(IImage source) : Control
    {
        public IImage Source { get; } = source;
        public GeometryState Geometry { get; set; }

        public override void Render(DrawingContext context)
        {
            using var viewport = context.PushClip(Geometry.Viewport);
            using var clip = context.PushClip(new RoundedRect(Geometry.Clip, Geometry.Radius));
            context.DrawImage(Source, new Rect(Source.Size), Geometry.Image);
        }
    }
}