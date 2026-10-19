using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Albums page: lists albums as tiles, sorted by album, artist, or year.
/// </summary>
public sealed class AlbumsPageViewModel : GridItemsPageViewModelBase
{
    private readonly TileActionCallbacks tileActions;
    private readonly AlbumArtService albumArt;
    private readonly IUnifiedLibraryQueryService libraryQuery;

    public AlbumsPageViewModel(
        TileActionCallbacks tileActions,
        AlbumArtService albumArt,
        IUnifiedLibraryQueryService libraryQuery)
        : base(LibraryItemKind.Album)
    {
        this.tileActions = tileActions;
        this.albumArt = albumArt;
        this.libraryQuery = libraryQuery;
        TitleKey = nameof(Strings.Resources.Nav_Albums);
        LoadingTextKey = nameof(Strings.Resources.Albums_Loading);
        EmptyHeaderKey = nameof(Strings.Resources.Albums_EmptyHeader);
        EmptyDescriptionKey = PlatformServices.NativeMediaLibrary?.SupportsMusic == true
            ? nameof(Strings.Resources.Library_EmptySourcesDescription) : nameof(Strings.Resources.Albums_EmptyDescription);
    }

    protected override bool SupportsIncrementalLoading => true;

    protected override async Task<GroupedLibraryQuery<LibraryTileModel>> QueryGroupsAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken) =>
        (await libraryQuery.GetGroupedAlbumsAsync(settings, cancellationToken))
            .Select(album => AlbumTileFactory.Create(album, tileActions, albumArt));

    protected override async Task<PagedResult<LibraryTileModel>> QueryPageAsync(
        string? cursor,
        CancellationToken cancellationToken)
    {
        var page = await libraryQuery.GetAlbumsPageAsync(
            new AlbumPageRequest(ToAlbumSort(SortField), Ascending, Cursor: cursor),
            cancellationToken);
        ApplyUnifiedMetadata(page);
        foreach (var removal in page.VisibilityRemovals ?? [])
        {
            var existing = Items.FirstOrDefault(item => item.Media is { } media &&
                media.SourceId == removal.SourceId &&
                string.Equals(media.Path, removal.Path, System.StringComparison.Ordinal));
            if (existing is not null)
            {
                Items.Remove(existing);
            }
        }
        return new PagedResult<LibraryTileModel>(
            page.Items.Select(album => AlbumTileFactory.Create(album, tileActions, albumArt)).ToArray(),
            page.NextCursor,
            page.TotalCount);
    }

    protected override IComparer<LibraryTileModel> CreateComparer(LibrarySortField field, bool ascending) =>
        new LibraryTileComparer(field, ascending);

    private static AlbumSortField ToAlbumSort(LibrarySortField field) => field switch
    {
        LibrarySortField.ArtistName => AlbumSortField.ArtistName,
        LibrarySortField.Year => AlbumSortField.Year,
        _ => AlbumSortField.Title,
    };
}
