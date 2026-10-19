using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.MediaLibraryCore.Library;
using Microsoft.Extensions.DependencyInjection;

namespace LightStudio.LocalServices;

public sealed class LibraryScanService : ILibraryScanService, IDisposable
{
    private readonly ILibraryLocationService libraryLocationService;
    private readonly IAppSettingsStore? settingsStore;
    private readonly object scanLock = new();
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Timer? scanTimer;
    private Task<LibraryScanResult>? currentScan;
    private CancellationTokenSource? scanCancellation;
    private long scheduledAt;
    private TimeSpan scheduledDelay;
    private bool isScheduled;
    private bool isDisposed;

    public LibraryScanService(ILibraryLocationService libraryLocationService, IAppSettingsStore? settingsStore = null)
    {
        this.libraryLocationService = libraryLocationService;
        this.settingsStore = settingsStore;
        scanTimer = OperatingSystem.IsBrowser() ? null : new Timer(OnScanTimer);
        if (settingsStore is not null)
        {
            settingsStore.LibraryScanSettingsChanged += OnLibraryScanSettingsChanged;
            lock (scanLock)
            {
                StartMonitoring();
            }
        }
    }

    public event EventHandler<LibraryScanStartedEventArgs>? ScanStarted;

    public event EventHandler<LibraryScanProgressEventArgs>? ScanProgressChanged;

    public event EventHandler<LibraryScanWarningEventArgs>? ScanWarning;

    public event EventHandler<LibraryScanCompletedEventArgs>? ScanCompleted;

    public Task<LibraryScanResult> ScanAsync() => ScanAsync(CancellationToken.None);

    public Task<LibraryScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<LibraryScanResult> completion;
        CancellationTokenSource cancellation;
        lock (scanLock)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (currentScan is not null)
            {
                return currentScan;
            }

            StopMonitoring();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scanCancellation = cancellation;
            completion = new TaskCompletionSource<LibraryScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            currentScan = completion.Task;
        }
        if (OperatingSystem.IsBrowser())
            _ = CompleteScanAsync(completion, cancellation);
        else
            _ = Task.Run(() => CompleteScanAsync(completion, cancellation));
        return completion.Task;
    }

    private async Task CompleteScanAsync(TaskCompletionSource<LibraryScanResult> completion, CancellationTokenSource cancellation)
    {
        try
        {
            completion.TrySetResult(await ScanCoreAsync(cancellation.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ScanCompleted?.Invoke(this, new LibraryScanCompletedEventArgs(0, 0));
            completion.TrySetCanceled(cancellation.Token);
        }
        catch (Exception exception)
        {
            AppLog.Error("Library.Scan", "The library scan failed.", exception);
            ScanWarning?.Invoke(this, new LibraryScanWarningEventArgs(
                new LibraryScanWarning("Library", exception.GetBaseException().Message)));
            ScanCompleted?.Invoke(this, new LibraryScanCompletedEventArgs(0, 1)
            {
                HasChanges = false,
                Failed = true,
            });
            completion.TrySetException(exception);
        }
        finally
        {
            lock (scanLock)
            {
                currentScan = null;
                scanCancellation = null;
                StartMonitoring();
            }
            cancellation.Dispose();
        }
    }

    private void OnLibraryScanSettingsChanged(object? sender, EventArgs eventArgs)
    {
        lock (scanLock)
        {
            if (isDisposed)
            {
                return;
            }

            StopMonitoring();
            StartMonitoring();
        }
    }

    private void StartMonitoring()
    {
        if (isDisposed || scanTimer is null || currentScan is not null || settingsStore?.AutoScanLibrary != true)
        {
            return;
        }

        var folders = libraryLocationService.GetLibraryFolderPaths()
            .Concat(libraryLocationService.GetVideoFolderPaths())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(GetPathComparer())
            .ToArray();
        if (folders.Length == 0)
        {
            return;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            ScheduleScan(TimeSpan.FromMinutes(settingsStore.AutoScanIntervalMinutes));
            return;
        }

        foreach (var folder in folders)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Created += OnFolderChanged;
                watcher.Changed += OnFolderChanged;
                watcher.Deleted += OnFolderChanged;
                watcher.Renamed += OnFolderChanged;
                watcher.Error += OnWatcherError;
                watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception exception)
            {
                if (watcher is not null)
                {
                    watchers.Remove(watcher);
                    watcher.Dispose();
                }

                AppLog.Warning("Library.AutoScan", $"Could not monitor {folder}.", exception);
                ScheduleScan(TimeSpan.FromMinutes(settingsStore.AutoScanIntervalMinutes));
            }
        }
    }

    private void StopMonitoring()
    {
        isScheduled = false;
        scanTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        watchers.Clear();
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs eventArgs)
    {
        lock (scanLock)
        {
            if (!isDisposed && sender is FileSystemWatcher watcher && watchers.Contains(watcher))
            {
                ScheduleScan(TimeSpan.FromSeconds(10));
            }
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs eventArgs)
    {
        lock (scanLock)
        {
            if (!isDisposed && sender is FileSystemWatcher watcher && watchers.Contains(watcher))
            {
                AppLog.Warning("Library.AutoScan", $"Monitoring failed for {watcher.Path}.", eventArgs.GetException());
                ScheduleScan(TimeSpan.FromSeconds(10));
            }
        }
    }

    private void ScheduleScan(TimeSpan delay)
    {
        scheduledAt = Stopwatch.GetTimestamp();
        scheduledDelay = delay;
        isScheduled = true;
        scanTimer?.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private async void OnScanTimer(object? state)
    {
        try
        {
            Task<LibraryScanResult> scan;
            lock (scanLock)
            {
                if (isDisposed || !isScheduled || currentScan is not null)
                {
                    return;
                }

                var remaining = scheduledDelay - Stopwatch.GetElapsedTime(scheduledAt);
                if (remaining > TimeSpan.Zero)
                {
                    scanTimer?.Change(remaining, Timeout.InfiniteTimeSpan);
                    return;
                }

                scan = ScanAsync();
            }

            await scan.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Error("Library.AutoScan", "Automatic library scan failed.", exception);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (scanLock)
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            if (settingsStore is not null)
            {
                settingsStore.LibraryScanSettingsChanged -= OnLibraryScanSettingsChanged;
            }

            StopMonitoring();
            scanTimer?.Dispose();
            cancellation = scanCancellation;
        }
        if (cancellation is not null) _ = CancelScanAsync(cancellation);
    }

    private static async Task CancelScanAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { AppLog.Error("Library.Scan", "Could not cancel the library scan.", exception); }
    }

    private async Task<LibraryScanResult> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var configuredFolders = libraryLocationService.GetLibraryFolderPaths()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(GetPathComparer())
            .ToArray();
        var folderChecks = await Task.WhenAll(configuredFolders.Select(path => CheckFolderAsync(path, cancellationToken)));
        var folders = folderChecks
            .Where(result => result.Accessible)
            .Select(result => result.Path)
            .ToArray();
        var warnings = folderChecks
            .Where(result => !result.Accessible && !result.AccessDenied)
            .Select(result => new LibraryScanWarning(result.Path, "Folder does not exist or is not accessible."))
            .ToList();

        var configuredVideoFolders = libraryLocationService.GetVideoFolderPaths()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(GetPathComparer())
            .ToArray();
        var videoFolderChecks = await Task.WhenAll(configuredVideoFolders.Select(path => CheckFolderAsync(path, cancellationToken)));
        var videoFolders = videoFolderChecks
            .Where(result => result.Accessible)
            .Select(result => result.Path)
            .ToArray();
        warnings.AddRange(videoFolderChecks
            .Where(result => !result.Accessible && !result.AccessDenied)
            .Select(result => new LibraryScanWarning(result.Path, "Folder does not exist or is not accessible.")));

        ScanStarted?.Invoke(this, new LibraryScanStartedEventArgs([.. folders, .. videoFolders]));
        foreach (var warning in warnings)
        {
            ScanWarning?.Invoke(this, new LibraryScanWarningEventArgs(warning));
        }

        // No configured music folders is an empty library state that must be indexed so stale
        // tracks are removed. Inaccessible configured folders are skipped to avoid removing them.
        var shouldScanMusic = settingsStore?.NativeLibrary.IsFileSystemEnabled(LibraryMediaCategory.Music) != false &&
            (configuredFolders.Length == 0 || folders.Length > 0);
        var shouldScanMedia = settingsStore?.NativeLibrary.IsFileSystemEnabled(LibraryMediaCategory.Video) != false &&
            (configuredVideoFolders.Length == 0 || videoFolders.Length > 0);
        if (!shouldScanMusic && !shouldScanMedia)
        {
            var emptyResult = new LibraryScanResult(0, warnings) { HasChanges = false };
            ScanCompleted?.Invoke(this, new LibraryScanCompletedEventArgs(emptyResult.IndexedCount, emptyResult.Warnings.Count) { HasChanges = false });
            return emptyResult;
        }

        var reportedWarnings = warnings.Count;
        var (indexedCount, musicChanged) = shouldScanMusic
            ? await ScanMusicAsync(folders, warnings, folders.Length == configuredFolders.Length, cancellationToken)
            : (0, false);

        var (videoCount, imageCount, mediaChanged) = shouldScanMedia
            ? await ScanMediaAsync(videoFolders, warnings, videoFolders.Length == configuredVideoFolders.Length, cancellationToken)
            : (0, 0, false);

        foreach (var warning in warnings.Skip(reportedWarnings))
        {
            ScanWarning?.Invoke(this, new LibraryScanWarningEventArgs(warning));
        }

        var scanResult = new LibraryScanResult(indexedCount, warnings)
        {
            VideoIndexedCount = videoCount,
            ImageIndexedCount = imageCount,
            HasChanges = musicChanged || mediaChanged,
        };
        ScanCompleted?.Invoke(this, new LibraryScanCompletedEventArgs(scanResult.IndexedCount, scanResult.Warnings.Count)
        {
            HasChanges = scanResult.HasChanges,
            VideoIndexedCount = scanResult.VideoIndexedCount,
            ImageIndexedCount = scanResult.ImageIndexedCount,
        });
        return scanResult;
    }

    private async Task<(int IndexedCount, bool HasChanges)> ScanMusicAsync(
        string[] folders, List<LibraryScanWarning> warnings, bool removeMissingFiles, CancellationToken cancellationToken)
    {
        await using var scope = await ApplicationServiceBase.App.GetScopeAsync().ConfigureAwait(false);
        var indexer = scope.ServiceProvider.GetRequiredService<MusicIndexer>();
        void OnIndexItemAdded(object? _, int count) => ScanProgressChanged?.Invoke(this, new LibraryScanProgressEventArgs(count));

        indexer.IndexItemAdded += OnIndexItemAdded;
        try
        {
            var result = await indexer.ScanAsync(folders, cancellationToken, removeMissingFiles);
            if (result.Item1 < 0)
            {
                warnings.Add(new LibraryScanWarning("Library database", "The scan finished, but the database update did not report a successful count."));
            }

            warnings.AddRange(result.Item2.Select(CreateWarning));
            return (result.Item1, indexer.HasChanges);
        }
        finally
        {
            indexer.IndexItemAdded -= OnIndexItemAdded;
        }
    }

    private async Task<(int VideoCount, int ImageCount, bool HasChanges)> ScanMediaAsync(
        string[] folders, List<LibraryScanWarning> warnings, bool removeMissingFiles, CancellationToken cancellationToken)
    {
        await using var scope = await ApplicationServiceBase.App.GetScopeAsync().ConfigureAwait(false);
        var indexer = scope.ServiceProvider.GetRequiredService<MediaIndexer>();
        void OnIndexItemAdded(object? _, int count) => ScanProgressChanged?.Invoke(this, new LibraryScanProgressEventArgs(count));

        indexer.IndexItemAdded += OnIndexItemAdded;
        try
        {
            var result = await indexer.ScanAsync(folders, cancellationToken, removeMissingFiles);
            warnings.AddRange(result.Item2.Select(CreateWarning));
            return (result.Item1, indexer.ImageIndexedCount, indexer.HasChanges);
        }
        finally
        {
            indexer.IndexItemAdded -= OnIndexItemAdded;
        }
    }

    private static LibraryScanWarning CreateWarning(Tuple<string, Exception> warning)
    {
        AppLog.Warning("Library.Scan", $"Could not scan {warning.Item1}.", warning.Item2);
        return new LibraryScanWarning(warning.Item1, warning.Item2.GetBaseException().Message);
    }

    private static async Task<(string Path, bool Accessible, bool AccessDenied)> CheckFolderAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            path = await IMultiMediaPlatformIO.Instance.NormalizePathAsync(path, cancellationToken).ConfigureAwait(false);
            return (path, await IMultiMediaPlatformIO.Instance.FolderExistsAsync(path, cancellationToken).ConfigureAwait(false), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            AppLog.Warning("Library.Scan", $"Could not check access to {path}.", exception);
            return (path, false, FileAccessErrors.IsAccessDenied(exception));
        }
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}