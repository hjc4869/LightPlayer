using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Remote;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Services;

public enum LibrarySourceStatus
{
    Disabled,
    Connecting,
    Online,
    Offline,
    SignInRequired,
    Incompatible,
}

public enum LibraryMediaCategory
{
    Music,
    Video,
}

public sealed record LibrarySourceCapabilities(
    bool SupportsFileReveal,
    RemoteTranscodeCapabilities? Transcoding = null);

/// <param name="VideoFolders">
/// Lists this source's video folders from wherever they live, or null when it cannot be browsed and
/// the index is all there is to go on.
/// </param>
public sealed record LibrarySourceServices(
    ILibraryQueryService LibraryQuery,
    IVideoLibraryQueryService VideoLibraryQuery,
    IMultiMediaPlatformIO MultimediaPlatform,
    ISubtitleFileBrowser? SubtitleFiles,
    ILibraryScanService LibraryScan,
    IVideoFolderBrowser? VideoFolders = null,
    IStorageCache? MediaBlockCache = null,
    IStorageCache? ImageCache = null,
    IPhotoSearchService? PhotoSearch = null,
    IMusicFolderBrowser? MusicFolders = null);

public interface ILibrarySource : IDisposable
{
    LibrarySourceId Id { get; }

    string DisplayName { get; }

    bool LoadMusic { get; }

    bool LoadVideo { get; }

    LibrarySourceStatus Status { get; }

    string? LastError { get; }

    LibrarySourceCapabilities Capabilities { get; }

    LibrarySourceServices? Services { get; }

    Task<LibrarySourceServices> EnsureConnectedAsync(CancellationToken cancellationToken = default);

    event EventHandler? StatusChanged;
}

public sealed record LibrarySourceSnapshotItem(ILibrarySource Source, int Priority);

public sealed record LibrarySourceSnapshot(
    IReadOnlyList<LibrarySourceSnapshotItem> Sources,
    long DefinitionsGeneration,
    long PriorityGeneration,
    long CatalogGeneration);

public sealed class LibrarySourceChangedEventArgs(LibrarySourceId? sourceId = null) : EventArgs
{
    public LibrarySourceId? SourceId { get; } = sourceId;
}

public interface ILibrarySourceRegistry
{
    IReadOnlyList<ILibrarySource> Sources { get; }

    long DefinitionsGeneration { get; }

    long PriorityGeneration { get; }

    long CatalogGeneration { get; }

    LibrarySourceSnapshot CreateSnapshot(LibraryMediaCategory category);

    bool TryGetSource(LibrarySourceId sourceId, out ILibrarySource source);

    event EventHandler<LibrarySourceChangedEventArgs>? DefinitionsChanged;

    event EventHandler<LibrarySourceChangedEventArgs>? PriorityChanged;

    event EventHandler<LibrarySourceChangedEventArgs>? StatusChanged;

    event EventHandler<LibrarySourceChangedEventArgs>? CatalogChanged;
}

public sealed class LibrarySourceRegistry : ILibrarySourceRegistry, IDisposable
{
    private readonly object gate = new();
    private readonly ILibrarySource? localSource;
    private readonly ILibrarySource? nativeSource;
    private readonly INativeMediaLibraryProvider? nativeProvider;
    private readonly IAppSettingsStore? settingsStore;
    private List<ILibrarySource> remoteSources = [];
    private long definitionsGeneration;
    private long priorityGeneration;
    private long catalogGeneration;
    private bool disposed;

    public LibrarySourceRegistry(
        ILibrarySource? localSource = null,
        IAppSettingsStore? settingsStore = null,
        IPhotoSearchService? photoSearch = null)
    {
        if (localSource is not null && localSource.Id != LibrarySourceId.Local)
        {
            throw new ArgumentException("The pinned local source must use the reserved local id.", nameof(localSource));
        }

        this.localSource = localSource;
        this.settingsStore = settingsStore;
        nativeProvider = localSource is not null && settingsStore is not null
            ? PlatformServices.NativeMediaLibrary
            : null;
        if (nativeProvider is not null)
        {
            nativeSource = nativeProvider.CreateSource(settingsStore!, photoSearch is IPhotoIndexService photoIndex
                ? photoIndex.GetSearchService(LibrarySourceId.System) : photoSearch);
            if (nativeSource.Id != LibrarySourceId.System)
            {
                throw new ArgumentException("The native library must use the reserved system source id.", nameof(nativeProvider));
            }

            nativeProvider.CatalogChanged += OnNativeCatalogChanged;
            settingsStore!.NativeLibraryChanged += OnNativeLibraryChanged;
            Subscribe(nativeSource);
        }

        Subscribe(localSource);
    }

    public IReadOnlyList<ILibrarySource> Sources
    {
        get
        {
            lock (gate)
            {
                return AllSourcesLocked().ToArray();
            }
        }
    }

    public long DefinitionsGeneration
    {
        get { lock (gate) { return definitionsGeneration; } }
    }

    public long PriorityGeneration
    {
        get { lock (gate) { return priorityGeneration; } }
    }

    public long CatalogGeneration
    {
        get { lock (gate) { return catalogGeneration; } }
    }

    public event EventHandler<LibrarySourceChangedEventArgs>? DefinitionsChanged;

    public event EventHandler<LibrarySourceChangedEventArgs>? PriorityChanged;

    public event EventHandler<LibrarySourceChangedEventArgs>? StatusChanged;

    public event EventHandler<LibrarySourceChangedEventArgs>? CatalogChanged;

    public LibrarySourceSnapshot CreateSnapshot(LibraryMediaCategory category)
    {
        lock (gate)
        {
            var sources = AllSourcesLocked()
                .Select((source, priority) => new LibrarySourceSnapshotItem(source, priority))
                .Where(item => category == LibraryMediaCategory.Music
                    ? item.Source.LoadMusic
                    : item.Source.LoadVideo)
                .Where(item => item.Source.Status is not
                    (LibrarySourceStatus.SignInRequired or LibrarySourceStatus.Incompatible))
                .ToArray();
            return new LibrarySourceSnapshot(
                sources,
                definitionsGeneration,
                priorityGeneration,
                catalogGeneration);
        }
    }

    public bool TryGetSource(LibrarySourceId sourceId, out ILibrarySource source)
    {
        lock (gate)
        {
            source = AllSourcesLocked().FirstOrDefault(candidate => candidate.Id == sourceId)!;
            return source is not null;
        }
    }

    public void ReplaceRemoteSources(IReadOnlyList<ILibrarySource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Any(source => source.Id.IsLocal) ||
            sources.Select(source => source.Id).Distinct().Count() != sources.Count)
        {
            throw new ArgumentException("Remote source ids must be unique and cannot use reserved device ids.", nameof(sources));
        }

        ILibrarySource[] removed;
        lock (gate)
        {
            ThrowIfDisposed();
            removed = remoteSources.Where(old => !sources.Contains(old)).ToArray();
            foreach (var source in removed)
            {
                Unsubscribe(source);
            }

            foreach (var source in sources.Where(source => !remoteSources.Contains(source)))
            {
                Subscribe(source);
            }

            remoteSources = [.. sources];
            definitionsGeneration++;
            priorityGeneration++;
        }

        DefinitionsChanged?.Invoke(this, new LibrarySourceChangedEventArgs());
        PriorityChanged?.Invoke(this, new LibrarySourceChangedEventArgs());

        foreach (var source in removed)
        {
            source.Dispose();
        }
    }

    public bool MoveRemoteSource(LibrarySourceId sourceId, int newIndex)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var oldIndex = remoteSources.FindIndex(source => source.Id == sourceId);
            if (oldIndex < 0)
            {
                return false;
            }

            newIndex = Math.Clamp(newIndex, 0, remoteSources.Count - 1);
            if (oldIndex == newIndex)
            {
                return false;
            }

            var source = remoteSources[oldIndex];
            remoteSources.RemoveAt(oldIndex);
            remoteSources.Insert(newIndex, source);
            priorityGeneration++;
        }

        PriorityChanged?.Invoke(this, new LibrarySourceChangedEventArgs(sourceId));
        return true;
    }

    public void NotifyDefinitionChanged(LibrarySourceId sourceId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            definitionsGeneration++;
        }

        DefinitionsChanged?.Invoke(this, new LibrarySourceChangedEventArgs(sourceId));
    }

    public void NotifyCatalogChanged(LibrarySourceId? sourceId = null)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            catalogGeneration++;
        }

        CatalogChanged?.Invoke(this, new LibrarySourceChangedEventArgs(sourceId));
    }

    private IEnumerable<ILibrarySource> AllSourcesLocked()
    {
        if (nativeSource is not null)
        {
            yield return nativeSource;
        }

        if (localSource is not null)
        {
            yield return localSource;
        }

        foreach (var source in remoteSources)
        {
            yield return source;
        }
    }

    private void Subscribe(ILibrarySource? source)
    {
        if (source is not null)
        {
            source.StatusChanged += OnSourceStatusChanged;
        }
    }

    private void Unsubscribe(ILibrarySource source) => source.StatusChanged -= OnSourceStatusChanged;

    private void OnNativeCatalogChanged(object? sender, EventArgs eventArgs) =>
        NotifyCatalogChanged();

    private void OnNativeLibraryChanged(object? sender, EventArgs eventArgs)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            definitionsGeneration++;
        }

        DefinitionsChanged?.Invoke(this, new LibrarySourceChangedEventArgs());
        NotifyCatalogChanged();
    }

    private void OnSourceStatusChanged(object? sender, EventArgs eventArgs)
    {
        if (sender is ILibrarySource source)
        {
            StatusChanged?.Invoke(this, new LibrarySourceChangedEventArgs(source.Id));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        ILibrarySource[] sources;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            sources = AllSourcesLocked().ToArray();
            remoteSources = [];
            if (nativeProvider is not null)
            {
                nativeProvider.CatalogChanged -= OnNativeCatalogChanged;
                settingsStore!.NativeLibraryChanged -= OnNativeLibraryChanged;
            }
        }

        foreach (var source in sources)
        {
            Unsubscribe(source);
            source.Dispose();
        }
    }
}

public sealed class LocalLibrarySource : ILibrarySource
{
    private readonly LibrarySourceServices services;
    private readonly string? displayName;
    private readonly IAppSettingsStore settingsStore;

    public LocalLibrarySource(ILocalServices localServices, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(localServices);
        this.displayName = displayName;
        settingsStore = localServices.Settings;
        services = new LibrarySourceServices(
            localServices.LibraryQuery,
            localServices.VideoLibraryQuery,
            localServices.MultimediaPlatform,
            localServices.SubtitleFiles,
            localServices.LibraryScan,
            new PlatformVideoFolderBrowser(
                localServices.MultimediaPlatform,
                localServices.LibraryLocation,
                PlatformServices.LibraryFolderAccess),
            PhotoSearch: localServices.PhotoIndex?.GetSearchService(LibrarySourceId.Local),
            MusicFolders: new IndexedMusicFolderBrowser(
                localServices.LibraryQuery,
                _ => Task.FromResult(localServices.LibraryLocation.GetLibraryFolderPaths()),
                localServices.MultimediaPlatform,
                PlatformServices.LibraryFolderAccess));
    }

    public LibrarySourceId Id => LibrarySourceId.Local;

    public string DisplayName => displayName ?? Strings.Resources.LibrarySource_ThisDevice;

    public bool LoadMusic => settingsStore.NativeLibrary.IsFileSystemEnabled(LibraryMediaCategory.Music);

    public bool LoadVideo => settingsStore.NativeLibrary.IsFileSystemEnabled(LibraryMediaCategory.Video);

    public LibrarySourceStatus Status => LibrarySourceStatus.Online;

    public string? LastError => null;

    public LibrarySourceCapabilities Capabilities { get; } = new(SupportsFileReveal: true);

    public LibrarySourceServices Services => services;

    public event EventHandler? StatusChanged
    {
        add { }
        remove { }
    }

    public Task<LibrarySourceServices> EnsureConnectedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(services);

    public void Dispose()
    {
    }
}