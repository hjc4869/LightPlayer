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
        if (change.Property != BoundsProperty || AlbumArtPanel is null)
        {
            return;
        }

        // Narrow: the album art moves into a banner above the track list which also
        // carries the title and the album actions, so the whole header collapses. The
        // page back button lives in that header, so when it is the only back affordance
        // available (wide shell, narrow page -- e.g. with the queue pane open) the banner
        // shows its own overlay back button instead; that one is bound in XAML because it
        // only needs the view model flag, the narrow condition being implied by its parent.
        //
        // Everything else stays driven by the bounds change alone: reacting to the bound
        // back-button visibility would re-parent the action row while Avalonia propagates
        // inherited values into the freshly attached page, which corrupts that tree walk.
        var wide = Bounds.Width >= AlbumArtBreakpoint;
        AlbumArtPanel.IsVisible = wide;
        NarrowArtBanner.IsVisible = !wide;
        HeaderTitle.IsVisible = wide;
        HeaderBar.IsVisible = wide;
        TrackListIndent.IsVisible = wide && BackButton.IsVisible;
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
            ArtBanner.ActionContent = null;
            HeaderBar.Children.Insert(HeaderBar.Children.IndexOf(HeaderTitle), HeaderActions);
        }
        else
        {
            HeaderBar.Children.Remove(HeaderActions);
            ArtBanner.ActionContent = HeaderActions;
        }
    }
}
