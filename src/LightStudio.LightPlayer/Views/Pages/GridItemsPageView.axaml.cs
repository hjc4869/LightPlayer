using Avalonia.Controls;
using LightStudio.LightPlayer.Behaviors;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class GridItemsPageView : UserControl
{
    public GridItemsPageView()
    {
        InitializeComponent();
        BrowseScrollMemory.Track(this, FlatScrollViewer, GroupedView.ScrollViewer);
        IncrementalLoadingScroll.Track(this, FlatScrollViewer);
    }
}
