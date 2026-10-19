using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.Services;

public sealed record UnifiedSourceFailure(
    LibrarySourceId SourceId,
    string SourceName,
    string Message,
    bool Retryable,
    DateTimeOffset? RetryAfterUtc = null);

public sealed record UnifiedVisibilityRemoval(
    string ItemKind,
    LibrarySourceId SourceId,
    string Path,
    int? StartTimeMs = null);

public sealed record UnifiedPage<T>(
    T[] Items,
    string? NextCursor,
    int? TotalCount = null,
    bool TotalCountIsProvisional = false,
    bool OrderingIsProvisional = false,
    IReadOnlyList<UnifiedSourceFailure>? SourceFailures = null,
    IReadOnlyList<UnifiedVisibilityRemoval>? VisibilityRemovals = null,
    DateTimeOffset? RetryAfterUtc = null);

public sealed record UnifiedArtistReference(
    string NormalizedName,
    string DisplayName,
    IReadOnlyList<LibrarySourceId> SourceIds,
    IReadOnlyDictionary<LibrarySourceId, string> SourceNames);

public sealed record UnifiedArtistBrowseItem(
    UnifiedArtistReference Reference,
    int FileCount,
    int AlbumCount,
    DateTimeOffset DatabaseItemAddedDate,
    IReadOnlyList<Sourced<string>> ArtworkPaths);

public sealed record UnifiedLibrarySearchResults(
    IReadOnlyList<Sourced<MediaLibraryAlbum>> Albums,
    IReadOnlyList<UnifiedArtistReference> Artists,
    IReadOnlyList<Sourced<DbMediaFile>> Files,
    IReadOnlyList<UnifiedSourceFailure> SourceFailures);

public sealed record UnifiedCollection<T>(
    IReadOnlyList<T> Items,
    IReadOnlyList<UnifiedSourceFailure> SourceFailures,
    bool OrderingIsProvisional = false);

public interface IUnifiedLibraryQueryService
{
    Task<GroupedLibraryQuery<Sourced<DbMediaFile>>> GetGroupedTracksAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken = default,
        LibrarySourceId? sourceId = null, string? folderPath = null) => throw new NotSupportedException();

    Task<GroupedLibraryQuery<Sourced<MediaLibraryAlbum>>> GetGroupedAlbumsAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<GroupedLibraryQuery<UnifiedArtistBrowseItem>> GetGroupedArtistsAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<UnifiedPage<Sourced<DbMediaFile>>> GetMediaFilesPageAsync(
        MediaFilePageRequest request,
        CancellationToken cancellationToken = default);

    Task<UnifiedPage<Sourced<MediaLibraryAlbum>>> GetAlbumsPageAsync(
        AlbumPageRequest request,
        CancellationToken cancellationToken = default);

    Task<UnifiedPage<UnifiedArtistBrowseItem>> GetArtistsPageAsync(
        ArtistPageRequest request,
        CancellationToken cancellationToken = default);

    Task<UnifiedPage<Sourced<DbMediaFile>>> GetShuffledMediaFilesPageAsync(
        ShuffledMediaFilePageRequest request,
        CancellationToken cancellationToken = default);

    Task<UnifiedLibrarySearchResults> SearchAsync(
        string keyword,
        CancellationToken cancellationToken = default);

    Task<Sourced<MediaLibraryAlbum>> GetAlbumAsync(
        LibrarySourceId sourceId,
        string? albumArtist,
        string? albumName,
        CancellationToken cancellationToken = default,
        string? albumGroupingKey = null);

    Task<IReadOnlyList<Sourced<DbMediaFile>>> GetAlbumTracksAsync(
        LibrarySourceId sourceId,
        string? albumName,
        string? artistName,
        CancellationToken cancellationToken = default,
        string? albumGroupingKey = null);

    Task<Sourced<MediaLibraryAlbum>> GetAlbumByTrackAsync(
        MediaReference media,
        CancellationToken cancellationToken = default);

    Task<UnifiedArtistReference> GetArtistByTrackAsync(
        MediaReference media,
        CancellationToken cancellationToken = default);

    Task<UnifiedCollection<Sourced<DbMediaFile>>> GetArtistTracksAsync(
        UnifiedArtistReference artist,
        CancellationToken cancellationToken = default);

    Task<UnifiedCollection<Sourced<MediaLibraryAlbum>>> GetArtistAlbumsAsync(
        UnifiedArtistReference artist,
        CancellationToken cancellationToken = default);
}

public interface IUnifiedVideoLibraryQueryService
{
    Task<GroupedLibraryQuery<Sourced<IndexedMediaFile>>> GetGroupedMediaAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<UnifiedPage<Sourced<IndexedMediaFile>>> GetMediaItemsPageAsync(
        PageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<IReadOnlyList<Sourced<DbImageFile>>> GetImagesInFolderAsync(
        LibrarySourceId sourceId, string folderPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Sourced<DbImageFile>>>([]);

    Task<GroupedLibraryQuery<Sourced<DbVideoFile>>> GetGroupedVideosAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<UnifiedPage<Sourced<DbVideoFile>>> GetVideosPageAsync(
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// At most <paramref name="limit"/> videos from every configured source, merged into one list in
    /// the requested order. Used by the shortcut rows that sample the library rather than browse it.
    /// </summary>
    Task<UnifiedCollection<Sourced<DbVideoFile>>> GetVideoPicksAsync(
        VideoPickOrder order,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists one source's folder as it is now, so a file added since the last scan is still there
    /// to browse to. Null lists the source's library roots.
    /// </summary>
    Task<VideoFolderListing> BrowseFolderAsync(
        LibrarySourceId sourceId,
        string? folderPath,
        CancellationToken cancellationToken = default);

    Task<UnifiedCollection<Sourced<DbVideoFile>>> SearchVideosAsync(
        string keyword,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Sourced<DbVideoFile>>> GetVideosInFolderAsync(
        LibrarySourceId sourceId,
        string folderPath,
        CancellationToken cancellationToken = default);

    Task<Sourced<DbVideoFile>?> GetVideoByPathAsync(
        MediaReference media,
        CancellationToken cancellationToken = default);

    Task<byte[]?> GetVideoPreviewAsync(
        MediaReference media,
        CancellationToken cancellationToken = default);
}
