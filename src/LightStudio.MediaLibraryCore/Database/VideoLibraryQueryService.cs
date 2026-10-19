#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;
using Microsoft.Extensions.DependencyInjection;

namespace LightStudio.MediaLibraryCore.Database;

/// <inheritdoc cref="IVideoLibraryQueryService" />
public sealed class VideoLibraryQueryService : IVideoLibraryQueryService, ILibraryBrowseIndexQuery<DbVideoFile>,
    IMediaLibraryQueryService
{
    private readonly ThumbnailMemoryCache thumbnails;
    private readonly Func<MediaLibraryDatabase>? databaseFactory;
    private readonly IMultiMediaPlatformIO? platform;
    private readonly LibraryBrowseIndexCache<DbVideoFile> browseIndex;
    private readonly LibraryBrowseIndexCache<IndexedMediaFile> mediaBrowseIndex;

    public VideoLibraryQueryService(ThumbnailMemoryCache? thumbnails = null)
    {
        this.thumbnails = thumbnails ?? ThumbnailMemoryCache.Shared;
        browseIndex = LibraryBrowseIndexes.Videos(this, readLocalCreationTime: true);
        mediaBrowseIndex = LibraryBrowseIndexes.Media(this, readLocalCreationTime: true);
    }

    public VideoLibraryQueryService(
        Func<MediaLibraryDatabase> databaseFactory,
        IMultiMediaPlatformIO platform,
        ThumbnailMemoryCache? thumbnails = null)
        : this(thumbnails)
    {
        this.databaseFactory = databaseFactory ?? throw new ArgumentNullException(nameof(databaseFactory));
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public Task<LibraryBrowseIndex> GetBrowseIndexAsync(
        LibraryBrowseIndexRequest request, CancellationToken cancellationToken = default) =>
        browseIndex.GetBrowseIndexAsync(request, cancellationToken);

    public Task<DbVideoFile[]> GetBrowseItemsAsync(
        LibraryBrowseItemsRequest request, CancellationToken cancellationToken = default) =>
        browseIndex.GetBrowseItemsAsync(request, cancellationToken);

    Task<LibraryBrowseIndex> ILibraryBrowseIndexQuery<IndexedMediaFile>.GetBrowseIndexAsync(
        LibraryBrowseIndexRequest request, CancellationToken cancellationToken) =>
        mediaBrowseIndex.GetBrowseIndexAsync(request, cancellationToken);

    Task<IndexedMediaFile[]> ILibraryBrowseIndexQuery<IndexedMediaFile>.GetBrowseItemsAsync(
        LibraryBrowseItemsRequest request, CancellationToken cancellationToken) =>
        mediaBrowseIndex.GetBrowseItemsAsync(request, cancellationToken);

    public Task<PagedResult<IndexedMediaFile>> GetMediaItemsPageAsync(
        PageRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        return QueryAsync(database => database.GetMediaItemsPageAsync(request, cancellationToken));
    }

    public async Task<IReadOnlyList<DbImageFile>> GetImagesInFolderAsync(
        string folderPath, CancellationToken cancellationToken = default) =>
        await QueryAsync(database => database.GetImageFilesInFolderAsync(folderPath, cancellationToken)).ConfigureAwait(false);

    public async Task<IReadOnlyList<DbImageFile>> SearchImagesAsync(
        string keyword, CancellationToken cancellationToken = default)
    {
        var normalized = keyword?.Trim();
        if (string.IsNullOrEmpty(normalized))
            return [];

        var likePattern = $"%{normalized.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
        return await QueryAsync(database => database.SearchImageFilesAsync(likePattern, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DbVideoFile>> GetVideosAsync() =>
        await QueryAsync(database => database.GetAllVideoFilesAsync()).ConfigureAwait(false);

    public Task<PagedResult<DbVideoFile>> GetVideosPageAsync(
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        return QueryAsync(database => database.GetVideoFilesPageAsync(request, cancellationToken));
    }

    public async Task<IReadOnlyList<DbVideoFile>> GetVideosInFolderAsync(
        string folderPath,
        CancellationToken cancellationToken = default) =>
        await QueryAsync(database => database.GetVideoFilesInFolderAsync(folderPath, cancellationToken)).ConfigureAwait(false);

    public async Task<IReadOnlyList<DbVideoFile>> GetVideoPicksAsync(
        VideoPickOrder order,
        int limit,
        CancellationToken cancellationToken = default) =>
        limit <= 0
            ? []
            : await QueryAsync(database => database.GetVideoFilePicksAsync(order, limit, cancellationToken)).ConfigureAwait(false);

    public async Task<IReadOnlyList<VideoFolderCount>> GetVideoFolderCountsAsync() =>
        await QueryAsync(database => database.GetVideoFolderCountsAsync()).ConfigureAwait(false);

    public async Task<IReadOnlyList<DbVideoFile>> SearchVideosAsync(
        string keyword,
        CancellationToken cancellationToken = default)
    {
        var normalized = keyword?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return [];
        }

        var likePattern = $"%{normalized}%";
        return await QueryAsync(database => database.SearchVideoFilesAsync(likePattern, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<DbVideoFile?> GetVideoByPathAsync(string path) =>
        await QueryAsync(database => database.GetVideoFileByPathAsync(path)).ConfigureAwait(false);

    public async Task<byte[]?> GetVideoPreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var cached = await thumbnails.TryGetAsync(ThumbnailCacheKind.VideoPreview, path, cancellationToken)
            .ConfigureAwait(false);
        if (cached.Found)
        {
            return cached.Thumbnail;
        }

        VideoProbeInfo? probe;
        try
        {
            probe = await (platform ?? IMultiMediaPlatformIO.Instance)
                .ProbeVideoAsync(path, withPreview: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A file that cannot be opened has no preview, which the tile draws its placeholder for.
            probe = null;
        }

        byte[]? preview = probe?.Preview is { Length: > 0 } bytes ? bytes : null;
        await thumbnails.SetAsync(ThumbnailCacheKind.VideoPreview, path, preview, cancellationToken)
            .ConfigureAwait(false);
        return preview;
    }

    private async Task<TResult> QueryAsync<TResult>(Func<MediaLibraryDatabase, Task<TResult>> query)
    {
        if (databaseFactory is not null)
        {
            await using var database = databaseFactory();
            return await query(database).ConfigureAwait(false);
        }

        await using var scope = await ApplicationServiceBase.App.GetScopeAsync().ConfigureAwait(false);
        var scopedDatabase = scope.ServiceProvider.GetRequiredService<MediaLibraryDatabase>();
        return await query(scopedDatabase).ConfigureAwait(false);
    }
}
