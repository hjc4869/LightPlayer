using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Artists page: lists artists as tiles. Only artist-name sorting is supported.
/// Each tile composites up to nine of the artist's album covers into a square
/// collage (1x1, 2x2, or 3x3) in place of the generic artist placeholder.
/// </summary>
public sealed class ArtistsPageViewModel : LibraryBrowsePageViewModelBase<LibraryTileModel, LibraryTileGroupModel>
{
    // Covers to attempt per artist. A little above the 3x3 ceiling so a full grid
    // can still form when a few of an artist's albums have no extractable art.
    private const int MaxCollageCandidates = 12;

    private readonly TileActionCallbacks tileActions;
    private readonly AlbumArtService albumArt;

    public ArtistsPageViewModel(TileActionCallbacks tileActions, AlbumArtService albumArt)
        : base(LibraryItemKind.Artist)
    {
        this.tileActions = tileActions;
        this.albumArt = albumArt;
        Title = "Artists";
        EmptyHeader = "No artists yet";
        EmptyDescription = "Add a library folder and scan to see your artists.";
    }

    protected override async Task<IReadOnlyList<LibraryTileModel>> QueryItemsAsync()
    {
        if (GlobalLibraryCache.CachedDbArtist is null)
        {
            await GlobalLibraryCache.LoadArtistAsync();
        }

        // The collage reuses the album cache, so make sure the album list is loaded.
        if (GlobalLibraryCache.CachedDbAlbum is null)
        {
            await GlobalLibraryCache.LoadAlbumAsync();
        }

        var artists = GlobalLibraryCache.CachedDbArtist ?? [];
        var albumsByArtist = BuildAlbumsByArtist(GlobalLibraryCache.CachedDbAlbum ?? []);
        return artists.Select(artist => CreateTile(artist, albumsByArtist)).ToList();
    }

    protected override IComparer<LibraryTileModel> CreateComparer(LibrarySortField field, bool ascending) =>
        new LibraryTileComparer(field, ascending);

    protected override string GetGroupKey(LibraryTileModel item, LibrarySortField field) =>
        LibrarySortKeys.AlphaKey(item.Title);

    protected override LibraryTileGroupModel CreateGroup(string key, IReadOnlyList<LibraryTileModel> items) =>
        new(key, items);

    // Maps every artist name to their albums, keyed on both the album-artist tag
    // and the album's computed display artist, so covers are found whether or not
    // the files carry an explicit album-artist tag.
    private static Dictionary<string, List<MediaLibraryAlbum>> BuildAlbumsByArtist(IReadOnlyList<MediaLibraryAlbum> albums)
    {
        var map = new Dictionary<string, List<MediaLibraryAlbum>>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var album in albums)
        {
            Add(map, album.ArtistForQuery, album);
            if (!string.Equals(album.ArtistForQuery, album.Artist, StringComparison.CurrentCultureIgnoreCase))
            {
                Add(map, album.Artist, album);
            }
        }

        return map;

        static void Add(Dictionary<string, List<MediaLibraryAlbum>> map, string? key, MediaLibraryAlbum album)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            list.Add(album);
        }
    }

    private LibraryTileModel CreateTile(MediaLibraryArtist artist, Dictionary<string, List<MediaLibraryAlbum>> albumsByArtist)
    {
        var name = LibrarySortKeys.Fallback(artist.Name, "Unknown Artist");
        var subtitle = artist.AlbumCount == 1 ? "1 album" : $"{artist.AlbumCount} albums";

        var tile = new LibraryTileModel(name, subtitle, ThumbnailKind.Artist)
        {
            ArtistName = name,
            Identifier = artist,
        };
        tile.OpenCommand = new RelayCommand(() => tileActions.Open?.Invoke(tile));
        tile.PlayCommand = new RelayCommand(() => tileActions.Play?.Invoke(tile));
        tile.AddToQueueCommand = new RelayCommand(() => tileActions.AddToQueue?.Invoke(tile));

        var collageAlbums = SelectCollageAlbums(artist, albumsByArtist);
        if (collageAlbums.Count > 0)
        {
            tile.ArtLoader = target => LoadCollageAsync(target, collageAlbums);
        }

        return tile;
    }

    // Picks up to MaxCollageCandidates distinct albums for the artist, newest first.
    private static List<MediaLibraryAlbum> SelectCollageAlbums(
        MediaLibraryArtist artist,
        Dictionary<string, List<MediaLibraryAlbum>> albumsByArtist)
    {
        if (artist.Name is null || !albumsByArtist.TryGetValue(artist.Name, out var albums))
        {
            return [];
        }

        return albums
            .GroupBy(album => (album.Title ?? string.Empty, album.ArtistForQuery ?? string.Empty))
            .Select(group => group.First())
            .OrderByDescending(album => LibrarySortKeys.ParseLeadingInt(album.Date))
            .ThenBy(album => album.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxCollageCandidates)
            .ToList();
    }

    // Loads the candidate covers (throttled and cached inside AlbumArtService) and
    // composites the largest fully filled square they support: >=9 covers -> 3x3,
    // >=4 -> 2x2, >=1 -> 1x1. Fewer than one keeps the placeholder glyph.
    private async Task LoadCollageAsync(LibraryTileModel tile, IReadOnlyList<MediaLibraryAlbum> albums)
    {
        var bitmaps = await Task.WhenAll(albums.Select(LoadAlbumArtAsync));
        var covers = bitmaps.OfType<IImage>().ToList();
        if (covers.Count == 0)
        {
            return;
        }

        var columns = covers.Count >= 9 ? 3 : covers.Count >= 4 ? 2 : 1;
        var collage = covers.Take(columns * columns).ToList();

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply(tile, collage, columns);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Apply(tile, collage, columns));
        }

        static void Apply(LibraryTileModel target, IReadOnlyList<IImage> images, int columns)
        {
            target.CollageColumns = columns;
            target.CollageImages = images;
        }
    }

    private Task<Bitmap?> LoadAlbumArtAsync(MediaLibraryAlbum album)
    {
        // Mirror AlbumTileFactory's cache keys so covers already extracted on the
        // albums page are reused instead of re-decoded.
        var artist = LibrarySortKeys.Fallback(album.Artist, "Unknown Artist");
        var artistKey = album.ArtistForQuery ?? artist;
        var albumKey = album.Title ?? "Unknown Album";
        return albumArt.GetAlbumArtAsync(artistKey, albumKey, album.FirstFileInAlbum);
    }
}

