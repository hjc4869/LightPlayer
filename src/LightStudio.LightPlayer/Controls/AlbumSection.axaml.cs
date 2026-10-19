using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// One album block on the artist detail page. Hides its cover column when the
/// section becomes narrow and shows a wide art banner above the tracks instead
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
        if (change.Property != BoundsProperty || CoverPanel is null)
        {
            return;
        }

        // Narrow: the cover moves into a banner above the tracks which also carries the
        // album title and actions, leaving the header row with nothing to show.
        var wide = Bounds.Width >= CoverBreakpoint;
        CoverPanel.IsVisible = wide;
        NarrowArtBanner.IsVisible = !wide;
        HeaderBar.IsVisible = wide;
        MoveActions(toHeader: wide);
    }

    // The action row is a single instance that is re-parented instead of duplicated, so
    // its commands and tooltips stay declared in one place.
    private void MoveActions(bool toHeader)
    {
        if (toHeader == HeaderBar.Children.Contains(HeaderActions))
        {
            return;
        }

        if (toHeader)
        {
            NarrowArtBanner.ActionContent = null;
            HeaderBar.Children.Insert(HeaderBar.Children.IndexOf(HeaderTitle), HeaderActions);
        }
        else
        {
            HeaderBar.Children.Remove(HeaderActions);
            NarrowArtBanner.ActionContent = HeaderActions;
        }
    }
}
