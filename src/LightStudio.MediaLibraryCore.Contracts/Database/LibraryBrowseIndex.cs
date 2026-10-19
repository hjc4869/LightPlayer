#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.MediaLibraryCore.Database;

public sealed record LibraryBrowseIndexRequest(string? FolderPath = null);

public sealed record LibraryBrowseIndexEntry(
    string Name,
    string? Artist = null,
    string? Album = null,
    string? Genre = null,
    int Year = 0,
    long DateAdded = 0,
    long Duration = 0,
    string? FileType = null,
    long FileSize = 0,
    int Disc = 0,
    int Track = 0,
    string? DuplicateKey = null,
    DateTimeOffset? CreatedUtc = null,
    bool IsFolder = false,
    DateTimeOffset? DateTaken = null,
    string? Country = null,
    string? State = null,
    string? City = null);

public sealed record LibraryBrowseIndex(string Id, LibraryBrowseIndexEntry[] Items);

public sealed record LibraryBrowseItemsRequest(string Id, int[] Indices)
{
    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || Indices is null ||
            Indices.Length is < 1 or > MediaFilePageRequest.MaxPageSize || Indices.Any(index => index < 0))
        {
            throw new ArgumentException("The browse index batch is invalid.");
        }
    }
}

public interface ILibraryBrowseIndexQuery<T>
{
    Task<LibraryBrowseIndex> GetBrowseIndexAsync(
        LibraryBrowseIndexRequest request, CancellationToken cancellationToken = default);

    Task<T[]> GetBrowseItemsAsync(
        LibraryBrowseItemsRequest request, CancellationToken cancellationToken = default);
}

public sealed class LibraryBrowseIndexExpiredException : InvalidOperationException
{
    public LibraryBrowseIndexExpiredException(Exception? innerException = null)
        : base("The library browse index has expired. Reload the page.", innerException)
    {
    }
}

public sealed class LibraryBrowseIndexCache<T>(
    Func<LibraryBrowseIndexRequest, CancellationToken, Task<T[]>> load,
    Func<T, LibraryBrowseIndexEntry> describe) : ILibraryBrowseIndexQuery<T>
{
    private const int MaximumSnapshots = 4;
    private readonly object gate = new();
    private readonly Dictionary<string, T[]> snapshots = new(StringComparer.Ordinal);
    private readonly Queue<string> snapshotOrder = new();

    public async Task<LibraryBrowseIndex> GetBrowseIndexAsync(
        LibraryBrowseIndexRequest request, CancellationToken cancellationToken = default)
    {
        var items = await load(request, cancellationToken).ConfigureAwait(false);
        var entries = await Task.Run(() =>
        {
            var result = new LibraryBrowseIndexEntry[items.Length];
            for (var index = 0; index < items.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result[index] = describe(items[index]);
            }
            return result;
        }, cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        lock (gate)
        {
            while (snapshotOrder.Count >= MaximumSnapshots)
            {
                snapshots.Remove(snapshotOrder.Dequeue());
            }
            snapshots.Add(id, items);
            snapshotOrder.Enqueue(id);
        }
        return new LibraryBrowseIndex(id, entries);
    }

    public Task<T[]> GetBrowseItemsAsync(
        LibraryBrowseItemsRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!snapshots.TryGetValue(request.Id, out var items))
            {
                throw new LibraryBrowseIndexExpiredException();
            }
            var result = new T[request.Indices.Length];
            for (var index = 0; index < result.Length; index++)
            {
                var position = request.Indices[index];
                if (position >= items.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(request));
                }
                result[index] = items[position];
            }
            return Task.FromResult(result);
        }
    }
}

public static class LibraryBrowseIndexes
{
    public static LibraryBrowseIndexCache<DbMediaFile> Tracks(ILibraryQueryService query) => new(
        (request, token) => ReadPagesAsync(cursor => query.GetMediaFilesPageAsync(
            new MediaFilePageRequest(PageSize: MediaFilePageRequest.MaxPageSize, Cursor: cursor,
                FolderPath: request.FolderPath), token), token),
        file => new LibraryBrowseIndexEntry(
            string.IsNullOrWhiteSpace(file.Title) ? Path.GetFileNameWithoutExtension(file.Path) ?? string.Empty : file.Title,
            file.Artist, file.Album, file.Genre, LeadingNumber(file.Date), file.DatabaseItemAddedDate.UtcTicks,
            file.Duration.Ticks, Disc: LeadingNumber(file.DiscNumber), Track: LeadingNumber(file.TrackNumber),
            DuplicateKey: string.IsNullOrWhiteSpace(file.Artist) || string.IsNullOrWhiteSpace(file.Title)
                ? null : $"{file.Artist.Trim()}\u001f{file.Album?.Trim()}\u001f{file.Title.Trim()}"));

    public static LibraryBrowseIndexCache<MediaLibraryAlbum> Albums(ILibraryQueryService query) => new(
        (_, token) => ReadPagesAsync(cursor => query.GetAlbumsPageAsync(
            new AlbumPageRequest(PageSize: MediaFilePageRequest.MaxPageSize, Cursor: cursor), token), token),
        album => new LibraryBrowseIndexEntry(album.Title ?? string.Empty, album.Artist, album.Title,
            album.Genre, LeadingNumber(album.Date), album.DatabaseItemAddedDate.UtcTicks,
            DuplicateKey: AlbumDuplicateKey(album)));

    public static LibraryBrowseIndexCache<MediaLibraryArtistBrowseItem> Artists(ILibraryQueryService query) => new(
        (_, token) => ReadPagesAsync(cursor => query.GetArtistsPageAsync(
            new ArtistPageRequest(PageSize: MediaFilePageRequest.MaxPageSize, Cursor: cursor), token), token),
        artist => new LibraryBrowseIndexEntry(artist.Artist.Name ?? string.Empty, artist.Artist.Name,
            DateAdded: artist.Artist.DatabaseItemAddedDate.UtcTicks,
            DuplicateKey: artist.Artist.Name?.Trim() ?? string.Empty));

    public static LibraryBrowseIndexCache<DbVideoFile> Videos(IVideoLibraryQueryService query,
        bool readLocalCreationTime = false) => new(
        (_, token) => ReadPagesAsync(cursor => query.GetVideosPageAsync(
            new PageRequest(MediaFilePageRequest.MaxPageSize, cursor), token), token),
        video => DescribeVideo(video, readLocalCreationTime));

    public static LibraryBrowseIndexCache<IndexedMediaFile> Media(IVideoLibraryQueryService query,
        bool readLocalCreationTime = false) => new(
        (_, token) => ReadPagesAsync(cursor => GetMediaItemsPageAsync(query,
            new PageRequest(MediaFilePageRequest.MaxPageSize, cursor), token), token),
        item => item.Image is { } image
            ? new LibraryBrowseIndexEntry(image.FileName,
                DateAdded: (image.DateTaken ?? image.FileLastModifiedDate).UtcTicks,
                FileType: Path.GetExtension(image.FileName), FileSize: image.FileSize,
                CreatedUtc: image.FileCreatedDate, DateTaken: image.DateTaken,
                Country: image.Country, State: image.State, City: image.City)
            : DescribeVideo(item.Video!, readLocalCreationTime));

    public static async Task<PagedResult<IndexedMediaFile>> GetMediaItemsPageAsync(
        IVideoLibraryQueryService query, PageRequest request, CancellationToken cancellationToken = default)
    {
        if (query is IMediaLibraryQueryService media)
        {
            return await media.GetMediaItemsPageAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var page = await query.GetVideosPageAsync(request, cancellationToken).ConfigureAwait(false);
        return new PagedResult<IndexedMediaFile>(page.Items.Select(video => new IndexedMediaFile(Video: video)).ToArray(),
            page.NextCursor, page.TotalCount);
    }

    private static LibraryBrowseIndexEntry DescribeVideo(DbVideoFile video, bool readLocalCreationTime)
    {
        if (readLocalCreationTime && video.FileCreatedDate is null)
        {
            video.FileCreatedDate = ReadCreationTime(video.Path);
        }
        return new LibraryBrowseIndexEntry(video.DisplayName ?? string.Empty,
            DateAdded: video.FileLastModifiedDate.UtcTicks, Duration: video.Duration.Ticks,
            FileType: Path.GetExtension(video.FileName), FileSize: video.FileSize,
            CreatedUtc: video.FileCreatedDate);
    }

    public static DateTimeOffset? ReadCreationTime(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.CreationTimeUtc > DateTime.UnixEpoch ? file.CreationTimeUtc : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<T[]> ReadPagesAsync<T>(
        Func<string?, Task<PagedResult<T>>> read, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await read(cursor).ConfigureAwait(false);
            result.AddRange(page.Items);
            if (page.NextCursor is not null && (page.Items.Length == 0 || page.NextCursor == cursor))
            {
                throw new InvalidOperationException("The browse index query did not advance.");
            }
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return result.ToArray();
    }

    private static int LeadingNumber(string? value)
    {
        var length = 0;
        while (length < value?.Length && length < 9 && char.IsDigit(value[length]))
        {
            length++;
        }
        return length > 0 && int.TryParse(value.AsSpan(0, length), NumberStyles.None,
            CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private static string? AlbumDuplicateKey(MediaLibraryAlbum album)
    {
        var artist = (string.IsNullOrWhiteSpace(album.ArtistForQuery) ? album.Artist : album.ArtistForQuery)?.Trim();
        return string.IsNullOrEmpty(artist) || string.IsNullOrWhiteSpace(album.Title)
            ? null : $"{artist}\u001f{album.Title.Trim()}\u001f{album.FileCount}";
    }
}
