using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class NowPlayingPageView : UserControl
{
    public NowPlayingPageView()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            ApplyAdaptiveLayout(Bounds.Width);
        }
    }

    // Collapse the metadata then the upcoming column as the page narrows, mirroring
    // the original now-playing view's 1100 / 750 px width breakpoints.
    private void ApplyAdaptiveLayout(double width)
    {
        if (RootGrid is null || MetadataPanel is null || UpcomingPanel is null ||
            RootGrid.ColumnDefinitions.Count < 3)
        {
            return;
        }

        var showMetadata = width > 750;
        var showUpcoming = width > 1100;

        RootGrid.ColumnDefinitions[0].Width = showMetadata ? new GridLength(270) : new GridLength(0);
        MetadataPanel.IsVisible = showMetadata;
        RootGrid.ColumnDefinitions[2].Width = showUpcoming ? new GridLength(360) : new GridLength(0);
        UpcomingPanel.IsVisible = showUpcoming;
    }
}
