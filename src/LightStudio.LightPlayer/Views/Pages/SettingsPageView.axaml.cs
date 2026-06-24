using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class SettingsPageView : UserControl
{
    private const double CompactThreshold = 600;

    public SettingsPageView()
    {
        InitializeComponent();
        SectionsList.SelectionChanged += OnSelectionChanged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            ApplyAdaptiveLayout(Bounds.Width);
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ApplyAdaptiveLayout(Bounds.Width);

    private void OnBackClick(object? sender, RoutedEventArgs e) =>
        SectionsList.SelectedItem = null;

    // The section list and the selected section sit side by side while there
    // is room, then collapse to a single pane with a back button on narrow
    // screens. A null selection means "show the list".
    private void ApplyAdaptiveLayout(double width)
    {
        if (RootGrid is null || SectionsList is null || DetailPane is null ||
            BackButton is null || width <= 0)
        {
            return;
        }

        var isCompact = width < CompactThreshold;
        var hasSelection = SectionsList.SelectedItem is not null;

        if (!isCompact)
        {
            RootGrid.ColumnDefinitions[0].Width = new GridLength(260);
            RootGrid.ColumnDefinitions[1].Width = GridLength.Star;
            SectionsList.IsVisible = true;
            DetailPane.IsVisible = true;
            DetailPane.Margin = new Thickness(24, 0, 0, 0);
            BackButton.IsVisible = false;

            // The wide layout always shows a section, as the original did.
            if (!hasSelection && SectionsList.ItemCount > 0)
            {
                SectionsList.SelectedIndex = 0;
            }

            return;
        }

        BackButton.IsVisible = hasSelection;
        DetailPane.Margin = new Thickness(0);
        if (hasSelection)
        {
            RootGrid.ColumnDefinitions[0].Width = new GridLength(0);
            RootGrid.ColumnDefinitions[1].Width = GridLength.Star;
            SectionsList.IsVisible = false;
            DetailPane.IsVisible = true;
        }
        else
        {
            RootGrid.ColumnDefinitions[0].Width = GridLength.Star;
            RootGrid.ColumnDefinitions[1].Width = new GridLength(0);
            SectionsList.IsVisible = true;
            DetailPane.IsVisible = false;
        }
    }
}
