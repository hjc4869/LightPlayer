using Avalonia.Controls;
using LightStudio.LightPlayer.Behaviors;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class PhotoSearchPageView : UserControl
{
    public PhotoSearchPageView()
    {
        InitializeComponent();
        BrowseScrollMemory.Track(this, PhotosScrollViewer);
        IncrementalLoadingScroll.Track(this, PhotosScrollViewer);
    }
}