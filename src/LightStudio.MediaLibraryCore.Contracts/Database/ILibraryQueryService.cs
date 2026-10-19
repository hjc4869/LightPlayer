#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.MediaLibraryCore.Database;

/// <summary>Reads library data for browse, detail, search, and queue workflows.</summary>
public interface ILibraryQueryService
{
    Task<IReadOnlyList<MediaLibraryAlbum>> GetAlbumsAsync(string? artist = null);
    Task<PagedResult<MediaLibraryAlbum>> GetAlbumsPageAsync(
        AlbumPageRequest request,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaLibraryArtist>> GetArtistsAsync();
    Task<PagedResult<MediaLibraryArtistBrowseItem>> GetArtistsPageAsync(
        ArtistPageRequest request,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DbMediaFile>> GetMediaFilesAsync();
    Task<IReadOnlyList<string>> GetMediaFolderPathsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
    Task<PagedResult<DbMediaFile>> GetMediaFilesPageAsync(
        MediaFilePageRequest request,
        CancellationToken cancellationToken = default);
    Task<PagedResult<DbMediaFile>> GetShuffledMediaFilesPageAsync(
        ShuffledMediaFilePageRequest request,
        CancellationToken cancellationToken = default);
    Task<MediaLibraryAlbum> GetAlbumByNameAsync(string? albumArtist, string? albumName);
    Task<MediaLibraryAlbum> GetAlbumByNameAsync(string? albumArtist, string? albumName, string? albumGroupingKey) =>
        GetAlbumByNameAsync(albumArtist, albumName);
    Task<MediaLibraryAlbum> GetAlbumByTrackPathAsync(string path);
    Task<MediaLibraryArtist> GetArtistByNameAsync(string? name);
    Task<MediaLibraryArtist> GetArtistByTrackPathAsync(string path);
    Task<bool> ArtistExistsAsync(string? name);
    Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByAlbumAsync(string? albumName, string? artistName);
    Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByAlbumAsync(string? albumName, string? artistName, string? albumGroupingKey) =>
        GetMediaFilesByAlbumAsync(albumName, artistName);
    Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByArtistAsync(MediaLibraryArtist artist);
    Task<LibrarySearchResults> SearchAsync(string keyword);
}