using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// A single settings entry with a header, optional description, and an action
/// control slot on the right.
/// </summary>
public partial class SettingRow : UserControl
{
    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<SettingRow, string>(nameof(Header), string.Empty);

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    public static readonly StyledProperty<object?> ActionContentProperty =
        AvaloniaProperty.Register<SettingRow, object?>(nameof(ActionContent));

    public SettingRow()
    {
        InitializeComponent();
    }

    public string Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }
}
