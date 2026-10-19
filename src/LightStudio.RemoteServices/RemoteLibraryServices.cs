using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.RemoteServices;

internal sealed class RemoteBrowseIndexQuery<T>(RemoteApiClient client, string path) : ILibraryBrowseIndexQuery<T>
{
    public async Task<LibraryBrowseIndex> GetBrowseIndexAsync(
        LibraryBrowseIndexRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await client.GetAsync<LibraryBrowseIndex>(path + "/index" +
                (request.FolderPath is null ? string.Empty : "?folder=" + Uri.EscapeDataString(request.FolderPath)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            throw new NotSupportedException("The remote library does not provide browse indexes.", exception);
        }
    }

    public async Task<T[]> GetBrowseItemsAsync(
        LibraryBrowseItemsRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        try
        {
            return await client.PostAsync<LibraryBrowseItemsRequest, T[]>(path + "/index/items", request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Gone)
        {
            throw new LibraryBrowseIndexExpiredException(exception);
        }
    }
}

public sealed class RemotePhotoSearchService(RemoteApiClient client) : IPhotoSearchService
{
    public async Task<PhotoSearchPage> SearchAsync(string query, int pageSize = 20, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var path = $"photos/search?q={Uri.EscapeDataString(query)}&limit={pageSize}";
        if (cursor is not null)
            path += "&cursor=" + Uri.EscapeDataString(cursor);
        try
        {
            return await client.GetAsync<PhotoSearchPage>(path, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return new PhotoSearchPage([], null, false);
        }
    }
}

public sealed class RemoteMusicFolderBrowser(RemoteApiClient client) : IMusicFolderBrowser
{
    public Task<MusicFolderListing> ListAsync(string? folderPath, CancellationToken cancellationToken = default) =>
        client.GetAsync<MusicFolderListing>("music/browse" +
            (folderPath is null ? string.Empty : $"?path={Uri.EscapeDataString(folderPath)}"), cancellationToken);
}

public sealed class RemoteLibraryLocationService(
    IReadOnlyList<string> folders,
    IReadOnlyList<string> videoFolders) : ILibraryLocationService
{
    public IReadOnlyList<string> GetLibraryFolderPaths() => folders;

    public IReadOnlyList<string> GetVideoFolderPaths() => videoFolders;
}

/// <param name="imageCache">
/// Where downloaded preview frames are kept, or <see langword="null"/> on heads whose HTTP stack
/// already caches responses (the browser).
/// </param>
public sealed class RemoteVideoLibraryQueryService(RemoteApiClient client, RemoteImageCache? imageCache = null)
    : IVideoLibraryQueryService, ILibraryBrowseIndexQuery<DbVideoFile>, IMediaLibraryQueryService
{
    private readonly RemoteBrowseIndexQuery<DbVideoFile> browseIndex = new(client, "videos");
    private readonly RemoteBrowseIndexQuery<IndexedMediaFile> mediaBrowseIndex = new(client, "media");
    private bool supportsMedia = true;
    private bool supportsMediaBrowseIndex = true;

    public Task<LibraryBrowseIndex> GetBrowseIndexAsync(
        LibraryBrowseIndexRequest request, CancellationToken cancellationToken = default) =>
        browseIndex.GetBrowseIndexAsync(request, cancellationToken);

    public Task<DbVideoFile[]> GetBrowseItemsAsync(
        LibraryBrowseItemsRequest request, CancellationToken cancellationToken = default) =>
        browseIndex.GetBrowseItemsAsync(request, cancellationToken);

    async Task<LibraryBrowseIndex> ILibraryBrowseIndexQuery<IndexedMediaFile>.GetBrowseIndexAsync(
        LibraryBrowseIndexRequest request, CancellationToken cancellationToken)
    {
        if (supportsMediaBrowseIndex)
        {
            try
            {
                return await mediaBrowseIndex.GetBrowseIndexAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                supportsMediaBrowseIndex = false;
            }
        }

        return await browseIndex.GetBrowseIndexAsync(request, cancellationToken).ConfigureAwait(false);
    }

    async Task<IndexedMediaFile[]> ILibraryBrowseIndexQuery<IndexedMediaFile>.GetBrowseItemsAsync(
        LibraryBrowseItemsRequest request, CancellationToken cancellationToken) => supportsMediaBrowseIndex
        ? await mediaBrowseIndex.GetBrowseItemsAsync(request, cancellationToken).ConfigureAwait(false)
        : (await browseIndex.GetBrowseItemsAsync(request, cancellationToken).ConfigureAwait(false))
            .Select(video => new IndexedMediaFile(Video: video)).ToArray();

    public async Task<PagedResult<IndexedMediaFile>> GetMediaItemsPageAsync(
        PageRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        if (supportsMedia)
        {
            try
            {
                return await client.GetAsync<PagedResult<IndexedMediaFile>>(
                    PagePath("media", request.PageSize, request.Cursor), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (
                exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
            {
                supportsMedia = false;
            }
        }

        var page = await GetVideosPageAsync(request, cancellationToken).ConfigureAwait(false);
        return new PagedResult<IndexedMediaFile>(page.Items.Select(video => new IndexedMediaFile(Video: video)).ToArray(),
            page.NextCursor, page.TotalCount);
    }

    public async Task<IReadOnlyList<DbImageFile>> GetImagesInFolderAsync(
        string folderPath, CancellationToken cancellationToken = default)
    {
        if (!supportsMedia)
        {
            return [];
        }

        try
        {
            return await client.GetAsync<DbImageFile[]>(
                $"images/by-folder?path={Encode(folderPath)}", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<DbImageFile>> SearchImagesAsync(
        string keyword, CancellationToken cancellationToken = default)
    {
        try
        {
            return await client.GetAsync<DbImageFile[]>(
                $"images/search?q={Encode(keyword)}", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<DbVideoFile>> GetVideosAsync()
    {
        var videos = new List<DbVideoFile>();
        string? cursor = null;
        do
        {
            var page = await GetVideosPageAsync(new PageRequest(
                MediaFilePageRequest.MaxPageSize,
                cursor)).ConfigureAwait(false);
            videos.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return videos;
    }

    public Task<PagedResult<DbVideoFile>> GetVideosPageAsync(
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        return client.GetAsync<PagedResult<DbVideoFile>>(
            PagePath("videos", request.PageSize, request.Cursor),
            cancellationToken);
    }

    public async Task<IReadOnlyList<DbVideoFile>> GetVideosInFolderAsync(
        string folderPath,
        CancellationToken cancellationToken = default) =>
        await client.GetAsync<DbVideoFile[]>(
            $"videos/by-folder?path={Encode(folderPath)}",
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<DbVideoFile>> GetVideoPicksAsync(
        VideoPickOrder order,
        int limit,
        CancellationToken cancellationToken = default) =>
        limit <= 0
            ? []
            : await client.GetAsync<DbVideoFile[]>(
                $"videos/picks?order={order}&limit={limit}",
                cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<VideoFolderCount>> GetVideoFolderCountsAsync() =>
        await client.GetAsync<VideoFolderCount[]>("videos/folders").ConfigureAwait(false);

    public async Task<IReadOnlyList<DbVideoFile>> SearchVideosAsync(
        string keyword,
        CancellationToken cancellationToken = default) =>
        await client.GetAsync<DbVideoFile[]>(
            $"videos/search?q={Encode(keyword)}",
            cancellationToken).ConfigureAwait(false);

    public Task<DbVideoFile?> GetVideoByPathAsync(string path) =>
        client.GetAsync<DbVideoFile?>($"video?path={Encode(path)}");

    public async Task<byte[]?> GetVideoPreviewAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var requestPath =
            $"video/preview?v={RemoteImageCache.VideoPreviewRepresentationVersion}&path={Encode(path)}";
        var bytes = imageCache is null
            ? await client.GetBytesAsync(requestPath, cancellationToken).ConfigureAwait(false)
            : await imageCache
                .GetVideoPreviewAsync(path, token => client.GetBytesAsync(requestPath, token))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        return bytes.Length == 0 ? null : bytes;
    }

    private static string Encode(string path) => Uri.EscapeDataString(path ?? string.Empty);

    private static string PagePath(string path, int pageSize, string? cursor) =>
        cursor is null
            ? $"{path}?limit={pageSize}"
            : $"{path}?limit={pageSize}&cursor={Encode(cursor)}";
}

public sealed class RemoteLibraryQueryService(RemoteApiClient client) : ILibraryQueryService,
    ILibraryBrowseIndexQuery<DbMediaFile>, ILibraryBrowseIndexQuery<MediaLibraryAlbum>,
    ILibraryBrowseIndexQuery<MediaLibraryArtistBrowseItem>
{
    private readonly RemoteBrowseIndexQuery<DbMediaFile> trackIndex = new(client, "tracks");
    private readonly RemoteBrowseIndexQuery<MediaLibraryAlbum> albumIndex = new(client, "albums");
    private readonly RemoteBrowseIndexQuery<MediaLibraryArtistBrowseItem> artistIndex = new(client, "artists");

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

    public async Task<IReadOnlyList<MediaLibraryAlbum>> GetAlbumsAsync(string? artist = null)
    {
        if (artist is not null)
        {
            return await client.GetAsync<MediaLibraryAlbum[]>(
                $"albums/by-artist?artist={Encode(artist)}").ConfigureAwait(false);
        }

        var albums = new List<MediaLibraryAlbum>();
        string? cursor = null;
        do
        {
            var page = await GetAlbumsPageAsync(new AlbumPageRequest(
                PageSize: MediaFilePageRequest.MaxPageSize,
                Cursor: cursor)).ConfigureAwait(false);
            albums.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return albums;
    }

    public Task<PagedResult<MediaLibraryAlbum>> GetAlbumsPageAsync(
        AlbumPageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var path = $"albums?sort={request.Sort}&direction={(request.Ascending ? "asc" : "desc")}" +
            $"&limit={request.PageSize}";
        if (request.Cursor is not null)
        {
            path += $"&cursor={Encode(request.Cursor)}";
        }
        return client.GetAsync<PagedResult<MediaLibraryAlbum>>(path, cancellationToken);
    }

    public async Task<IReadOnlyList<MediaLibraryArtist>> GetArtistsAsync()
    {
        var artists = new List<MediaLibraryArtist>();
        string? cursor = null;
        do
        {
            var page = await GetArtistsPageAsync(new ArtistPageRequest(
                PageSize: MediaFilePageRequest.MaxPageSize,
                Cursor: cursor)).ConfigureAwait(false);
            artists.AddRange(page.Items.Select(value => value.Artist));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return artists;
    }

    public Task<PagedResult<MediaLibraryArtistBrowseItem>> GetArtistsPageAsync(
        ArtistPageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var path = $"artists?direction={(request.Ascending ? "asc" : "desc")}&limit={request.PageSize}";
        if (request.Cursor is not null)
        {
            path += $"&cursor={Encode(request.Cursor)}";
        }
        return client.GetAsync<PagedResult<MediaLibraryArtistBrowseItem>>(path, cancellationToken);
    }

    public async Task<IReadOnlyList<DbMediaFile>> GetMediaFilesAsync()
    {
        var files = new List<DbMediaFile>();
        string? cursor = null;
        do
        {
            var page = await GetMediaFilesPageAsync(new MediaFilePageRequest(
                PageSize: MediaFilePageRequest.MaxPageSize,
                Cursor: cursor)).ConfigureAwait(false);
            files.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return files;
    }

    public async Task<IReadOnlyList<string>> GetMediaFolderPathsAsync(CancellationToken cancellationToken = default) =>
        await client.GetAsync<string[]>("tracks/folders", cancellationToken).ConfigureAwait(false);

    public Task<PagedResult<DbMediaFile>> GetMediaFilesPageAsync(
        MediaFilePageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var path = $"tracks?sort={request.Sort}" +
            $"&direction={(request.Ascending ? "asc" : "desc")}" +
            $"&limit={request.PageSize}";
        if (!string.IsNullOrEmpty(request.Cursor))
        {
            path += $"&cursor={Encode(request.Cursor)}";
        }
        if (request.FolderPath is not null)
        {
            path += $"&folder={Encode(request.FolderPath)}";
        }

        return client.GetAsync<PagedResult<DbMediaFile>>(path, cancellationToken);
    }

    public Task<PagedResult<DbMediaFile>> GetShuffledMediaFilesPageAsync(
        ShuffledMediaFilePageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var path = $"tracks/shuffled?seed={request.Seed}&limit={request.PageSize}";
        if (request.Cursor is not null)
        {
            path += $"&cursor={Encode(request.Cursor)}";
        }
        return client.GetAsync<PagedResult<DbMediaFile>>(path, cancellationToken);
    }

    public Task<MediaLibraryAlbum> GetAlbumByNameAsync(string? albumArtist, string? albumName) =>
        GetAlbumByNameAsync(albumArtist, albumName, albumGroupingKey: null);

    public Task<MediaLibraryAlbum> GetAlbumByNameAsync(string? albumArtist, string? albumName, string? albumGroupingKey) =>
        client.GetAsync<MediaLibraryAlbum>($"album?artist={Encode(albumArtist)}&name={Encode(albumName)}" +
            (albumGroupingKey is null ? string.Empty : $"&group={Encode(albumGroupingKey)}"));

    public Task<MediaLibraryAlbum> GetAlbumByTrackPathAsync(string path) =>
        client.GetAsync<MediaLibraryAlbum>($"album/by-track?path={Encode(path)}");

    public Task<MediaLibraryArtist> GetArtistByNameAsync(string? name) =>
        client.GetAsync<MediaLibraryArtist>($"artist?name={Encode(name)}");

    public Task<MediaLibraryArtist> GetArtistByTrackPathAsync(string path) =>
        client.GetAsync<MediaLibraryArtist>($"artist/by-track?path={Encode(path)}");

    public Task<bool> ArtistExistsAsync(string? name) =>
        client.GetAsync<bool>($"artist/exists?name={Encode(name)}");

    public Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByAlbumAsync(string? albumName, string? artistName) =>
        GetMediaFilesByAlbumAsync(albumName, artistName, albumGroupingKey: null);

    public async Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByAlbumAsync(string? albumName, string? artistName, string? albumGroupingKey) =>
        await client.GetAsync<DbMediaFile[]>(
            $"tracks/by-album?album={Encode(albumName)}&artist={Encode(artistName)}" +
            (albumGroupingKey is null ? string.Empty : $"&group={Encode(albumGroupingKey)}")).ConfigureAwait(false);

    public async Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByArtistAsync(MediaLibraryArtist artist) =>
        await client.GetAsync<DbMediaFile[]>($"tracks/by-artist?name={Encode(artist.Name)}").ConfigureAwait(false);

    public Task<LibrarySearchResults> SearchAsync(string keyword) =>
        client.GetAsync<LibrarySearchResults>($"search?q={Encode(keyword)}");

    private static string Encode(string? value) => Uri.EscapeDataString(value ?? string.Empty);
}

public sealed class RemoteLibraryScanService : ILibraryScanService, IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly RemoteApiClient client;
    private readonly CancellationTokenSource disposeCancellation = new();
    private readonly Task pollingTask;
    private long? eventCursor;

    public RemoteLibraryScanService(RemoteApiClient client)
    {
        this.client = client;
        pollingTask = PollEventsAsync(disposeCancellation.Token);
    }

    public event EventHandler<LibraryScanStartedEventArgs>? ScanStarted;
    public event EventHandler<LibraryScanProgressEventArgs>? ScanProgressChanged;
    public event EventHandler<LibraryScanWarningEventArgs>? ScanWarning;
    public event EventHandler<LibraryScanCompletedEventArgs>? ScanCompleted;

    /// <summary>Starts a scan on the server and follows its events until it completes.</summary>
    public async Task<LibraryScanResult> ScanAsync()
    {
        var cancellationToken = disposeCancellation.Token;
        var started = await client.PostAsync<RemoteScanStarted>("library/scan", cancellationToken).ConfigureAwait(false);
        var warnings = new List<LibraryScanWarning>();
        var after = started.Cursor;
        while (true)
        {
            var page = await client.GetAsync<RemoteScanEvents>(
                $"library/scan/events?after={after}",
                cancellationToken).ConfigureAwait(false);
            foreach (var value in page.Events.OrderBy(value => value.Id))
            {
                if (value is { Type: "warning", Warning: { } warning })
                {
                    warnings.Add(warning);
                }
                else if (value.Type == "completed")
                {
                    if (value.Failed == true)
                    {
                        throw new InvalidOperationException(
                            warnings.LastOrDefault()?.Message ?? "The library scan failed on the server.");
                    }

                    return new LibraryScanResult(value.IndexedCount ?? 0, warnings)
                    {
                        HasChanges = value.HasChanges ?? true,
                        VideoIndexedCount = value.VideoIndexedCount ?? 0,
                        ImageIndexedCount = value.ImageIndexedCount ?? 0,
                    };
                }
            }

            after = page.Cursor;
        }
    }

    private async Task PollEventsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // The first request names no cursor, so it returns at once with the current position
                // and only events from then on are published.
                var page = await client.GetAsync<RemoteScanEvents>(
                    eventCursor is { } after ? $"library/scan/events?after={after}" : "library/scan/events",
                    cancellationToken).ConfigureAwait(false);
                foreach (var value in page.Events.OrderBy(value => value.Id))
                {
                    Publish(value);
                }

                eventCursor = page.Cursor;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Any failure here is transient by nature (the server restarted, the request timed
                // out, the connection dropped mid-response); giving up would silently stop scan
                // progress for the rest of the session.
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void Publish(RemoteScanEvent value)
    {
        switch (value.Type)
        {
            case "started":
                ScanStarted?.Invoke(this, new LibraryScanStartedEventArgs(value.Folders ?? []));
                break;
            case "progress":
                ScanProgressChanged?.Invoke(this, new LibraryScanProgressEventArgs(value.IndexedCount ?? 0));
                break;
            case "warning" when value.Warning is not null:
                ScanWarning?.Invoke(this, new LibraryScanWarningEventArgs(value.Warning));
                break;
            case "completed":
                ScanCompleted?.Invoke(this, new LibraryScanCompletedEventArgs(
                    value.IndexedCount ?? 0,
                    value.WarningCount ?? 0)
                {
                    HasChanges = value.HasChanges ?? true,
                    Failed = value.Failed == true,
                    VideoIndexedCount = value.VideoIndexedCount ?? 0,
                    ImageIndexedCount = value.ImageIndexedCount ?? 0,
                });
                break;
        }
    }

    public void Dispose()
    {
        disposeCancellation.Cancel();
        disposeCancellation.Dispose();
        _ = pollingTask;
    }
}
