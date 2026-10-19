#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace LightStudio.MediaLibraryCore.Database;

public sealed class LibraryQueryService : ILibraryQueryService,
    ILibraryBrowseIndexQuery<DbMediaFile>, ILibraryBrowseIndexQuery<MediaLibraryAlbum>,
    ILibraryBrowseIndexQuery<MediaLibraryArtistBrowseItem>
{
    private readonly Func<MediaLibraryDatabase>? databaseFactory;
    private readonly Func<AlbumGroupingMode>? albumGrouping;
    private readonly LibraryBrowseIndexCache<DbMediaFile> trackIndex;
    private readonly LibraryBrowseIndexCache<MediaLibraryAlbum> albumIndex;
    private readonly LibraryBrowseIndexCache<MediaLibraryArtistBrowseItem> artistIndex;

    public LibraryQueryService(Func<AlbumGroupingMode>? albumGrouping = null)
    {
        this.albumGrouping = albumGrouping;
        trackIndex = LibraryBrowseIndexes.Tracks(this);
        albumIndex = LibraryBrowseIndexes.Albums(this);
        artistIndex = LibraryBrowseIndexes.Artists(this);
    }

    public LibraryQueryService(Func<MediaLibraryDatabase> databaseFactory, Func<AlbumGroupingMode>? albumGrouping = null)
        : this(albumGrouping)
    {
        this.databaseFactory = databaseFactory ?? throw new ArgumentNullException(nameof(databaseFactory));
    }

    Task<LibraryBrowseIndex> ILibraryBrowseIndexQuery<DbMediaFile>.GetBrowseIndexAsync(
        LibraryBrowseIndexRequest request, CancellationToken cancellationToken) =>
        trackIndex.GetBrowseIndexAsync(request, cancellationToken);

    Task<DbMediaFile[]> ILibraryBrowseIndexQuery<DbMediaFile>.GetBrowseItemsAsync(
        LibraryBrowseItemsRequest request, CancellationToken cancellationToken) =>
        trackIndex.GetBrowseItemsAsync(request, cancellationToken);

    Task<LibraryBrowseIndex> ILibraryBrowseIndexQuery<MediaLibraryAlbum>.GetBrowseIndexAsync(
        LibraryBrowseIndexRequest request, CancellationToken cancellationToken) =>
        albumIndex.GetBrowseIndexAsync(request, cancellationToken);

    Task<MediaLibraryAlbum[]> ILibraryBrowseIndexQuery<MediaLibraryAlbum>.GetBrowseItemsAsync(
        LibraryBrowseItemsRequest request, CancellationToken cancellationToken) =>
        albumIndex.GetBrowseItemsAsync(request, cancellationToken);

    Task<LibraryBrowseIndex> ILibraryBrowseIndexQuery<MediaLibraryArtistBrowseItem>.GetBrowseIndexAsync(
        LibraryBrowseIndexRequest request, CancellationToken cancellationToken) =>
        artistIndex.GetBrowseIndexAsync(request, cancellationToken);

    Task<MediaLibraryArtistBrowseItem[]> ILibraryBrowseIndexQuery<MediaLibraryArtistBrowseItem>.GetBrowseItemsAsync(
        LibraryBrowseItemsRequest request, CancellationToken cancellationToken) =>
        artistIndex.GetBrowseItemsAsync(request, cancellationToken);

    public async Task<IReadOnlyList<MediaLibraryAlbum>> GetAlbumsAsync(string? artist = null) =>
        await QueryAsync(database => database.GetAlbumsAsync(artist)).ConfigureAwait(false);

    public Task<PagedResult<MediaLibraryAlbum>> GetAlbumsPageAsync(
        AlbumPageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        return QueryAsync(database => database.GetAlbumsPageAsync(request, cancellationToken));
    }

    public async Task<IReadOnlyList<MediaLibraryArtist>> GetArtistsAsync() =>
        await QueryAsync(database => database.GetArtistsAsync()).ConfigureAwait(false);

    public Task<PagedResult<MediaLibraryArtistBrowseItem>> GetArtistsPageAsync(
        ArtistPageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        return QueryAsync(database => database.GetArtistsPageAsync(request, cancellationToken));
    }

    public async Task<IReadOnlyList<DbMediaFile>> GetMediaFilesAsync() =>
        await QueryAsync(database => database.GetAllMediaFilesAsync()).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetMediaFolderPathsAsync(CancellationToken cancellationToken = default) =>
        await QueryAsync(database => database.GetMediaFolderPathsAsync(cancellationToken)).ConfigureAwait(false);

    public Task<PagedResult<DbMediaFile>> GetMediaFilesPageAsync(
        MediaFilePageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        return QueryAsync(database => database.GetMediaFilesPageAsync(request, cancellationToken));
    }

    public Task<PagedResult<DbMediaFile>> GetShuffledMediaFilesPageAsync(
        ShuffledMediaFilePageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        return QueryAsync(database => database.GetShuffledMediaFilesPageAsync(request, cancellationToken));
    }

    public Task<MediaLibraryAlbum> GetAlbumByNameAsync(string? albumArtist, string? albumName) =>
        GetAlbumByNameAsync(albumArtist, albumName, albumGroupingKey: null);

    public async Task<MediaLibraryAlbum> GetAlbumByNameAsync(string? albumArtist, string? albumName, string? albumGroupingKey) =>
        await QueryAsync(database => database.GetAlbumAsync(albumArtist, albumName, albumGroupingKey)).ConfigureAwait(false)
            ?? throw new LibraryItemNotFoundException($"Album {albumName} from artist {albumArtist} not found.");

    public async Task<MediaLibraryAlbum> GetAlbumByTrackPathAsync(string path)
    {
        var album = await QueryAsync(async database =>
        {
            var file = await database.GetMediaFileByPathAsync(path).ConfigureAwait(false);
            return file is null || string.IsNullOrWhiteSpace(file.Album)
                ? null
                : await database.GetAlbumAsync(file.AlbumArtist, file.Album, file.AlbumGroupingKey).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return album ?? throw new LibraryItemNotFoundException($"No album found for the file at {path}.");
    }

    public async Task<MediaLibraryArtist> GetArtistByNameAsync(string? name) =>
        await QueryAsync(database => database.GetArtistAsync(name)).ConfigureAwait(false)
            ?? throw new LibraryItemNotFoundException($"Artist {name} not found.");

    public async Task<MediaLibraryArtist> GetArtistByTrackPathAsync(string path)
    {
        var artist = await QueryAsync(async database =>
        {
            var file = await database.GetMediaFileByPathAsync(path).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(file?.Artist)
                ? null
                : await database.GetArtistAsync(file.Artist).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return artist ?? throw new LibraryItemNotFoundException($"No artist found for the file at {path}.");
    }

    public async Task<bool> ArtistExistsAsync(string? name) =>
        !string.IsNullOrWhiteSpace(name) && await QueryAsync(database => database.ArtistExistsAsync(name)).ConfigureAwait(false);

    public Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByAlbumAsync(string? albumName, string? artistName) =>
        GetMediaFilesByAlbumAsync(albumName, artistName, albumGroupingKey: null);

    public async Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByAlbumAsync(string? albumName, string? artistName, string? albumGroupingKey) =>
        await QueryAsync(database => database.GetMediaFilesByAlbumAsync(albumName, artistName, albumGroupingKey)).ConfigureAwait(false);

    public async Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByArtistAsync(MediaLibraryArtist artist) =>
        await QueryAsync(database => database.GetMediaFilesByArtistAsync(artist.Name)).ConfigureAwait(false);

    public async Task<LibrarySearchResults> SearchAsync(string keyword)
    {
        var results = new LibrarySearchResults();
        var normalizedKeyword = keyword?.ToLower().Trim();
        if (string.IsNullOrEmpty(normalizedKeyword))
        {
            return results;
        }

        var likePattern = $"%{normalizedKeyword}%";
        var (albums, artists, files) = await QueryAsync(async database => (
            await database.SearchAlbumsAsync(likePattern).ConfigureAwait(false),
            await database.SearchArtistsAsync(likePattern).ConfigureAwait(false),
            await database.SearchMediaFilesAsync(likePattern).ConfigureAwait(false))).ConfigureAwait(false);

        results.Albums = albums
            .OrderByDescending(album => (album.Title ?? string.Empty).ToLower().Similarity(normalizedKeyword)).ToList();
        results.Artists = artists
            .OrderByDescending(artist => (artist.Name ?? string.Empty).ToLower().Similarity(normalizedKeyword)).ToList();
        results.Files = files
            .OrderByDescending(file => (file.Title ?? string.Empty).ToLower().Similarity(normalizedKeyword)).ToList();
        return results;
    }

    private async Task<TResult> QueryAsync<TResult>(Func<MediaLibraryDatabase, Task<TResult>> query)
    {
        if (databaseFactory is not null)
        {
            await using var database = databaseFactory();
            if (albumGrouping is not null)
            {
                database.AlbumGrouping = albumGrouping();
            }
            return await query(database).ConfigureAwait(false);
        }

        await using var scope = await ApplicationServiceBase.App.GetScopeAsync().ConfigureAwait(false);
        var scopedDatabase = scope.ServiceProvider.GetRequiredService<MediaLibraryDatabase>();
        if (albumGrouping is not null)
        {
            scopedDatabase.AlbumGrouping = albumGrouping();
        }
        return await query(scopedDatabase).ConfigureAwait(false);
    }
}