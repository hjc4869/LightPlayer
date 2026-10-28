using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightMediaRenderer.Avalonia.Rendering;
using LightMediaRenderer.Contracts;
using LightStudio.Logging;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Controls;

public sealed class BackdropBorder : Decorator
{
    private static readonly ConditionalWeakTable<TopLevel, HashSet<BackdropBorder>> backdropsByRoot = new();
    private readonly ForegroundState foregroundState = new();
    private readonly ConditionalWeakTable<Control, Dictionary<Color, IBrush>> foregrounds = new();
    private TopLevel? backdropRoot;
    private ScaleTransform? pressScale;
    private ITransform? restingTransform;
    private TransformGroup? pressTransform;
    private TopLevel? pressRoot;
    private IPointer? pressedPointer;

    public static readonly AttachedProperty<BackdropBorder?> ForegroundSourceProperty =
        AvaloniaProperty.RegisterAttached<BackdropBorder, Control, BackdropBorder?>("ForegroundSource", inherits: true);

    public static BackdropBorder? GetForegroundSource(Control control) => control.GetValue(ForegroundSourceProperty);
    public static void SetForegroundSource(Control control, BackdropBorder? value) => control.SetValue(ForegroundSourceProperty, value);

    public IBrush Foreground { get; }

    public BackdropBorder()
    {
        SetForegroundSource(this, this);
        Foreground = CreateForeground(Brushes.White, this);
        AddHandler(PointerPressedEvent, OnGlassPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        DetachedFromVisualTree += (_, _) => ResetPress();
    }

    internal IBrush CreateForeground(IBrush original, Control target)
    {
        if (original is not ISolidColorBrush solid)
            return original;
        Color color = solid.Color;
        color = Color.FromArgb((byte)Math.Clamp(color.A * original.Opacity, 0, 255), color.R, color.G, color.B);
        Dictionary<Color, IBrush> targetBrushes = foregrounds.GetOrCreateValue(target);
        if (targetBrushes.TryGetValue(color, out IBrush? existing))
            return existing;
        var visual = new ForegroundVisual(foregroundState, color, target);
        targetBrushes.Add(color, visual.Brush);
        return visual.Brush;
    }

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        Border.CornerRadiusProperty.AddOwner<BackdropBorder>();

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        Border.BorderBrushProperty.AddOwner<BackdropBorder>();

    public static readonly StyledProperty<BoxShadows> BoxShadowProperty =
        Border.BoxShadowProperty.AddOwner<BackdropBorder>();

    public static readonly StyledProperty<BackdropStyle> BackdropStyleProperty =
        AvaloniaProperty.Register<BackdropBorder, BackdropStyle>(nameof(BackdropStyle));

    public static readonly StyledProperty<bool> IsForegroundBoostEnabledProperty =
        AvaloniaProperty.Register<BackdropBorder, bool>(nameof(IsForegroundBoostEnabled));

    public static readonly StyledProperty<bool> IsPressAnimationEnabledProperty =
        AvaloniaProperty.Register<BackdropBorder, bool>(nameof(IsPressAnimationEnabled));

    public static readonly StyledProperty<Color> FallbackColorProperty =
        AvaloniaProperty.Register<BackdropBorder, Color>(nameof(FallbackColor), Color.Parse("#E6222222"));

    public static readonly StyledProperty<Color> TintColorProperty =
        AvaloniaProperty.Register<BackdropBorder, Color>(nameof(TintColor), Color.Parse("#B0222222"));

    public static readonly StyledProperty<double> GlassTintStrengthProperty =
        AvaloniaProperty.Register<BackdropBorder, double>(nameof(GlassTintStrength),
            coerce: static (_, value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0);

    public static readonly StyledProperty<double> GlassFrostProperty =
        AvaloniaProperty.Register<BackdropBorder, double>(nameof(GlassFrost),
            coerce: static (_, value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0);

    public static readonly StyledProperty<Thickness> LightSpillProperty =
        AvaloniaProperty.Register<BackdropBorder, Thickness>(nameof(LightSpill),
            coerce: static (_, value) => new Thickness(
                CoerceSpill(value.Left), CoerceSpill(value.Top), CoerceSpill(value.Right), CoerceSpill(value.Bottom)));

    static BackdropBorder()
    {
        AffectsRender<BackdropBorder>(CornerRadiusProperty, BorderBrushProperty, BoxShadowProperty, BackdropStyleProperty,
            IsForegroundBoostEnabledProperty, FallbackColorProperty, TintColorProperty, GlassTintStrengthProperty,
            GlassFrostProperty, LightSpillProperty);
    }

    private static double CoerceSpill(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 256) : 0;

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    /// <summary>
    /// Shadows cast around the material. When set, they replace the default liquid glass shadow.
    /// </summary>
    public BoxShadows BoxShadow
    {
        get => GetValue(BoxShadowProperty);
        set => SetValue(BoxShadowProperty, value);
    }

    public BackdropStyle BackdropStyle
    {
        get => GetValue(BackdropStyleProperty);
        set => SetValue(BackdropStyleProperty, value);
    }

    public bool IsForegroundBoostEnabled
    {
        get => GetValue(IsForegroundBoostEnabledProperty);
        set => SetValue(IsForegroundBoostEnabledProperty, value);
    }

    public bool IsPressAnimationEnabled
    {
        get => GetValue(IsPressAnimationEnabledProperty);
        set => SetValue(IsPressAnimationEnabledProperty, value);
    }

    public Color FallbackColor
    {
        get => GetValue(FallbackColorProperty);
        set => SetValue(FallbackColorProperty, value);
    }

    public Color TintColor
    {
        get => GetValue(TintColorProperty);
        set => SetValue(TintColorProperty, value);
    }

    public double GlassTintStrength
    {
        get => GetValue(GlassTintStrengthProperty);
        set => SetValue(GlassTintStrengthProperty, value);
    }

    /// <summary>
    /// How frosted <see cref="BackdropStyle.LiquidGlass"/> appears, from 0 for clear glass to 1 for a
    /// heavily diffused, milky material.
    /// </summary>
    public double GlassFrost
    {
        get => GetValue(GlassFrostProperty);
        set => SetValue(GlassFrostProperty, value);
    }

    /// <summary>
    /// How far beyond each edge, in DIPs, the blur material gathers the colors of nearby content and lets
    /// them spill inward from that edge. Applies to <see cref="BackdropStyle.Blur"/>.
    /// </summary>
    public Thickness LightSpill
    {
        get => GetValue(LightSpillProperty);
        set => SetValue(LightSpillProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == BackdropStyleProperty ||
            change.Property == IsForegroundBoostEnabledProperty)
            foregroundState.Enabled = IsVisible && BackdropStyle != BackdropStyle.Off && IsForegroundBoostEnabled;
        if (change.Property == IsVisibleProperty || change.Property == BackdropStyleProperty ||
            change.Property == IsPressAnimationEnabledProperty)
        {
            if (!IsVisible || BackdropStyle != BackdropStyle.LiquidGlass || !IsPressAnimationEnabled)
                ResetPress();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        if (TopLevel.GetTopLevel(this) is not { } root)
            return;
        backdropRoot = root;
        root.PropertyChanged += OnBackdropRootPropertyChanged;
        root.ScalingChanged += OnBackdropRootScalingChanged;
        backdropsByRoot.GetValue(root, static _ => []).Add(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        if (backdropRoot is { } root)
        {
            root.PropertyChanged -= OnBackdropRootPropertyChanged;
            root.ScalingChanged -= OnBackdropRootScalingChanged;
            if (backdropsByRoot.TryGetValue(root, out HashSet<BackdropBorder>? backdrops))
                backdrops.Remove(this);
        }
        backdropRoot = null;
        base.OnDetachedFromVisualTree(args);
    }

    private void OnBackdropRootPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == TopLevel.ClientSizeProperty)
            InvalidateVisual();
    }

    private void OnBackdropRootScalingChanged(object? sender, EventArgs args) => InvalidateVisual();

    private static void InvalidateBackdrops(TopLevel root)
    {
        if (!backdropsByRoot.TryGetValue(root, out HashSet<BackdropBorder>? backdrops))
            return;
        foreach (BackdropBorder backdrop in backdrops)
        {
            if (backdrop.IsEffectivelyVisible)
                backdrop.InvalidateVisual();
        }
    }

    private void OnGlassPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!IsPressAnimationEnabled || BackdropStyle != BackdropStyle.LiquidGlass ||
            !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed || pressedPointer is not null ||
            TopLevel.GetTopLevel(this) is not { } root)
            return;

        if (pressScale is null)
        {
            pressScale = new ScaleTransform
            {
                Transitions =
                [
                    new DoubleTransition
                    {
                        Property = ScaleTransform.ScaleXProperty,
                        Duration = TimeSpan.FromMilliseconds(160),
                        Easing = new CubicEaseOut(),
                    },
                    new DoubleTransition
                    {
                        Property = ScaleTransform.ScaleYProperty,
                        Duration = TimeSpan.FromMilliseconds(160),
                        Easing = new CubicEaseOut(),
                    },
                ],
            };
        }

        if (RenderTransform != pressTransform || pressTransform is null)
        {
            restingTransform = RenderTransform;
            pressTransform = new TransformGroup();
            if (restingTransform is not null)
                pressTransform.Children.Add(restingTransform as Transform ?? new MatrixTransform(restingTransform.Value));
            pressTransform.Children.Add(pressScale);
            RenderTransform = pressTransform;
        }
        pressedPointer = args.Pointer;
        pressRoot = root;
        root.AddHandler(PointerReleasedEvent, OnGlassPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(PointerCaptureLostEvent, OnGlassPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        if (root is Window window)
            window.Deactivated += OnGlassWindowDeactivated;
        pressScale.ScaleX = 1.04;
        pressScale.ScaleY = 1.04;
    }

    private void OnGlassPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (args.Pointer == pressedPointer && args.InitialPressMouseButton == MouseButton.Left)
            ResetPress();
    }

    private void OnGlassPointerCaptureLost(object? sender, PointerCaptureLostEventArgs args)
    {
        if (args.Pointer == pressedPointer)
            ResetPress();
    }

    private void OnGlassWindowDeactivated(object? sender, EventArgs args) => ResetPress();

    private void ResetPress()
    {
        if (pressRoot is { } root)
        {
            root.RemoveHandler(PointerReleasedEvent, OnGlassPointerReleased);
            root.RemoveHandler(PointerCaptureLostEvent, OnGlassPointerCaptureLost);
            if (root is Window window)
                window.Deactivated -= OnGlassWindowDeactivated;
        }
        pressRoot = null;
        pressedPointer = null;
        if (pressScale is not null)
        {
            pressScale.ScaleX = 1;
            pressScale.ScaleY = 1;
        }
        if (!this.IsAttachedToVisualTree() || !IsVisible || !IsPressAnimationEnabled || BackdropStyle != BackdropStyle.LiquidGlass)
        {
            if (RenderTransform == pressTransform)
                RenderTransform = restingTransform;
            pressTransform = null;
            restingTransform = null;
            pressScale = null;
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width > 0 && Bounds.Height > 0)
        {
            TopLevel? root = TopLevel.GetTopLevel(this);
            var shape = new RoundedRect(new Rect(Bounds.Size), CornerRadius);
            BoxShadows shadows = BoxShadow;
            RoundedClip[] roundedClips = root is null ? [] : GetRoundedClips(root.RenderScaling);
            context.Custom(new BackdropDrawOperation(new Rect(Bounds.Size), CornerRadius,
                BackdropStyle, IsForegroundBoostEnabled, FallbackColor, TintColor, GlassTintStrength, GlassFrost, LightSpill,
                shadows.Count == 0, foregroundState, root is null ? default : root.ClientSize * root.RenderScaling,
                roundedClips,
                root is null ? null : () =>
                {
                    if (IsEffectivelyVisible)
                        InvalidateBackdrops(root);
                },
                root is null ? null : () =>
                {
                    if (IsEffectivelyVisible && TopLevel.GetTopLevel(this) == root &&
                        !GetRoundedClips(root.RenderScaling).AsSpan().SequenceEqual(roundedClips))
                        InvalidateVisual();
                }));
            // Drawn after the material so the backdrop it samples excludes its own shadow.
            if (shadows.Count > 0)
                context.DrawRectangle(null, null, shape, shadows);
            if (BorderBrush is { } borderBrush)
            {
                var outline = shape.Deflate(0.5, 0.5);
                context.DrawRectangle(null, new Pen(borderBrush), outline);
            }
        }

        base.Render(context);
    }

    // Ancestor borders that clip to rounded corners, in device pixels of the visual root.
    private RoundedClip[] GetRoundedClips(double scaling)
    {
        Visual root = this;
        while (root.GetVisualParent() is { } parent)
            root = parent;

        List<RoundedClip>? clips = null;
        for (Visual? ancestor = this.GetVisualParent(); ancestor is not null; ancestor = ancestor.GetVisualParent())
        {
            if (ancestor is not Border { ClipToBounds: true } border || border.CornerRadius == default ||
                border.TransformToVisual(root) is not { } transform || transform.ContainsPerspective() ||
                transform.M12 != 0 || transform.M21 != 0 || transform.M11 <= 0 || transform.M22 <= 0)
                continue;

            Rect outline = new Rect(border.Bounds.Size).TransformToAABB(transform * Matrix.CreateScale(scaling, scaling));
            CornerRadius radius = border.CornerRadius;
            float scaleX = (float)(transform.M11 * scaling);
            float scaleY = (float)(transform.M22 * scaling);
            (clips ??= []).Add(new RoundedClip(
                new SKRect((float)outline.Left, (float)outline.Top, (float)outline.Right, (float)outline.Bottom),
                new SKPoint((float)radius.TopLeft * scaleX, (float)radius.TopLeft * scaleY),
                new SKPoint((float)radius.TopRight * scaleX, (float)radius.TopRight * scaleY),
                new SKPoint((float)radius.BottomRight * scaleX, (float)radius.BottomRight * scaleY),
                new SKPoint((float)radius.BottomLeft * scaleX, (float)radius.BottomLeft * scaleY)));
        }

        return clips?.ToArray() ?? [];
    }

    private readonly record struct RoundedClip(SKRect Outline, SKPoint TopLeft, SKPoint TopRight,
        SKPoint BottomRight, SKPoint BottomLeft);

    private sealed class ForegroundState
    {
        public BackdropDrawOperation? Backdrop;
        public volatile bool Enabled;
        public SKRect ReachableBounds;
        public SKRectI ReachableSurface;
        public SKRectI? ReachableInterior;
        public SKRectI ReachableClip;
    }

    private sealed class ForegroundVisual : Control
    {
        private readonly ForegroundState state;
        private readonly Color color;
        private readonly Control target;

        public VisualBrush Brush { get; }

        public ForegroundVisual(ForegroundState state, Color color, Control target)
        {
            this.state = state;
            this.color = color;
            this.target = target;
            Brush = new VisualBrush(this) { Stretch = Stretch.None, TileMode = TileMode.Tile };
            target.AttachedToVisualTree += OnAttached;
            target.DetachedFromVisualTree += OnDetached;
            if (target.IsAttachedToVisualTree())
                target.LayoutUpdated += OnLayoutUpdated;
            UpdateSize();
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs args)
        {
            target.LayoutUpdated += OnLayoutUpdated;
            UpdateSize();
        }

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs args) =>
            target.LayoutUpdated -= OnLayoutUpdated;

        private void OnLayoutUpdated(object? sender, EventArgs args) => UpdateSize();

        private void UpdateSize()
        {
            Size size = target.Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0)
                size = new Size(1, 1);
            if (Bounds.Size == size)
                return;
            Width = size.Width;
            Height = size.Height;
            Measure(size);
            Arrange(new Rect(size));
            Brush.SourceRect = new RelativeRect(size, RelativeUnit.Absolute);
            Brush.DestinationRect = new RelativeRect(size, RelativeUnit.Absolute);
        }

        public override void Render(DrawingContext context) =>
            context.Custom(new ForegroundOperation(state, color, new Rect(Bounds.Size)));
    }

    private sealed class ForegroundOperation(ForegroundState state, Color color, Rect bounds) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is ISkiaSharpApiLeaseFeature feature)
            {
                using ISkiaSharpApiLease lease = feature.Lease();
                if (state.Enabled && state.Backdrop?.DrawForeground(lease, color, bounds) == true)
                    return;
            }

            context.DrawRectangle(new ImmutableSolidColorBrush(color), null, Bounds);
        }
    }

    private sealed class BackdropDrawOperation(
        Rect bounds,
        CornerRadius cornerRadius,
        BackdropStyle style,
        bool isForegroundBoostEnabled,
        Color fallbackColor,
        Color tintColor,
        double glassTintStrength,
        double glassFrost,
        Thickness lightSpill,
        bool hasGlassShadow,
        ForegroundState foregroundState,
        Size surfacePixelSize,
        RoundedClip[] roundedClips,
        Action? requestFullRepaint,
        Action? requestClipRefresh) : ICustomDrawOperation
    {
        private const int SamplePadding = 64;
        private static readonly Lazy<SKRuntimeEffect?> GlassEffect = new(CreateGlassEffect);
        private static readonly Lazy<SKRuntimeEffect?> SpillEffect = new(CreateSpillEffect);
        private static readonly Lazy<SKRuntimeEffect?> OpacityEffect = new(CreateOpacityEffect);
        private static readonly Lazy<SKRuntimeEffect?> ForegroundEffect = new(CreateForegroundEffect);
        private GRContext? foregroundContext;
        private SKCanvas? backdropCanvas;
        private SKShader? foregroundBackdrop;
        private float foregroundPeak = 1;
        private bool failed;
        private SKImage? cleanBackdrop;
        private SKRectI cleanCapture;
        private SKRect cleanBounds;
        private bool repaintQueued;
        private volatile bool disposed;

        public Rect Bounds => bounds.Inflate(lightSpill).Inflate(SamplePadding);

        public bool HitTest(Point point) => bounds.Contains(point);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
            disposed = true;
            if (foregroundState.Backdrop == this)
                foregroundState.Backdrop = null;
            backdropCanvas = null;
            foregroundBackdrop?.Dispose();
            foregroundBackdrop = null;
            cleanBackdrop?.Dispose();
            cleanBackdrop = null;
        }

        public void Render(ImmediateDrawingContext context)
        {
            foregroundState.Backdrop = null;
            foregroundBackdrop?.Dispose();
            foregroundBackdrop = null;
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
            {
                using (context.PushClip(new RoundedRect(bounds, cornerRadius)))
                    context.DrawRectangle(new ImmutableSolidColorBrush(fallbackColor), null, bounds);
                return;
            }

            using ISkiaSharpApiLease lease = feature.Lease();
            SKCanvas canvas = lease.SkCanvas;
            var rectangle = new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height);
            using var rounded = new SKRoundRect();
            rounded.SetRectRadii(rectangle,
            [
                new SKPoint((float)cornerRadius.TopLeft, (float)cornerRadius.TopLeft),
                new SKPoint((float)cornerRadius.TopRight, (float)cornerRadius.TopRight),
                new SKPoint((float)cornerRadius.BottomRight, (float)cornerRadius.BottomRight),
                new SKPoint((float)cornerRadius.BottomLeft, (float)cornerRadius.BottomLeft),
            ]);

            using var paint = new SKPaint { IsAntialias = true };
            BackdropStyle resolved = style == BackdropStyle.LiquidGlassContentOnly
                ? BackdropStyle.Blur
                : style;

            if (!failed && resolved is BackdropStyle.Blur or BackdropStyle.LiquidGlass && lease.SkSurface is { } surface)
            {
                try
                {
                    if (DrawBackdrop(lease, surface, rounded, paint, resolved))
                        return;
                }
                catch (Exception exception)
                {
                    failed = true;
                    AppLog.Warning("Appearance.Backdrop", "Could not render the backdrop material.", exception);
                }
            }

            foregroundState.Backdrop = null;
            paint.Shader = null;
            paint.ImageFilter = null;
            paint.ColorFilter = null;
            paint.Color = ToSkColor(fallbackColor, lease.CurrentOpacity);
            canvas.DrawRoundRect(rounded, paint);
        }

        private bool DrawBackdrop(ISkiaSharpApiLease lease, SKSurface surface, SKRoundRect rounded,
            SKPaint paint, BackdropStyle resolved)
        {
            SKCanvas canvas = lease.SkCanvas;
            SKMatrix transform = canvas.TotalMatrix;
            float scaleX = MathF.Sqrt(transform.ScaleX * transform.ScaleX + transform.SkewY * transform.SkewY);
            float scaleY = MathF.Sqrt(transform.ScaleY * transform.ScaleY + transform.SkewX * transform.SkewX);
            if (!float.IsFinite(scaleX) || !float.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0 ||
                transform.Persp0 != 0 || transform.Persp1 != 0 || transform.Persp2 != 1)
                return false;

            float sigma = resolved == BackdropStyle.LiquidGlass ? 5 : 12;
            SKRect deviceBounds = transform.MapRect(rounded.Rect);
            float padding = SamplePadding * Math.Max(scaleX, scaleY);
            // Spill only feeds the blur material, which gathers content from beyond its edges.
            bool hasSpill = resolved == BackdropStyle.Blur && lightSpill != default;
            var spill = hasSpill
                ? new SKRect((float)lightSpill.Left * scaleX, (float)lightSpill.Top * scaleY,
                    (float)lightSpill.Right * scaleX, (float)lightSpill.Bottom * scaleY)
                : SKRect.Empty;
            var capture = new SKRectI(
                Math.Max(0, (int)MathF.Floor(deviceBounds.Left - padding - spill.Left)),
                Math.Max(0, (int)MathF.Floor(deviceBounds.Top - padding - spill.Top)),
                (int)MathF.Ceiling(deviceBounds.Right + padding + spill.Right),
                (int)MathF.Ceiling(deviceBounds.Bottom + padding + spill.Bottom));
            if (capture.Width <= 0 || capture.Height <= 0)
                return false;

            SKRectI? interior = GetRoundedClipInterior(capture, deviceBounds);
            bool hasRectangularClip = TryGetRectangularClip(canvas, deviceBounds, padding, spill, interior, out SKRectI clip);
            SKRectI surfaceBounds = surfacePixelSize.Width > 0 && surfacePixelSize.Height > 0
                ? new SKRectI(0, 0, (int)Math.Ceiling(surfacePixelSize.Width), (int)Math.Ceiling(surfacePixelSize.Height))
                : SKRectI.Ceiling(SKRect.Union(deviceBounds, capture));
            SKRect visibleBounds = SKRect.Intersect(deviceBounds, surfaceBounds);
            SKRectI visibleCapture = SKRectI.Intersect(capture, surfaceBounds);
            if (visibleBounds.Width <= 0 || visibleBounds.Height <= 0 || visibleCapture.Width <= 0 ||
                visibleCapture.Height <= 0)
                return false;

            if (hasRectangularClip)
            {
                // A new operation is first drawn in a frame that repaints all of it, so its first clip is everything
                // ancestor clips allow. Partial redraws of the same operation cannot provide pixels outside the union.
                foregroundState.ReachableClip = cleanBackdrop is not null &&
                                                foregroundState.ReachableBounds == deviceBounds &&
                                                foregroundState.ReachableSurface == surfaceBounds &&
                                                foregroundState.ReachableInterior == interior
                    ? SKRectI.Union(foregroundState.ReachableClip, clip)
                    : clip;
                foregroundState.ReachableBounds = deviceBounds;
                foregroundState.ReachableSurface = surfaceBounds;
                foregroundState.ReachableInterior = interior;
            }
            SKRectI reachableCapture = SKRectI.Intersect(visibleCapture, foregroundState.ReachableClip);
            SKRect reachableBounds = SKRect.Intersect(visibleBounds, foregroundState.ReachableClip);
            // Material outside every clip it has been drawn with stays hidden; repainting cannot reveal it.
            if (hasRectangularClip && (reachableBounds.Width <= 0 || reachableBounds.Height <= 0))
                return false;
            bool coversBackdrop = clip.Left <= reachableBounds.Left && clip.Top <= reachableBounds.Top &&
                                  clip.Right >= reachableBounds.Right && clip.Bottom >= reachableBounds.Bottom;
            bool hasBackdrop = cleanBackdrop is not null && cleanBounds == deviceBounds;
            bool coversCapture = hasBackdrop && clip.Left <= cleanCapture.Left && clip.Top <= cleanCapture.Top &&
                                 clip.Right >= cleanCapture.Right && clip.Bottom >= cleanCapture.Bottom;
            if (hasRectangularClip)
            {
                if (!hasBackdrop || coversCapture)
                {
                    SKRectI available = SKRectI.Intersect(capture, clip);
                    if (available.Width <= 0 || available.Height <= 0)
                        return false;

                    SKImage? snapshot = surface.Snapshot(available);
                    if (snapshot is null)
                        return false;

                    cleanBackdrop?.Dispose();
                    cleanBackdrop = snapshot;
                    cleanCapture = new SKRectI(available.Left, available.Top,
                        available.Left + snapshot.Width, available.Top + snapshot.Height);
                    cleanBounds = deviceBounds;
                }
                else
                {
                    SKRectI update = SKRectI.Intersect(cleanCapture, clip);
                    if (update.Width > 0 && update.Height > 0)
                    {
                        using SKImage? snapshot = surface.Snapshot(update);
                        if (snapshot is null)
                            return false;

                        var info = new SKImageInfo(cleanBackdrop!.Width, cleanBackdrop.Height,
                            cleanBackdrop.ColorType, cleanBackdrop.AlphaType, cleanBackdrop.ColorSpace);
                        using SKSurface? merged = lease.GrContext is { } graphics
                            ? SKSurface.Create(graphics, true, info)
                            : SKSurface.Create(info);
                        if (merged is null)
                            return false;

                        using var copy = new SKPaint { BlendMode = SKBlendMode.Src };
                        merged.Canvas.DrawImage(cleanBackdrop, 0, 0, SKSamplingOptions.Default, copy);
                        merged.Canvas.DrawImage(snapshot, update.Left - cleanCapture.Left, update.Top - cleanCapture.Top, SKSamplingOptions.Default, copy);
                        cleanBackdrop.Dispose();
                        cleanBackdrop = merged.Snapshot();
                    }
                }
            }

            bool complete = cleanBackdrop is not null && cleanBounds == deviceBounds && hasRectangularClip && coversBackdrop &&
                            cleanCapture.Left <= reachableCapture.Left && cleanCapture.Top <= reachableCapture.Top &&
                            cleanCapture.Right >= reachableCapture.Right && cleanCapture.Bottom >= reachableCapture.Bottom;
            if (complete)
                repaintQueued = false;
            else if (!repaintQueued && (hasRectangularClip ? requestFullRepaint : requestClipRefresh) is { } request)
            {
                // A full repaint supplies clean pixels outside a partial clip. Repainting cannot make a
                // non-rectangular clip rectangular, so that case only refreshes stale rounded clip geometry.
                repaintQueued = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!disposed)
                        request();
                }, DispatcherPriority.Render);
            }

            if (cleanBackdrop is null || cleanBounds != deviceBounds)
                return false;
            capture = cleanCapture;
            SKImage backdrop = cleanBackdrop;

            foregroundContext = lease.GrContext;
            backdropCanvas = canvas;
            foregroundPeak = 1;
            if (isForegroundBoostEnabled && lease.GetOutputColorSpace().IsExtendedRange())
            {
                if (lease.GetOutputColorVolume() is { ReferenceWhiteScale: > 0 } volume &&
                    double.IsFinite(volume.ReferenceWhiteScale.Value))
                {
                    foregroundPeak = volume.ToneMapping switch
                    {
                        VideoToneMappingMode.Platform => 16,
                        VideoToneMappingMode.Client when volume.HeadroomRatio is >= 1 and var headroom &&
                            double.IsFinite(headroom) => (float)Math.Clamp(headroom, 1, 16),
                        _ => 1,
                    };
                }
                foregroundState.Backdrop = this;
            }

            using SKImageFilter blur = SKImageFilter.CreateBlur(sigma * scaleX, sigma * scaleY, SKShaderTileMode.Clamp);
            SKRuntimeEffect? opacityEffect = lease.CurrentOpacity < 1 ? OpacityEffect.Value : null;
            if (lease.CurrentOpacity < 1 && opacityEffect is null)
                return false;
            using SKBlender? blender = opacityEffect is not null
                ? opacityEffect.ToBlender(new SKRuntimeEffectUniforms(opacityEffect)
                {
                    ["opacity"] = (float)Math.Clamp(lease.CurrentOpacity, 0, 1),
                })
                : SKBlender.CreateBlendMode(SKBlendMode.Src);
            if (blender is null)
                return false;

            int saved = canvas.Save();
            try
            {
                // Replace sampled pixels; source-over would retain sharp content on translucent window surfaces.
                paint.Color = SKColors.White;
                paint.Blender = blender;
                if (resolved == BackdropStyle.LiquidGlass)
                {
                    return DrawGlass(lease, backdrop, blur, capture, transform, rounded, paint);
                }

                canvas.ClipRoundRect(rounded, SKClipOperation.Intersect, true);
                // Composite the blur and tint in the captured color space, including extended-linear HDR surfaces.
                var info = new SKImageInfo(backdrop.Width, backdrop.Height, backdrop.ColorType,
                    backdrop.AlphaType, backdrop.ColorSpace);
                using SKSurface? filtered = lease.GrContext is { } graphics
                    ? SKSurface.Create(graphics, true, info)
                    : SKSurface.Create(info);
                if (filtered is null)
                    return false;

                filtered.Canvas.Clear(SKColors.Transparent);
                using (var filterPaint = new SKPaint { ImageFilter = blur })
                    filtered.Canvas.DrawImage(backdrop, 0, 0, SKSamplingOptions.Default, filterPaint);
                if (foregroundPeak > 1)
                {
                    using SKImage untinted = filtered.Snapshot();
                    foregroundBackdrop = untinted.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                        new SKSamplingOptions(SKFilterMode.Linear));
                }
                using (var tintPaint = new SKPaint { Color = ToSkColor(tintColor, 1) })
                    filtered.Canvas.DrawRect(new SKRect(0, 0, backdrop.Width, backdrop.Height), tintPaint);
                using SKImage blurred = filtered.Snapshot();

                if (hasSpill && DrawSpill(lease, backdrop, blurred, capture, transform, rounded.Rect, paint))
                    return true;

                canvas.SetMatrix(SKMatrix.Identity);
                // Clamped so the material also covers edges that the capture leaves out.
                using SKShader materialShader = blurred.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                    SKSamplingOptions.Default, SKMatrix.CreateTranslation(capture.Left, capture.Top));
                paint.Shader = materialShader;
                canvas.DrawRect(SKRectI.Ceiling(SKRect.Union(deviceBounds, capture), true), paint);
                paint.Shader = null;
                return true;
            }
            finally
            {
                paint.BlendMode = SKBlendMode.SrcOver;
                canvas.RestoreToCount(saved);
            }
        }

        private static bool TryGetRectangularClip(SKCanvas canvas, SKRect deviceBounds, float padding, SKRect spill,
            SKRectI? interior, out SKRectI clip)
        {
            clip = canvas.DeviceClipBounds;
            if (canvas.IsClipRect)
            {
                // Limited on every frame so partial and full redraws agree on what is reachable.
                if (interior is { } limit)
                    clip = SKRectI.Intersect(clip, limit);
                return clip.Width > 0 && clip.Height > 0;
            }

            int saved = canvas.Save();
            try
            {
                canvas.ResetMatrix();
                float extent = 1;
                while (true)
                {
                    // Limit sampling padding to a known-clean rectangle inside rounded ancestor and dirty clips.
                    var sampleBounds = new SKRect(
                        deviceBounds.Left - (padding + spill.Left) * extent,
                        deviceBounds.Top - (padding + spill.Top) * extent,
                        deviceBounds.Right + (padding + spill.Right) * extent,
                        deviceBounds.Bottom + (padding + spill.Bottom) * extent);
                    if (interior is { } limit)
                        sampleBounds = extent == 1 ? limit : SKRect.Intersect(sampleBounds, limit);
                    if (sampleBounds.Width <= 0 || sampleBounds.Height <= 0)
                        return false;
                    canvas.ClipRect(sampleBounds, SKClipOperation.Intersect, false);
                    if (canvas.IsClipRect)
                    {
                        clip = canvas.DeviceClipBounds;
                        return clip.Width > 0 && clip.Height > 0;
                    }

                    if (extent == 0)
                        return false;
                    extent = padding * extent > 1 ? extent * 0.5f : 0;
                }
            }
            finally
            {
                canvas.RestoreToCount(saved);
            }
        }

        // The part of the capture that rounded ancestor clips cover fully, leaving out their antialiased
        // outlines and corners. Clipping to it keeps the effective clip rectangular.
        private SKRectI? GetRoundedClipInterior(SKRectI capture, SKRect deviceBounds)
        {
            if (roundedClips.Length == 0)
                return null;

            SKRectI interior = capture;
            foreach (RoundedClip rounded in roundedClips)
            {
                SKRect outline = rounded.Outline;
                interior = SKRectI.Intersect(interior, new SKRectI(
                    (int)MathF.Ceiling(outline.Left + 0.5f), (int)MathF.Ceiling(outline.Top + 0.5f),
                    (int)MathF.Floor(outline.Right - 0.5f), (int)MathF.Floor(outline.Bottom - 0.5f)));
                interior = ExcludeCorner(interior, deviceBounds, outline, rounded.TopLeft, left: true, top: true);
                interior = ExcludeCorner(interior, deviceBounds, outline, rounded.TopRight, left: false, top: true);
                interior = ExcludeCorner(interior, deviceBounds, outline, rounded.BottomRight, left: false, top: false);
                interior = ExcludeCorner(interior, deviceBounds, outline, rounded.BottomLeft, left: true, top: false);
            }

            return interior;
        }

        // Drops the corner's columns or rows, whichever keeps more of the material.
        private static SKRectI ExcludeCorner(SKRectI interior, SKRect deviceBounds, SKRect outline, SKPoint radius,
            bool left, bool top)
        {
            if (radius.X <= 0 || radius.Y <= 0 || interior.Width <= 0 || interior.Height <= 0)
                return interior;

            int cornerX = left
                ? (int)MathF.Ceiling(outline.Left + radius.X + 0.5f)
                : (int)MathF.Floor(outline.Right - radius.X - 0.5f);
            int cornerY = top
                ? (int)MathF.Ceiling(outline.Top + radius.Y + 0.5f)
                : (int)MathF.Floor(outline.Bottom - radius.Y - 0.5f);
            if ((left ? interior.Left >= cornerX : interior.Right <= cornerX) ||
                (top ? interior.Top >= cornerY : interior.Bottom <= cornerY))
                return interior;

            SKRectI byColumns = left ? interior with { Left = cornerX } : interior with { Right = cornerX };
            SKRectI byRows = top ? interior with { Top = cornerY } : interior with { Bottom = cornerY };
            return Coverage(byRows, deviceBounds).CompareTo(Coverage(byColumns, deviceBounds)) > 0 ? byRows : byColumns;
        }

        private static (float Material, float Total) Coverage(SKRectI rectangle, SKRect deviceBounds)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
                return (-1, -1);
            SKRect material = SKRect.Intersect(rectangle, deviceBounds);
            return (material.Width * material.Height, (float)rectangle.Width * rectangle.Height);
        }

        private bool DrawSpill(ISkiaSharpApiLease lease, SKImage backdrop, SKImage material, SKRectI capture,
            SKMatrix transform, SKRect rectangle, SKPaint paint)
        {
            if (SpillEffect.Value is not { } effect)
                return false;

            float scaleX = MathF.Sqrt(transform.ScaleX * transform.ScaleX + transform.SkewY * transform.SkewY);
            float scaleY = MathF.Sqrt(transform.ScaleY * transform.ScaleY + transform.SkewX * transform.SkewX);
            double reach = Math.Max(Math.Max(lightSpill.Left, lightSpill.Right), Math.Max(lightSpill.Top, lightSpill.Bottom));
            // Diffuse the gathered content well beyond the material blur so it arrives as color rather than detail.
            float sigma = (float)Math.Clamp(reach * 0.25, 8, 48);
            var info = new SKImageInfo(backdrop.Width, backdrop.Height, backdrop.ColorType,
                backdrop.AlphaType, backdrop.ColorSpace);
            using SKSurface? diffused = lease.GrContext is { } graphics
                ? SKSurface.Create(graphics, true, info)
                : SKSurface.Create(info);
            if (diffused is null)
                return false;

            using SKImageFilter blur = SKImageFilter.CreateBlur(sigma * scaleX, sigma * scaleY, SKShaderTileMode.Clamp);
            diffused.Canvas.Clear(SKColors.Transparent);
            using (var filterPaint = new SKPaint { ImageFilter = blur })
                diffused.Canvas.DrawImage(backdrop, 0, 0, SKSamplingOptions.Default, filterPaint);
            using SKImage light = diffused.Snapshot();
            using SKShader materialInput = material.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Linear));
            using SKShader lightInput = light.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Linear));
            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["size"] = new[] { rectangle.Width, rectangle.Height },
                ["spill"] = new[]
                {
                    (float)lightSpill.Left, (float)lightSpill.Top, (float)lightSpill.Right, (float)lightSpill.Bottom,
                },
                ["transformX"] = new[] { transform.ScaleX, transform.SkewX, transform.TransX - capture.Left },
                ["transformY"] = new[] { transform.SkewY, transform.ScaleY, transform.TransY - capture.Top },
            };
            var children = new SKRuntimeEffectChildren(effect)
            {
                ["material"] = materialInput,
                ["light"] = lightInput,
            };
            using SKShader? shader = effect.ToShader(uniforms, children);
            if (shader is null)
                return false;

            paint.Shader = shader;
            lease.SkCanvas.DrawRect(rectangle, paint);
            paint.Shader = null;
            return true;
        }

        private bool DrawGlass(ISkiaSharpApiLease lease, SKImage backdrop, SKImageFilter blur,
            SKRectI capture, SKMatrix transform, SKRoundRect rounded, SKPaint paint)
        {
            if (GlassEffect.Value is not { } effect)
                return false;

            SKRect rectangle = rounded.Rect;
            var info = new SKImageInfo(backdrop.Width, backdrop.Height, backdrop.ColorType,
                backdrop.AlphaType, backdrop.ColorSpace);
            using SKSurface? filtered = lease.GrContext is { } graphics
                ? SKSurface.Create(graphics, true, info)
                : SKSurface.Create(info);
            if (filtered is null)
                return false;

            filtered.Canvas.Clear(SKColors.Transparent);
            using (var filterPaint = new SKPaint { ImageFilter = blur })
                filtered.Canvas.DrawImage(backdrop, 0, 0, SKSamplingOptions.Default, filterPaint);
            using SKImage blurred = filtered.Snapshot();

            float scaleX = MathF.Sqrt(transform.ScaleX * transform.ScaleX + transform.SkewY * transform.SkewY);
            float scaleY = MathF.Sqrt(transform.ScaleY * transform.ScaleY + transform.SkewX * transform.SkewX);
            using SKImageFilter baseBlur = SKImageFilter.CreateBlur(0.75f * scaleX, 0.75f * scaleY, SKShaderTileMode.Clamp);
            filtered.Canvas.Clear(SKColors.Transparent);
            using (var filterPaint = new SKPaint { ImageFilter = baseBlur })
                filtered.Canvas.DrawImage(backdrop, 0, 0, SKSamplingOptions.Default, filterPaint);
            using SKImage softened = filtered.Snapshot();
            using SKImageFilter reflectionBlur = SKImageFilter.CreateBlur(12 * scaleX, 12 * scaleY, SKShaderTileMode.Clamp);
            filtered.Canvas.Clear(SKColors.Transparent);
            using (var filterPaint = new SKPaint { ImageFilter = reflectionBlur })
                filtered.Canvas.DrawImage(backdrop, 0, 0, SKSamplingOptions.Default, filterPaint);
            using SKImage reflection = filtered.Snapshot();
            using SKShader input = softened.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Linear));
            using SKShader softInput = blurred.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Linear));
            using SKShader reflectionInput = reflection.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Linear));
            if (foregroundPeak > 1)
                foregroundBackdrop = blurred.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                    new SKSamplingOptions(SKFilterMode.Linear));
            bool lightAppearance = fallbackColor.R * 0.2126f + fallbackColor.G * 0.7152f + fallbackColor.B * 0.0722f > 127.5f;
            float frost = (float)glassFrost;
            // Follows the macOS fill: white reaching half opacity, or dark gray from the midpoint on.
            float frostFill = lightAppearance
                ? frost <= 0.5f ? frost * 0.4f : 0.2f + (frost - 0.5f) * 0.6f
                : Math.Max(frost - 0.5f, 0);
            float frostLevel = lightAppearance ? 1 : 0.125f;
            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["size"] = new[] { rectangle.Width, rectangle.Height },
                ["corners"] = new[]
                {
                    (float)cornerRadius.TopLeft, (float)cornerRadius.TopRight,
                    (float)cornerRadius.BottomRight, (float)cornerRadius.BottomLeft,
                },
                ["transformX"] = new[] { transform.ScaleX, transform.SkewX, transform.TransX - capture.Left },
                ["transformY"] = new[] { transform.SkewY, transform.ScaleY, transform.TransY - capture.Top },
                ["tint"] = new[] { tintColor.R / 255f, tintColor.G / 255f, tintColor.B / 255f, tintColor.A / 255f },
                ["tintStrength"] = (float)glassTintStrength,
                ["lightAppearance"] = lightAppearance ? 1f : 0f,
                ["bodySoftness"] = Math.Clamp((Math.Min(rectangle.Width, rectangle.Height) - 80) / 120, 0, 1),
                ["frostBlur"] = frost * (2 - frost),
                ["frostFill"] = new[] { frostLevel, frostLevel, frostLevel, frostFill },
                ["linearWorkingSpace"] = backdrop.ColorSpace is { GammaIsLinear: true } ? 1f : 0f,
            };
            var children = new SKRuntimeEffectChildren(effect)
            {
                ["backdrop"] = input,
                ["softBackdrop"] = softInput,
                ["reflectionBackdrop"] = reflectionInput,
            };
            using SKShader? shader = effect.ToShader(uniforms, children);
            if (shader is null)
                return false;

            if (hasGlassShadow)
            {
                float shadowSigma = Math.Clamp((Math.Min(rectangle.Width, rectangle.Height) - 48) * 0.04f, 1.5f, 5);
                using SKImageFilter shadow = SKImageFilter.CreateDropShadowOnly(0, shadowSigma * 1.5f,
                    shadowSigma, shadowSigma, ToSkColor(Color.FromArgb(18, 0, 0, 0), lease.CurrentOpacity));
                int saved = lease.SkCanvas.Save();
                try
                {
                    lease.SkCanvas.ClipRoundRect(rounded, SKClipOperation.Difference, true);
                    using var shadowPaint = new SKPaint { IsAntialias = true, ImageFilter = shadow };
                    lease.SkCanvas.DrawRoundRect(rounded, shadowPaint);
                }
                finally
                {
                    lease.SkCanvas.RestoreToCount(saved);
                }
            }
            lease.SkCanvas.ClipRoundRect(rounded, SKClipOperation.Intersect, true);
            paint.Shader = shader;
            lease.SkCanvas.DrawRect(rectangle, paint);
            paint.Shader = null;
            return true;
        }

        public bool DrawForeground(ISkiaSharpApiLease lease, Color color, Rect foregroundBounds)
        {
            if (disposed || foregroundBackdrop is not { } backdrop || foregroundPeak <= 1 ||
                backdropCanvas is not { } canvas || canvas.Handle == IntPtr.Zero ||
                !lease.GetOutputColorSpace().IsExtendedRange() ||
                lease.GrContext != foregroundContext || ForegroundEffect.Value is not { } effect)
                return false;

            // The brush is rasterized on an intermediate canvas; its destination canvas retains
            // the actual draw transform, including text-run origins and template child offsets.
            SKMatrix transform = canvas.TotalMatrix;
            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["transformX"] = new[] { transform.ScaleX, transform.SkewX, transform.TransX - cleanCapture.Left },
                ["transformY"] = new[] { transform.SkewY, transform.ScaleY, transform.TransY - cleanCapture.Top },
                ["peak"] = foregroundPeak,
                ["foreground"] = new[] { color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f },
            };
            using SKShader? shader = effect.ToShader(uniforms, new SKRuntimeEffectChildren(effect) { ["backdrop"] = backdrop });
            if (shader is null)
                return false;
            using var paint = new SKPaint { Shader = shader, Color = ToSkColor(Colors.White, lease.CurrentOpacity) };
            lease.SkCanvas.DrawRect(new SKRect(0, 0, (float)foregroundBounds.Width, (float)foregroundBounds.Height), paint);
            return true;
        }

        private static SKRuntimeEffect? CreateForegroundEffect()
        {
            SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader("""
                uniform shader backdrop;
                uniform float3 transformX;
                uniform float3 transformY;
                uniform float peak;
                layout(color) uniform half4 foreground;
                half4 main(float2 position) {
                    float3 local = float3(position, 1.0);
                    float2 samplePosition = float2(dot(transformX, local), dot(transformY, local));
                    half4 sampleColor = backdrop.eval(samplePosition);
                    half3 linear = toLinearSrgb(sampleColor.rgb / max(sampleColor.a, 0.0001));
                    float luminance = dot(linear, half3(0.2126, 0.7152, 0.0722));
                    float brightness = clamp(luminance * 1.2 + 0.1, 1.0, peak);
                    half3 linearForeground = toLinearSrgb(foreground.rgb);
                    linearForeground = min(linearForeground + brightness - 1.0, half3(peak));
                    return half4(fromLinearSrgb(linearForeground) * foreground.a, foreground.a);
                }
                """, out string errors);
            if (effect is null)
                AppLog.Error("Appearance.Backdrop", $"Could not compile the backdrop foreground shader: {errors}");
            return effect;
        }

        private static SKRuntimeEffect? CreateOpacityEffect()
        {
            SKRuntimeEffect? effect = SKRuntimeEffect.CreateBlender("""
                uniform half opacity;
                half4 main(half4 source, half4 destination) {
                    return mix(destination, source, opacity);
                }
                """, out string errors);
            if (effect is null)
                AppLog.Error("Appearance.Backdrop", $"Could not compile the backdrop opacity blender: {errors}");
            return effect;
        }

        private static SKRuntimeEffect? CreateSpillEffect()
        {
            SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader("""
                uniform shader material;
                uniform shader light;
                uniform float2 size;
                uniform float4 spill;
                uniform float3 transformX;
                uniform float3 transformY;

                float2 toCapture(float2 position) {
                    float3 local = float3(position, 1.0);
                    return float2(dot(transformX, local), dot(transformY, local));
                }

                float falloff(float depth, float reach) {
                    return reach > 0.0 ? exp(-depth / (reach * 0.4)) : 0.0;
                }

                // Content just past an edge lands next to it, while content further out reaches deeper
                // into the material, the way a soft reflection is stretched across a pane of glass.
                half4 gather(float2 position, float depth, float reach, float2 outward) {
                    float travel = reach * 0.15 + min(depth * 0.6, reach * 0.85);
                    return light.eval(toCapture(position + outward * (depth + travel)));
                }

                half4 main(float2 position) {
                    half4 color = material.eval(toCapture(position));
                    float4 depth = float4(position.x, position.y, size.x - position.x, size.y - position.y);
                    float4 weight = float4(falloff(depth.x, spill.x), falloff(depth.y, spill.y),
                        falloff(depth.z, spill.z), falloff(depth.w, spill.w));
                    float total = weight.x + weight.y + weight.z + weight.w;
                    if (total <= 0.0001) {
                        return color;
                    }

                    half4 gathered = half4(0.0);
                    if (weight.x > 0.0) {
                        gathered += gather(position, depth.x, spill.x, float2(-1.0, 0.0)) * weight.x;
                    }
                    if (weight.y > 0.0) {
                        gathered += gather(position, depth.y, spill.y, float2(0.0, -1.0)) * weight.y;
                    }
                    if (weight.z > 0.0) {
                        gathered += gather(position, depth.z, spill.z, float2(1.0, 0.0)) * weight.z;
                    }
                    if (weight.w > 0.0) {
                        gathered += gather(position, depth.w, spill.w, float2(0.0, 1.0)) * weight.w;
                    }
                    return mix(color, gathered / total, min(total, 1.0) * 0.45);
                }
                """, out string errors);
            if (effect is null)
                AppLog.Error("Appearance.Backdrop", $"Could not compile the backdrop spill shader: {errors}");
            return effect;
        }

        private static SKRuntimeEffect? CreateGlassEffect()
        {
            SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader("""
                uniform shader backdrop;
                uniform shader softBackdrop;
                uniform shader reflectionBackdrop;
                uniform float2 size;
                uniform float4 corners;
                uniform float3 transformX;
                uniform float3 transformY;
                layout(color) uniform half4 tint;
                uniform float tintStrength;
                uniform float lightAppearance;
                uniform float bodySoftness;
                uniform float frostBlur;
                uniform float4 frostFill;
                uniform float linearWorkingSpace;

                half4 sampleBackground(float2 position, float softness) {
                    float3 local = float3(position, 1.0);
                    float2 samplePosition = float2(dot(transformX, local), dot(transformY, local));
                    // Frosting raises the blur level from the material's own toward the reflection blur.
                    float level = mix(max(softness, bodySoftness), 2.0, frostBlur);
                    half4 color = mix(backdrop.eval(samplePosition), softBackdrop.eval(samplePosition),
                        min(level, 1.0));
                    if (level > 1.0) {
                        color = mix(color, reflectionBackdrop.eval(samplePosition), level - 1.0);
                    }
                    color.rgb = toLinearSrgb(color.rgb / max(color.a, 0.0001)) * color.a;
                    return color;
                }

                float3 encodeSrgb(float3 linear) {
                    float3 magnitude = abs(linear);
                    return sign(linear) * mix(magnitude * 12.92, 1.055 * pow(magnitude, float3(1.0 / 2.4)) - 0.055,
                        step(0.0031308, magnitude));
                }

                float3 decodeSrgb(float3 encoded) {
                    float3 magnitude = abs(encoded);
                    return sign(encoded) * mix(magnitude / 12.92, pow((magnitude + 0.055) / 1.055, float3(2.4)),
                        step(0.04045, magnitude));
                }

                float wavelengthIor(float wavelength) {
                    float spectrum = clamp((wavelength - 450.0) / 200.0, 0.0, 1.0);
                    return mix(1.6, 1.4, 1.0 - pow(1.0 - spectrum, 4.0));
                }

                float2 limitOffset(float2 offset) {
                    return offset * (42.0 / max(length(offset), 42.0));
                }

                half3 sampleRefraction(float2 position, float3 normal, float thickness,
                                       float curvature, float wavelength) {
                    float3 ray = refract(float3(0.0, 0.0, 1.0), normal, 1.0003 / wavelengthIor(wavelength));
                    float2 offset = ray.xy * thickness / max(abs(ray.z), 0.15);
                    return sampleBackground(position + limitOffset(offset),
                        mix(0.04, 0.65, curvature)).rgb;
                }

                half4 main(float2 position) {
                    float2 halfSize = size * 0.5;
                    float2 centered = position - halfSize;
                    float radius = centered.x < 0.0
                        ? (centered.y < 0.0 ? corners.x : corners.w)
                        : (centered.y < 0.0 ? corners.y : corners.z);
                    radius = min(radius, min(halfSize.x, halfSize.y));
                    float2 edge = abs(centered) - halfSize + radius;
                    float2 outside = max(edge, 0.0);
                    float outsideLength = length(outside);
                    float distance = outsideLength + min(max(edge.x, edge.y), 0.0) - radius;
                    float2 outward = outsideLength > 0.001
                        ? outside / outsideLength * sign(centered)
                        : (edge.x > edge.y ? float2(sign(centered.x), 0.0) : float2(0.0, sign(centered.y)));
                    float depth = max(-distance, 0.0);
                    float extent = min(halfSize.x, halfSize.y);
                    float bevel = clamp(extent * 0.2, 24.0, 108.0);
                    float curvature = pow(clamp(1.0 - depth / bevel, 0.0, 1.0), 4.0);
                    float noise = fract(52.9829189 * fract(dot(position, float2(0.06711056, 0.00583715))));
                    float angle = noise * 6.28318530718;
                    float3 normal = normalize(mix(float3(sin(angle) * 0.006, cos(angle) * 0.006, -1.0),
                        float3(outward, 0.0), curvature));
                    float thickness = clamp(extent * 1.25, 10.0, 36.0);
                    half3 refracted = sampleRefraction(position, normal, thickness, curvature, 611.4) * half3(1, 0, 0);
                    refracted += sampleRefraction(position, normal, thickness, curvature, 570.5) * half3(1, 1, 0);
                    refracted += sampleRefraction(position, normal, thickness, curvature, 549.1) * half3(0, 1, 0);
                    refracted += sampleRefraction(position, normal, thickness, curvature, 491.4) * half3(0, 1, 1);
                    refracted += sampleRefraction(position, normal, thickness, curvature, 464.2) * half3(0, 0, 1);
                    refracted += sampleRefraction(position, normal, thickness, curvature, 374.0) * half3(1, 0, 1);
                    refracted /= 3.0;

                    float normalView = clamp(-normal.z, 0.0, 1.0);
                    float fresnel = 0.04 + 0.96 * pow(1.0 - normalView, 5.0);
                    float3 local = float3(position, 1.0);
                    float2 reflectionPosition = float2(dot(transformX, local), dot(transformY, local));
                    half4 reflection = reflectionBackdrop.eval(reflectionPosition);
                    half3 reflected = toLinearSrgb(reflection.rgb / max(reflection.a, 0.0001)) * reflection.a;
                    float reflectionAmount = mix(0.2, 1.0, fresnel) * exp(-depth / (thickness * 0.35));
                    half4 base = sampleBackground(position, 0.04);
                    half4 color = half4(mix(refracted, reflected, reflectionAmount), base.a);
                    float bodyAmount = smoothstep(0.0, min(4.0, bevel), depth);
                    float backgroundLuminance = dot(color.rgb / max(color.a, 0.0001), half3(0.2126, 0.7152, 0.0722));
                    float luminanceLift = mix(mix(0.033, 0.028, bodySoftness), 0.24, lightAppearance)
                        * (1.0 - smoothstep(0.25, 1.0, backgroundLuminance));
                    color.rgb += half3(luminanceLift * color.a);
                    float whiteAmount = smoothstep(0.25, 1.0, backgroundLuminance)
                        * (1.0 - smoothstep(1.0, 2.0, backgroundLuminance));
                    float whiteOffset = mix(0.1, -0.035, lightAppearance) * whiteAmount;
                    color.rgb *= 1.0 - whiteOffset / max(backgroundLuminance, 0.0001);
                    color.rgb = fromLinearSrgb(color.rgb / max(color.a, 0.0001)) * color.a;
                    float tintAmount = tint.a * tintStrength * bodyAmount;
                    color.rgb = color.rgb * (1.0 - tintAmount) + tint.rgb * tintAmount;
                    color.a = color.a * (1.0 - tintAmount) + tintAmount;
                    // Covers the face to its edge, blending gamma-encoded values as the system material does.
                    float fillAmount = frostFill.a;
                    if (fillAmount > 0.0) {
                        float3 encoded = color.rgb / max(color.a, 0.0001);
                        encoded = linearWorkingSpace > 0.5 ? encodeSrgb(encoded) : encoded;
                        float filledAlpha = color.a * (1.0 - fillAmount) + fillAmount;
                        encoded = (encoded * color.a * (1.0 - fillAmount) + frostFill.rgb * fillAmount) / filledAlpha;
                        color.rgb = half3(linearWorkingSpace > 0.5 ? decodeSrgb(encoded) : encoded) * filledAlpha;
                        color.a = filledAlpha;
                    }
                    float lightFacing = dot(outward, normalize(float2(-0.2, -1.0)));
                    float rim = exp(-depth * depth / 0.65);
                    float rimLight = 0.008 + 0.16 * pow(abs(lightFacing), 4.0);
                    float rimLuminance = dot(reflected / max(reflection.a, 0.0001), half3(0.2126, 0.7152, 0.0722));
                    rimLight += lightAppearance * (0.16 + 0.28 * pow(abs(lightFacing), 4.0))
                        * (1.0 - smoothstep(0.25, 0.95, rimLuminance));
                    float innerLight = 0.01 * pow(max(lightFacing, 0.0), 2.0) * exp(-depth / 3.0);
                    half3 illuminated = toLinearSrgb(color.rgb / max(color.a, 0.0001));
                    float endContour = pow(abs(outward.x), 2.0) * exp(-depth / max(radius * 0.55, 1.0));
                    illuminated *= 1.0 - mix(0.045, 0.07, lightAppearance) * endContour;
                    illuminated *= 1.0 - rim * 0.12 * (1.0 - abs(lightFacing));
                    illuminated *= 1.0 - lightAppearance * 0.035 * pow(abs(lightFacing), 4.0)
                        * exp(-pow((depth - 6.0) / 4.0, 2.0));
                    illuminated += half3(rim * rimLight + innerLight);
                    color.rgb = fromLinearSrgb(illuminated) * color.a;
                    return color;
                }
                """, out string errors);
            if (effect is null)
                AppLog.Error("Appearance.Backdrop", $"Could not compile the liquid glass shader: {errors}");
            return effect;
        }

        private static SKColor ToSkColor(Color color, double opacity) =>
            new(color.R, color.G, color.B, (byte)Math.Clamp(color.A * opacity, 0, 255));
    }
}