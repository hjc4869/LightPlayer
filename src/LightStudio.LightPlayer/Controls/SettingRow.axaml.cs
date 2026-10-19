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

    /// <summary>Extra content under <see cref="Description"/>, for descriptions that need a link.</summary>
    public static readonly StyledProperty<object?> DescriptionContentProperty =
        AvaloniaProperty.Register<SettingRow, object?>(nameof(DescriptionContent));

    public static readonly StyledProperty<object?> ActionContentProperty =
        AvaloniaProperty.Register<SettingRow, object?>(nameof(ActionContent));

    public static readonly StyledProperty<object?> RowsContentProperty =
        AvaloniaProperty.Register<SettingRow, object?>(nameof(RowsContent));

    public static readonly StyledProperty<bool> IsHeaderVisibleProperty =
        AvaloniaProperty.Register<SettingRow, bool>(nameof(IsHeaderVisible), true);

    public static readonly StyledProperty<bool> IsGroupedProperty =
        AvaloniaProperty.Register<SettingRow, bool>(nameof(IsGrouped));

    public static readonly StyledProperty<bool> HasTopDividerProperty =
        AvaloniaProperty.Register<SettingRow, bool>(nameof(HasTopDivider));

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

    public object? DescriptionContent
    {
        get => GetValue(DescriptionContentProperty);
        set => SetValue(DescriptionContentProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    public object? RowsContent
    {
        get => GetValue(RowsContentProperty);
        set => SetValue(RowsContentProperty, value);
    }

    public bool IsHeaderVisible
    {
        get => GetValue(IsHeaderVisibleProperty);
        set => SetValue(IsHeaderVisibleProperty, value);
    }

    public bool IsGrouped
    {
        get => GetValue(IsGroupedProperty);
        set => SetValue(IsGroupedProperty, value);
    }

    public bool HasTopDivider
    {
        get => GetValue(HasTopDividerProperty);
        set => SetValue(HasTopDividerProperty, value);
    }
}
