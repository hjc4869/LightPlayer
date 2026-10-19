using LightMediaRenderer.Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Controls;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Artists page: lists artists as tiles. Only artist-name sorting is supported.
/// Each tile composites up to nine of the artist's album covers into a square
/// collage (1x1, 2x2, or 3x3) in place of the generic artist placeholder.
/// </summary>
public sealed class ArtistsPageViewModel : GridItemsPageViewModelBase
{
    // Covers to attempt per artist. A little above the 3x3 ceiling so a full grid
    // can still form when a few of an artist's albums have no extractable art.
    private const int MaxCollageCandidates = 12;

    private readonly TileActionCallbacks tileActions;
    private readonly AlbumArtService albumArt;
    private readonly IUnifiedLibraryQueryService libraryQuery;

    public ArtistsPageViewModel(
        TileActionCallbacks tileActions,
        AlbumArtService albumArt,
        IUnifiedLibraryQueryService libraryQuery)
        : base(LibraryItemKind.Artist)
    {
        this.tileActions = tileActions;
        this.albumArt = albumArt;
        this.libraryQuery = libraryQuery;
        TitleKey = nameof(Strings.Resources.Nav_Artists);
        LoadingTextKey = nameof(Strings.Resources.Artists_Loading);
        EmptyHeaderKey = nameof(Strings.Resources.Artists_EmptyHeader);
        EmptyDescriptionKey = PlatformServices.NativeMediaLibrary?.SupportsMusic == true
            ? nameof(Strings.Resources.Library_EmptySourcesDescription) : nameof(Strings.Resources.Artists_EmptyDescription);
    }

    protected override bool SupportsIncrementalLoading => true;

    protected override async Task<GroupedLibraryQuery<LibraryTileModel>> QueryGroupsAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken) =>
        (await libraryQuery.GetGroupedArtistsAsync(settings, cancellationToken)).Select(CreateTile);

    protected override async Task<PagedResult<LibraryTileModel>> QueryPageAsync(
        string? cursor,
        CancellationToken cancellationToken)
    {
        var page = await libraryQuery.GetArtistsPageAsync(
            new ArtistPageRequest(Ascending, Cursor: cursor),
            cancellationToken);
        ApplyUnifiedMetadata(page);
        foreach (var removal in page.VisibilityRemovals ?? [])
        {
            var existing = Items.FirstOrDefault(item =>
                item.Identifier is LibraryItemReference { UnifiedArtist: { } artist } &&
                string.Equals(
                    artist.NormalizedName,
                    removal.Path,
                    StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Items.Remove(existing);
            }
        }
        return new PagedResult<LibraryTileModel>(
            page.Items.Select(CreateTile).ToArray(),
            page.NextCursor,
            page.TotalCount);
    }

    protected override IComparer<LibraryTileModel> CreateComparer(LibrarySortField field, bool ascending) =>
        new LibraryTileComparer(field, ascending);

    private LibraryTileModel CreateTile(UnifiedArtistBrowseItem value)
    {
        var name = LibrarySortKeys.Fallback(value.Reference.DisplayName, Strings.Resources.Common_UnknownArtist);
        var subtitle = value.AlbumCount == 1
            ? Strings.Resources.Common_OneAlbum
            : string.Format(Strings.Resources.Common_AlbumsFormat, value.AlbumCount);

        var tile = new LibraryTileModel(name, subtitle, ThumbnailKind.Artist)
        {
            ArtistName = name,
            Identifier = LibraryItemReference.Artist(value.Reference),
        };
        tile.OpenCommand = new RelayCommand(() => tileActions.Open?.Invoke(tile));
        tile.PlayCommand = new RelayCommand(() => tileActions.Play?.Invoke(tile));
        tile.PlayNextCommand = new RelayCommand(() => tileActions.PlayNext?.Invoke(tile));
        tile.AddToQueueCommand = new RelayCommand(() => tileActions.AddToQueue?.Invoke(tile));
        tile.AddToPlaylistCommand = new RelayCommand(() => tileActions.AddToPlaylist?.Invoke(tile));
        tile.OpenFolderCommand = new RelayCommand(() => tileActions.OpenContainingFolder?.Invoke(tile));

        if (value.ArtworkPaths.Count > 0)
        {
            tile.ArtLoader = (target, token) => LoadCollageAsync(target, value.ArtworkPaths, token);
        }

        return tile;
    }

    // Loads the candidate covers (throttled inside AlbumArtService) and composites the largest
    // fully filled square they support: >=9 covers -> 3x3, >=4 -> 2x2, >=1 -> 1x1. Fewer than one
    // keeps the placeholder glyph.
    private async Task LoadCollageAsync(
        LibraryTileModel tile,
        IReadOnlyList<Sourced<string>> artworkPaths,
        CancellationToken cancellationToken)
    {
        var candidates = Math.Min(artworkPaths.Count, MaxCollageCandidates);
        var fifoWindowSize = tile.ArtLoadFifoWindowSize * candidates;

        // Each cover fills one cell of the collage the candidates are expected to complete.
        var expectedColumns = CollageColumns(candidates);
        var pixelSize = (tile.ArtPixelSize + expectedColumns - 1) / expectedColumns;
        var bitmaps = await Task.WhenAll(
            artworkPaths.Take(MaxCollageCandidates).Select(
                path => LoadAlbumArtAsync(path, fifoWindowSize, pixelSize, cancellationToken, tile.ArtCachePool)))
            .ConfigureAwait(false);
        var covers = bitmaps.OfType<IImage>().ToList();
        if (covers.Count == 0)
        {
            return;
        }

        var columns = CollageColumns(covers.Count);
        var collage = covers.Take(columns * columns).ToList();

        // Candidates beyond the square the collage can fill are never shown.
        AlbumArtService.ReleaseImages(covers.Skip(collage.Count));

        AlbumArtService.ApplyOnUiThread(
            collage,
            images =>
            {
                tile.CollageColumns = columns;
                tile.CollageImages = images;
            },
            cancellationToken);
    }

    private static int CollageColumns(int covers) => covers >= 9 ? 3 : covers >= 4 ? 2 : 1;

    private Task<FastImageSource?> LoadAlbumArtAsync(
        Sourced<string> path,
        int fifoWindowSize,
        int pixelSize,
        CancellationToken cancellationToken,
        FastImageCache? cachePool)
    {
        return albumArt.GetLowQualityTrackArtAsync(
            new MediaReference(path.SourceId, path.Value),
            fifoWindowSize,
            pixelSize,
            cancellationToken,
            cachePool);
    }
}

