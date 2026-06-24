using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// One album block on the artist detail page. Hides its cover column when the
/// section becomes narrow
/// </summary>
public partial class AlbumSection : UserControl
{
    // Below this content width the cover column is dropped so the track rows use
    // the full width. Matches the 650px wide/narrow breakpoint used by TrackRow.
    private const double CoverBreakpoint = 650;

    public AlbumSection()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && CoverPanel is not null)
        {
            CoverPanel.IsVisible = Bounds.Width >= CoverBreakpoint;
        }
    }
}
