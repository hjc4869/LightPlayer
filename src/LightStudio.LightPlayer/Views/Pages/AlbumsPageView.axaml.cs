using Avalonia.Controls;
using LightStudio.LightPlayer.Behaviors;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class AlbumsPageView : UserControl
{
    public AlbumsPageView()
    {
        InitializeComponent();
        BrowseScrollMemory.Track(this, FlatScrollViewer, GroupedScrollViewer);
    }
}
