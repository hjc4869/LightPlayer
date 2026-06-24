using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Albums page: lists albums as tiles, sorted by album, artist, or year.
/// </summary>
public sealed class AlbumsPageViewModel : LibraryBrowsePageViewModelBase<LibraryTileModel, LibraryTileGroupModel>
{
    private readonly TileActionCallbacks tileActions;
    private readonly AlbumArtService albumArt;

    public AlbumsPageViewModel(TileActionCallbacks tileActions, AlbumArtService albumArt)
        : base(LibraryItemKind.Album)
    {
        this.tileActions = tileActions;
        this.albumArt = albumArt;
        Title = "Albums";
        EmptyHeader = "No albums yet";
        EmptyDescription = "Add a library folder and scan to see your albums.";
    }

    protected override async Task<IReadOnlyList<LibraryTileModel>> QueryItemsAsync()
    {
        if (GlobalLibraryCache.CachedDbAlbum is null)
        {
            await GlobalLibraryCache.LoadAlbumAsync();
        }

        var albums = GlobalLibraryCache.CachedDbAlbum ?? [];
        return albums.Select(album => AlbumTileFactory.Create(album, tileActions, albumArt)).ToList();
    }

    protected override IComparer<LibraryTileModel> CreateComparer(LibrarySortField field, bool ascending) =>
        new LibraryTileComparer(field, ascending);

    protected override string GetGroupKey(LibraryTileModel item, LibrarySortField field) => field switch
    {
        LibrarySortField.ArtistName => LibrarySortKeys.Fallback(item.ArtistName, "Unknown Artist"),
        LibrarySortField.Year => LibrarySortKeys.Fallback(AlbumTileFactory.YearLabel(item.Year), "Unknown Year"),
        _ => LibrarySortKeys.AlphaKey(item.Title),
    };

    protected override LibraryTileGroupModel CreateGroup(string key, IReadOnlyList<LibraryTileModel> items) =>
        new(key, items);
}
