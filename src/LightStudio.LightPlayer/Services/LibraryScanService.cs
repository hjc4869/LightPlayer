using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Library;
using Microsoft.Extensions.DependencyInjection;

namespace LightStudio.LightPlayer.Services;

public interface ILibraryScanService
{
    event EventHandler<LibraryScanStartedEventArgs>? ScanStarted;

    event EventHandler<LibraryScanProgressEventArgs>? ScanProgressChanged;

    event EventHandler<LibraryScanWarningEventArgs>? ScanWarning;

    event EventHandler<LibraryScanCompletedEventArgs>? ScanCompleted;

    Task<LibraryScanResult> ScanAsync();
}

public sealed record LibraryScanStartedEventArgs(IReadOnlyList<string> Folders);

public sealed record LibraryScanProgressEventArgs(int IndexedCount);

public sealed record LibraryScanWarning(string Path, string Message);

public sealed record LibraryScanWarningEventArgs(LibraryScanWarning Warning);

public sealed record LibraryScanCompletedEventArgs(int IndexedCount, int WarningCount);

public sealed record LibraryScanResult(int IndexedCount, IReadOnlyList<LibraryScanWarning> Warnings);

public sealed class LibraryScanService(ILibraryLocationService libraryLocationService) : ILibraryScanService
{
    public event EventHandler<LibraryScanStartedEventArgs>? ScanStarted;

    public event EventHandler<LibraryScanProgressEventArgs>? ScanProgressChanged;

    public event EventHandler<LibraryScanWarningEventArgs>? ScanWarning;

    public event EventHandler<LibraryScanCompletedEventArgs>? ScanCompleted;

    public async Task<LibraryScanResult> ScanAsync()
    {
        var configuredFolders = libraryLocationService.GetLibraryFolderPaths()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(GetPathComparer())
            .ToArray();
        var folders = configuredFolders.Where(Directory.Exists).ToArray();
        var warnings = configuredFolders
            .Where(path => !Directory.Exists(path))
            .Select(path => new LibraryScanWarning(path, "Folder does not exist."))
            .ToList();

        ScanStarted?.Invoke(this, new LibraryScanStartedEventArgs(folders));
        foreach (var warning in warnings)
        {
            ScanWarning?.Invoke(this, new LibraryScanWarningEventArgs(warning));
        }

        if (folders.Length == 0)
        {
            var emptyResult = new LibraryScanResult(0, warnings);
            ScanCompleted?.Invoke(this, new LibraryScanCompletedEventArgs(emptyResult.IndexedCount, emptyResult.Warnings.Count));
            return emptyResult;
        }

        using var scope = ApplicationServiceBase.App.GetScope();
        var indexer = scope.ServiceProvider.GetRequiredService<FileIndexer>();
        void OnIndexItemAdded(object? _, int count) => ScanProgressChanged?.Invoke(this, new LibraryScanProgressEventArgs(count));

        indexer.IndexItemAdded += OnIndexItemAdded;
        try
        {
            var result = await indexer.ScanAsync(folders, new NoopThumbnailOperations());

            // The library changed on disk; drop cached projections so browse pages reload fresh data.
            GlobalLibraryCache.Invalidate();

            if (result.Item1 < 0)
            {
                warnings.Add(new LibraryScanWarning("Library database", "The scan finished, but the database update did not report a successful count."));
            }

            warnings.AddRange(result.Item2.Select(CreateWarning));
            foreach (var warning in warnings.Skip(configuredFolders.Length - folders.Length))
            {
                ScanWarning?.Invoke(this, new LibraryScanWarningEventArgs(warning));
            }

            var scanResult = new LibraryScanResult(result.Item1, warnings);
            ScanCompleted?.Invoke(this, new LibraryScanCompletedEventArgs(scanResult.IndexedCount, scanResult.Warnings.Count));
            return scanResult;
        }
        finally
        {
            indexer.IndexItemAdded -= OnIndexItemAdded;
        }
    }

    private static LibraryScanWarning CreateWarning(Tuple<string, Exception> warning)
    {
        return new LibraryScanWarning(warning.Item1, warning.Item2.GetBaseException().Message);
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}

internal sealed class NoopThumbnailOperations : IThumbnailOperations
{
    public Task FetchAlbumAsync(string artist, string album, string filePath) => Task.CompletedTask;

    public Task RemoveAlbumAsync(string artist, string album) => Task.CompletedTask;

    public Task RemoveArtistAsync(string artist) => Task.CompletedTask;
}