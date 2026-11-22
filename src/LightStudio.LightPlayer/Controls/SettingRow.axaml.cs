using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// A single settings entry with a header, optional description, and an action
/// control slot at the trailing edge.
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

    /// <summary>
    /// Width of the action beside the header, for pickers and fields that fill it. Such a row moves
    /// the action below the header when the row is too narrow for both (see <see cref="SettingRowPanel"/>).
    /// </summary>
    public static readonly StyledProperty<double> ActionWidthProperty =
        AvaloniaProperty.Register<SettingRow, double>(nameof(ActionWidth), double.NaN);

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsGroupedProperty || change.Property == RowsContentProperty ||
            change.Property == DescriptionProperty)
        {
            // A row that is a group of its own can state its description under the group.
            PseudoClasses.Set(":footed", !IsGrouped && RowsContent is null && !string.IsNullOrEmpty(Description));
        }
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

    public double ActionWidth
    {
        get => GetValue(ActionWidthProperty);
        set => SetValue(ActionWidthProperty, value);
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
