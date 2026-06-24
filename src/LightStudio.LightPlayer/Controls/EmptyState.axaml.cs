using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// Centered "nothing here yet" indicator with a title and description.
/// </summary>
public partial class EmptyState : UserControl
{
    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Header), "It's lonely here.");

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Description), "Add something here to get started.");

    public EmptyState()
    {
        InitializeComponent();
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
}
