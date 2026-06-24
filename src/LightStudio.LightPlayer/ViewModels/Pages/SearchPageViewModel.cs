using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Search results page: song, album, and artist sections for a keyword. The
/// keyword arrives as a navigation parameter, and the shell re-runs the query in
/// place via <see cref="UpdateKeyword"/> when the user searches again, so the
/// search box never couples directly to this page.
/// </summary>
public sealed partial class SearchPageViewModel : PageViewModelBase
{
    private readonly LibraryCommands commands;
    private readonly AlbumArtService albumArt;
    private int queryVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private string keyword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResults))]
    [NotifyPropertyChangedFor(nameof(ShowNoResults))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoResults))]
    private bool noResults;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbums))]
    [NotifyPropertyChangedFor(nameof(AlbumsHeader))]
    private IReadOnlyList<LibraryTileModel> albumResults = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArtists))]
    [NotifyPropertyChangedFor(nameof(ArtistsHeader))]
    private IReadOnlyList<LibraryTileModel> artistResults = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSongs))]
    [NotifyPropertyChangedFor(nameof(SongsHeader))]
    private IReadOnlyList<TrackRowModel> songResults = [];

    public SearchPageViewModel(string keyword, LibraryCommands commands, AlbumArtService albumArt)
    {
        this.commands = commands;
        this.albumArt = albumArt;
        Title = "Search";
        this.keyword = keyword ?? string.Empty;
    }

    public string HeaderText =>
        string.IsNullOrWhiteSpace(Keyword) ? "Search" : $"Results for \u201c{Keyword}\u201d";

    public bool HasAlbums => AlbumResults.Count > 0;

    public bool HasArtists => ArtistResults.Count > 0;

    public bool HasSongs => SongResults.Count > 0;

    public string AlbumsHeader => $"Albums ({AlbumResults.Count})";

    public string ArtistsHeader => $"Artists ({ArtistResults.Count})";

    public string SongsHeader => $"Music ({SongResults.Count})";

    public bool ShowResults => !IsBusy && !NoResults;

    public bool ShowNoResults => !IsBusy && NoResults;

    /// <summary>Runs the initial query for the keyword supplied at navigation.</summary>
    public Task LoadAsync() => RunQueryAsync();

    /// <summary>Re-runs the search for a new keyword without recreating the page.</summary>
    public void UpdateKeyword(string newKeyword)
    {
        Keyword = newKeyword ?? string.Empty;
        _ = RunQueryAsync();
    }

    private async Task RunQueryAsync()
    {
        var version = ++queryVersion;
        var term = Keyword;

        IsBusy = true;
        NoResults = false;

        try
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                if (version != queryVersion)
                {
                    return;
                }

                AlbumResults = [];
                ArtistResults = [];
                SongResults = [];
                NoResults = true;
                return;
            }

            var results = await Task.Run(() => LibraryHelper.SearchAsync(term));
            if (version != queryVersion)
            {
                return;
            }

            AlbumResults = results.Albums.Select(a => AlbumTileFactory.Create(a, commands.AlbumTileActions, albumArt)).ToList();
            ArtistResults = results.Artists.Select(CreateArtistTile).ToList();
            SongResults = results.Files.Select(file => TrackRowFactory.CreateRow(file, commands.TrackActions, () => SongResults)).ToList();
            NoResults = AlbumResults.Count == 0 && ArtistResults.Count == 0 && SongResults.Count == 0;
        }
        catch (Exception)
        {
            if (version != queryVersion)
            {
                return;
            }

            AlbumResults = [];
            ArtistResults = [];
            SongResults = [];
            NoResults = true;
        }
        finally
        {
            if (version == queryVersion)
            {
                IsBusy = false;
            }
        }
    }

    private LibraryTileModel CreateArtistTile(MediaLibraryArtist artist)
    {
        var name = LibrarySortKeys.Fallback(artist.Name, "Unknown Artist");
        var subtitle = artist.AlbumCount == 1 ? "1 album" : $"{artist.AlbumCount} albums";
        var tile = new LibraryTileModel(name, subtitle, ThumbnailKind.Artist)
        {
            ArtistName = name,
            Identifier = artist,
        };
        tile.OpenCommand = new RelayCommand(() => commands.ArtistTileActions.Open?.Invoke(tile));
        tile.PlayCommand = new RelayCommand(() => commands.ArtistTileActions.Play?.Invoke(tile));
        tile.AddToQueueCommand = new RelayCommand(() => commands.ArtistTileActions.AddToQueue?.Invoke(tile));
        return tile;
    }
}
