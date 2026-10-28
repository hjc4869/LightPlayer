using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Media;
using LightMediaRenderer.Avalonia.Rendering;
using LightMediaRenderer.Avalonia.Shaders;

namespace LightMediaRenderer.Avalonia.Controls;

/// <summary>
/// Shows a photo or animated image through the video renderer, with zoom and pan.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="VideoView"/> rather than a mode of it: a photo is navigated rather than
/// played, so it needs a viewport the video path has no use for, and it needs none of the sink,
/// track and subtitle machinery that makes up most of that control.
/// </para>
/// <para>
/// The zoom is fed into the destination rectangle rather than applied as a render transform on the
/// composited output. Scaling the result of the draw would resample an already resampled picture
/// instead of sampling the decoded picture directly. Scaling the destination uses bilinear
/// sampling at whatever magnification is on screen.
/// </para>
/// </remarks>
public partial class PhotoView : Control
{
    /// <summary>How much one wheel notch multiplies the zoom by.</summary>
    private const double WheelZoomStep = 1.25;

    private const double DipsPerWheelDelta = 50;
    private const double ZoomAnimationMs = 160;
    private const double PanAnimationMs = 180;
    private const ulong PanSampleWindowMs = 100;
    private const ulong PanReleaseTimeoutMs = 200;
    private const double MinimumPanSpeed = 50;
    private const double MaximumPanSpeed = 8000;
    private const double PanStoppingSpeed = 5;

    public static readonly StyledProperty<VideoFrame?> SourceProperty =
        AvaloniaProperty.Register<PhotoView, VideoFrame?>(nameof(Source));

    public static readonly StyledProperty<IImage?> PreviewSourceProperty =
        AvaloniaProperty.Register<PhotoView, IImage?>(nameof(PreviewSource));

    public static readonly StyledProperty<Size> PreviewNaturalSizeProperty =
        AvaloniaProperty.Register<PhotoView, Size>(nameof(PreviewNaturalSize));

    public static readonly StyledProperty<object?> SourceKeyProperty =
        AvaloniaProperty.Register<PhotoView, object?>(nameof(SourceKey));

    public static readonly StyledProperty<IImage?> PreviousPreviewSourceProperty =
        AvaloniaProperty.Register<PhotoView, IImage?>(nameof(PreviousPreviewSource));

    public static readonly StyledProperty<IImage?> NextPreviewSourceProperty =
        AvaloniaProperty.Register<PhotoView, IImage?>(nameof(NextPreviewSource));

    public static readonly StyledProperty<Size> PreviousNaturalSizeProperty =
        AvaloniaProperty.Register<PhotoView, Size>(nameof(PreviousNaturalSize));

    public static readonly StyledProperty<Size> NextNaturalSizeProperty =
        AvaloniaProperty.Register<PhotoView, Size>(nameof(NextNaturalSize));

    public static readonly StyledProperty<Rect?> TransitionBoundsProperty =
        AvaloniaProperty.Register<PhotoView, Rect?>(nameof(TransitionBounds));

    public static readonly StyledProperty<RoundedRect?> TransitionClipProperty =
        AvaloniaProperty.Register<PhotoView, RoundedRect?>(nameof(TransitionClip));

    public static readonly StyledProperty<double> TransitionRotationProperty =
        AvaloniaProperty.Register<PhotoView, double>(nameof(TransitionRotation));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<PhotoView>();

    public static readonly StyledProperty<bool> EnableHdrPassthroughProperty =
        AvaloniaProperty.Register<PhotoView, bool>(nameof(EnableHdrPassthrough), true);

    public static readonly StyledProperty<bool> EnableGainMapProperty =
        AvaloniaProperty.Register<PhotoView, bool>(nameof(EnableGainMap), true);

    public static readonly StyledProperty<VideoSdrToneMappingOptions?> ToneMappingProperty =
        AvaloniaProperty.Register<PhotoView, VideoSdrToneMappingOptions?>(nameof(ToneMapping));

    public static readonly DirectProperty<PhotoView, VideoSdrToneMappingOptions> AutomaticToneMappingProperty =
        AvaloniaProperty.RegisterDirect<PhotoView, VideoSdrToneMappingOptions>(nameof(AutomaticToneMapping), view => view.AutomaticToneMapping);

    public static readonly DirectProperty<PhotoView, VideoDisplayToneTarget> DisplayToneTargetProperty =
        AvaloniaProperty.RegisterDirect<PhotoView, VideoDisplayToneTarget>(nameof(DisplayToneTarget), view => view.DisplayToneTarget);

    public static readonly DirectProperty<PhotoView, bool> IsExtendedRangeOutputProperty =
        AvaloniaProperty.RegisterDirect<PhotoView, bool>(nameof(IsExtendedRangeOutput), view => view.IsExtendedRangeOutput);

    public static readonly DirectProperty<PhotoView, bool> SupportsHdrOutputProperty =
        AvaloniaProperty.RegisterDirect<PhotoView, bool>(nameof(SupportsHdrOutput), view => view.SupportsHdrOutput);

    public static readonly StyledProperty<int> DisplayPanelBitDepthProperty =
        AvaloniaProperty.Register<PhotoView, int>(nameof(DisplayPanelBitDepth));

    public static readonly StyledProperty<int> RotationProperty =
        AvaloniaProperty.Register<PhotoView, int>(nameof(Rotation));

    public static readonly StyledProperty<VideoFrame?> PreviousSourceProperty =
        AvaloniaProperty.Register<PhotoView, VideoFrame?>(nameof(PreviousSource));

    public static readonly StyledProperty<VideoFrame?> NextSourceProperty =
        AvaloniaProperty.Register<PhotoView, VideoFrame?>(nameof(NextSource));

    public static readonly StyledProperty<double> SwipeOffsetProperty =
        AvaloniaProperty.Register<PhotoView, double>(nameof(SwipeOffset));

    public static readonly StyledProperty<Vector> DismissOffsetProperty =
        AvaloniaProperty.Register<PhotoView, Vector>(nameof(DismissOffset));

    public static readonly StyledProperty<double> DismissScaleProperty =
        AvaloniaProperty.Register<PhotoView, double>(nameof(DismissScale), 1);

    /// <summary>Space left between one picture and the next while they are being swiped past.</summary>
    public const double NeighborGap = 24;

    /// <summary>Smallest distance between two contacts that may be read as a magnification.</summary>
    private const double MinimumContactSpan = 1;

    private static readonly Cursor GrabCursor = new(StandardCursorType.SizeAll);

    private readonly FrameSlot current;
    private readonly FrameSlot previous;
    private readonly FrameSlot next;
    private readonly HdrContentHint hdrContentHint;
    private readonly Glide zoomGlide;
    private readonly Glide panGlide;
    private readonly Glide previewGlide;
    private readonly List<(ulong Timestamp, Vector Offset)> panSamples = new();
    private readonly Stopwatch panInertiaClock = new();
    private Vector panInertiaVelocity;
    private Vector panInertiaOrigin;
    private int panInertiaGeneration;
    private Rect? previewBounds;
    private Rect previewStart;
    private Rect previewEnd;
    private readonly List<Visual> visibilityAncestors = new();
    private double? zoomTarget;
    private Point zoomAnchor;
    private Point zoomFocus;
    private TopLevel? topLevel;
    private int reportedExtendedRange;
    private bool isExtendedRangeOutput;
    private int reportedHdrOutput;
    private bool supportsHdrOutput;
    private VideoSdrToneMappingOptions? reportedAutomaticToneMapping;
    private readonly object displayToneTargetSync = new();
    private VideoDisplayToneTarget? reportedDisplayToneTarget;
    private VideoDisplayToneTarget displayToneTarget = new(1, VideoOutputColorSpaceExtensions.HdrReferenceWhiteNits);
    private VideoSdrToneMappingOptions automaticToneMapping = VideoSdrToneMappingOptions.Default with
    {
        Algorithm = VideoSdrToneMappingAlgorithm.Spline,
        TargetHeadroom = 1,
    };

    /// <summary>The two contacts a pinch is being read from, in the order they arrived.</summary>
    private readonly List<(IPointer Pointer, Point Position)> contacts = new(2);
    private readonly HashSet<IPointer> activeContacts = new();
    private double zoom = 1;
    private bool initialZoomPending;
    private Vector offset;
    private Vector panStartOffset;
    private Vector panTargetOffset;
    private Point? dragOrigin;
    private Vector dragStartOffset;
    private double gestureStartZoom;
    private double gestureStartSpan;
    private Point gestureAnchor;
    private bool gesturing;
    private bool hadMultipleContacts;
    private IPointer? tapPointer;
    private IPointer? dragPointer;
    private Rect tapBounds;
    private bool previousWasTap;
    private bool doubleTapPending;

    static PhotoView()
    {
        AffectsRender<PhotoView>(
            BackgroundProperty,
            PreviewSourceProperty,
            PreviewNaturalSizeProperty,
            PreviousPreviewSourceProperty,
            NextPreviewSourceProperty,
            PreviousNaturalSizeProperty,
            NextNaturalSizeProperty,
            TransitionBoundsProperty,
            TransitionClipProperty,
            TransitionRotationProperty,
            EnableHdrPassthroughProperty,
            EnableGainMapProperty,
            ToneMappingProperty,
            DisplayPanelBitDepthProperty,
            SwipeOffsetProperty,
            DismissOffsetProperty,
            DismissScaleProperty);
        SourceProperty.Changed.AddClassHandler<PhotoView>((view, e) => view.OnSourceChanged(e));
        SourceKeyProperty.Changed.AddClassHandler<PhotoView>((view, _) => view.ResetViewport());
        PreviewSourceProperty.Changed.AddClassHandler<PhotoView>((view, args) => view.OnPreviewSourceChanged(args));
        PreviousPreviewSourceProperty.Changed.AddClassHandler<PhotoView>((view, args) => view.OnPreviewSourceChanged(args));
        NextPreviewSourceProperty.Changed.AddClassHandler<PhotoView>((view, args) => view.OnPreviewSourceChanged(args));
        PreviewNaturalSizeProperty.Changed.AddClassHandler<PhotoView>((view, args) => view.OnPreviewSizeChanged(args));
        PreviousSourceProperty.Changed.AddClassHandler<PhotoView>(
            (view, e) => view.OnNeighborChanged(view.previous, e));
        NextSourceProperty.Changed.AddClassHandler<PhotoView>(
            (view, e) => view.OnNeighborChanged(view.next, e));
        RotationProperty.Changed.AddClassHandler<PhotoView>((view, _) => view.OnRotationChanged());
        FocusableProperty.OverrideDefaultValue<PhotoView>(true);
    }

    public PhotoView()
    {
        hdrContentHint = new HdrContentHint(this);
        current = new FrameSlot(RequestImageRender, OnImageFailed);
        previous = new FrameSlot(RequestImageRender, OnImageFailed);
        next = new FrameSlot(RequestImageRender, OnImageFailed);
        zoomGlide = new Glide(this, ApplyAnimatedZoom) { Completed = () => zoomTarget = null };
        panGlide = new Glide(this, ApplyAnimatedPan);
        previewGlide = new Glide(this, progress =>
        {
            previewBounds = new Rect(
                previewStart.X + (previewEnd.X - previewStart.X) * progress,
                previewStart.Y + (previewEnd.Y - previewStart.Y) * progress,
                previewStart.Width + (previewEnd.Width - previewStart.Width) * progress,
                previewStart.Height + (previewEnd.Height - previewStart.Height) * progress);
            InvalidateVisual();
        })
        {
            Completed = () =>
            {
                previewBounds = null;
                InvalidateVisual();
            },
        };
        PropertyChanged += OnVisibilityChanged;
        EffectiveViewportChanged += (_, e) =>
        {
            Rect visible = e.EffectiveViewport.Intersect(new Rect(Bounds.Size));
            if (!IsEffectivelyVisible || visible.Width <= 0 || visible.Height <= 0)
                StopZoomAnimation();
            animationViewportVisible = visible.Width > 0 && visible.Height > 0;
            UpdatePhotoAnimation();
        };

        // The pinch recognizer fixes its origin at the midpoint the fingers started from, which is
        // enough to magnify about but not to be carried by, so the contacts are read directly here.
        DoubleTapped += OnDoubleTapped;
        PointerTouchPadGestureMagnify += OnTouchpadMagnify;
    }

    /// <summary>
    /// The picture on screen. The control holds its own reference for as long as it shows it, so
    /// the caller may release the one it decoded as soon as it has been assigned.
    /// </summary>
    public VideoFrame? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IImage? PreviewSource
    {
        get => GetValue(PreviewSourceProperty);
        set => SetValue(PreviewSourceProperty, value);
    }

    public Size PreviewNaturalSize
    {
        get => GetValue(PreviewNaturalSizeProperty);
        set => SetValue(PreviewNaturalSizeProperty, value);
    }

    public object? SourceKey
    {
        get => GetValue(SourceKeyProperty);
        set => SetValue(SourceKeyProperty, value);
    }

    public IImage? PreviousPreviewSource
    {
        get => GetValue(PreviousPreviewSourceProperty);
        set => SetValue(PreviousPreviewSourceProperty, value);
    }

    public IImage? NextPreviewSource
    {
        get => GetValue(NextPreviewSourceProperty);
        set => SetValue(NextPreviewSourceProperty, value);
    }

    public Size PreviousNaturalSize
    {
        get => GetValue(PreviousNaturalSizeProperty);
        set => SetValue(PreviousNaturalSizeProperty, value);
    }

    public Size NextNaturalSize
    {
        get => GetValue(NextNaturalSizeProperty);
        set => SetValue(NextNaturalSizeProperty, value);
    }

    public bool HasDisplayImage => current.Frame is not null || PreviewSource is not null;

    public Rect? TransitionBounds
    {
        get => GetValue(TransitionBoundsProperty);
        set => SetValue(TransitionBoundsProperty, value);
    }

    public RoundedRect? TransitionClip
    {
        get => GetValue(TransitionClipProperty);
        set => SetValue(TransitionClipProperty, value);
    }

    public double TransitionRotation
    {
        get => GetValue(TransitionRotationProperty);
        set => SetValue(TransitionRotationProperty, value);
    }

    public void ShareImageCache(PhotoView source) => current.ShareCache(source.current);

    public void ContinueFrom(Rect rectangle) => AnimatePreviewSize(rectangle,
        current.Frame is { } frame ? Viewport(frame).Destination(zoom, offset) : PreviewDestination(PreviewNaturalSize));

    public Rect DisplayedImageBounds
    {
        get
        {
            if (TransitionBounds is { } transition)
                return transition;
            if (SourceKey is not null && initialZoomPending && current.Frame is null)
                return default;

            Size natural = current.Frame is { } frame ? NaturalSize(frame, Rotation) : PreviewSize(PreviewSource, PreviewNaturalSize);
            if (current.Frame is null && Rotation % 180 == 90)
                natural = new Size(natural.Height, natural.Width);
            if (natural.Width <= 0 || natural.Height <= 0)
                return default;

            Rect destination = previewBounds ?? new PhotoViewport(natural, Bounds.Size, RenderScaling).Destination(zoom, offset);
            return PushedAway(destination).Translate(new Vector(SwipeOffset, 0));
        }
    }

    /// <summary>Painted behind and around the picture.</summary>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>
    /// The picture a swipe to the right brings in, held ready off screen. Null while it is still
    /// being read, which only leaves the space beside the picture empty until it arrives.
    /// </summary>
    public VideoFrame? PreviousSource
    {
        get => GetValue(PreviousSourceProperty);
        set => SetValue(PreviousSourceProperty, value);
    }

    /// <inheritdoc cref="PreviousSource"/>
    public VideoFrame? NextSource
    {
        get => GetValue(NextSourceProperty);
        set => SetValue(NextSourceProperty, value);
    }

    /// <summary>
    /// How far the pictures have been dragged sideways, in device-independent pixels. The whole
    /// row moves together, so the neighbor follows the finger in from the edge as the current
    /// picture leaves.
    /// </summary>
    public double SwipeOffset
    {
        get => GetValue(SwipeOffsetProperty);
        set => SetValue(SwipeOffsetProperty, value);
    }

    /// <summary>
    /// How far the picture has been pushed away from where it belongs, in device-independent
    /// pixels. Only the picture being looked at moves: this is the page being dragged aside, not
    /// the picture being scrolled within it.
    /// </summary>
    public Vector DismissOffset
    {
        get => GetValue(DismissOffsetProperty);
        set => SetValue(DismissOffsetProperty, value);
    }

    /// <summary>
    /// How much of its size the picture keeps while it is being pushed away. Applied about the
    /// middle of the viewport, so the picture shrinks towards where it came from.
    /// </summary>
    public double DismissScale
    {
        get => GetValue(DismissScaleProperty);
        set => SetValue(DismissScaleProperty, value);
    }

    /// <inheritdoc cref="VideoView.EnableHdrPassthrough"/>
    public bool EnableHdrPassthrough
    {
        get => GetValue(EnableHdrPassthroughProperty);
        set => SetValue(EnableHdrPassthroughProperty, value);
    }

    public bool EnableGainMap
    {
        get => GetValue(EnableGainMapProperty);
        set => SetValue(EnableGainMapProperty, value);
    }

    public bool IsExtendedRangeOutput
    {
        get => isExtendedRangeOutput;
        private set => SetAndRaise(IsExtendedRangeOutputProperty, ref isExtendedRangeOutput, value);
    }

    public VideoSdrToneMappingOptions? ToneMapping
    {
        get => GetValue(ToneMappingProperty);
        set => SetValue(ToneMappingProperty, value);
    }

    public VideoSdrToneMappingOptions AutomaticToneMapping
    {
        get => automaticToneMapping;
        private set => SetAndRaise(AutomaticToneMappingProperty, ref automaticToneMapping, value);
    }

    /// <summary>What the tone mapping targets left to the display currently resolve to.</summary>
    public VideoDisplayToneTarget DisplayToneTarget
    {
        get => displayToneTarget;
        private set => SetAndRaise(DisplayToneTargetProperty, ref displayToneTarget, value);
    }

    public bool SupportsHdrOutput
    {
        get => supportsHdrOutput;
        private set => SetAndRaise(SupportsHdrOutputProperty, ref supportsHdrOutput, value);
    }

    /// <inheritdoc cref="VideoView.DisplayPanelBitDepth"/>
    public int DisplayPanelBitDepth
    {
        get => GetValue(DisplayPanelBitDepthProperty);
        set => SetValue(DisplayPanelBitDepthProperty, value);
    }

    /// <summary>Magnification over the size the picture is shown at when it first opens.</summary>
    public double Zoom => zoom;

    public double TargetZoom => zoomTarget ?? zoom;

    public double MinimumZoom => current.Frame is { } frame ? Viewport(frame).MinimumZoom : 1;

    public bool IsFitToWindow => Math.Abs(zoom - 1) < 1e-6;

    public double ZoomPercentage => current.Frame is { } frame ? 100 * zoom / Viewport(frame).ActualSizeZoom : 100;

    public event EventHandler? ViewportChanged;

    public event Action<Exception>? ImageFailed;

    public void ZoomTo(double value) => ApplyZoom(value, Center);

    public void ZoomTo(double value, bool animate)
    {
        if (animate)
            AnimateZoom(value, Center);
        else
            ApplyZoom(value, Center);
    }

    /// <summary>
    /// Whether the picture is larger than the control and so answers a drag by moving. False leaves
    /// the drag to the page, whose sideways swipe turns to the next picture.
    /// </summary>
    public bool CanPan => current.Frame is { } frame && Viewport(frame).CanPan(zoom);

    /// <summary>Moves the picture by a viewport-relative amount with the same frame-paced easing as zoom.</summary>
    public void PanBy(Vector delta)
    {
        StopZoomAnimation();
        if (current.Frame is not { } frame || !CanPan)
            return;

        Vector target = Viewport(frame).Clamp(offset + delta, zoom);
        if (target == offset)
            return;

        if (topLevel is null || !IsEffectivelyVisible)
        {
            offset = target;
            UpdateCursor();
            InvalidateVisual();
            return;
        }

        panStartOffset = offset;
        panTargetOffset = target;
        panGlide.Start(0, 1, PanAnimationMs);
    }

    /// <summary>
    /// Whether two contacts are on the picture, moving and magnifying it together. The page asks so
    /// that it neither starts a swipe underneath the gesture nor mistakes it for a request for the
    /// context menu.
    /// </summary>
    public bool IsGestureActive => gesturing;

    /// <summary>
    /// Clockwise turn asked for on top of the one the file declares, in degrees. Nothing is written
    /// back to the file: this is how the picture is being looked at, not how it is stored.
    /// </summary>
    public int Rotation
    {
        get => GetValue(RotationProperty);
        set => SetValue(RotationProperty, value);
    }

    /// <summary>Steps the magnification up by one wheel notch, about the middle of the viewport.</summary>
    public void ZoomIn() => AnimateZoom((zoomTarget ?? zoom) * WheelZoomStep, Center);

    public void ZoomOut() => AnimateZoom((zoomTarget ?? zoom) / WheelZoomStep, Center);

    /// <summary>Shows one stored pixel per display pixel.</summary>
    public void ZoomToActualSize()
    {
        if (current.Frame is { } frame)
            AnimateZoom(Viewport(frame).ActualSizeZoom, Center);
    }

    /// <summary>Returns to the size the picture opened at, filling the viewport without cropping.</summary>
    public void ZoomToFit() => AnimateZoom(1, Center);

    public void ToggleZoom() => ToggleZoom(Center);

    private Point Center => new(Bounds.Width / 2, Bounds.Height / 2);

    public sealed override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);

        // The custom draw operation is not hit testable, so without a fill of its own the control
        // would be transparent to the wheel and pointer and neither zoom nor pan would arrive.
        context.FillRectangle(Background ?? Brushes.Transparent, bounds);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            hdrContentHint.Update(false);
            return;
        }

        double swipe = SwipeOffset;
        bool hasHdrContent = false;
        HdrContentHint.Metadata? contentMetadata = null;

        // The row is wider than the control while a swipe is in flight, and the neighbor must not
        // be drawn over whatever the page put beside the picture.
        using DrawingContext.PushedState clip = context.PushClip(bounds);
        using DrawingContext.PushedState imageClip = context.PushClip(TransitionClip ?? new RoundedRect(bounds));

        Rect destination = DisplayedImageBounds;
        using (context.PushTransform(
            Matrix.CreateTranslation(-destination.Center.X, -destination.Center.Y) *
            Matrix.CreateRotation(TransitionRotation * Math.PI / 180) *
            Matrix.CreateTranslation(destination.Center.X, destination.Center.Y)))
        {
            if (current.Frame?.HasAlpha != true &&
                (current.Frame?.PhotoSurface is not null || current.Frame?.PhotoTiles is not null || current.Frame?.GainMap?.Samples is not null) && PreviewSource is { } thumbnail)
                hasHdrContent |= DrawPreview(context, thumbnail, destination, Rotation, ref contentMetadata);
            if (current.Frame is not null)
                hasHdrContent |= Draw(context, current, bounds, destination, Rotation, ref contentMetadata);
            else if (PreviewSource is { } preview)
                hasHdrContent |= DrawPreview(context, preview, destination, Rotation, ref contentMetadata);
        }

        // Only the side being swiped towards can be seen, so the other one is not drawn at all.
        if (swipe > 0)
            hasHdrContent |= DrawNeighbor(context, previous, PreviousPreviewSource, PreviousNaturalSize, bounds, -1, swipe, ref contentMetadata);
        else if (swipe < 0)
            hasHdrContent |= DrawNeighbor(context, next, NextPreviewSource, NextNaturalSize, bounds, 1, swipe, ref contentMetadata);
        hdrContentHint.Update(hasHdrContent, contentMetadata);
    }

    private bool DrawPreview(DrawingContext context, IImage preview, Rect destination, int rotation,
        ref HdrContentHint.Metadata? contentMetadata)
    {
        if (destination.Width <= 0 || destination.Height <= 0)
            return false;
        if (preview is PhotoFrameImage photo)
            return Draw(context, photo.Frame, photo.Cache, new Rect(Bounds.Size), destination, rotation, ref contentMetadata);

        Point center = destination.Center;
        if (rotation % 180 == 90)
            destination = new Rect(center.X - destination.Height / 2, center.Y - destination.Width / 2,
                destination.Height, destination.Width);

        using DrawingContext.PushedState transform = context.PushTransform(
            Matrix.CreateTranslation(-center.X, -center.Y) *
            Matrix.CreateRotation(rotation * Math.PI / 180) *
            Matrix.CreateTranslation(center.X, center.Y));
        context.DrawImage(preview, new Rect(preview.Size), destination);
        return false;
    }

    /// <summary>
    /// Where the picture is drawn once a drag pushing the page away has moved and shrunk it. Like
    /// the zoom, this changes the destination rather than transforming the composited output, so
    /// the shader still resamples once, straight from the decoded samples.
    /// </summary>
    private Rect PushedAway(Rect destination)
    {
        double scale = DismissScale;
        Vector pushed = DismissOffset;
        if (Math.Abs(scale - 1) < 1e-6 && pushed == default)
            return destination;

        Point center = Center;
        return new Rect(
            center.X + ((destination.X - center.X) * scale) + pushed.X,
            center.Y + ((destination.Y - center.Y) * scale) + pushed.Y,
            destination.Width * scale,
            destination.Height * scale);
    }

    /// <summary>
    /// A picture waiting off screen, drawn at the size it would open at: it has not been zoomed or
    /// turned, because it is not the one being looked at yet.
    /// </summary>
    private Size PreviewSize(IImage? preview, Size natural)
    {
        if (natural.Width > 0 && natural.Height > 0 || preview is null)
            return natural;
        Size size = preview.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return default;
        double scale = Math.Max(1, RenderScaling * Math.Max(Bounds.Width / size.Width, Bounds.Height / size.Height));
        return new Size(size.Width * scale, size.Height * scale);
    }

    private bool DrawNeighbor(DrawingContext context, FrameSlot slot, IImage? preview, Size natural,
        Rect bounds, int direction, double swipe, ref HdrContentHint.Metadata? contentMetadata)
    {
        Size size = slot.Frame is { } frame ? NaturalSize(frame, 0) : PreviewSize(preview, natural);
        PhotoViewport viewport = new(size, Bounds.Size, RenderScaling);
        Rect destination = viewport.Destination(viewport.MinimumZoom, default)
            .Translate(new Vector(swipe + (direction * (Bounds.Width + NeighborGap)), 0));
        bool hasHdrContent = false;
        if (slot.Frame?.HasAlpha != true &&
            (slot.Frame?.PhotoSurface is not null || slot.Frame?.PhotoTiles is not null) && preview is not null)
            hasHdrContent |= DrawPreview(context, preview, destination, 0, ref contentMetadata);
        if (slot.Frame is not null)
            hasHdrContent |= Draw(context, slot, bounds, destination, 0, ref contentMetadata);
        else if (preview is not null)
            hasHdrContent |= DrawPreview(context, preview, destination, 0, ref contentMetadata);
        return hasHdrContent;
    }

    private bool Draw(DrawingContext context, FrameSlot slot, Rect bounds, Rect destination, int rotation,
        ref HdrContentHint.Metadata? contentMetadata) =>
        slot.Frame is { } frame && slot.Cache is { } cache &&
        Draw(context, frame, cache, bounds, destination, rotation, ref contentMetadata);

    private bool Draw(DrawingContext context, VideoFrame frame, VideoFrameImageCache cache, Rect bounds, Rect destination, int rotation,
        ref HdrContentHint.Metadata? contentMetadata)
    {
        // A picture the swipe has carried off the side still costs a shader pass if it is handed
        // over, so one with nothing left inside the control is not drawn at all.
        if (frame is not { Width: > 0, Height: > 0 } ||
            !destination.Intersects(bounds))
        {
            return false;
        }

        cache.SetDisposalCompositor(ElementComposition.GetElementVisual(this)?.Compositor);
        bool hdrPassthrough = EnableHdrPassthrough && (frame.GainMap is null || EnableGainMap);

        // The operation takes over the reference and releases it once the render thread is done.
        context.Custom(new VideoDrawOperation(
            frame.AddRef(),
            bounds,
            destination,
            VideoDebandingQuality.Off,
            VideoScalingQuality.Bilinear,
            VideoScalingQuality.Bilinear,
            VideoScalingQuality.Bilinear,
            VideoShaderPackageCatalog.DisabledOverride,
            hdrPassthrough,
            DisplayPanelBitDepth,
            imageCache: cache.AddRef(),
            extraRotation: rotation,
            outputChanged: ReportOutput,
            hdrOutputChanged: ReportHdrOutput,
            compositor: ElementComposition.GetElementVisual(this)?.Compositor,
            toneMapping: frame is { GainMap: null, ColorTransfer: VideoColorTransfer.Pq } ? ToneMapping : null,
            toneMappingOutputChanged: ReferenceEquals(frame, current.Frame) &&
                frame is { GainMap: null, ColorTransfer: VideoColorTransfer.Pq } ? ReportAutomaticToneMapping : null));
        bool hasHdrContent = hdrPassthrough && HdrContentHint.IsHdr(frame);
        if (hasHdrContent)
            contentMetadata = HdrContentHint.Combine(contentMetadata, HdrContentHint.Describe(frame));
        return hasHdrContent;
    }

    private void ReportOutput(bool extended, double peakScale)
    {
        int value = extended ? 1 : 0;
        if (Interlocked.Exchange(ref reportedExtendedRange, value) == value)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (topLevel is not null)
                IsExtendedRangeOutput = extended;
        });
    }

    private double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private void ReportAutomaticToneMapping(VideoHdrOutputMapping output, VideoDisplayToneTarget display)
    {
        VideoDisplayToneTarget? previousDisplay;
        lock (displayToneTargetSync)
        {
            previousDisplay = reportedDisplayToneTarget;
            reportedDisplayToneTarget = display;
        }
        if (previousDisplay != display)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (topLevel is not null)
                    DisplayToneTarget = display;
            });
        }

        bool sdr = output.ToneMapPeakNits <= output.LinearNitsPerUnit;
        var algorithm = !output.AdaptsToDisplay ? VideoSdrToneMappingAlgorithm.Auto : sdr
            ? VideoSdrToneMappingAlgorithm.Spline : VideoSdrToneMappingAlgorithm.Bt2390;
        double headroom = output.ToneMapPeakNits / output.LinearNitsPerUnit;
        var previousOptions = Volatile.Read(ref reportedAutomaticToneMapping);
        if (previousOptions is not null && previousOptions.Algorithm == algorithm &&
            previousOptions.TargetHeadroom == headroom && previousOptions.ReferenceWhiteNits == output.LinearNitsPerUnit)
            return;
        var options = VideoSdrToneMappingOptions.Default with
        {
            Algorithm = algorithm,
            TargetHeadroom = headroom,
            ReferenceWhiteNits = output.LinearNitsPerUnit,
            Bt2390KneeOffset = 0.5f,
        };
        Interlocked.Exchange(ref reportedAutomaticToneMapping, options);
        Dispatcher.UIThread.Post(() =>
        {
            if (topLevel is not null)
                AutomaticToneMapping = options;
        });
    }

    private void ReportHdrOutput(bool supported)
    {
        int value = supported ? 1 : 0;
        if (Interlocked.Exchange(ref reportedHdrOutput, value) == value)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (topLevel is not null)
                SupportsHdrOutput = supported;
        });
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (current.Frame is null || e.Delta == default)
            return;

        bool touchpad = false;
    #if LMR_AVALONIA_FORK
        touchpad = e.IsTouchpad;
    #endif
        if (touchpad && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            SetOffset(offset + e.Delta * DipsPerWheelDelta);
        else if (e.Delta.Y != 0)
        {
            if (touchpad)
                ApplyZoom(zoom * Math.Pow(WheelZoomStep, e.Delta.Y), e.GetPosition(this));
            else
                AnimateZoom((zoomTarget ?? zoom) * Math.Pow(WheelZoomStep, e.Delta.Y), e.GetPosition(this));
        }
        else
            return;

        e.Handled = true;
    }

    private void OnTouchpadMagnify(object? sender, PointerDeltaEventArgs e)
    {
        if (current.Frame is null || !double.IsFinite(e.Delta.Y) || e.Delta.Y <= -1)
            return;

        ApplyZoom(zoom * (1 + e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (current.Frame is null)
            return;

        StopZoomAnimation();
        panSamples.Clear();
        doubleTapPending = false;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            SwipeOffset == 0 && DismissOffset == default)
        {
            tapPointer = e.Pointer;
            Size tapSize = Application.Current?.PlatformSettings?.GetTapSize(e.Pointer.Type) ?? new Size(4, 4);
            tapBounds = new Rect(e.GetPosition(this), default(Size))
                .Inflate(new Thickness(tapSize.Width, tapSize.Height));
        }
        else
        {
            CancelTap();
        }

        if (e.Pointer.Type is PointerType.Touch or PointerType.Pen)
        {
            Track(e.Pointer, e.GetPosition(this));

            // Two contacts move and magnify the picture together, so a drag already under way on
            // the first one is given up rather than left running underneath them.
            if (contacts.Count >= 2)
            {
                dragPointer = null;
                dragOrigin = null;
                BeginGesture();
                return;
            }
        }

        // Only a picture with somewhere to be moved to takes the pointer. A picture that fits the
        // viewport leaves it to the page, whose sideways swipe is what turns to the next one.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CanPan)
        {
            return;
        }

        dragOrigin = e.GetPosition(this);
        dragPointer = e.Pointer;
        dragStartOffset = offset;
        e.Pointer.Capture(this);
        if (e.Pointer.Type == PointerType.Touch)
            RecordPanSample(e.Timestamp);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (ReferenceEquals(tapPointer, e.Pointer) && !tapBounds.ContainsExclusive(e.GetPosition(this)))
            CancelTap();

        // Where each contact is now, whether or not it is yet doing anything: a second finger
        // arriving is measured against where the first has got to, not where it first landed.
        bool followed = Follow(e.Pointer, e.GetPosition(this));

        if (gesturing && followed)
        {
            UpdateGesture();
            e.Handled = true;
            return;
        }

        if (!ReferenceEquals(dragPointer, e.Pointer) ||
            dragOrigin is not { } origin ||
            current.Frame is null)
            return;

        SetOffset(dragStartOffset + (e.GetPosition(this) - origin));
        if (e.Pointer.Type == PointerType.Touch)
            RecordPanSample(e.Timestamp);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        bool tapped = ReferenceEquals(tapPointer, e.Pointer) &&
            tapBounds.ContainsExclusive(e.GetPosition(this)) && !gesturing;
        bool toggleZoom = tapped && doubleTapPending;
        Vector velocity = !tapped && !gesturing && ReferenceEquals(dragPointer, e.Pointer) &&
            e.Pointer.Type == PointerType.Touch ? PanReleaseVelocity(e.Timestamp) : default;
        CancelTap();

        if (!gesturing && ReferenceEquals(dragPointer, e.Pointer) && dragOrigin is not null)
            e.Pointer.Capture(null);

        EndContact(e.Pointer);
        if (dragPointer?.Type == PointerType.Touch)
            RecordPanSample(e.Timestamp);
        else if (contacts.Count == 0)
            StartPanInertia(velocity);
        previousWasTap = tapped && !toggleZoom;
        if (toggleZoom)
        {
            ToggleZoom(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (ReferenceEquals(tapPointer, e.Pointer))
            CancelTap();
        EndContact(e.Pointer);
    }

    /// <summary>
    /// Starts following a contact. A third finger is ignored: the first two already say everything
    /// a pinch needs, and dropping it keeps what is being followed from shifting mid-gesture.
    /// </summary>
    private void Track(IPointer pointer, Point position)
    {
        if (activeContacts.Add(pointer))
            hadMultipleContacts = activeContacts.Count > 1;

        if (Follow(pointer, position) || contacts.Count >= 2)
            return;

        contacts.Add((pointer, position));
    }

    /// <summary>Moves a contact already being followed; an unknown pointer is left alone.</summary>
    private bool Follow(IPointer pointer, Point position)
    {
        for (int i = 0; i < contacts.Count; i++)
        {
            if (ReferenceEquals(contacts[i].Pointer, pointer))
            {
                contacts[i] = (pointer, position);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes hold of the picture with both contacts, remembering the part of it they closed on and
    /// how far apart they were, so everything that follows is measured against the moment they met
    /// rather than compounded frame by frame.
    /// </summary>
    private void BeginGesture()
    {
        if (current.Frame is not { } frame)
            return;

        CancelTap();
        Point a = contacts[0].Position;
        Point b = contacts[1].Position;

        gestureStartZoom = zoom;
        gestureStartSpan = Math.Max(MinimumContactSpan, Distance(a, b));
        gestureAnchor = Viewport(frame).Anchor(zoom, offset, Midpoint(a, b));
        gesturing = true;

        // Taken from whoever held them -- the page's own swipe, most likely -- so both contacts are
        // reported here for as long as the gesture lasts.
        contacts[0].Pointer.Capture(this);
        contacts[1].Pointer.Capture(this);
    }

    /// <summary>
    /// Puts the part of the picture the fingers closed on back under their middle, at whatever
    /// magnification their distance now asks for. Moving both together carries the picture,
    /// spreading them magnifies it, and doing both at once does both.
    /// </summary>
    private void UpdateGesture()
    {
        if (current.Frame is not { } frame || contacts.Count < 2)
            return;

        Point a = contacts[0].Position;
        Point b = contacts[1].Position;
        double span = Math.Max(MinimumContactSpan, Distance(a, b));

        (double target, Vector moved) = Viewport(frame)
            .ZoomToAnchor(gestureStartZoom * (span / gestureStartSpan), gestureAnchor, Midpoint(a, b));

        zoom = target;
        offset = moved;
        UpdateCursor();
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Takes a contact away. A pinch left with one finger hands the picture to it rather than
    /// ending, so a picture larger than the viewport does not stop dead with a hand still on it.
    /// </summary>
    private void EndContact(IPointer pointer)
    {
        activeContacts.Remove(pointer);
        if (ReferenceEquals(dragPointer, pointer) || gesturing)
            panSamples.Clear();
        bool removed = false;
        for (int i = 0; i < contacts.Count; i++)
        {
            if (ReferenceEquals(contacts[i].Pointer, pointer))
            {
                contacts.RemoveAt(i);
                removed = true;
                break;
            }
        }

        if (gesturing)
        {
            if (!removed || contacts.Count >= 2)
                return;

            gesturing = false;

            if (contacts.Count == 1 && CanPan)
            {
                dragPointer = contacts[0].Pointer;
                dragOrigin = contacts[0].Position;
                dragStartOffset = offset;
                contacts[0].Pointer.Capture(this);
            }
            else
            {
                dragPointer = null;
                dragOrigin = null;
            }

            return;
        }

        if (ReferenceEquals(dragPointer, pointer))
        {
            dragPointer = null;
            dragOrigin = null;
        }
    }

    private void RecordPanSample(ulong timestamp)
    {
        if (panSamples.Count > 0 && timestamp <= panSamples[^1].Timestamp)
        {
            if (timestamp == panSamples[^1].Timestamp)
            {
                panSamples[^1] = (timestamp, offset);
                return;
            }
            panSamples.Clear();
        }

        while (panSamples.Count > 0 && timestamp - panSamples[0].Timestamp > PanSampleWindowMs)
            panSamples.RemoveAt(0);
        panSamples.Add((timestamp, offset));
    }

    private Vector PanReleaseVelocity(ulong timestamp)
    {
        if (hadMultipleContacts || panSamples.Count < 2 || timestamp == 0 || timestamp < panSamples[^1].Timestamp ||
            timestamp - panSamples[^1].Timestamp > PanReleaseTimeoutMs)
            return default;

        var first = panSamples[0];
        var last = panSamples[^1];
        Vector velocity = (last.Offset - first.Offset) * (1000d / (last.Timestamp - first.Timestamp));
        double speed = velocity.Length;
        return speed >= MinimumPanSpeed ? velocity * (Math.Min(speed, MaximumPanSpeed) / speed) : default;
    }

    private void StartPanInertia(Vector velocity)
    {
        if (velocity == default || topLevel is null || !IsEffectivelyVisible || Opacity <= 0 || !CanPan)
            return;

        panInertiaOrigin = offset;
        panInertiaVelocity = velocity;
        panInertiaClock.Restart();
        int generation = ++panInertiaGeneration;
        topLevel.RequestAnimationFrame(_ => AdvancePanInertia(generation));
    }

    private void AdvancePanInertia(int generation)
    {
        if (generation != panInertiaGeneration)
            return;
        if (current.Frame is not { } frame || topLevel is null || !IsEffectivelyVisible || Opacity <= 0)
        {
            StopPanInertia();
            return;
        }

        double decay = Math.Pow(ScrollGestureRecognizer.InertialResistance, panInertiaClock.Elapsed.TotalSeconds);
        Vector target = Viewport(frame).Clamp(panInertiaOrigin + panInertiaVelocity *
            ((decay - 1) / Math.Log(ScrollGestureRecognizer.InertialResistance)), zoom);
        Vector speed = new(target.X == offset.X ? 0 : panInertiaVelocity.X * decay,
            target.Y == offset.Y ? 0 : panInertiaVelocity.Y * decay);
        offset = target;
        InvalidateVisual();

        if (Math.Abs(speed.X) <= PanStoppingSpeed && Math.Abs(speed.Y) <= PanStoppingSpeed)
            StopPanInertia();
        else
            topLevel.RequestAnimationFrame(_ => AdvancePanInertia(generation));
    }

    private void StopPanInertia()
    {
        panInertiaGeneration++;
        panInertiaClock.Reset();
    }

    private static double Distance(Point a, Point b) => ((Vector)(b - a)).Length;

    private static Point Midpoint(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        doubleTapPending = previousWasTap && tapPointer is not null && !gesturing;
        e.Handled = true;
    }

    private void CancelTap()
    {
        tapPointer = null;
        previousWasTap = false;
        doubleTapPending = false;
    }

    /// <summary>Alternates between filling the viewport and one image pixel per display pixel.</summary>
    private void ToggleZoom(Point focus)
    {
        if (current.Frame is not { } frame)
            return;

        double target = IsFitToWindow ? Viewport(frame).ActualSizeZoom : 1;
        AnimateZoom(target, focus);
    }

    private void AnimateZoom(double requested, Point focus)
    {
        if (current.Frame is not { } frame)
            return;

        double target = Math.Clamp(requested, Viewport(frame).MinimumZoom, PhotoViewport.MaximumZoom);
        if (zoomTarget is { } pending && Math.Abs(target - pending) < 1e-6)
            return;

        StopZoomAnimation();
        if (topLevel is null || !IsEffectivelyVisible)
        {
            ApplyZoom(target, focus);
            return;
        }

        if (Math.Abs(target - zoom) < 1e-6)
            return;

        zoomTarget = target;
        zoomAnchor = Viewport(frame).Anchor(zoom, offset, focus);
        zoomFocus = focus;
        zoomGlide.Start(zoom, target, ZoomAnimationMs);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyAnimatedZoom(double value)
    {
        if (current.Frame is not { } frame || !IsEffectivelyVisible)
        {
            StopZoomAnimation();
            return;
        }

        (zoom, offset) = Viewport(frame).ZoomToAnchor(value, zoomAnchor, zoomFocus);
        UpdateCursor();
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyAnimatedPan(double progress)
    {
        if (current.Frame is not { } frame || !IsEffectivelyVisible)
        {
            StopZoomAnimation();
            return;
        }

        offset = Viewport(frame).Clamp(panStartOffset + ((panTargetOffset - panStartOffset) * progress), zoom);
        UpdateCursor();
        InvalidateVisual();
    }

    private void StopZoomAnimation()
    {
        StopPanInertia();
        bool wasAnimating = zoomTarget is not null;
        zoomGlide.Stop();
        panGlide.Stop();
        previewGlide.Stop();
        if (previewBounds is not null)
        {
            previewBounds = null;
            InvalidateVisual();
        }
        zoomTarget = null;
        if (wasAnimating)
            ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && !IsEffectivelyVisible ||
            e.Property == OpacityProperty && sender is Visual { Opacity: <= 0 })
            StopZoomAnimation();
        if (e.Property == IsVisibleProperty || e.Property == OpacityProperty || e.Property == Window.WindowStateProperty)
            UpdatePhotoAnimation();
    }

    /// <summary>Zooms so that whatever sits under <paramref name="focus"/> stays there.</summary>
    private void ApplyZoom(double requested, Point focus)
    {
        StopZoomAnimation();
        if (current.Frame is not { } frame)
            return;

        (double target, Vector moved) = Viewport(frame).ZoomAbout(zoom, offset, requested, focus);
        if (Math.Abs(target - zoom) < 1e-6)
            return;

        zoom = target;
        offset = moved;
        UpdateCursor();
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetOffset(Vector value)
    {
        StopZoomAnimation();
        if (current.Frame is { } frame)
        {
            PhotoViewport viewport = Viewport(frame);
            zoom = Math.Clamp(zoom, viewport.MinimumZoom, PhotoViewport.MaximumZoom);
            offset = viewport.Clamp(value, zoom);
        }
        else
        {
            offset = default;
        }
        UpdateCursor();
        InvalidateVisual();
    }

    private PhotoViewport Viewport(VideoFrame frame) => new(NaturalSize(frame, Rotation), Bounds.Size, RenderScaling);

    /// <summary>Size the picture is meant to be seen at, with pixel aspect and rotation applied.</summary>
    private static Size NaturalSize(VideoFrame frame, int rotation)
    {
        double width = frame.Width * (frame.PixelAspectRatio > 0 ? frame.PixelAspectRatio : 1.0);
        return (frame.Rotation + rotation) % 180 == 90
            ? new Size(frame.Height, width)
            : new Size(width, frame.Height);
    }

    private void UpdateCursor() => Cursor = CanPan ? GrabCursor : null;

    private void OnSourceChanged(AvaloniaPropertyChangedEventArgs e)
    {
        StopPhotoAnimation();
        Rect? previous = current.Frame is null && PreviewSource is not null && SourceKey is not null && !initialZoomPending
            ? previewBounds ?? PreviewDestination(PreviewNaturalSize) : null;
        current.Set(e.NewValue as VideoFrame);
        current.ShareCache(this.previous);
        current.ShareCache(next);
        if (SourceKey is null)
            ResetViewport();
        else
        {
            ApplyInitialZoomIfReady();
            if (previous is { } rectangle && current.Frame is { } frame)
                AnimatePreviewSize(rectangle, Viewport(frame).Destination(zoom, offset));
            UpdateCursor();
            InvalidateVisual();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }
        UpdatePhotoAnimation();
    }

    private Rect PreviewDestination(Size natural)
    {
        natural = PreviewSize(PreviewSource, natural);
        if (Rotation % 180 == 90)
            natural = new Size(natural.Height, natural.Width);
        return new PhotoViewport(natural, Bounds.Size, RenderScaling).Destination(zoom, offset);
    }

    private void OnPreviewSizeChanged(AvaloniaPropertyChangedEventArgs args)
    {
        Rect? from = current.Frame is null && PreviewSource is not null &&
            PreviewNaturalSize is { Width: > 0, Height: > 0 }
                ? previewBounds ?? PreviewDestination((Size)args.OldValue!)
                : null;
        bool initialZoomApplied = ApplyInitialZoomIfReady();
        if (!initialZoomApplied && from is { } start)
            AnimatePreviewSize(start, PreviewDestination(PreviewNaturalSize));
        if (initialZoomApplied)
        {
            UpdateCursor();
            InvalidateVisual();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPreviewSourceChanged(AvaloniaPropertyChangedEventArgs args)
    {
        if (VisualRoot is null)
            return;
        if (args.OldValue is PhotoFrameImage previousImage)
            previousImage.Cache.Invalidated -= RequestImageRender;
        if (args.NewValue is PhotoFrameImage nextImage)
            nextImage.Cache.Invalidated += RequestImageRender;
    }

    private void AnimatePreviewSize(Rect from, Rect to)
    {
        if (VisualRoot is null || !IsEffectivelyVisible || Opacity <= 0 || TransitionBounds is not null ||
            from.Width <= 0 || from.Height <= 0 || to.Width <= 0 || to.Height <= 0 || from == to ||
            previewBounds is not null && previewEnd == to)
            return;
        previewStart = from;
        previewEnd = to;
        previewBounds = from;
        previewGlide.Start(0, 1, ZoomAnimationMs);
        InvalidateVisual();
    }

    private void ResetViewport()
    {
        StopZoomAnimation();
        zoom = 1;
        initialZoomPending = true;
        offset = default;
        gesturing = false;
        contacts.Clear();
        activeContacts.Clear();
        dragPointer = null;
        dragOrigin = null;
        panSamples.Clear();
        CancelTap();
        ApplyInitialZoomIfReady();
        UpdateCursor();
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool ApplyInitialZoomIfReady()
    {
        if (!initialZoomPending)
            return false;

        Size natural;
        if (current.Frame is { } frame)
            natural = NaturalSize(frame, Rotation);
        else if (PreviewNaturalSize is { Width: > 0, Height: > 0 } previewNaturalSize)
            natural = Rotation % 180 == 90
                ? new Size(previewNaturalSize.Height, previewNaturalSize.Width)
                : previewNaturalSize;
        else
            return false;

        PhotoViewport viewport = new(natural, Bounds.Size, RenderScaling);
        if (viewport.IsEmpty)
            return false;

        zoom = viewport.MinimumZoom;
        initialZoomPending = false;
        return true;
    }

    private void OnNeighborChanged(FrameSlot slot, AvaloniaPropertyChangedEventArgs e)
    {
        slot.Set(e.NewValue as VideoFrame);
        InvalidateVisual();
    }

    /// <summary>
    /// A turn swaps which way the picture is long, so where it was scrolled to no longer names
    /// anywhere; the magnification is kept, since the point of turning is to look at the same thing.
    /// </summary>
    private void OnRotationChanged()
    {
        SetOffset(default);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // The clamp is measured against the viewport, so a resize can leave the picture off center.
        SetOffset(offset);
        UpdatePhotoAnimation();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        RenderThreadScheduler.Detach(this);

        StopPhotoAnimation();
        StopZoomAnimation();
        foreach (Visual ancestor in visibilityAncestors)
            ancestor.PropertyChanged -= OnVisibilityChanged;
        visibilityAncestors.Clear();
        if (topLevel is not null)
            topLevel.ScalingChanged -= OnScalingChanged;
        topLevel = null;
        Interlocked.Exchange(ref reportedExtendedRange, 0);
        IsExtendedRangeOutput = false;
        Interlocked.Exchange(ref reportedHdrOutput, 0);
        SupportsHdrOutput = false;
        Interlocked.Exchange(ref reportedAutomaticToneMapping, null);
        lock (displayToneTargetSync)
            reportedDisplayToneTarget = null;

        gesturing = false;
        contacts.Clear();
        activeContacts.Clear();
        dragPointer = null;
        dragOrigin = null;
        panSamples.Clear();
        CancelTap();

        // A draw already in flight holds its own reference, so this only drops the control's.
        current.Set(null);
        previous.Set(null);
        next.Set(null);
        foreach (IImage? preview in new[] { PreviewSource, PreviousPreviewSource, NextPreviewSource })
            if (preview is PhotoFrameImage photo)
                photo.Cache.Invalidated -= RequestImageRender;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RenderThreadScheduler.Attach(this);
        Interlocked.Exchange(ref reportedExtendedRange, -1);
        Interlocked.Exchange(ref reportedHdrOutput, -1);

        foreach (Visual ancestor in this.GetVisualAncestors())
        {
            ancestor.PropertyChanged += OnVisibilityChanged;
            visibilityAncestors.Add(ancestor);
        }

        topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
            topLevel.ScalingChanged += OnScalingChanged;

        current.Set(Source);
        previous.Set(PreviousSource);
        next.Set(NextSource);
        UpdatePhotoAnimation();
        foreach (IImage? preview in new[] { PreviewSource, PreviousPreviewSource, NextPreviewSource })
            if (preview is PhotoFrameImage photo)
                photo.Cache.Invalidated += RequestImageRender;
        ViewportChanged?.Invoke(this, EventArgs.Empty);

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        SetOffset(offset);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One picture the control is holding, with the image cache that lets its scene node be reused
    /// between draws. Kept together because the two are only ever replaced as a pair.
    /// </summary>
    private void RequestImageRender() => Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);

    private void OnImageFailed(VideoFrame frame, Exception exception) => Dispatcher.UIThread.Post(() =>
    {
        if (ReferenceEquals(current.Frame, frame))
            ImageFailed?.Invoke(exception);
    });

    private sealed class FrameSlot(Action invalidate, Action<VideoFrame, Exception> failed)
    {
        public VideoFrame? Frame { get; private set; }

        public VideoFrameImageCache? Cache { get; private set; }

        public void ShareCache(FrameSlot source)
        {
            if (!ReferenceEquals(Frame, source.Frame) || ReferenceEquals(Cache, source.Cache) || source.Cache is null)
                return;
            VideoFrameImageCache? previous = Cache;
            Cache = source.Cache.AddRef();
            Cache.Invalidated += invalidate;
            Cache.Failed += failed;
            if (previous is not null)
            {
                previous.Invalidated -= invalidate;
                previous.Failed -= failed;
            }
            previous?.Release();
        }

        public void Set(VideoFrame? frame)
        {
            if (ReferenceEquals(frame, Frame))
                return;

            VideoFrame? outgoing = Frame;
            VideoFrameImageCache? outgoingCache = Cache;
            Frame = frame?.AddRef();
            Cache = Frame is null ? null : new VideoFrameImageCache(Frame);
            if (Cache is not null)
            {
                Cache.Invalidated += invalidate;
                Cache.Failed += failed;
            }
            if (outgoingCache is not null)
            {
                outgoingCache.Invalidated -= invalidate;
                outgoingCache.Failed -= failed;
            }
            outgoingCache?.Release();
            outgoing?.Release();
        }
    }
}
