#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.Android.Services;

internal sealed class AndroidMediaStoreQueryService : ILibraryQueryService, IVideoLibraryQueryService,
    IMediaLibraryQueryService, IVideoFolderBrowser
{
    private static readonly StringComparer TextComparer = StringComparer.OrdinalIgnoreCase;
    private readonly AndroidMediaStoreCatalog catalog;
    private readonly LibraryBrowseIndexCache<IndexedMediaFile> browseIndex;
    private readonly ConcurrentDictionary<string, object> pages = new(StringComparer.Ordinal);

    public AndroidMediaStoreQueryService(AndroidMediaStoreCatalog catalog)
    {
        this.catalog = catalog;
        browseIndex = LibraryBrowseIndexes.Media(this);
    }

    public void Invalidate() => pages.Clear();

    public async Task<IReadOnlyList<DbMediaFile>> GetMediaFilesAsync() =>
        (await catalog.GetMusicAsync().ConfigureAwait(false)).Items.Select(track => track.File).ToArray();

    public async Task<IReadOnlyList<string>> GetMediaFolderPathsAsync(CancellationToken cancellationToken = default) =>
        (await catalog.GetMusicAsync(cancellationToken).ConfigureAwait(false)).Items
            .Select(track => track.Folder).Distinct(StringComparer.Ordinal).ToArray();

    public async Task<IReadOnlyList<string>> GetMusicRootsAsync(CancellationToken cancellationToken) =>
        (await catalog.GetMusicAsync(cancellationToken).ConfigureAwait(false)).Items
            .Select(track => track.Root).Distinct(StringComparer.Ordinal).ToArray();

    public async Task<PagedResult<DbMediaFile>> GetMediaFilesPageAsync(
        MediaFilePageRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        var snapshot = await catalog.GetMusicAsync(cancellationToken).ConfigureAwait(false);
        return Page(snapshot.Id, $"tracks:{request.Sort}:{request.Ascending}:{request.FolderPath}",
            () => Order(snapshot.Items.Where(track => request.FolderPath is null || track.Folder == request.FolderPath)
                .Select(track => track.File), FederatedLibraryOrdering.Tracks(request)), request.PageSize, request.Cursor);
    }

    public async Task<PagedResult<DbMediaFile>> GetShuffledMediaFilesPageAsync(
        ShuffledMediaFilePageRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        var snapshot = await catalog.GetMusicAsync(cancellationToken).ConfigureAwait(false);
        return Page(snapshot.Id, $"shuffle:{request.Seed}", () => snapshot.Items.Select(track => track.File)
            .OrderBy(file => ShuffleKey(file.Id, request.Seed)).ThenBy(file => file.Id).ToArray(),
            request.PageSize, request.Cursor);
    }

    public async Task<IReadOnlyList<MediaLibraryAlbum>> GetAlbumsAsync(string? artist = null)
    {
        var tracks = (await catalog.GetMusicAsync().ConfigureAwait(false)).Items.Select(track => track.File).ToArray();
        var keys = artist is null ? null : tracks.Where(track => IsArtist(track, artist))
            .Select(track => track.AlbumGroupingKey).ToHashSet(StringComparer.Ordinal);
        return Albums(tracks).Where(album => keys is null || keys.Contains(album.AlbumGroupingKey)).ToArray();
    }

    public async Task<PagedResult<MediaLibraryAlbum>> GetAlbumsPageAsync(
        AlbumPageRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        var snapshot = await catalog.GetMusicAsync(cancellationToken).ConfigureAwait(false);
        return Page(snapshot.Id, $"albums:{request.Sort}:{request.Ascending}",
            () => Order(Albums(snapshot.Items.Select(track => track.File)), FederatedLibraryOrdering.Albums(request)),
            request.PageSize, request.Cursor);
    }

    public async Task<IReadOnlyList<MediaLibraryArtist>> GetArtistsAsync() =>
        Artists((await catalog.GetMusicAsync().ConfigureAwait(false)).Items.Select(track => track.File).ToArray())
            .Select(item => item.Artist).ToArray();

    public async Task<PagedResult<MediaLibraryArtistBrowseItem>> GetArtistsPageAsync(
        ArtistPageRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        var snapshot = await catalog.GetMusicAsync(cancellationToken).ConfigureAwait(false);
        return Page(snapshot.Id, $"artists:{request.Ascending}",
            () => Order(Artists(snapshot.Items.Select(track => track.File).ToArray()), FederatedLibraryOrdering.Artists(request)),
            request.PageSize, request.Cursor);
    }

    public Task<MediaLibraryAlbum> GetAlbumByNameAsync(string? albumArtist, string? albumName) =>
        GetAlbumByNameAsync(albumArtist, albumName, null);

    public async Task<MediaLibraryAlbum> GetAlbumByNameAsync(string? albumArtist, string? albumName, string? albumGroupingKey) =>
        (await GetAlbumsAsync().ConfigureAwait(false)).FirstOrDefault(album =>
            TextComparer.Equals(album.Title, albumName ?? string.Empty) &&
            (albumGroupingKey is not null ? album.AlbumGroupingKey == albumGroupingKey :
                TextComparer.Equals(album.ArtistForQuery, albumArtist ?? string.Empty)))
        ?? throw new FileNotFoundException("The album is no longer in the system library.");

    public async Task<MediaLibraryAlbum> GetAlbumByTrackPathAsync(string path)
    {
        var file = await FindTrackAsync(path).ConfigureAwait(false);
        return await GetAlbumByNameAsync(file.AlbumArtist, file.Album, file.AlbumGroupingKey).ConfigureAwait(false);
    }

    public async Task<MediaLibraryArtist> GetArtistByNameAsync(string? name) =>
        (await GetArtistsAsync().ConfigureAwait(false)).FirstOrDefault(artist => TextComparer.Equals(artist.Name, name ?? string.Empty))
        ?? throw new FileNotFoundException("The artist is no longer in the system library.");

    public async Task<MediaLibraryArtist> GetArtistByTrackPathAsync(string path) =>
        await GetArtistByNameAsync((await FindTrackAsync(path).ConfigureAwait(false)).Artist).ConfigureAwait(false);

    public async Task<bool> ArtistExistsAsync(string? name) =>
        (await GetArtistsAsync().ConfigureAwait(false)).Any(artist => TextComparer.Equals(artist.Name, name ?? string.Empty));

    public Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByAlbumAsync(string? albumName, string? artistName) =>
        GetMediaFilesByAlbumAsync(albumName, artistName, null);

    public async Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByAlbumAsync(
        string? albumName, string? artistName, string? albumGroupingKey) =>
        (await GetMediaFilesAsync().ConfigureAwait(false)).Where(file =>
            TextComparer.Equals(file.Album, albumName ?? string.Empty) &&
            (albumGroupingKey is not null ? file.AlbumGroupingKey == albumGroupingKey :
                TextComparer.Equals(file.AlbumArtist, artistName ?? string.Empty)))
            .OrderBy(file => FederatedLibraryOrdering.ParseLeadingInt(file.DiscNumber))
            .ThenBy(file => FederatedLibraryOrdering.ParseLeadingInt(file.TrackNumber))
            .ThenBy(file => file.Title, TextComparer).ThenBy(file => file.Path, StringComparer.Ordinal).ToArray();

    public async Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByArtistAsync(MediaLibraryArtist artist) =>
        (await GetMediaFilesAsync().ConfigureAwait(false)).Where(file => IsArtist(file, artist.Name)).ToArray();

    public async Task<LibrarySearchResults> SearchAsync(string keyword)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0)
        {
            return new LibrarySearchResults();
        }

        var files = (await GetMediaFilesAsync().ConfigureAwait(false)).ToArray();
        return new LibrarySearchResults
        {
            Files = files.Where(file => Contains(file.Title, keyword) || Contains(file.Album, keyword) ||
                Contains(file.Artist, keyword) || Contains(file.AlbumArtist, keyword) || Contains(file.Genre, keyword)).ToList(),
            Albums = Albums(files).Where(album => Contains(album.Title, keyword) || Contains(album.Artist, keyword) ||
                Contains(album.Genre, keyword)).ToList(),
            Artists = Artists(files).Select(item => item.Artist).Where(artist => Contains(artist.Name, keyword)).ToList(),
        };
    }

    public async Task<IReadOnlyList<DbVideoFile>> GetVideosAsync() =>
        (await catalog.GetMediaAsync().ConfigureAwait(false)).Items.Select(item => item.Video).OfType<DbVideoFile>().ToArray();

    public async Task<PagedResult<DbVideoFile>> GetVideosPageAsync(PageRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        var snapshot = await catalog.GetMediaAsync(cancellationToken).ConfigureAwait(false);
        return Page(snapshot.Id, "videos", () => Order(snapshot.Items.Select(item => item.Video).OfType<DbVideoFile>(),
            FederatedLibraryOrdering.Videos()), request.PageSize, request.Cursor);
    }

    public async Task<PagedResult<IndexedMediaFile>> GetMediaItemsPageAsync(PageRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        var snapshot = await catalog.GetMediaAsync(cancellationToken).ConfigureAwait(false);
        return Page(snapshot.Id, "media", () => Order(snapshot.Items, FederatedLibraryOrdering.Media()), request.PageSize, request.Cursor);
    }

    public Task<LibraryBrowseIndex> GetBrowseIndexAsync(LibraryBrowseIndexRequest request, CancellationToken cancellationToken = default) =>
        browseIndex.GetBrowseIndexAsync(request, cancellationToken);

    public Task<IndexedMediaFile[]> GetBrowseItemsAsync(LibraryBrowseItemsRequest request, CancellationToken cancellationToken = default) =>
        browseIndex.GetBrowseItemsAsync(request, cancellationToken);

    public async Task<IReadOnlyList<DbImageFile>> GetImagesInFolderAsync(string folderPath, CancellationToken cancellationToken = default) =>
        (await catalog.GetMediaAsync(cancellationToken).ConfigureAwait(false)).Items.Select(item => item.Image)
            .OfType<DbImageFile>().Where(image => image.FolderPath == folderPath).ToArray();

    public async Task<IReadOnlyList<DbImageFile>> SearchImagesAsync(string keyword, CancellationToken cancellationToken = default) =>
        (await catalog.GetMediaAsync(cancellationToken).ConfigureAwait(false)).Items.Select(item => item.Image)
            .OfType<DbImageFile>().Where(image => Contains(image.FileName, keyword.Trim())).ToArray();

    public async Task<IReadOnlyList<DbVideoFile>> GetVideosInFolderAsync(string folderPath, CancellationToken cancellationToken = default) =>
        (await catalog.GetMediaAsync(cancellationToken).ConfigureAwait(false)).Items.Select(item => item.Video)
            .OfType<DbVideoFile>().Where(video => video.FolderPath == folderPath).ToArray();

    public async Task<IReadOnlyList<DbVideoFile>> SearchVideosAsync(string keyword, CancellationToken cancellationToken = default) =>
        (await catalog.GetMediaAsync(cancellationToken).ConfigureAwait(false)).Items.Select(item => item.Video)
            .OfType<DbVideoFile>().Where(video => Contains(video.Title, keyword.Trim()) || Contains(video.FileName, keyword.Trim())).ToArray();

    public async Task<DbVideoFile?> GetVideoByPathAsync(string path) =>
        (await GetVideosAsync().ConfigureAwait(false)).FirstOrDefault(video => video.Path == path);

    public async Task<IReadOnlyList<DbVideoFile>> GetVideoPicksAsync(VideoPickOrder order, int limit, CancellationToken cancellationToken = default)
    {
        var videos = (await catalog.GetMediaAsync(cancellationToken).ConfigureAwait(false)).Items
            .Select(item => item.Video).OfType<DbVideoFile>();
        return (order == VideoPickOrder.Random ? videos.OrderBy(_ => Random.Shared.Next()) :
            videos.OrderByDescending(video => video.DatabaseItemAddedDate)).Take(Math.Max(0, limit)).ToArray();
    }

    public async Task<IReadOnlyList<VideoFolderCount>> GetVideoFolderCountsAsync() =>
        (await GetVideosAsync().ConfigureAwait(false)).GroupBy(video => (video.FolderPath, video.LibraryRoot))
            .Select(group => new VideoFolderCount
            {
                FolderPath = group.Key.FolderPath,
                LibraryRoot = group.Key.LibraryRoot,
                VideoCount = group.Count(),
            }).ToArray();

    public Task<byte[]?> GetVideoPreviewAsync(string path, CancellationToken cancellationToken = default) =>
        catalog.GetThumbnailAsync(path, cancellationToken);

    public async Task<VideoFolderListing> ListAsync(string? folderPath, CancellationToken cancellationToken = default)
    {
        var items = (await catalog.GetMediaAsync(cancellationToken).ConfigureAwait(false)).Items;
        var counts = items.GroupBy(item => (Folder: Folder(item), Root: item.Image?.LibraryRoot ?? item.Video!.LibraryRoot))
            .Select(group => new VideoFolderCount
            {
                FolderPath = group.Key.Folder,
                LibraryRoot = group.Key.Root,
                VideoCount = group.Count(),
            }).ToArray();
        var tree = VideoFolderTree.Build(counts);
        var chain = folderPath is null ? null : VideoFolderTree.FindPath(tree, folderPath);
        if (folderPath is not null && chain is not { Count: > 1 })
        {
            return VideoFolderListing.Empty;
        }

        var node = chain is null ? tree : chain[^1];
        var entries = node.Children.Select(child => new VideoFolderEntry(VideoFolderEntryKind.Folder, child.Name, child.Path!,
            PreviewPaths: items.Where(item => VideoFolderTree.IsUnder(Folder(item), child.Path!) || Folder(item) == child.Path)
                .Select(item => item.Path).Take(4).ToArray())).ToList();
        if (folderPath is not null)
        {
            entries.AddRange(items.Where(item => Folder(item) == folderPath).Select(item => new VideoFolderEntry(
                item.Image is null ? VideoFolderEntryKind.Video : VideoFolderEntryKind.Photo,
                item.Image?.FileName ?? item.Video!.FileName, item.Path,
                SizeInBytes: item.Image?.FileSize ?? item.Video!.FileSize,
                LastModifiedUtc: item.Image?.FileLastModifiedDate ?? item.Video!.FileLastModifiedDate,
                CreatedUtc: item.Image?.FileCreatedDate ?? item.Video?.FileCreatedDate)));
        }

        return new VideoFolderListing(folderPath,
            chain?.Skip(1).Select(parent => new VideoFolderEntry(VideoFolderEntryKind.Folder, parent.Name, parent.Path!)).ToArray() ?? [],
            entries.OrderBy(entry => entry.Kind).ThenBy(entry => entry.Name, TextComparer).ToArray(),
            items.Where(item => folderPath is null || Folder(item) == folderPath || VideoFolderTree.IsUnder(Folder(item), folderPath))
                .Select(item => item.Path).Take(4).ToArray());
    }

    private async Task<DbMediaFile> FindTrackAsync(string path) =>
        (await GetMediaFilesAsync().ConfigureAwait(false)).FirstOrDefault(file => file.Path == path)
        ?? throw new FileNotFoundException("The track is no longer in the system library.", path);

    private static string Folder(IndexedMediaFile item) => item.Image?.FolderPath ?? item.Video!.FolderPath;

    private static bool Contains(string? value, string keyword) =>
        keyword.Length > 0 && value?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsArtist(DbMediaFile file, string artist) =>
        TextComparer.Equals(file.Artist, artist) || TextComparer.Equals(file.AlbumArtist, artist);

    private static IEnumerable<MediaLibraryAlbum> Albums(IEnumerable<DbMediaFile> files) =>
        files.GroupBy(file => file.AlbumGroupingKey, StringComparer.Ordinal).Select(group =>
        {
            var first = group.First();
            return new MediaLibraryAlbum
            {
                Title = first.Album,
                ArtistForQuery = first.AlbumArtist,
                Artist = string.Join(", ", group.Select(file => file.AlbumArtist).Distinct(TextComparer)),
                AlbumGroupingKey = group.Key,
                Genre = first.Genre,
                Date = first.Date,
                FirstFileInAlbum = first.Path,
                FileCount = group.Count(),
                DatabaseItemAddedDate = group.Max(file => file.DatabaseItemAddedDate),
            };
        });

    private static IEnumerable<MediaLibraryArtistBrowseItem> Artists(DbMediaFile[] files) =>
        files.SelectMany(file => new[] { file.Artist, file.AlbumArtist }).Distinct(TextComparer).Select(name =>
        {
            var tracks = files.Where(file => IsArtist(file, name)).ToArray();
            var albums = tracks.GroupBy(file => file.AlbumGroupingKey, StringComparer.Ordinal).ToArray();
            return new MediaLibraryArtistBrowseItem
            {
                Artist = new MediaLibraryArtist
                {
                    Name = name,
                    FileCount = tracks.Length,
                    AlbumCount = albums.Length,
                    DatabaseItemAddedDate = tracks.Max(file => file.DatabaseItemAddedDate),
                },
                ArtworkPaths = albums.Take(4).Select(group => group.First().Path).ToArray(),
            };
        });

    private static T[] Order<T>(IEnumerable<T> values, IComparer<RankedSourceItem<T>> comparer) =>
        values.Select(value => new RankedSourceItem<T>(LibrarySourceId.System, 0, value))
            .OrderBy(item => item, comparer).Select(item => item.Value).ToArray();

    private PagedResult<T> Page<T>(string snapshot, string query, Func<T[]> load, int pageSize, string? cursor)
    {
        var key = snapshot + ":" + query;
        var prefix = key + ":";
        var offset = 0;
        if (cursor is not null && (!cursor.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(cursor.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out offset)))
        {
            throw new LibraryBrowseIndexExpiredException();
        }

        if (pages.Count >= 64)
        {
            pages.Clear();
        }

        var items = (T[])pages.GetOrAdd(key, _ => load());
        var result = items.Skip(offset).Take(pageSize).ToArray();
        return new PagedResult<T>(result, offset + result.Length < items.Length
            ? prefix + (offset + result.Length).ToString(CultureInfo.InvariantCulture) : null, items.Length);
    }

    private static (int Phase, uint Key) ShuffleKey(int id, long seed)
    {
        var value = unchecked((ulong)seed + 0x9e3779b97f4a7c15UL);
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        var pivot = (uint)(value ^ (value >> 31));
        var key = unchecked((uint)id * 2654435761u);
        return (key >= pivot ? 0 : 1, key);
    }
}
