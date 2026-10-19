using Avalonia;
using Avalonia.Controls;
using LightStudio.LightPlayer.Behaviors;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class SongsPageView : UserControl
{
    public SongsPageView()
    {
        InitializeComponent();
        BrowseScrollMemory.Track(this, SongsScrollViewer, GroupedView.ScrollViewer);
        IncrementalLoadingScroll.Track(this, SongsScrollViewer);
        HeaderGrid.SizeChanged += (_, args) => ApplyAdaptiveHeader(args.NewSize.Width + 24);
    }

    private void OnTrackRowSizeChanged(object? sender, SizeChangedEventArgs args) =>
        GroupedView.MinItemHeight = args.NewSize.Width >= 650 ? 48 : 60;

    // Collapse the Album/Year/Genre header columns in lock-step with the TrackRow
    // breakpoints so the header labels stay aligned with the rows beneath them.
    private void ApplyAdaptiveHeader(double width)
    {
        if (HeaderGrid is null || HeaderGrid.ColumnDefinitions.Count < 6)
        {
            return;
        }

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
