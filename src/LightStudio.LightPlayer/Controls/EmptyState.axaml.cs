using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Centered "nothing here yet" indicator with an icon, title, and description.
/// </summary>
public partial class EmptyState : UserControl
{
    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Header), "It's lonely here.");

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Description), "Add something here to get started.");

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<EmptyState, Geometry?>(nameof(Icon));

    public EmptyState()
    {
        InitializeComponent();

        // Default the glyph to the empty-library icon unless a consumer sets Icon explicitly. Consumer
        // assignments run after this constructor, so they override this fallback.
        if (Icon is null &&
            Application.Current is { } app &&
            app.Resources.TryGetResource("IconEmptyLibrary", app.ActualThemeVariant, out var resource) &&
            resource is Geometry geometry)
        {
            Icon = geometry;
        }
    }

    public string Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>The glyph geometry shown above the header. Defaults to the empty-library icon.</summary>
    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
