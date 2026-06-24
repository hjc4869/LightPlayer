using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class AlbumDetailPageView : UserControl
{
    // Below this page width the album art column is dropped so the track list uses
    // the full width.
    private const double AlbumArtBreakpoint = 700;

    public AlbumDetailPageView()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && AlbumArtPanel is not null)
        {
            AlbumArtPanel.IsVisible = Bounds.Width >= AlbumArtBreakpoint;
        }
    }
}
