using System;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.Services;

public sealed record SystemLibrarySnapshot<T>(string Id, T[] Items);

public sealed record SystemLibraryTrack(DbMediaFile File, string Folder, string Root);

/// <summary>Reads a platform media catalog as immutable snapshots for <see cref="SystemLibraryQueryService"/>.</summary>
public interface ISystemLibraryCatalog
{
    Task<SystemLibrarySnapshot<SystemLibraryTrack>> GetMusicAsync(CancellationToken cancellationToken = default);

    Task<SystemLibrarySnapshot<IndexedMediaFile>> GetMediaAsync(CancellationToken cancellationToken = default);

    Task<byte[]?> GetThumbnailAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Loads a catalog snapshot once per invalidation, discarding loads that an invalidation overtook.</summary>
public sealed class SystemLibraryCatalogCache<T>(Func<CancellationToken, T[]> load)
{
    private readonly object gate = new();
    private readonly SemaphoreSlim loading = new(1, 1);
    private SystemLibrarySnapshot<T>? snapshot;
    private long generation;

    public async Task<SystemLibrarySnapshot<T>> GetAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (snapshot is not null)
                    return snapshot;
            }

            await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                long version;
                lock (gate)
                {
                    if (snapshot is not null)
                        return snapshot;
                    version = generation;
                }

                var items = await Task.Run(() => load(cancellationToken), cancellationToken).ConfigureAwait(false);
                lock (gate)
                {
                    if (version == generation)
                        return snapshot = new SystemLibrarySnapshot<T>(Guid.NewGuid().ToString("N"), items);
                }
            }
            finally
            {
                loading.Release();
            }
        }
    }

    public void Invalidate()
    {
        lock (gate)
        {
            generation++;
            snapshot = null;
        }
    }
}

/// <summary>The reserved system source backed by a platform media catalog.</summary>
public sealed class SystemLibrarySource(
    IAppSettingsStore settings,
    LibrarySourceServices services,
    LibrarySourceCapabilities capabilities,
    Func<bool> isDisposed) : ILibrarySource
{
    public LibrarySourceId Id => LibrarySourceId.System;
    public string DisplayName => Strings.Resources.LibrarySource_SystemLibrary;
    public bool LoadMusic => settings.NativeLibrary.UseSystemMusicLibrary;
    public bool LoadVideo => settings.NativeLibrary.UseSystemMediaLibrary;
    public LibrarySourceStatus Status => LibrarySourceStatus.Online;
    public string? LastError => null;
    public LibrarySourceCapabilities Capabilities => capabilities;
    public LibrarySourceServices Services => services;
    public event EventHandler? StatusChanged
    {
        add { }
        remove { }
    }

    public Task<LibrarySourceServices> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(isDisposed(), this);
        return Task.FromResult(services);
    }

    public void Dispose() { }
}

/// <summary>Maps a library scan request to a catalog refresh.</summary>
public sealed class SystemLibraryRefreshService(Action refresh) : ILibraryScanService
{
    public event EventHandler<LibraryScanStartedEventArgs>? ScanStarted { add { } remove { } }
    public event EventHandler<LibraryScanProgressEventArgs>? ScanProgressChanged { add { } remove { } }
    public event EventHandler<LibraryScanWarningEventArgs>? ScanWarning { add { } remove { } }
    public event EventHandler<LibraryScanCompletedEventArgs>? ScanCompleted { add { } remove { } }

    public Task<LibraryScanResult> ScanAsync()
    {
        refresh();
        return Task.FromResult(new LibraryScanResult(0, []));
    }
}
