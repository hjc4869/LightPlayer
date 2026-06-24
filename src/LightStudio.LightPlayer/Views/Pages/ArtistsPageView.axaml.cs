using Avalonia.Controls;
using LightStudio.LightPlayer.Behaviors;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class ArtistsPageView : UserControl
{
    public ArtistsPageView()
    {
        InitializeComponent();
        BrowseScrollMemory.Track(this, FlatScrollViewer, GroupedScrollViewer);
    }
}
