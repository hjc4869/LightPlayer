using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Embedding;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Shows its child on the platform's glass material (<see cref="PlatformServices.NativeGlass"/>) while
/// <see cref="IsGlassEnabled"/> is set, and in place otherwise. On glass, the child renders in a top level
/// of its own inside a native view above the window content, staying in this control's logical tree for
/// styles, resources, and bindings. The panel then fills the space its parent arranges it in.
/// </summary>
public class NativeGlassPanel : Control
{
    public static readonly StyledProperty<Control?> ChildProperty =
        AvaloniaProperty.Register<NativeGlassPanel, Control?>(nameof(Child));

    public static readonly StyledProperty<bool> IsGlassEnabledProperty =
        AvaloniaProperty.Register<NativeGlassPanel, bool>(nameof(IsGlassEnabled));

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<NativeGlassPanel, double>(nameof(CornerRadius));

    public static readonly StyledProperty<NativeGlassMaterial> MaterialProperty =
        AvaloniaProperty.Register<NativeGlassPanel, NativeGlassMaterial>(nameof(Material));

    /// <summary>The fill the window draws beneath the glass, which the glass takes its color from.</summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<NativeGlassPanel>();

    /// <summary>
    /// Whether window content drawn over the panel covers it; the native view would otherwise stay above that
    /// content. Inherited, so a view can set it for every panel it holds.
    /// </summary>
    public static readonly AttachedProperty<bool> IsOccludedProperty =
        AvaloniaProperty.RegisterAttached<NativeGlassPanel, Control, bool>("IsOccluded", inherits: true);

    private GlassHost? host;
    private IInputElement? focusToRestore;
    private bool restoresFocus;
    private bool isInViewport = true;

    static NativeGlassPanel()
    {
        AffectsMeasure<NativeGlassPanel>(ChildProperty);
        AffectsRender<NativeGlassPanel>(BackgroundProperty);
    }

    public NativeGlassPanel()
    {
        // The native view draws its edges and shadow even where the window would clip the panel.
        EffectiveViewportChanged += (_, e) =>
        {
            isInViewport = e.EffectiveViewport.Intersects(new Rect(Bounds.Size));
            UpdateVisibility();
        };
    }

    [Content]
    public Control? Child
    {
        get => GetValue(ChildProperty);
        set => SetValue(ChildProperty, value);
    }

    public bool IsGlassEnabled
    {
        get => GetValue(IsGlassEnabledProperty);
        set => SetValue(IsGlassEnabledProperty, value);
    }

    /// <summary>The corner radius of <see cref="NativeGlassMaterial.Floating"/> glass.</summary>
    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>The kind of glass the panel sits on.</summary>
    public NativeGlassMaterial Material
    {
        get => GetValue(MaterialProperty);
        set => SetValue(MaterialProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public static bool GetIsOccluded(Control control) => control.GetValue(IsOccludedProperty);

    public static void SetIsOccluded(Control control, bool value) => control.SetValue(IsOccludedProperty, value);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChildProperty)
        {
            if (change.OldValue is Control oldChild)
            {
                if (host is not null && host.Frame.Child == oldChild)
                    host.Frame.Child = null;
                else
                    VisualChildren.Remove(oldChild);
                LogicalChildren.Remove(oldChild);
            }

            if (change.NewValue is Control newChild)
            {
                LogicalChildren.Add(newChild);
                if (host is not null)
                    host.Frame.Child = newChild;
                else
                    VisualChildren.Add(newChild);
            }
        }
        else if (change.Property == IsGlassEnabledProperty || change.Property == IsOccludedProperty)
        {
            Update();
        }
        else if (change.Property == CornerRadiusProperty)
        {
            host?.SetCornerRadius(CornerRadius);
        }
        else if (change.Property == MaterialProperty)
        {
            ReplaceHost();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (host is not null && Background is { } background)
            context.FillRectangle(background, new Rect(Bounds.Size));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // Kept across a move to another parent; released once the panel has left for good.
        Dispatcher.UIThread.Post(() =>
        {
            if (!this.IsAttachedToVisualTree())
                LeaveGlass();
        }, DispatcherPriority.Background);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (host is not null)
        {
            host.Measure(availableSize);
            return default;
        }

        if (Child is not { } child)
            return default;
        child.Measure(availableSize);
        return child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (host is not null)
            host.Arrange(new Rect(finalSize));
        else
            Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private void Update()
    {
        if (!this.IsAttachedToVisualTree())
            return;

        if (IsGlassEnabled && PlatformServices.NativeGlass is { } glass && TopLevel.GetTopLevel(this) is Window)
            EnterGlass(glass);
        else
            LeaveGlass();

        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (host is not null)
            host.IsVisible = isInViewport && !GetIsOccluded(this);
    }

    private void EnterGlass(INativeGlass glass)
    {
        if (host is not null)
            return;

        var root = new EmbeddableControlRoot
        {
            Background = Brushes.Transparent,
            TransparencyBackgroundFallback = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
        };
        var frame = new GlassFrame();
        root.Content = frame;
        root.AddHandler(PointerPressedEvent, OnGlassPointerPressed);
        root.AddHandler(GotFocusEvent, OnGlassGotFocus);
        root.AddHandler(PointerReleasedEvent, OnGlassPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        foreach (var dragEvent in new[] { DragDrop.DragEnterEvent, DragDrop.DragOverEvent, DragDrop.DragLeaveEvent, DragDrop.DropEvent })
            root.AddHandler(dragEvent, OnGlassDrag, RoutingStrategies.Bubble, handledEventsToo: true);
        root.Prepare();
        root.StartRendering();

        if (Child is { } child)
        {
            VisualChildren.Remove(child);
            frame.Child = child;
        }

        AddHost(new GlassHost(glass, root, frame, Material));
    }

    private void AddHost(GlassHost added)
    {
        host = added;
        added.IsVisible = isInViewport && !GetIsOccluded(this);
        added.SetCornerRadius(CornerRadius);
        VisualChildren.Add(added);
        InvalidateMeasure();
        InvalidateVisual();
    }

    // A new native view takes over the content of the old one, which leaves the top level running.
    private void ReplaceHost()
    {
        if (host is not { } replaced || PlatformServices.NativeGlass is not { } glass)
            return;

        VisualChildren.Remove(replaced);
        replaced.Release(disposeRoot: false);
        AddHost(new GlassHost(glass, replaced.Root, replaced.Frame, Material));
    }

    private void LeaveGlass()
    {
        if (host is not { } leaving)
            return;

        host = null;
        leaving.Frame.Child = null;
        VisualChildren.Remove(leaving);
        leaving.Release(disposeRoot: true);
        if (Child is { } child)
            VisualChildren.Add(child);
        InvalidateMeasure();
        InvalidateVisual();
    }

    // Drag events on the glass bubble only to its top level; the panel raises them again in the window so drop
    // targets and drag handlers among its ancestors see them.
    private void OnGlassDrag(object? sender, DragEventArgs e)
    {
        if (host is not { } current || e.RoutedEvent is not RoutedEvent<DragEventArgs> dragEvent)
            return;

        var forwarded = new DragEventArgs(dragEvent, e.DataTransfer, current.Root, e.GetPosition(current.Root), e.KeyModifiers)
        {
            DragEffects = e.DragEffects,
        };
        RaiseEvent(forwarded);
        e.DragEffects = forwarded.DragEffects;
        e.Handled |= forwarded.Handled;
    }

    // The glass covers title bar regions of the window, which the platform drags only within the window's own view.
    private void OnGlassPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (host is null || e.Source is not Visual source ||
            WindowDecorationProperties.GetElementRole(source) != WindowDecorationsElementRole.TitleBar ||
            !e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2 && TopLevel.GetTopLevel(this) is Window window)
        {
            if (window.WindowState is WindowState.Maximized or WindowState.FullScreen)
            {
                if (window.CanResize)
                    window.WindowState = WindowState.Normal;
            }
            else if (window.CanMaximize)
            {
                window.WindowState = WindowState.Maximized;
            }
        }
        else if (e.ClickCount == 1 && host.Root.TryGetPlatformHandle() is { } handle)
        {
            host.Glass.BeginWindowDrag(handle.Handle);
        }

        e.Handled = true;
    }

    // Keys arrive at the window's own view, so a click on the glass hands focus back for the window's shortcuts,
    // once the press that took the focus is over.
    private void OnGlassGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.NavigationMethod != NavigationMethod.Pointer || host is null)
            return;

        if (!restoresFocus)
            focusToRestore = e.OldFocusedElement;
        restoresFocus = true;
    }

    private void OnGlassPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!restoresFocus || host is not { } current)
            return;

        IInputElement? previous = focusToRestore;
        restoresFocus = false;
        focusToRestore = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (host != current || current.Root.FocusManager?.GetFocusedElement() is not Visual focused ||
                TopLevel.GetTopLevel(focused) != current.Root)
                return;

            if (previous is InputElement { IsEffectivelyVisible: true, Focusable: true } element &&
                TopLevel.GetTopLevel(element) == TopLevel.GetTopLevel(this) && element.Focus())
                return;
            current.Root.FocusManager?.Focus(null);
        }, DispatcherPriority.Background);
    }

    private sealed class GlassHost(INativeGlass glass, EmbeddableControlRoot root, GlassFrame frame, NativeGlassMaterial material)
        : NativeControlHost
    {
        private IntPtr view;
        private double cornerRadius;
        private bool disposesRoot;

        public INativeGlass Glass => glass;

        public EmbeddableControlRoot Root => root;

        public GlassFrame Frame => frame;

        public void SetCornerRadius(double radius)
        {
            cornerRadius = radius;
            if (view != IntPtr.Zero)
                glass.SetCornerRadius(view, radius);
        }

        // The top level outlives the native view, which the host destroys after leaving the tree.
        public void Release(bool disposeRoot)
        {
            disposesRoot = disposeRoot;
            if (disposeRoot && view == IntPtr.Zero)
                root.Dispose();
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            if (root.TryGetPlatformHandle() is not { } content)
                return base.CreateNativeControlCore(parent);

            view = glass.Create(content.Handle, material, cornerRadius);
            return new PlatformHandle(view, content.HandleDescriptor);
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (control.Handle != view || view == IntPtr.Zero)
            {
                base.DestroyNativeControlCore(control);
                return;
            }

            glass.Destroy(view);
            view = IntPtr.Zero;
            if (disposesRoot)
                root.Dispose();
        }
    }

    // Lays the panel's child out across the glass, which keeps it visually while the panel keeps it logically.
    private sealed class GlassFrame : Control
    {
        private Control? child;

        public Control? Child
        {
            get => child;
            set
            {
                if (child == value)
                    return;
                if (child is not null)
                    VisualChildren.Remove(child);
                child = value;
                if (child is not null)
                    VisualChildren.Add(child);
                InvalidateMeasure();
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (child is null)
                return default;
            child.Measure(availableSize);
            return child.DesiredSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            child?.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }
}
