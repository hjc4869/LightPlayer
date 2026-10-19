using Avalonia.Controls;
using LightStudio.LightPlayer.Behaviors;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class MusicSearchPageView : UserControl
{
    public MusicSearchPageView()
    {
        InitializeComponent();
        BrowseScrollMemory.Track(this, TilesScrollViewer, TracksScrollViewer);
        IncrementalLoadingScroll.Track(this, TilesScrollViewer);
        IncrementalLoadingScroll.Track(this, TracksScrollViewer);
    }
}