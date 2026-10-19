using Avalonia.Controls;
using LightStudio.LightPlayer.Behaviors;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class MediaPageView : UserControl
{
    public MediaPageView()
    {
        InitializeComponent();
        BrowseScrollMemory.Track(this, MediaScrollViewer, GroupedView.ScrollViewer);
        IncrementalLoadingScroll.Track(this, MediaScrollViewer);
    }
}
