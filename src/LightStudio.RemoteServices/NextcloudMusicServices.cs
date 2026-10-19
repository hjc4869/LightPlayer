using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.RemoteServices;

internal sealed record NextcloudMusicCatalogState(
    NextcloudMusicAlbum[] Albums,
    NextcloudMusicSong[] Songs,
    string CacheRevision = "")
{
    public static NextcloudMusicCatalogState Empty { get; } = new([], []);
}

internal sealed class NextcloudMusicCatalog
{
    private const int RemotePageSize = 500;
    private readonly NextcloudMusicClient client;
    private readonly Func<NextcloudMusicCatalogState, Task> save;
    private readonly Func<AlbumGroupingMode> albumGrouping;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private NextcloudMusicSnapshot snapshot;

    public NextcloudMusicCatalog(
        NextcloudMusicClient client,
        NextcloudMusicCatalogState initialState,
        Func<NextcloudMusicCatalogState, Task> save,
        Func<AlbumGroupingMode> albumGrouping)
    {
        this.client = client;
        this.save = save;
        this.albumGrouping = albumGrouping;
        snapshot = new NextcloudMusicSnapshot(
            initialState.Albums, initialState.Songs, initialState.CacheRevision, albumGrouping());
    }

    public NextcloudMusicSnapshot Snapshot
    {
        get
        {
            while (true)
            {
                var current = Volatile.Read(ref snapshot);
                var mode = albumGrouping();
                if (current.AlbumGrouping == mode)
                {
                    return current;
                }
                var updated = current.WithAlbumGrouping(mode);
                if (ReferenceEquals(Interlocked.CompareExchange(ref snapshot, updated, current), current))
                {
                    return updated;
                }
            }
        }
    }

    public async Task<int> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var albumsTask = ReadAllAlbumsAsync(cancellationToken);
            var songsTask = ReadAllSongsAsync(cancellationToken);
            await Task.WhenAll(albumsTask, songsTask).ConfigureAwait(false);
            var state = new NextcloudMusicCatalogState(
                albumsTask.Result,
                songsTask.Result,
                Guid.NewGuid().ToString("N"));
            var updated = new NextcloudMusicSnapshot(state.Albums, state.Songs, state.CacheRevision, albumGrouping());
            Volatile.Write(ref snapshot, updated);
            await save(state).ConfigureAwait(false);
            return updated.Files.Length;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task<NextcloudMusicAlbum[]> ReadAllAlbumsAsync(CancellationToken cancellationToken)
    {
        var result = new List<NextcloudMusicAlbum>();
        while (true)
        {
            var page = await client
                .GetAlbumsPageAsync(result.Count, RemotePageSize, cancellationToken)
                .ConfigureAwait(false);
            result.AddRange(page);
            if (page.Length < RemotePageSize)
            {
                return result.ToArray();
            }
        }
    }

    private async Task<NextcloudMusicSong[]> ReadAllSongsAsync(CancellationToken cancellationToken)
    {
        var result = new List<NextcloudMusicSong>();
        while (true)
        {
            var page = await client
                .GetSongsPageAsync(result.Count, RemotePageSize, cancellationToken)
                .ConfigureAwait(false);
            result.AddRange(page);
            if (page.Length < RemotePageSize)
            {
                return result.ToArray();
            }
        }
    }
}

internal sealed class NextcloudMusicSnapshot
{
    private static readonly StringComparer TextComparer = StringComparer.OrdinalIgnoreCase;
    private readonly IReadOnlyList<NextcloudMusicAlbum> remoteAlbums;
    private readonly IReadOnlyList<NextcloudMusicSong> remoteSongs;
    private readonly Dictionary<string, NextcloudMusicSong> songsById;
    private readonly Dictionary<string, DbMediaFile> filesByPath;
    private readonly Dictionary<string, DbMediaFile[]> filesByFolder;
    private readonly Dictionary<string, MediaLibraryAlbum> albumsByTrack;
    private readonly Dictionary<string, string?> coverArtByTrack;

    public NextcloudMusicSnapshot(
        IReadOnlyList<NextcloudMusicAlbum> remoteAlbums,
        IReadOnlyList<NextcloudMusicSong> remoteSongs,
        string cacheRevision,
        AlbumGroupingMode albumGrouping = AlbumGroupingMode.SameFolder)
    {
        this.remoteAlbums = remoteAlbums;
        this.remoteSongs = remoteSongs;
        AlbumGrouping = albumGrouping;
        CacheRevision = cacheRevision;
        var remoteAlbumsById = remoteAlbums
            .GroupBy(album => album.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        songsById = remoteSongs
            .GroupBy(song => song.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var songs = songsById.Values.ToArray();
        coverArtByTrack = songs.ToDictionary(
            song => song.Id,
            song => song.CoverArt,
            StringComparer.Ordinal);

        var explicitAlbumArtists = songs
            .Where(song => remoteAlbumsById.TryGetValue(song.AlbumId, out var album)
                && !string.IsNullOrEmpty(album.Artist) && !TextComparer.Equals(album.Artist, song.Artist))
            .Select(song => song.AlbumId)
            .ToHashSet(StringComparer.Ordinal);
        Files = songs.Select(song => ToMediaFile(song, remoteAlbumsById)).ToArray();
        foreach (var file in Files)
        {
            if (!explicitAlbumArtists.Contains(songsById[file.Path].AlbumId))
            {
                file.AlbumArtist = string.Empty;
            }
        }
        var multipleArtistAlbums = Files.Where(file => file.AlbumArtist.Length == 0)
            .GroupBy(file => file.Album, StringComparer.Ordinal)
            .Where(group => group.Select(file => file.Artist).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            var song = songsById[file.Path];
            file.AlbumGroupingKey = file.AlbumArtist.Length > 0 ? string.Empty : albumGrouping switch
            {
                AlbumGroupingMode.SeparateArtists => $"artist:{file.Artist}",
                AlbumGroupingMode.SameFolder when multipleArtistAlbums.Contains(file.Album) =>
                    song.Path.Length == 0 ? $"album:{AlbumKey(song)}" : $"folder:{FolderPath(song.Path)}",
                _ => string.Empty,
            };
        }
        filesByPath = Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        filesByFolder = songs
            .GroupBy(song => FolderPath(song.Path), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(song => filesByPath[song.Id]).ToArray(),
                StringComparer.Ordinal);
        FolderPaths = songs.Any(song => !string.IsNullOrEmpty(song.Path)) ? filesByFolder.Keys.ToArray() : [];

        var albums = new List<MediaLibraryAlbum>();
        albumsByTrack = new Dictionary<string, MediaLibraryAlbum>(StringComparer.Ordinal);
        foreach (var group in Files.GroupBy(file => (file.Album, file.AlbumArtist, file.AlbumGroupingKey)))
        {
            var first = group.First();
            remoteAlbumsById.TryGetValue(songsById[first.Path].AlbumId, out var remoteAlbum);
            var artist = first.AlbumArtist.Length > 0 ? first.AlbumArtist
                : string.Join(", ", group.Select(file => file.Artist).Distinct(StringComparer.Ordinal));
            var album = new MediaLibraryAlbum
            {
                Title = first.Album,
                ArtistForQuery = first.AlbumArtist,
                AlbumGroupingKey = first.AlbumGroupingKey,
                Artist = artist,
                Genre = remoteAlbum?.Genre ?? first.Genre,
                Date = remoteAlbum?.Year?.ToString(CultureInfo.InvariantCulture) ?? first.Date,
                FirstFileInAlbum = first.Path,
                FileCount = group.Count(),
                DatabaseItemAddedDate = group.Min(file => file.DatabaseItemAddedDate),
            };
            albums.Add(album);
            foreach (var file in group)
            {
                albumsByTrack[file.Path] = album;
                file.TotalTracks = album.FileCount.ToString(CultureInfo.InvariantCulture);
            }
        }
        Albums = albums.ToArray();

        Artists = BuildArtists(Files, albumsByTrack);
        ArtistBrowseItems = BuildArtistBrowseItems(songs, Artists, filesByPath, albumsByTrack);
    }

    public AlbumGroupingMode AlbumGrouping { get; }

    public NextcloudMusicSnapshot WithAlbumGrouping(AlbumGroupingMode mode) =>
        new(remoteAlbums, remoteSongs, CacheRevision, mode);

    public string CacheRevision { get; }

    public DbMediaFile[] Files { get; }

    public IReadOnlyList<string> FolderPaths { get; }

    public IReadOnlyList<DbMediaFile> FilesInFolder(string? folderPath) =>
        folderPath is null ? Files : filesByFolder.GetValueOrDefault(folderPath) ?? [];

    private static string FolderPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator].TrimStart('/');
    }

    public MediaLibraryAlbum[] Albums { get; }

    public MediaLibraryArtist[] Artists { get; }

    public MediaLibraryArtistBrowseItem[] ArtistBrowseItems { get; }

    public IReadOnlyList<MediaLibraryAlbum> AlbumsForArtist(string artist) => Files
        .Where(file => TextComparer.Equals(file.Artist, artist) || TextComparer.Equals(file.AlbumArtist, artist))
        .Select(file => albumsByTrack[file.Path])
        .Distinct()
        .ToArray();

    public bool TryGetSong(string trackId, out NextcloudMusicSong song) =>
        songsById.TryGetValue(trackId, out song!);

    public bool TryGetFile(string trackId, out DbMediaFile file) =>
        filesByPath.TryGetValue(trackId, out file!);

    public bool TryGetAlbumByTrack(string trackId, out MediaLibraryAlbum album) =>
        albumsByTrack.TryGetValue(trackId, out album!);

    public string? CoverArtForTrack(string trackId) => coverArtByTrack.GetValueOrDefault(trackId);

    private static DbMediaFile ToMediaFile(
        NextcloudMusicSong song,
        IReadOnlyDictionary<string, NextcloudMusicAlbum> albums)
    {
        albums.TryGetValue(song.AlbumId, out var album);
        return new DbMediaFile
        {
            Id = EntityNumber(song.Id),
            Duration = TimeSpan.FromSeconds(song.DurationSeconds),
            TotalDiscs = string.Empty,
            DiscNumber = song.DiscNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            TotalTracks = album?.SongCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Description = string.Empty,
            Copyright = string.Empty,
            Comments = string.Empty,
            Grouping = string.Empty,
            Genre = song.Genre,
            TrackNumber = song.TrackNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            AlbumArtist = album?.Artist ?? song.Artist,
            Performer = song.Artist,
            Composer = string.Empty,
            Date = song.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Album = song.Album,
            Artist = song.Artist,
            Title = song.Title,
            Path = song.Id,
            DatabaseItemAddedDate = song.Created,
            FileLastModifiedDate = song.Created,
        };
    }

    private static MediaLibraryArtist[] BuildArtists(
        IReadOnlyList<DbMediaFile> files,
        IReadOnlyDictionary<string, MediaLibraryAlbum> albumsByTrack)
    {
        var filesByArtist = new Dictionary<string, List<DbMediaFile>>(TextComparer);
        foreach (var file in files)
        {
            Add(file.Artist, file);
            if (!TextComparer.Equals(file.AlbumArtist, file.Artist))
            {
                Add(file.AlbumArtist, file);
            }
        }
        return filesByArtist.Keys.Select(name =>
        {
            var artistFiles = filesByArtist[name];
            var artistAlbums = artistFiles.Select(file => albumsByTrack[file.Path]).Distinct().ToArray();
            var dates = artistFiles.Select(file => file.DatabaseItemAddedDate)
                .Concat(artistAlbums.Select(album => album.DatabaseItemAddedDate))
                .ToArray();
            return new MediaLibraryArtist
            {
                Name = name,
                FileCount = artistFiles.Count,
                AlbumCount = artistAlbums.Length,
                DatabaseItemAddedDate = dates.Length == 0 ? DateTimeOffset.UnixEpoch : dates.Min(),
            };
        }).ToArray();

        void Add(string artist, DbMediaFile file)
        {
            if (string.IsNullOrWhiteSpace(artist))
            {
                return;
            }
            if (!filesByArtist.TryGetValue(artist, out var artistFiles))
            {
                artistFiles = [];
                filesByArtist.Add(artist, artistFiles);
            }
            artistFiles.Add(file);
        }
    }

    private static MediaLibraryArtistBrowseItem[] BuildArtistBrowseItems(
        IReadOnlyList<NextcloudMusicSong> songs,
        IReadOnlyList<MediaLibraryArtist> artists,
        IReadOnlyDictionary<string, DbMediaFile> files,
        IReadOnlyDictionary<string, MediaLibraryAlbum> albumsByTrack)
    {
        var artwork = new Dictionary<string, List<string>>(TextComparer);
        var artworkAlbums = new Dictionary<string, HashSet<string>>(TextComparer);
        foreach (var song in songs.Where(song => song.CoverArt is not null))
        {
            var albumKey = albumsByTrack[song.Id].FirstFileInAlbum;
            AddArtwork(song.Artist, albumKey, song.Id);
            AddArtwork(files[song.Id].AlbumArtist, albumKey, song.Id);
        }

        return artists.Select(artist => new MediaLibraryArtistBrowseItem
        {
            Artist = artist,
            ArtworkPaths = artwork.GetValueOrDefault(artist.Name)?.ToArray() ?? [],
        }).ToArray();

        void AddArtwork(string artist, string albumKey, string path)
        {
            if (artist.Length == 0)
            {
                return;
            }
            if (!artwork.TryGetValue(artist, out var paths))
            {
                paths = [];
                artwork.Add(artist, paths);
            }
            if (!artworkAlbums.TryGetValue(artist, out var albumKeys))
            {
                albumKeys = new HashSet<string>(StringComparer.Ordinal);
                artworkAlbums.Add(artist, albumKeys);
            }
            if (paths.Count < 4 && albumKeys.Add(albumKey))
            {
                paths.Add(path);
            }
        }
    }

    private static string AlbumKey(NextcloudMusicSong song) =>
        song.AlbumId.Length > 0
            ? song.AlbumId
            : $"{song.Artist}\u001f{song.Album}";

    private static int EntityNumber(string id)
    {
        var separator = id.LastIndexOf('-');
        return int.TryParse(
            separator >= 0 ? id[(separator + 1)..] : id,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var result)
                ? result
                : unchecked((int)StableHash(0, id));
    }

    public static ulong StableHash(long seed, string text)
    {
        var hash = 14695981039346656037UL ^ unchecked((ulong)seed);
        foreach (var character in text)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }
        hash ^= hash >> 33;
        hash *= 0xff51afd7ed558ccdUL;
        hash ^= hash >> 33;
        return hash;
    }
}

internal sealed class NextcloudMusicQueryService(NextcloudMusicCatalog catalog) : ILibraryQueryService
{
    private static readonly StringComparer TextComparer = StringComparer.OrdinalIgnoreCase;

    public Task<IReadOnlyList<MediaLibraryAlbum>> GetAlbumsAsync(string? artist = null)
    {
        var snapshot = catalog.Snapshot;
        IReadOnlyList<MediaLibraryAlbum> result = artist is null
            ? snapshot.Albums
            : snapshot.AlbumsForArtist(artist);
        return Task.FromResult(result);
    }

    public Task<PagedResult<MediaLibraryAlbum>> GetAlbumsPageAsync(
        AlbumPageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<MediaLibraryAlbum> ordered = request.Sort switch
        {
            AlbumSortField.ArtistName => catalog.Snapshot.Albums
                .OrderBy(album => album.Artist, TextComparer)
                .ThenBy(album => album.Title, TextComparer),
            AlbumSortField.Year => catalog.Snapshot.Albums
                .OrderBy(album => album.Date, TextComparer)
                .ThenBy(album => album.Title, TextComparer),
            _ => catalog.Snapshot.Albums.OrderBy(album => album.Title, TextComparer),
        };
        if (!request.Ascending)
        {
            ordered = ordered.Reverse();
        }
        return Task.FromResult(Page(ordered, request.PageSize, request.Cursor));
    }

    public Task<IReadOnlyList<MediaLibraryArtist>> GetArtistsAsync() =>
        Task.FromResult<IReadOnlyList<MediaLibraryArtist>>(catalog.Snapshot.Artists);

    public Task<PagedResult<MediaLibraryArtistBrowseItem>> GetArtistsPageAsync(
        ArtistPageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<MediaLibraryArtistBrowseItem> ordered = catalog.Snapshot.ArtistBrowseItems
            .OrderBy(item => item.Artist.Name, TextComparer);
        if (!request.Ascending)
        {
            ordered = ordered.Reverse();
        }
        return Task.FromResult(Page(ordered, request.PageSize, request.Cursor));
    }

    public Task<IReadOnlyList<DbMediaFile>> GetMediaFilesAsync() =>
        Task.FromResult<IReadOnlyList<DbMediaFile>>(catalog.Snapshot.Files);

    public Task<IReadOnlyList<string>> GetMediaFolderPathsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(catalog.Snapshot.FolderPaths);
    }

    public Task<PagedResult<DbMediaFile>> GetMediaFilesPageAsync(
        MediaFilePageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var files = catalog.Snapshot.FilesInFolder(request.FolderPath);
        if (request.Sort == MediaFileSortField.Duration)
        {
            var byDuration = request.Ascending
            ? files.OrderBy(file => file.Duration)
            : files.OrderByDescending(file => file.Duration);
            return Task.FromResult(Page(
                byDuration.ThenBy(file => file.Title, TextComparer).ThenBy(file => file.Id),
                request.PageSize,
                request.Cursor));
        }

        IEnumerable<DbMediaFile> ordered = request.Sort switch
        {
            MediaFileSortField.AlbumName => files
                .OrderBy(file => file.Album, TextComparer)
                .ThenBy(file => Number(file.DiscNumber))
                .ThenBy(file => Number(file.TrackNumber))
                .ThenBy(file => file.Title, TextComparer),
            MediaFileSortField.ArtistName => files
                .OrderBy(file => file.Artist, TextComparer)
                .ThenBy(file => file.Album, TextComparer)
                .ThenBy(file => file.Title, TextComparer),
            MediaFileSortField.Genre => files
                .OrderBy(file => file.Genre, TextComparer)
                .ThenBy(file => file.Title, TextComparer),
            MediaFileSortField.Year => files
                .OrderBy(file => file.Date, TextComparer)
                .ThenBy(file => file.Title, TextComparer),
            MediaFileSortField.DateAdded => files
                .OrderBy(file => file.DatabaseItemAddedDate)
                .ThenBy(file => file.Title, TextComparer),
            _ => files.OrderBy(file => file.Title, TextComparer),
        };
        if (!request.Ascending)
        {
            ordered = ordered.Reverse();
        }
        return Task.FromResult(Page(ordered, request.PageSize, request.Cursor));
    }

    public Task<PagedResult<DbMediaFile>> GetShuffledMediaFilesPageAsync(
        ShuffledMediaFilePageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = catalog.Snapshot.Files
            .OrderBy(file => NextcloudMusicSnapshot.StableHash(request.Seed, file.Path))
            .ThenBy(file => file.Path, StringComparer.Ordinal);
        return Task.FromResult(Page(ordered, request.PageSize, request.Cursor));
    }

    public Task<MediaLibraryAlbum> GetAlbumByNameAsync(string? albumArtist, string? albumName) =>
        GetAlbumByNameAsync(albumArtist, albumName, albumGroupingKey: null);

    public Task<MediaLibraryAlbum> GetAlbumByNameAsync(
        string? albumArtist, string? albumName, string? albumGroupingKey) =>
        Task.FromResult(catalog.Snapshot.Albums.FirstOrDefault(album =>
            TextComparer.Equals(album.ArtistForQuery, albumArtist ?? string.Empty)
            && TextComparer.Equals(album.Title, albumName ?? string.Empty)
            && (albumGroupingKey is null || StringComparer.Ordinal.Equals(album.AlbumGroupingKey, albumGroupingKey)))
            ?? throw new ArgumentException($"Album {albumName} from artist {albumArtist} not found."));

    public Task<MediaLibraryAlbum> GetAlbumByTrackPathAsync(string path) =>
        Task.FromResult(catalog.Snapshot.TryGetAlbumByTrack(path, out var album)
            ? album
            : throw new ArgumentException($"No album found for the track '{path}'."));

    public Task<MediaLibraryArtist> GetArtistByNameAsync(string? name) =>
        Task.FromResult(catalog.Snapshot.Artists.FirstOrDefault(artist =>
            TextComparer.Equals(artist.Name, name ?? string.Empty))
            ?? throw new ArgumentException($"Artist {name} not found."));

    public Task<MediaLibraryArtist> GetArtistByTrackPathAsync(string path)
    {
        if (!catalog.Snapshot.TryGetFile(path, out var file))
        {
            throw new ArgumentException($"No artist found for the track '{path}'.");
        }
        return GetArtistByNameAsync(file.Artist);
    }

    public Task<bool> ArtistExistsAsync(string? name) =>
        Task.FromResult(catalog.Snapshot.Artists.Any(artist => TextComparer.Equals(artist.Name, name)));

    public Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByAlbumAsync(string? albumName, string? artistName) =>
        GetMediaFilesByAlbumAsync(albumName, artistName, albumGroupingKey: null);

    public Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByAlbumAsync(
        string? albumName, string? artistName, string? albumGroupingKey) =>
        Task.FromResult<IReadOnlyList<DbMediaFile>>(catalog.Snapshot.Files.Where(file =>
            TextComparer.Equals(file.Album, albumName ?? string.Empty)
            && TextComparer.Equals(file.AlbumArtist, artistName ?? string.Empty)
            && (albumGroupingKey is null || StringComparer.Ordinal.Equals(file.AlbumGroupingKey, albumGroupingKey))).ToArray());

    public Task<IReadOnlyList<DbMediaFile>> GetMediaFilesByArtistAsync(MediaLibraryArtist artist) =>
        Task.FromResult<IReadOnlyList<DbMediaFile>>(catalog.Snapshot.Files.Where(file =>
            TextComparer.Equals(file.Artist, artist.Name)
            || TextComparer.Equals(file.AlbumArtist, artist.Name)).ToArray());

    public Task<LibrarySearchResults> SearchAsync(string keyword)
    {
        var normalized = keyword?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return Task.FromResult(new LibrarySearchResults());
        }

        return Task.FromResult(new LibrarySearchResults
        {
            Albums = catalog.Snapshot.Albums.Where(album =>
                Contains(album.Title, normalized)
                || Contains(album.Artist, normalized)
                || Contains(album.Genre, normalized)).ToList(),
            Artists = catalog.Snapshot.Artists.Where(artist => Contains(artist.Name, normalized)).ToList(),
            Files = catalog.Snapshot.Files.Where(file =>
                Contains(file.Title, normalized)
                || Contains(file.Artist, normalized)
                || Contains(file.AlbumArtist, normalized)
                || Contains(file.Album, normalized)
                || Contains(file.Genre, normalized)).ToList(),
        });
    }

    private static bool Contains(string? value, string keyword) =>
        value?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;

    private static int Number(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : int.MaxValue;

    private static PagedResult<T> Page<T>(IEnumerable<T> values, int pageSize, string? cursor)
    {
        var offset = ParseCursor(cursor);
        var page = values.Skip(offset).Take(pageSize + 1).ToArray();
        var hasMore = page.Length > pageSize;
        return new PagedResult<T>(
            page.Take(pageSize).ToArray(),
            hasMore ? (offset + pageSize).ToString(CultureInfo.InvariantCulture) : null,
            TotalCount: null);
    }

    private static int ParseCursor(string? cursor)
    {
        if (cursor is null)
        {
            return 0;
        }
        return int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            && offset >= 0
                ? offset
                : throw new ArgumentException("The Nextcloud Music page cursor is invalid.", nameof(cursor));
    }
}

internal sealed class NextcloudMusicScanService(NextcloudMusicCatalog catalog) : ILibraryScanService
{
    public event EventHandler<LibraryScanStartedEventArgs>? ScanStarted;
    public event EventHandler<LibraryScanProgressEventArgs>? ScanProgressChanged;
    public event EventHandler<LibraryScanWarningEventArgs>? ScanWarning
    {
        add { }
        remove { }
    }
    public event EventHandler<LibraryScanCompletedEventArgs>? ScanCompleted;

    public async Task<LibraryScanResult> ScanAsync()
    {
        ScanStarted?.Invoke(this, new LibraryScanStartedEventArgs([]));
        var count = await catalog.RefreshAsync().ConfigureAwait(false);
        ScanProgressChanged?.Invoke(this, new LibraryScanProgressEventArgs(count));
        ScanCompleted?.Invoke(this, new LibraryScanCompletedEventArgs(count, 0));
        return new LibraryScanResult(count, []);
    }
}

internal sealed class NextcloudMusicVideoQueryService : IVideoLibraryQueryService
{
    public Task<IReadOnlyList<DbVideoFile>> GetVideosAsync() =>
        Task.FromResult<IReadOnlyList<DbVideoFile>>([]);

    public Task<PagedResult<DbVideoFile>> GetVideosPageAsync(
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PagedResult<DbVideoFile>([], null, 0));
    }

    public Task<IReadOnlyList<DbVideoFile>> GetVideosInFolderAsync(
        string folderPath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DbVideoFile>>([]);

    public Task<IReadOnlyList<DbVideoFile>> GetVideoPicksAsync(
        VideoPickOrder order,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DbVideoFile>>([]);

    public Task<IReadOnlyList<VideoFolderCount>> GetVideoFolderCountsAsync() =>
        Task.FromResult<IReadOnlyList<VideoFolderCount>>([]);

    public Task<IReadOnlyList<DbVideoFile>> SearchVideosAsync(
        string keyword,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DbVideoFile>>([]);

    public Task<DbVideoFile?> GetVideoByPathAsync(string path) => Task.FromResult<DbVideoFile?>(null);

    public Task<byte[]?> GetVideoPreviewAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);
}

internal sealed class NextcloudMusicPlatformIO : IMultiMediaPlatformIO
{
    private readonly NextcloudMusicClient client;
    private readonly NextcloudMusicCatalog catalog;
    private readonly RemoteMediaBlockCache blockCache;
    private readonly RemoteImageCache? imageCache;

    public NextcloudMusicPlatformIO(
        NextcloudMusicClient client,
        NextcloudMusicCatalog catalog,
        IMediaBlockStore blockStore,
        RemoteImageCache? imageCache)
    {
        this.client = client;
        this.catalog = catalog;
        this.imageCache = imageCache;
        blockCache = new RemoteMediaBlockCache(blockStore);
    }

    public int OptimalIoThreadCount => 4;

    public string LocalDataPath => Path.Combine(PlatformServices.AppDataPath, "LightStudio.LightPlayer");

    public IStorageCache MediaBlockCache => blockCache;

    public IStorageCache? ImageCache => imageCache;

    public void InitializePlatform() => IMultiMediaPlatformIO.Instance = this;

    public async Task<Stream> OpenMediaFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var snapshot = catalog.Snapshot;
        EnsureTrack(snapshot, path);
        try
        {
            return await RemoteMediaStream.CreateAsync(
                client,
                blockCache,
                path,
                cancellationToken,
                cacheIdentity: $"{client.EndpointIdentity}\n{path}").ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            var bytes = await client.GetBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return new MemoryStream(bytes, writable: false);
        }
    }

    public Task<bool> FolderExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task<MediaFileProperties?> GetMediaFilePropertiesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var song = EnsureTrack(path);
        var separator = song.Path.LastIndexOf('/');
        var name = separator >= 0 ? song.Path[(separator + 1)..] : song.Path;
        var directory = separator > 0 ? song.Path[..separator] : null;
        if (name.Length == 0)
        {
            name = song.Suffix.Length == 0 ? song.Title : $"{song.Title}.{song.Suffix}";
        }
        return Task.FromResult<MediaFileProperties?>(new(
            name,
            path,
            directory,
            song.Size,
            song.Created.UtcDateTime));
    }

    public Task<string?> ResolveFileRelativeToAsync(
        string sourceFilePath,
        string referencedFileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public Task<IFile[]> ListFilesFullNameInPathAsync(string path) =>
        throw new NotSupportedException("Nextcloud Music does not expose filesystem enumeration through OpenSubsonic.");

    public Task<string[]> ListFoldersFullNameInPathAsync(string path) =>
        throw new NotSupportedException("Nextcloud Music does not expose filesystem enumeration through OpenSubsonic.");

    public Task<MediaInfo> GetMediaInfoAsync(string mediaFilePath)
    {
        var file = EnsureFile(mediaFilePath);
        return Task.FromResult(new MediaInfo
        {
            Album = file.Album,
            AlbumArtist = file.AlbumArtist,
            Artist = file.Artist,
            Comments = file.Comments,
            Composer = file.Composer,
            Copyright = file.Copyright,
            Date = file.Date,
            Description = file.Description,
            DiscNumber = file.DiscNumber,
            Duration = file.Duration,
            Genre = file.Genre,
            Grouping = file.Grouping,
            Performer = file.Performer,
            Title = file.Title,
            TotalDiscs = file.TotalDiscs,
            TotalTracks = file.TotalTracks,
            TrackNumber = file.TrackNumber,
            AllProperties = new Dictionary<string, string>(StringComparer.Ordinal),
        });
    }

    public async Task<byte[]?> GetAlbumCoverAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        var snapshot = catalog.Snapshot;
        if (!snapshot.TryGetSong(mediaFilePath, out _))
        {
            return null;
        }

        var coverArt = snapshot.CoverArtForTrack(mediaFilePath);
        if (coverArt is null)
        {
            return null;
        }

        return imageCache is null
            ? await client.GetCoverArtAsync(coverArt, cancellationToken).ConfigureAwait(false)
            : await imageCache.GetAlbumArtAsync(
                    $"{client.EndpointIdentity}\n{coverArt}\n{snapshot.CacheRevision}",
                    token => client.GetCoverArtAsync(coverArt, token))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
    }

    public Task<VideoProbeInfo?> ProbeVideoAsync(
        string mediaFilePath,
        bool withPreview = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<VideoProbeInfo?>(null);
    }

    public Task<IReadOnlyList<AudioStreamProbeInfo>> ProbeAudioStreamsAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AudioStreamProbeInfo>>([]);
    }

    public Task<MediaProbeReport?> InspectMediaAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<MediaProbeReport?>(null);
    }

    public T GetConfigurationValue<T>(string key) => default!;

    public void SetConfigurationValue<T>(string key, T value)
    {
    }

    private NextcloudMusicSong EnsureTrack(string path) => EnsureTrack(catalog.Snapshot, path);

    private static NextcloudMusicSong EnsureTrack(NextcloudMusicSnapshot snapshot, string path) =>
        snapshot.TryGetSong(path, out var song)
            ? song
            : throw new FileNotFoundException("The Nextcloud Music track is no longer in the catalog.", path);

    private DbMediaFile EnsureFile(string path) =>
        catalog.Snapshot.TryGetFile(path, out var file)
            ? file
            : throw new FileNotFoundException("The Nextcloud Music track is no longer in the catalog.", path);
}
