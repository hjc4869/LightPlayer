using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
        ArtworkImage.IsVisible = hasImage;
        PlaceholderHost.IsVisible = !hasImage;

        var key = PlaceholderKind == ThumbnailKind.Artist ? "IconPerson" : "IconDisc";
        if (this.TryFindResource(key, out var resource) && resource is Geometry geometry)
        {
            PlaceholderGlyph.Data = geometry;
        }
    }
}
