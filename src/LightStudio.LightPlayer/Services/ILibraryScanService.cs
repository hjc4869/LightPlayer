using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

public interface ILibraryScanService
{
    event EventHandler<LibraryScanStartedEventArgs>? ScanStarted;
    event EventHandler<LibraryScanProgressEventArgs>? ScanProgressChanged;
    event EventHandler<LibraryScanWarningEventArgs>? ScanWarning;
    event EventHandler<LibraryScanCompletedEventArgs>? ScanCompleted;
    Task<LibraryScanResult> ScanAsync();

    async Task<LibraryScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await ScanAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}

public sealed record LibraryScanStartedEventArgs(IReadOnlyList<string> Folders);
public sealed record LibraryScanProgressEventArgs(int IndexedCount);
public sealed record LibraryScanWarning(string Path, string Message);
public sealed record LibraryScanWarningEventArgs(LibraryScanWarning Warning);
public sealed record LibraryScanCompletedEventArgs(int IndexedCount, int WarningCount)
{
    public bool HasChanges { get; init; } = true;

    public bool Failed { get; init; }

    public int VideoIndexedCount { get; init; }

    public int ImageIndexedCount { get; init; }
}
public sealed record LibraryScanResult(int IndexedCount, IReadOnlyList<LibraryScanWarning> Warnings)
{
    public bool HasChanges { get; init; } = true;

    /// <summary>Number of videos in the library after the scan, counted separately from music.</summary>
    public int VideoIndexedCount { get; init; }

    public int ImageIndexedCount { get; init; }
}