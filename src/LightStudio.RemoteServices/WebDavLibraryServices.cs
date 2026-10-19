using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.MediaLibraryCore.Library;

namespace LightStudio.RemoteServices;

internal sealed class WebDavIndexedLibrary : IDisposable
{
    private readonly string databaseConnectionString;

    public WebDavIndexedLibrary(
        string databasePath,
        WebDavClient client,
        WebDavMultimediaPlatformIO platform,
        Func<bool> loadMusic,
        Func<bool> loadVideo,
        Func<AlbumGroupingMode>? albumGrouping = null)
    {
        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new ArgumentException("The WebDAV index path has no parent directory.", nameof(databasePath));
        Directory.CreateDirectory(directory);
        databaseConnectionString = $"Data Source=\"{databasePath.Replace("\"", "\"\"")}\"";
        LibraryQuery = new LibraryQueryService(CreateDatabase, albumGrouping);
        VideoLibraryQuery = new VideoLibraryQueryService(CreateDatabase, platform, new ThumbnailMemoryCache());
        LibraryScan = new WebDavLibraryScanService(
            CreateDatabase,
            platform,
            client.RootPath,
            loadMusic,
            loadVideo);
        var locations = new WebDavLibraryLocationService(client.RootPath);
        VideoFolders = new PlatformVideoFolderBrowser(platform, locations);
        MusicFolders = new IndexedMusicFolderBrowser(LibraryQuery,
            _ => Task.FromResult(locations.GetLibraryFolderPaths()), platform);
        SubtitleFiles = new WebDavSubtitleFileBrowser(client);
    }

    public ILibraryQueryService LibraryQuery { get; }

    public IVideoLibraryQueryService VideoLibraryQuery { get; }

    public WebDavLibraryScanService LibraryScan { get; }

    public IVideoFolderBrowser VideoFolders { get; }

    public IMusicFolderBrowser MusicFolders { get; }

    public ISubtitleFileBrowser SubtitleFiles { get; }

    private MediaLibraryDatabase CreateDatabase() => new(databaseConnectionString);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var database = CreateDatabase();
        await database.EnsureSchemaCreatedAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => LibraryScan.Dispose();
}

internal sealed class WebDavLibraryScanService : ILibraryScanService, IDisposable
{
    private readonly Func<MediaLibraryDatabase> databaseFactory;
    private readonly WebDavMultimediaPlatformIO platform;
    private readonly string rootPath;
    private readonly Func<bool> loadMusic;
    private readonly Func<bool> loadVideo;
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    public WebDavLibraryScanService(
        Func<MediaLibraryDatabase> databaseFactory,
        WebDavMultimediaPlatformIO platform,
        string rootPath,
        Func<bool> loadMusic,
        Func<bool> loadVideo)
    {
        this.databaseFactory = databaseFactory;
        this.platform = platform;
        this.rootPath = rootPath;
        this.loadMusic = loadMusic;
        this.loadVideo = loadVideo;
    }

    public event EventHandler<LibraryScanStartedEventArgs>? ScanStarted;
    public event EventHandler<LibraryScanProgressEventArgs>? ScanProgressChanged;
    public event EventHandler<LibraryScanWarningEventArgs>? ScanWarning;
    public event EventHandler<LibraryScanCompletedEventArgs>? ScanCompleted;

    public Task<LibraryScanResult> ScanAsync() => ScanAsync(CancellationToken.None);

    public async Task<LibraryScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        await scanGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ScanStarted?.Invoke(this, new LibraryScanStartedEventArgs([rootPath]));
            var warnings = new List<LibraryScanWarning>();
            var (indexedCount, musicChanged) = loadMusic() ? await ScanMusicAsync(warnings, linked.Token).ConfigureAwait(false) : (0, false);
            ObjectDisposedException.ThrowIf(disposed, this);
            var (videoCount, imageCount, mediaChanged) = loadVideo()
                ? await ScanMediaAsync(indexedCount, warnings, linked.Token).ConfigureAwait(false)
                : (0, 0, false);

            foreach (var warning in warnings)
            {
                ScanWarning?.Invoke(this, new LibraryScanWarningEventArgs(warning));
            }

            var result = new LibraryScanResult(indexedCount, warnings)
            {
                VideoIndexedCount = videoCount,
                ImageIndexedCount = imageCount,
                HasChanges = musicChanged || mediaChanged,
            };
            ScanCompleted?.Invoke(this, new LibraryScanCompletedEventArgs(indexedCount, warnings.Count)
            {
                HasChanges = result.HasChanges,
                VideoIndexedCount = videoCount,
                ImageIndexedCount = imageCount,
            });
            return result;
        }
        finally
        {
            scanGate.Release();
        }
    }

    private async Task<(int IndexedCount, bool HasChanges)> ScanMusicAsync(
        List<LibraryScanWarning> warnings, CancellationToken cancellationToken)
    {
        await using var database = databaseFactory();
        var indexer = new MusicIndexer(
            database,
            platform,
            StringComparer.Ordinal,
            path => platform.OpenMediaFileForScanAsync(path, cancellationToken));
        void OnProgress(object? _, int count) =>
            ScanProgressChanged?.Invoke(this, new LibraryScanProgressEventArgs(count));

        indexer.IndexItemAdded += OnProgress;
        try
        {
            var result = await indexer.ScanAsync([rootPath], cancellationToken).ConfigureAwait(false);
            warnings.AddRange(result.Item2.Select(CreateWarning));
            return (result.Item1, indexer.HasChanges);
        }
        finally
        {
            indexer.IndexItemAdded -= OnProgress;
        }
    }

    private async Task<(int VideoCount, int ImageCount, bool HasChanges)> ScanMediaAsync(
        int musicCount, List<LibraryScanWarning> warnings, CancellationToken cancellationToken)
    {
        await using var database = databaseFactory();
        var indexer = new MediaIndexer(database, platform, StringComparer.Ordinal);
        void OnProgress(object? _, int count) =>
            ScanProgressChanged?.Invoke(this, new LibraryScanProgressEventArgs(musicCount + count));

        indexer.IndexItemAdded += OnProgress;
        try
        {
            var result = await indexer.ScanAsync([rootPath], cancellationToken).ConfigureAwait(false);
            warnings.AddRange(result.Item2.Select(CreateWarning));
            return (result.Item1, indexer.ImageIndexedCount, indexer.HasChanges);
        }
        finally
        {
            indexer.IndexItemAdded -= OnProgress;
        }
    }

    private static LibraryScanWarning CreateWarning(Tuple<string, Exception> value)
    {
        AppLog.Warning("WebDAV.Scan", $"Could not scan '{value.Item1}'.", value.Item2);
        return new LibraryScanWarning(value.Item1, value.Item2.GetBaseException().Message);
    }

    public void Dispose()
    {
        disposed = true;
        lifetime.Cancel();
    }
}

internal sealed class WebDavLibraryLocationService(string rootPath) : ILibraryLocationService
{
    public IReadOnlyList<string> GetLibraryFolderPaths() => [rootPath];

    public IReadOnlyList<string> GetVideoFolderPaths() => [rootPath];
}

internal sealed class WebDavSubtitleFileBrowser(WebDavClient client) : ISubtitleFileBrowser
{
    public async Task<SubtitleFileListing> ListAsync(
        string? folderPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(folderPath))
        {
            return new SubtitleFileListing(
                null,
                [],
                [new SubtitleFileEntry(VideoFolderTree.LeafName(client.RootPath), client.RootPath, true)]);
        }

        var values = await client.ListAsync(folderPath, cancellationToken).ConfigureAwait(false);
        var entries = values
            .Where(value => !SamePath(value.Path, folderPath))
            .Where(value => value.IsCollection ||
                MediaContainerFormats.ExternalSubtitle.Contains(Path.GetExtension(value.Name)))
            .Select(value => new SubtitleFileEntry(value.Name, value.Path, value.IsCollection))
            .OrderByDescending(value => value.IsFolder)
            .ThenBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var ancestors = VideoFolderPaths
            .BuildAncestors(folderPath, [client.RootPath])
            .Select(value => new SubtitleFileEntry(value.Name, value.Path, true))
            .ToArray();
        return new SubtitleFileListing(folderPath, ancestors, entries);
    }

    public Task<byte[]> ReadAsync(
        string filePath,
        CancellationToken cancellationToken = default) =>
        client.GetBytesAsync(filePath, cancellationToken);

    private static bool SamePath(string first, string second) =>
        string.Equals(first.TrimEnd('/'), second.TrimEnd('/'), StringComparison.Ordinal);
}