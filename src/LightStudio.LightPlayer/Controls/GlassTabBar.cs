using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightMediaRenderer.Avalonia.Controls;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Lays tab entries out in a row or a column of equal cells; the entry with the <c>selected</c> class is the
/// selected one. With <see cref="IsGlassEnabled"/> the bar draws the entries itself in a glass capsule, as an
/// iOS tab bar does: a touch lifts the selection into a clear lens that follows the finger and tints the
/// entries beneath it, and lifting the finger activates the entry under the lens. A column rests as a compact
/// capsule of glyphs and grows to full size, with labels, while it is held.
/// </summary>
public sealed class GlassTabBar : Panel
{
    private const string SelectedClass = "selected";
    private const double LabelSpacing = 3;
    private const double DefaultIconSize = 24;

    // A column's metrics follow the system's vertical tab bar: compact at rest, full size while held.
    private const double CompactThickness = 48;
    private const double CompactPitch = 50;
    private const double CompactEnd = 6;
    private const double ExpandedThickness = 70;
    private const double ExpandedPitch = 53;
    private const double ExpandedEnd = 8;
    private const double ColumnPillInset = 3;
    private const double ColumnPillOverlap = 4;
    private const double ExpandedLabelWidth = 64;

    // The lifted lens follows the system's: a capsule longer than a cell along a row and as long in a compact
    // column as in a grown one, wider than the bar by more where the bar is slimmer.
    private const double RowLensReach = 12.5;
    private const double RowLensOverhang = 4.5;
    private const double ColumnLensLength = 78;
    private const double CompactLensOverhang = 7.5;
    private const double ExpandedLensOverhang = 6;
    private const double LensMagnification = 0.16;
    private const double ColumnPressScale = 1.1;
    private const double RowPressScale = 1.05;

    // The space kept between the grown column and the edges of the window.
    private const double WindowMargin = 10;

    private static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(600);
    private const double MinimumLiftSeconds = 0.12;

    // How long a released entry waits for the lens to land on it before it is activated anyway.
    private const double ActivationSeconds = 0.15;

    // How far a finger travels, in entries, before the lens trails it as a drag.
    private const double DragThreshold = 0.15;

    public static readonly StyledProperty<Orientation> OrientationProperty =
        StackPanel.OrientationProperty.AddOwner<GlassTabBar>();

    public static readonly StyledProperty<Thickness> PaddingProperty =
        Decorator.PaddingProperty.AddOwner<GlassTabBar>();

    /// <summary>Whether the bar draws its entries in a glass capsule and answers touches with the lens.</summary>
    public static readonly StyledProperty<bool> IsGlassEnabledProperty =
        AvaloniaProperty.Register<GlassTabBar, bool>(nameof(IsGlassEnabled));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<GlassTabBar, IBrush?>(nameof(Foreground));

    /// <summary>Colors the entries under the selection and the lens.</summary>
    public static readonly StyledProperty<IBrush?> SelectionForegroundProperty =
        AvaloniaProperty.Register<GlassTabBar, IBrush?>(nameof(SelectionForeground));

    /// <summary>Fills the resting selection.</summary>
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<GlassTabBar, IBrush?>(nameof(SelectionBrush));

    private readonly BackdropBorder capsule = new() { ZIndex = -1 };
    private readonly EntryLayer layer;
    private readonly BackdropBorder lens = new()
    {
        ZIndex = 2,
        IsHitTestVisible = false,
        IsVisible = false,
        IsLens = true,
        GlassFrost = 0,
        GlassTintStrength = 0,
        BoxShadow = BoxShadows.Parse("0 5 16 0 #2E000000"),
    };
    private readonly ScaleTransform pressTransform = new();
    private readonly List<Entry> entries = [];
    private readonly DispatcherTimer holdTimer = new() { Interval = HoldDelay };
    private readonly DispatcherTimer settleTimer = new() { Interval = SettleDelay };

    private Size arrangedSize;
    private Spring position;
    private Spring lift;
    private Spring expansion;
    private Spring press;
    private double positionTarget;
    private double presence;
    private int selectedIndex = -1;
    private IPointer? pointer;
    private double pointerAxis;
    private double pressIndex;
    private double dragIndex;
    private bool isDragging;
    private bool isExpanded;
    private double expandedOrigin;
    private long liftUntil;
    private Button? activating;
    private long activateBy;
    private bool frameRequested;
    private TimeSpan? lastFrame;

    static GlassTabBar()
    {
        OrientationProperty.OverrideDefaultValue<GlassTabBar>(Orientation.Horizontal);
        AffectsMeasure<GlassTabBar>(OrientationProperty, PaddingProperty);
    }

    public GlassTabBar()
    {
        layer = new EntryLayer(this) { ZIndex = 1, IsHitTestVisible = false, IsVisible = false };

        // Styled as the narrow layout's other glass capsules.
        capsule.Classes.Add("narrowGlass");
        capsule.PropertyChanged += OnCapsulePropertyChanged;
        UpdateLensMaterial();
        LogicalChildren.Add(capsule);
        VisualChildren.Add(capsule);
        LogicalChildren.Add(layer);
        VisualChildren.Add(layer);
        LogicalChildren.Add(lens);
        VisualChildren.Add(lens);

        RenderTransform = pressTransform;
        holdTimer.Tick += OnHoldTimerTick;
        settleTimer.Tick += OnSettleTimerTick;
        AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnTabPointerCaptureLost, RoutingStrategies.Direct);
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    public bool IsGlassEnabled
    {
        get => GetValue(IsGlassEnabledProperty);
        set => SetValue(IsGlassEnabledProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? SelectionForeground
    {
        get => GetValue(SelectionForegroundProperty);
        set => SetValue(SelectionForegroundProperty, value);
    }

    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    private bool IsColumn => Orientation == Orientation.Vertical;

    private static long Now => Stopwatch.GetTimestamp();

    protected override void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        foreach (Entry entry in entries)
        {
            entry.Detach();
        }

        if (e.OldItems is not null)
        {
            foreach (object? removed in e.OldItems)
            {
                (removed as Control)?.ClearValue(OpacityProperty);
            }
        }

        entries.Clear();
        foreach (Control child in Children)
        {
            entries.Add(new Entry(this, child));
        }

        UpdateEntryVisibility();
        UpdateSelection();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsGlassEnabledProperty)
        {
            layer.IsVisible = IsGlassEnabled;
            UpdateEntryVisibility();
            Settle();
        }
        else if (change.Property == OrientationProperty)
        {
            Settle();
        }
        else if (change.Property == ForegroundProperty || change.Property == SelectionForegroundProperty)
        {
            InvalidateLabels();
        }
        else if (change.Property == SelectionBrushProperty)
        {
            layer.InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateSelection();
        Settle();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Settle();
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        capsule.Measure(availableSize);
        layer.Measure(availableSize);
        lens.Measure(availableSize);
        int count = Children.Count;
        if (count == 0)
        {
            return default;
        }

        if (IsColumn)
        {
            foreach (Control child in Children)
            {
                child.Measure(new Size(CompactThickness, CompactPitch));
            }

            return new Size(CompactThickness, (count * CompactPitch) + (2 * CompactEnd));
        }

        Thickness padding = Padding;
        var cell = new Size(
            Math.Max(0, (availableSize.Width - padding.Left - padding.Right) / count),
            Math.Max(0, availableSize.Height - padding.Top - padding.Bottom));
        double width = 0;
        double height = 0;
        foreach (Control child in Children)
        {
            child.Measure(cell);
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size((width * count) + padding.Left + padding.Right, height + padding.Top + padding.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = Children.Count;
        Thickness padding = Padding;
        double pitch = count == 0 ? 0 : Math.Max(0, finalSize.Width - padding.Left - padding.Right) / count;
        double height = Math.Max(0, finalSize.Height - padding.Top - padding.Bottom);
        for (int index = 0; index < count; index++)
        {
            Children[index].Arrange(IsColumn
                ? new Rect(0, CompactEnd + (index * CompactPitch), finalSize.Width, CompactPitch)
                : new Rect(padding.Left + (index * pitch), padding.Top, pitch, height));
        }

        arrangedSize = finalSize;
        ApplyFrame();
        return finalSize;
    }

    private void OnCapsulePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BackdropBorder.BackdropStyleProperty || change.Property == BackdropBorder.FallbackColorProperty)
        {
            UpdateLensMaterial();
        }
    }

    // The lens takes the capsule's appearance, and stays clear where the material falls back to a fill.
    private void UpdateLensMaterial()
    {
        Color fallback = capsule.FallbackColor;
        lens.BackdropStyle = capsule.BackdropStyle;
        lens.FallbackColor = Color.FromArgb(Math.Min(fallback.A, (byte)0x24), fallback.R, fallback.G, fallback.B);
    }

    // The bar draws the entries itself on glass, and the children stay as its hit and focus targets.
    private void UpdateEntryVisibility()
    {
        foreach (Control child in Children)
        {
            if (IsGlassEnabled)
            {
                child.Opacity = 0;
            }
            else
            {
                child.ClearValue(OpacityProperty);
            }
        }
    }

    private void InvalidateLabels()
    {
        foreach (Entry entry in entries)
        {
            entry.InvalidateLabels();
        }

        layer.InvalidateVisual();
    }

    private void UpdateSelection()
    {
        int index = -1;
        for (int candidate = 0; candidate < Children.Count && index < 0; candidate++)
        {
            if (Children[candidate].Classes.Contains(SelectedClass))
            {
                index = candidate;
            }
        }

        if (index == selectedIndex)
        {
            return;
        }

        selectedIndex = index;
        if (index >= 0 && pointer is null)
        {
            // A selection that was not showing appears in place instead of traveling there.
            if (presence < 0.01 && lift.Value < 0.01)
            {
                position = new Spring { Value = index };
            }

            positionTarget = index;
        }

        if (IsGlassEnabled && IsEffectivelyVisible)
        {
            RequestFrame();
        }
        else
        {
            Settle();
        }
    }

    // Ends any interaction and animation at the resting state.
    private void Settle()
    {
        ReleasePointer();
        Activate();
        settleTimer.Stop();
        if (selectedIndex >= 0)
        {
            positionTarget = selectedIndex;
        }

        position = new Spring { Value = positionTarget };
        lift = default;
        expansion = default;
        press = default;
        presence = selectedIndex >= 0 ? 1 : 0;
        lastFrame = null;
        ApplyFrame();
    }

    private void ReleasePointer()
    {
        IPointer? captured = pointer;
        pointer = null;
        isDragging = false;
        isExpanded = false;
        holdTimer.Stop();
        if (captured is not null && ReferenceEquals(captured.Captured, this))
        {
            captured.Capture(null);
        }
    }

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsGlassEnabled || e.Pointer.Type != PointerType.Touch || Children.Count == 0)
        {
            return;
        }

        if (pointer is not null)
        {
            // A second finger does not reach the entries while the lens is held.
            e.Handled = true;
            return;
        }

        Point local = e.GetPosition(this);
        BarGeometry geometry = GetGeometry(expansion.Value);
        double offset = (IsColumn ? local.Y : local.X) - geometry.Lead;
        int index = geometry.Pitch > 0 ? Math.Clamp((int)Math.Floor(offset / geometry.Pitch), 0, Children.Count - 1) : 0;

        Activate();
        pointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
        pointerAxis = GetPointerAxis(e);
        pressIndex = index;
        dragIndex = index;
        positionTarget = index;
        if (presence < 0.01 && lift.Value < 0.01)
        {
            position = new Spring { Value = index };
        }

        liftUntil = Now + (long)(MinimumLiftSeconds * Stopwatch.Frequency);
        settleTimer.Stop();
        if (IsColumn)
        {
            holdTimer.Stop();
            holdTimer.Start();
        }

        RequestFrame();
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, pointer))
        {
            return;
        }

        // The finger moves the lens by its own travel, so the bar growing or shifting beneath it changes nothing.
        double axis = GetPointerAxis(e);
        double pitch = GetGeometry(expansion.Value).Pitch * pressTransform.ScaleX;
        if (pitch > 0)
        {
            dragIndex += (axis - pointerAxis) / pitch;
        }

        pointerAxis = axis;
        isDragging |= Math.Abs(dragIndex - pressIndex) > DragThreshold;
        double last = Children.Count - 1;
        double clamped = Math.Clamp(dragIndex, 0, last);
        double beyond = dragIndex - clamped;
        positionTarget = clamped + (Math.Sign(beyond) * 0.3 * (1 - (1 / ((Math.Abs(beyond) * 0.9) + 1))));
        e.Handled = true;
        RequestFrame();
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, pointer))
        {
            return;
        }

        int index = Math.Clamp((int)Math.Round(dragIndex), 0, Children.Count - 1);
        ReleasePointer();
        e.Handled = true;
        if (Children[index] is Button { IsEffectivelyEnabled: true, Command: not null } button)
        {
            // The entry is activated once the lens lands on it, so the page change does not hold up its travel.
            positionTarget = index;
            activating = button;
            activateBy = Now + (long)(ActivationSeconds * Stopwatch.Frequency);
        }
        else if (selectedIndex >= 0)
        {
            positionTarget = selectedIndex;
        }

        RequestFrame();
    }

    private void Activate()
    {
        if (activating is not { } button)
        {
            return;
        }

        activating = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (button is { IsEffectivelyEnabled: true, Command: { } command } && command.CanExecute(button.CommandParameter))
            {
                command.Execute(button.CommandParameter);
            }

            // Returns the lens to the selection when the entry did not become the selected one.
            settleTimer.Stop();
            settleTimer.Start();
        });
    }

    private void OnTabPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, pointer) || ReferenceEquals(e.Pointer.Captured, this))
        {
            return;
        }

        ReleasePointer();
        if (selectedIndex >= 0)
        {
            positionTarget = selectedIndex;
        }

        RequestFrame();
    }

    private void OnHoldTimerTick(object? sender, EventArgs e)
    {
        holdTimer.Stop();
        if (pointer is null || !IsColumn || isExpanded)
        {
            return;
        }

        isExpanded = true;
        expandedOrigin = GetExpandedOrigin((int)Math.Round(Math.Clamp(dragIndex, 0, Children.Count - 1)));
        RequestFrame();
    }

    private void OnSettleTimerTick(object? sender, EventArgs e)
    {
        settleTimer.Stop();
        if (pointer is null && selectedIndex >= 0 && positionTarget != selectedIndex)
        {
            positionTarget = selectedIndex;
            RequestFrame();
        }
    }

    // The pointer's position along the bar in the parent's coordinates, which the press scale leaves alone.
    private double GetPointerAxis(PointerEventArgs e)
    {
        Point point = e.GetPosition(this.GetVisualParent());
        return IsColumn ? point.Y : point.X;
    }

    // Where the grown column starts so that the held entry stays in place, kept inside the window.
    private double GetExpandedOrigin(int anchor)
    {
        double length = (Children.Count * ExpandedPitch) + (2 * ExpandedEnd);
        double origin = CompactEnd + (CompactPitch * (anchor + 0.5)) - ExpandedEnd - (ExpandedPitch * (anchor + 0.5));
        if (TopLevel.GetTopLevel(this) is { } topLevel && this.GetVisualParent() is { } parent &&
            parent.TranslatePoint(Bounds.Position, topLevel) is { } windowOrigin)
        {
            double center = arrangedSize.Height / 2;
            double start = center + ((WindowMargin - windowOrigin.Y - center) / ColumnPressScale);
            double end = center + ((topLevel.ClientSize.Height - WindowMargin - windowOrigin.Y - center) / ColumnPressScale);
            origin = Math.Max(start, Math.Min(origin, end - length));
        }

        return origin;
    }

    private void RequestFrame()
    {
        if (frameRequested || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        frameRequested = true;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan time)
    {
        frameRequested = false;
        if (!IsGlassEnabled || !IsEffectivelyVisible || !this.IsAttachedToVisualTree())
        {
            Settle();
            return;
        }

        double elapsed = lastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.05) : 1d / 60;
        lastFrame = time;
        bool active = Advance(elapsed);
        ApplyFrame();
        if (active)
        {
            RequestFrame();
        }
        else
        {
            lastFrame = null;
        }
    }

    private bool Advance(double elapsed)
    {
        bool pressed = pointer is not null;
        bool waiting = Now < liftUntil;

        // The lens trails a dragging finger closely, and springs past its entry when it travels on its own.
        bool moving = position.Advance(positionTarget, isDragging ? 40 : 25, isDragging ? 0.85 : 0.55, elapsed);
        bool arrived = Math.Abs(position.Value - positionTarget) < 0.08 && Math.Abs(position.Velocity) < 1.5;
        if (activating is not null && (Math.Abs(position.Value - positionTarget) < 0.25 || Now >= activateBy))
        {
            Activate();
        }

        bool lifted = pressed || waiting || !arrived;
        bool lifting = lift.Advance(lifted ? 1 : 0, lifted ? 30 : 22, lifted ? 0.7 : 0.9, elapsed);
        bool expanding = expansion.Advance(isExpanded ? 1 : 0, 20, 0.85, elapsed);
        bool pressing = press.Advance(pressed ? 1 : 0, 25, 0.6, elapsed);

        double shown = selectedIndex >= 0 || lifted ? 1 : 0;
        presence += (shown - presence) * (1 - Math.Exp(-elapsed * 18));
        bool fading = Math.Abs(shown - presence) > 0.01;
        if (!fading)
        {
            presence = shown;
        }

        return moving || lifting || expanding || pressing || fading || waiting || activating is not null;
    }

    private readonly record struct BarGeometry(Rect Bar, double Lead, double Pitch, Size Selection, double Cross)
    {
        public double Thickness => Math.Min(Bar.Width, Bar.Height);
    }

    // The capsule, the first cell's start and the cells' pitch along the bar, the resting selection's size, and
    // the center line across the bar, for a column grown by the given amount.
    private BarGeometry GetGeometry(double grown)
    {
        int count = Children.Count;
        if (!IsColumn)
        {
            Thickness padding = Padding;
            double rowPitch = count == 0 ? 0 : Math.Max(0, arrangedSize.Width - padding.Left - padding.Right) / count;
            Rect first = count == 0 ? default : Children[0].Bounds;
            return new BarGeometry(new Rect(arrangedSize), padding.Left, rowPitch, first.Size, first.Center.Y);
        }

        double thickness = Lerp(CompactThickness, ExpandedThickness, grown);
        double pitch = Lerp(CompactPitch, ExpandedPitch, grown);
        double end = Lerp(CompactEnd, ExpandedEnd, grown);
        double origin = expandedOrigin * grown;
        var bar = new Rect((arrangedSize.Width - thickness) / 2, origin, thickness, (count * pitch) + (2 * end));
        return new BarGeometry(bar, origin + end, pitch,
            new Size(thickness - (2 * ColumnPillInset), pitch + (2 * ColumnPillOverlap)), arrangedSize.Width / 2);
    }

    private static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);

    private Point GetEntryCenter(BarGeometry geometry, double index)
    {
        double along = geometry.Lead + (geometry.Pitch * (index + 0.5));
        return IsColumn ? new Point(geometry.Cross, along) : new Point(along, geometry.Cross);
    }

    // The selection's outline, which the lift grows into the lens and travel stretches along the bar.
    private Rect GetSelectionRect(BarGeometry geometry)
    {
        double lifted = Math.Max(0, lift.Value);
        double stretch = Math.Min(Math.Abs(position.Velocity) * geometry.Pitch * 0.045, geometry.Pitch * 0.4) * Math.Min(1, lifted);
        Size size = geometry.Selection;
        double length = IsColumn ? ColumnLensLength : geometry.Pitch + (2 * RowLensReach);
        double overhang = IsColumn
            ? Lerp(CompactLensOverhang, ExpandedLensOverhang, Math.Clamp(expansion.Value, 0, 1))
            : RowLensOverhang;
        double along = Lerp(IsColumn ? size.Height : size.Width, length, lifted) + stretch;
        double across = Math.Max(0, Lerp(IsColumn ? size.Width : size.Height, geometry.Thickness + (2 * overhang), lifted) - (stretch * 0.12));

        // A spring carrying the selection past the first or last entry stays near the end of the bar.
        double within = Math.Clamp(position.Value, 0, Math.Max(0, Children.Count - 1));
        Point center = GetEntryCenter(geometry, within + Math.Clamp((position.Value - within) * 0.35, -0.2, 0.2));
        return IsColumn
            ? new Rect(center.X - (across / 2), center.Y - (along / 2), across, along)
            : new Rect(center.X - (along / 2), center.Y - (across / 2), along, across);
    }

    private static double GetSelectionRadius(Rect selection) => Math.Min(selection.Width, selection.Height) / 2;

    // Places the capsule, the entry layer, and the lens for the current state.
    private void ApplyFrame()
    {
        // A part measured while the bar is outside the logical tree would be styled without the tree's styles.
        if (arrangedSize == default || !((ILogical)this).IsAttachedToLogicalTree)
        {
            return;
        }

        if (!IsGlassEnabled)
        {
            capsule.Arrange(new Rect(arrangedSize));
            lens.IsVisible = false;
            pressTransform.ScaleX = 1;
            pressTransform.ScaleY = 1;
            return;
        }

        BarGeometry geometry = GetGeometry(expansion.Value);
        capsule.CornerRadius = new CornerRadius(Math.Min(geometry.Bar.Width, geometry.Bar.Height) / 2);
        capsule.Arrange(geometry.Bar);

        Rect selection = GetSelectionRect(geometry);
        layer.Arrange(geometry.Bar.Union(selection).Union(new Rect(arrangedSize)).Inflate(2));
        layer.InvalidateVisual();

        double glass = Math.Clamp(lift.Value * 2, 0, 1);
        lens.IsVisible = glass > 0.01 && selection.Width > 0 && selection.Height > 0;
        if (lens.IsVisible)
        {
            lens.Opacity = glass;
            lens.CornerRadius = new CornerRadius(GetSelectionRadius(selection));
            lens.Arrange(selection);
            lens.InvalidateVisual();
        }

        double scale = 1 + (((IsColumn ? ColumnPressScale : RowPressScale) - 1) * press.Value);
        pressTransform.ScaleX = scale;
        pressTransform.ScaleY = scale;
    }

    private void RenderEntries(DrawingContext context, Rect layerBounds)
    {
        if (entries.Count == 0)
        {
            return;
        }

        BarGeometry geometry = GetGeometry(expansion.Value);
        Rect selection = GetSelectionRect(geometry);
        double lifted = Math.Clamp(lift.Value, 0, 1);
        double labels = IsColumn ? Math.Clamp(expansion.Value, 0, 1) : 1;
        double labelWidth = IsColumn ? ExpandedLabelWidth : Math.Max(0, geometry.Selection.Width - 4);
        bool hasSelection = presence > 0.01 && selection.Width > 0 && selection.Height > 0;
        double radius = GetSelectionRadius(selection);

        using DrawingContext.PushedState origin = context.PushTransform(Matrix.CreateTranslation(-layerBounds.X, -layerBounds.Y));
        if (!hasSelection)
        {
            DrawEntries(context, geometry, Foreground, selected: false, labels, labelWidth);
            return;
        }

        if (SelectionBrush is { } fill)
        {
            using (context.PushOpacity(presence * (1 - Math.Clamp(lift.Value * 2, 0, 1))))
            {
                context.DrawRectangle(fill, null, new RoundedRect(selection, radius));
            }
        }

        var outline = new RectangleGeometry(selection, radius, radius);
        using (context.PushGeometryClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(layerBounds), outline)))
        {
            DrawEntries(context, geometry, Foreground, selected: false, labels, labelWidth);
        }

        // The lens shows the entries beneath it enlarged.
        double magnification = 1 + (LensMagnification * lifted);
        Point center = selection.Center;
        using (context.PushGeometryClip(outline))
        using (context.PushTransform(Matrix.CreateTranslation(-center.X, -center.Y) *
                                     Matrix.CreateScale(magnification, magnification) *
                                     Matrix.CreateTranslation(center.X, center.Y)))
        {
            DrawEntries(context, geometry, SelectionForeground, selected: true, labels, labelWidth);
        }
    }

    private void DrawEntries(DrawingContext context, BarGeometry geometry, IBrush? brush, bool selected,
        double labels, double labelWidth)
    {
        if (brush is null)
        {
            return;
        }

        for (int index = 0; index < entries.Count; index++)
        {
            Entry entry = entries[index];
            Point center = GetEntryCenter(geometry, index);
            FormattedText? label = labels > 0.01 ? entry.GetLabel(brush, selected, labelWidth) : null;
            double icon = entry.IconSize;
            double block = icon + (label is null ? 0 : (LabelSpacing + label.Height) * labels);
            double top = center.Y - (block / 2);
            if (entry.Icon?.Data is { Bounds: { Width: > 0, Height: > 0 } bounds } glyph)
            {
                double scale = icon / Math.Max(bounds.Width, bounds.Height);
                using (context.PushTransform(Matrix.CreateTranslation(-bounds.Center.X, -bounds.Center.Y) *
                                             Matrix.CreateScale(scale, scale) *
                                             Matrix.CreateTranslation(center.X, top + (icon / 2))))
                {
                    context.DrawGeometry(brush, null, glyph);
                }
            }

            if (label is not null)
            {
                using (context.PushOpacity(labels))
                {
                    context.DrawText(label, new Point(center.X - (label.Width / 2), top + icon + (LabelSpacing * labels)));
                }
            }
        }
    }

    private struct Spring
    {
        public double Value;
        public double Velocity;

        // Advances toward the target at the given angular frequency and damping ratio; false once at rest there.
        public bool Advance(double target, double frequency, double damping, double elapsed)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(elapsed / 0.004));
            double step = elapsed / steps;
            for (int index = 0; index < steps; index++)
            {
                Velocity += ((frequency * frequency * (target - Value)) - (2 * damping * frequency * Velocity)) * step;
                Value += Velocity * step;
            }

            if (Math.Abs(target - Value) < 0.001 && Math.Abs(Velocity) < 0.01)
            {
                Value = target;
                Velocity = 0;
                return false;
            }

            return true;
        }
    }

    // An entry's glyph and label, taken from the child that presents it outside the glass capsule.
    private sealed class Entry
    {
        private readonly GlassTabBar owner;
        private readonly Control control;
        private Path? icon;
        private TextBlock? label;
        private FormattedText? text;
        private FormattedText? selectedText;

        public Entry(GlassTabBar owner, Control control)
        {
            this.owner = owner;
            this.control = control;
            control.Classes.CollectionChanged += OnClassesChanged;
        }

        public Path? Icon
        {
            get
            {
                Resolve();
                return icon;
            }
        }

        public double IconSize => Icon?.FindAncestorOfType<Viewbox>() is { Bounds.Width: > 0 } box ? box.Bounds.Width : DefaultIconSize;

        public FormattedText? GetLabel(IBrush brush, bool selected, double maximumWidth)
        {
            Resolve();
            if (label is not { Text: { Length: > 0 } content } source)
            {
                return null;
            }

            ref FormattedText? cached = ref selected ? ref selectedText : ref text;
            cached ??= new FormattedText(content, CultureInfo.CurrentUICulture, source.FlowDirection,
                new Typeface(source.FontFamily, source.FontStyle, source.FontWeight, source.FontStretch), source.FontSize, brush)
            {
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            if (maximumWidth > 0 && cached.MaxTextWidth != maximumWidth)
            {
                cached.MaxTextWidth = maximumWidth;
            }

            return cached;
        }

        public void InvalidateLabels()
        {
            text = null;
            selectedText = null;
        }

        public void Detach()
        {
            control.Classes.CollectionChanged -= OnClassesChanged;
            if (label is not null)
            {
                label.PropertyChanged -= OnLabelPropertyChanged;
            }
        }

        private void Resolve()
        {
            if (icon is not null && label is not null)
            {
                return;
            }

            foreach (Visual visual in control.GetVisualDescendants())
            {
                if (icon is null && visual is Path path)
                {
                    icon = path;
                }
                else if (label is null && visual is TextBlock block)
                {
                    label = block;
                    block.PropertyChanged += OnLabelPropertyChanged;
                }
            }
        }

        private void OnClassesChanged(object? sender, NotifyCollectionChangedEventArgs e) => owner.UpdateSelection();

        private void OnLabelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
        {
            if (change.Property == TextBlock.TextProperty || change.Property == TextBlock.FontFamilyProperty ||
                change.Property == TextBlock.FontSizeProperty || change.Property == TextBlock.FontWeightProperty ||
                change.Property == TextBlock.FontStyleProperty || change.Property == TextBlock.FontStretchProperty ||
                change.Property == FlowDirectionProperty)
            {
                InvalidateLabels();
                owner.layer.InvalidateVisual();
            }
        }
    }

    private sealed class EntryLayer(GlassTabBar owner) : Control
    {
        public override void Render(DrawingContext context) => owner.RenderEntries(context, Bounds);
    }
}
