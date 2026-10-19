using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.Controls;
using LightStudio.LightPlayer.Models;
using LightMediaRenderer.Avalonia.Controls;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.LightPlayer.Views.Pages;

namespace LightStudio.LightPlayer.Animations;

internal sealed class PhotoPageTransition : ConnectedPageTransition
{
    private readonly PhotoViewerPageViewModel photo;
    private readonly IScrollPositionPageViewModel browser;
    private readonly MediaReference media;
    private readonly PhotoView image;
    private readonly RectangleGeometry viewportClip = new();
    private readonly PhotoPreview? preview;
    private readonly Rect? browserBounds;
    private readonly IDisposable? paneTransitions;
    private readonly bool dismissing;
    private readonly List<IDisposable> browserLayout = [];
    private readonly TranslateTransform browserOffset = new();
    private UserControl? frozenBrowser;
    private PhotoView? viewerImage;
    private PhotoViewerPageView? viewer;
    private Control? browserHeader;
    private FastImage? tileImage;
    private bool fadeOnly;

    private PhotoPageTransition(Panel host, PhotoViewerPageViewModel photo, IScrollPositionPageViewModel browser,
        IPageTransition fallback, bool entering, GeometryState origin, PhotoPreview? preview, Control sourceImage,
        UserControl? browserView = null)
        : base(host, new PhotoView(), entering, fallback)
    {
        this.photo = photo;
        this.browser = browser;
        this.preview = preview;
        browserBounds = browserView is null ? null : Map(browserView, host, new Rect(browserView.Bounds.Size));
        if (!entering)
        {
            paneTransitions = host.GetVisualDescendants().OfType<SplitView>()
                .FirstOrDefault(view => view.Classes.Contains("navHost"))?
                .GetVisualDescendants().OfType<Panel>().FirstOrDefault(panel => panel.Name == "PART_PaneRoot")?
                .SetValue(Animatable.TransitionsProperty, null, BindingPriority.Animation);
        }
        media = photo.Current;
        image = (PhotoView)Overlay;
        image.Source = entering ? null : photo.Image;
        image.PreviewSource = preview?.Image;
        image.EnableHdrPassthrough = photo.EnableHdrPassthrough;
        image.EnableGainMap = photo.EnableGainMap;
        image.DisplayPanelBitDepth = photo.DisplayPanelBitDepth;
        image.Clip = viewportClip;
        if (sourceImage is PhotoView sourcePhoto)
        {
            image.ShareImageCache(sourcePhoto);
            dismissing = !entering && sourcePhoto.DismissOffset.Y > 0;
        }
        photo.IsTransitioning = true;
        FindPhoto(host, photo)?.SetTransitionChrome(0);
        Initialize(sourceImage, origin);
        FreezeBrowserLayout(browserView);
        photo.PropertyChanged += OnPhotoChanged;
    }

    internal static PhotoPageTransition? Create(Panel host, PageViewModelBase? previous,
        PageViewModelBase next, IPageTransition fallback)
    {
        if (previous is MediaPageViewModel origin && next is PhotoViewerPageViewModel opening &&
            FindBrowser(host, origin) is { } originView)
        {
            opening.RetainOriginPreviews(originView.GetVisualDescendants()
                .Where(control => control is VideoTile or VideoListRow)
                .Select(control => control.DataContext)
                .OfType<VideoTileModel>());
        }

        if (previous is IScrollPositionPageViewModel browser && next is PhotoViewerPageViewModel photo &&
            FindTile(host, browser, photo.Current, null) is { } tile && tile.Model.Preview is { } bitmap)
        {
            tile.Model.RequestPreview();
            var preview = new PhotoPreview(bitmap, tile.Model.ReleasePreview);
            photo.SupplyPreview(preview.AddRef());
            photo.OriginPage = browser;
            photo.OriginScrollOffset = browser.ScrollOffset;
            return new PhotoPageTransition(host, photo, browser, fallback, true, tile.Geometry, preview, tile.Image, tile.View);
        }

        if (previous is PhotoViewerPageViewModel leaving && next is IScrollPositionPageViewModel returning &&
            next is MediaPageViewModel or SearchPageViewModel or PhotoSearchPageViewModel &&
            FindPhoto(host, leaving) is { } view && view.FindControl<PhotoView>("Photo") is { HasDisplayImage: true } picture &&
            CapturePhoto(host, picture, leaving.Rotation) is { } geometry)
        {
            if (ReferenceEquals(leaving.OriginPage, returning))
                returning.ScrollOffset = leaving.OriginScrollOffset;
            return new PhotoPageTransition(host, leaving, returning, fallback, false, geometry,
                leaving.Preview?.AddRef(), picture);
        }

        return null;
    }

    internal override void RetainOutgoingPage(PageViewModelBase page)
    {
        base.RetainOutgoingPage(page);
        photo.IsFullScreen = false;
    }

    protected override void OnStarting()
    {
        if (Entering)
        {
            FreezeBrowserLayout(FindBrowser(From, browser));
            tileImage = FindTile(Host, browser, media, null, From)?.Image;
            if (tileImage is not null)
                Hide(tileImage);
        }
        viewer = FindPhoto(Entering ? To : From, photo);
        viewer?.SetTransitionChrome(0);
        if (dismissing)
            SetOutgoingOpacity(0);
        browserHeader = FindBrowser(Entering ? From : To, browser)?.FindControl<Control>("SearchHeader");
        if (browserHeader is not null)
            browserHeader.Opacity = Entering || dismissing ? 1 : 0;
    }

    protected override async Task<bool> PrepareAsync(CancellationToken token)
    {
        if (Entering)
        {
            tileImage = FindTile(Host, browser, media, null, From)?.Image;
            if (tileImage is not null)
                Hide(tileImage);
            viewerImage = FindPhoto(To, photo)?.FindControl<PhotoView>("Photo");
            if (viewerImage is null)
                return false;
            Hide(viewerImage);
            var clock = Stopwatch.StartNew();
            while (photo.NaturalSize.Width <= 0 && photo.Image is null && !photo.HasFailed &&
                clock.Elapsed.TotalMilliseconds < 250)
                await NextFrame(Host, token);
            if (CapturePhoto(Host, viewerImage, photo.Rotation) is not { } target)
                return false;
            Destination = target;
        }
        else
        {
            if (ReferenceEquals(photo.OriginPage, browser) &&
                FindBrowser(To, browser) is { } browserView &&
                (browserView.FindControl<ScrollViewer>("MediaScrollViewer") ??
                 browserView.FindControl<ScrollViewer>("ResultsScrollViewer") ??
                 browserView.FindControl<ScrollViewer>("PhotosScrollViewer")) is { } scroll)
            {
                var clock = Stopwatch.StartNew();
                do
                {
                    scroll.UpdateLayout();
                    double offset = Math.Min(photo.OriginScrollOffset, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
                    if (Math.Abs(scroll.Offset.Y - offset) <= 0.5)
                        break;
                    browser.ScrollOffset = offset;
                    scroll.Offset = new Vector(scroll.Offset.X, offset);
                    await NextFrame(Host, token);
                    await NextFrame(Host, token);
                }
                while (clock.Elapsed.TotalMilliseconds < 250);
            }
            TileTarget? target = FindTile(Host, browser, media, preview, To);
            if (target is null)
            {
                await NextFrame(Host, token);
                target = FindTile(Host, browser, media, preview, To);
            }
            if (target is null)
            {
                if (!dismissing)
                    return false;
                fadeOnly = true;
                Destination = Origin;
            }
            else
            {
                Destination = target.Geometry;
                tileImage = target.Image;
                Hide(tileImage);
            }
        }
        UpdateSource();
        return true;
    }

    private void OnPhotoChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (photo.Current != media)
            Cancel();
        else if (!IsFinished && args.PropertyName == nameof(PhotoViewerPageViewModel.Image))
            UpdateSource();
    }

    private void UpdateSource()
    {
        if (photo.Image is { } frame)
        {
            if (Entering && (OperatingSystem.IsWindows() || OperatingSystem.IsAndroid()) &&
                (frame.PhotoTiles is not null || frame.GainMap?.Samples?.PhotoTiles is not null))
                return;
            image.Source = frame;
        }
    }

    protected override void UpdateDestination()
    {
        if (fadeOnly)
            return;
        // Follow the destination through shell layout changes and thumbnail recycling.
        if (Entering)
        {
            if (viewerImage is not null && CapturePhoto(Host, viewerImage, photo.Rotation) is { } target)
                Destination = target;
            return;
        }

        TileTarget? tile = FindTile(Host, browser, media, preview, To);
        if (!ReferenceEquals(tileImage, tile?.Image))
        {
            if (tileImage is not null)
                tileImage.Opacity = 1;
            tileImage = tile?.Image;
            if (tileImage is not null)
                Hide(tileImage);
        }
        if (tile is not null)
            Destination = tile.Geometry;
    }

    protected override void OnProgress(double progress)
    {
        double incomingChrome = Math.Clamp((progress - 0.65) / 0.35, 0, 1);
        double outgoingChrome = Math.Clamp(1 - progress * 3, 0, 1);
        viewer?.SetTransitionChrome(0);
        if (dismissing)
            SetOutgoingOpacity(0);
        if (browserHeader is not null)
            browserHeader.Opacity = Entering ? outgoingChrome : dismissing ? 1 : incomingChrome;
        if (fadeOnly)
            image.Opacity = 1 - progress;
        else if (!Entering)
        {
            if (tileImage is not null)
                tileImage.Opacity = progress > 0.85 ? 1 : 0;
            image.Opacity = Math.Clamp((1 - progress) / 0.15, 0, 1);
        }
    }

    protected override void SetGeometry(GeometryState geometry)
    {
        image.TransitionBounds = geometry.Image;
        image.TransitionClip = new RoundedRect(geometry.Clip, geometry.Radius);
        image.TransitionRotation = geometry.Rotation;
        viewportClip.Rect = geometry.Viewport;
    }

    protected override void OnRemovingOverlay() => viewerImage?.ShareImageCache(image);

    protected override void OnRestoring()
    {
        paneTransitions?.Dispose();
        RestoreBrowserLayout();
        photo.IsTransitioning = false;
        viewer?.SetTransitionChrome(1);
        if (browserHeader is not null)
            browserHeader.Opacity = 1;
        if (viewerImage is not null)
        {
            viewerImage.Opacity = 1;
            if (Landed && Entering && image.TransitionBounds is { } rectangle &&
                Map(Host, viewerImage, rectangle) is { } local)
                viewerImage.ContinueFrom(local);
        }
        if (tileImage is not null)
            tileImage.Opacity = 1;
        if (FindPhoto(Host, photo) is { } activeViewer)
        {
            activeViewer.SetTransitionChrome(1);
            if (activeViewer.FindControl<PhotoView>("Photo") is { } picture)
                picture.Opacity = 1;
        }
    }

    protected override void OnFinished()
    {
        photo.PropertyChanged -= OnPhotoChanged;
        image.Source = null;
        image.PreviewSource = null;
        preview?.Release();
    }

    private void FreezeBrowserLayout(UserControl? view)
    {
        if (view is null || browserBounds is not { Width: > 0, Height: > 0 } bounds ||
            ReferenceEquals(view, frozenBrowser))
            return;

        RestoreBrowserLayout();
        frozenBrowser = view;
        browserLayout.Add(view.SetValue(BrowseScrollMemory.IsReadOnlyProperty, true, BindingPriority.Animation)!);
        browserLayout.Add(view.SetValue(Layoutable.WidthProperty, bounds.Width, BindingPriority.Animation)!);
        browserLayout.Add(view.SetValue(Layoutable.HeightProperty, bounds.Height, BindingPriority.Animation)!);
        browserLayout.Add(view.SetValue(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Left, BindingPriority.Animation)!);
        browserLayout.Add(view.SetValue(Layoutable.VerticalAlignmentProperty, VerticalAlignment.Top, BindingPriority.Animation)!);
        browserLayout.Add(view.SetValue(Layoutable.MarginProperty, new Thickness(bounds.X, bounds.Y, 0, 0), BindingPriority.Animation)!);
        browserLayout.Add(view.SetValue(Visual.RenderTransformProperty, browserOffset, BindingPriority.Animation)!);
        view.LayoutUpdated += OnBrowserLayoutUpdated;
    }

    private void OnBrowserLayoutUpdated(object? sender, EventArgs args)
    {
        if (frozenBrowser is { } view && browserBounds is { } bounds &&
            Map(view, Host, new Rect(view.Bounds.Size)) is { } current)
        {
            browserOffset.X += bounds.X - current.X;
            browserOffset.Y += bounds.Y - current.Y;
        }
    }

    private void RestoreBrowserLayout()
    {
        if (frozenBrowser is not null)
            frozenBrowser.LayoutUpdated -= OnBrowserLayoutUpdated;
        foreach (IDisposable value in browserLayout)
            value.Dispose();
        browserLayout.Clear();
        browserOffset.X = 0;
        browserOffset.Y = 0;
        frozenBrowser = null;
    }

    private static UserControl? FindBrowser(Visual? root, IScrollPositionPageViewModel model) =>
        root is UserControl page && IsBrowserView(page) && ReferenceEquals(page.DataContext, model) ? page :
        root?.GetVisualDescendants().OfType<UserControl>()
            .FirstOrDefault(view => IsBrowserView(view) && ReferenceEquals(view.DataContext, model) && view.IsEffectivelyVisible);

    private static bool IsBrowserView(UserControl view) =>
        view is MediaPageView or SearchPageView or PhotoSearchPageView;

    private static PhotoViewerPageView? FindPhoto(Visual? root, PhotoViewerPageViewModel model) =>
        root is PhotoViewerPageView page && ReferenceEquals(page.DataContext, model) ? page :
        root?.GetVisualDescendants().OfType<PhotoViewerPageView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, model));

    private static GeometryState? CapturePhoto(Visual host, PhotoView picture, int rotation)
    {
        Rect rectangle = picture.DisplayedImageBounds;
        if (rotation % 180 == 90)
            rectangle = new Rect(rectangle.Center.X - rectangle.Height / 2, rectangle.Center.Y - rectangle.Width / 2,
                rectangle.Height, rectangle.Width);
        if (Map(picture, host, rectangle) is not { Width: > 0, Height: > 0 } image ||
            Map(picture, host, new Rect(picture.Bounds.Size)) is not { } viewport)
            return null;
        viewport = viewport.Intersect(new Rect(host.Bounds.Size));
        return new GeometryState(image, viewport, viewport, 0, rotation > 180 ? rotation - 360 : rotation);
    }

    private static TileTarget? FindTile(Panel host, IScrollPositionPageViewModel browser, MediaReference media,
        PhotoPreview? preview, Visual? root = null)
    {
        UserControl? view = FindBrowser(root ?? host, browser);
        if (view is null)
            return null;

        foreach (Border frame in view.GetVisualDescendants().OfType<Border>())
        {
            if (frame.Name != "PreviewFrame" || !frame.IsEffectivelyVisible ||
                frame.DataContext is not VideoTileModel { Photo: { } file } model || file.Media != media ||
                Map(frame, host, new Rect(frame.Bounds.Size)) is not { Width: > 0, Height: > 0 } bounds)
                continue;

            Rect viewport = VisibleViewport(frame, host);
            if (view.GetVisualDescendants().OfType<PageAddressBarSlot>().FirstOrDefault(slot => slot.IsVisible) is { } path &&
                Map(path, host, new Rect(path.Bounds.Size)) is { } pathBounds && pathBounds.Bottom > viewport.Top)
                viewport = viewport.Intersect(new Rect(viewport.Left, pathBounds.Bottom, viewport.Width,
                    Math.Max(0, viewport.Bottom - pathBounds.Bottom)));
            Rect visible = bounds.Intersect(viewport);
            if (visible.Width <= 1 || visible.Height <= 1)
                continue;

            FastImage? image = frame.GetVisualDescendants().OfType<FastImage>().FirstOrDefault();
            if (image is null)
                continue;
            if (model.Preview is null && preview is not null)
                model.Preview = preview.RetainImage();
            if (model.Preview is not { Size.Width: > 0, Size.Height: > 0 } bitmap)
                continue;

            double scale = Math.Max(bounds.Width / bitmap.Size.Width, bounds.Height / bitmap.Size.Height);
            var destination = new Rect(bounds.Center.X - bitmap.Size.Width * scale / 2,
                bounds.Center.Y - bitmap.Size.Height * scale / 2, bitmap.Size.Width * scale, bitmap.Size.Height * scale);
            double radius = frame.CornerRadius.TopLeft * bounds.Width / Math.Max(1, frame.Bounds.Width);
            return new TileTarget(view, model, image, new GeometryState(destination, bounds, viewport, radius, 0));
        }
        return null;
    }

    private sealed record TileTarget(UserControl View, VideoTileModel Model, FastImage Image, GeometryState Geometry);
}