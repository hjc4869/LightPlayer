using Avalonia;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class SongsPageView : UserControl
{
    public SongsPageView()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            ApplyAdaptiveHeader(Bounds.Width);
        }
    }

    // Collapse the Album/Year/Genre header columns in lock-step with the TrackRow
    // breakpoints so the header labels stay aligned with the rows beneath them.
    private void ApplyAdaptiveHeader(double width)
    {
        if (HeaderGrid is null || HeaderGrid.ColumnDefinitions.Count < 6)
        {
            return;
        }

        // margin
        width -= 24;
        var showArtist = width >= 650;
        var showAlbum = width >= 750;
        var showYear = width >= 850;
        var showGenre = width >= 1000;

        HeaderGrid.ColumnDefinitions[1].Width = showArtist ? GridLength.Star : new GridLength(0);
        ArtistHeader.IsVisible = showArtist;
        HeaderGrid.ColumnDefinitions[2].Width = showAlbum ? GridLength.Star : new GridLength(0);
        AlbumHeader.IsVisible = showAlbum;
        HeaderGrid.ColumnDefinitions[3].Width = showYear ? new GridLength(70) : new GridLength(0);
        YearHeader.IsVisible = showYear;
        HeaderGrid.ColumnDefinitions[4].Width = showGenre ? new GridLength(90) : new GridLength(0);
        GenreHeader.IsVisible = showGenre;
    }
}
