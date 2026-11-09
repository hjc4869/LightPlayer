using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Square artwork host that shows a kind-specific placeholder glyph when no
/// artwork is supplied.
/// </summary>
public partial class MediaThumbnail : UserControl
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<MediaThumbnail, IImage?>(nameof(Source));

    public static readonly StyledProperty<ThumbnailKind> PlaceholderKindProperty =
        AvaloniaProperty.Register<MediaThumbnail, ThumbnailKind>(nameof(PlaceholderKind));

    /// <summary>
    /// Receives the device pixels this thumbnail displays its artwork at. Inherited, so a host
    /// can set it once for the thumbnails it contains.
    /// </summary>
    public static readonly AttachedProperty<IArtworkSizeTarget?> SizeTargetProperty =
        AvaloniaProperty.RegisterAttached<MediaThumbnail, Control, IArtworkSizeTarget?>("SizeTarget", inherits: true);

    private int reportedDisplaySize;
    private bool observingLayout;

    public MediaThumbnail()
    {
        InitializeComponent();
        UpdateVisualState();
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public ThumbnailKind PlaceholderKind
    {
        get => GetValue(PlaceholderKindProperty);
        set => SetValue(PlaceholderKindProperty, value);
    }

    public IArtworkSizeTarget? SizeTarget
    {
        get => GetValue(SizeTargetProperty);
        set => SetValue(SizeTargetProperty, value);
    }

    public static IArtworkSizeTarget? GetSizeTarget(Control control) => control.GetValue(SizeTargetProperty);

    public static void SetSizeTarget(Control control, IArtworkSizeTarget? value) => control.SetValue(SizeTargetProperty, value);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // App resources (the placeholder glyphs) are only resolvable once attached;
        // recycled tiles also re-attach, so refresh the visual state here.
        UpdateVisualState();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == PlaceholderKindProperty)
        {
            UpdateVisualState();
        }
        else if (change.Property == SizeTargetProperty)
        {
            reportedDisplaySize = 0;
            ObserveLayout(SizeTarget is not null);
            ReportDisplaySize();
        }
    }

    // The on-screen size includes ancestor transforms (a Viewbox scaling the host), which change
    // without touching this control's bounds, so it is read after every layout pass.
    private void ObserveLayout(bool observe)
    {
        if (observe == observingLayout)
        {
            return;
        }

        observingLayout = observe;
        if (observe)
        {
            LayoutUpdated += OnLayoutUpdated;
        }
        else
        {
            LayoutUpdated -= OnLayoutUpdated;
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => ReportDisplaySize();

    // The artwork fills the bounds and is cropped around the center, so a square cover needs its
    // shorter side to cover the longer edge. A shrinking ancestor transform is ignored so the
    // request is never undersized, and only growth is reported.
    private void ReportDisplaySize()
    {
        if (SizeTarget is not { } target || !IsEffectivelyVisible ||
            Bounds is not { Width: > 0, Height: > 0 } bounds || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var scale = topLevel.RenderScaling;
        if (this.TransformToVisual(topLevel) is { } transform)
        {
            scale *= Math.Max(1, Math.Max(
                Math.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12),
                Math.Sqrt(transform.M21 * transform.M21 + transform.M22 * transform.M22)));
        }

        var pixelSize = (int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height) * scale);
        if (pixelSize > reportedDisplaySize)
        {
            reportedDisplaySize = pixelSize;
            target.SetArtworkDisplaySize(pixelSize);
        }
    }

    private void UpdateVisualState()
    {
        // Named fields are null until InitializeComponent runs.
        if (PlaceholderGlyph is null || ArtworkImage is null || PlaceholderHost is null)
        {
            return;
        }

        var hasImage = Source is not null;
        ArtworkImage.Source = Source;
        PlaceholderHost.Opacity = hasImage ? 0 : 1;

        var key = PlaceholderKind == ThumbnailKind.Artist ? "IconPerson" : "IconDisc";
        if (this.TryFindResource(key, out var resource) && resource is Geometry geometry)
        {
            PlaceholderGlyph.Data = geometry;
        }
    }
}
