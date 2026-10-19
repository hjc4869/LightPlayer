using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Constant;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.RemoteServices;

public sealed class RemoteLibraryManager : IRemoteLibraryManager
{
    private const string DefinitionsKey = "librarySources.v3";
    private const string LegacyDefinitionsKey = "librarySources.v2";
    private const string VisibilityKey = "libraryVisibility.v2";
    private const string LegacyServersKey = "servers";
    private const string NextcloudCatalogKey = "nextcloudMusic.catalog.v1";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly object gate = new();
    private readonly IKeyValueStore deviceState;
    private readonly Func<RemoteLibraryDefinition, IMediaBlockStore> blockStoreFactory;
    private readonly Func<RemoteLibraryDefinition, RemoteImageCache?> imageCacheFactory;
    private readonly Func<RemoteLibraryDefinition, string> indexPathFactory;
    private readonly Func<RemoteLibraryDefinition, string?, RemoteApiClient> createClient;
    private readonly bool requireForwardingConfirmation;
    private readonly Action<RemoteLibraryDefinition>? deleteStorage;
    private readonly Func<string, Task>? deleteIndexAsync;
    private readonly Func<RemoteLibraryDefinition, string>? nextcloudCatalogPathFactory;
    private readonly Func<Func<string, CancellationToken, Task<Stream>>, IMultiMediaPlatformIO>? mediaProbeFactory;
    private readonly LibrarySourceRegistry registry;
    private readonly ILibraryScanService? localScanService;
    private readonly IAppSettingsStore? appSettings;
    private readonly List<RemoteLibraryDefinition> definitions;
    private readonly Dictionary<LibrarySourceId, RemoteLibrarySource> sources = [];
    private readonly Dictionary<LibrarySourceId, string> sessionPasswords = [];
    private readonly Dictionary<LibrarySourceId, RemoteRefreshProgress> remoteRefreshProgress = [];
    private LibraryVisibilitySettings visibilitySettings;
    private RemoteLibraryRefreshStatus refreshStatus = new(0, false, false, 0, 0, false);
    private bool disposed;

    public RemoteLibraryManager(
        string dataDirectory,
        string cacheDirectory,
        ILocalServices? localServices = null)
    {
        deviceState = new FileKeyValueStore(Path.Combine(dataDirectory, "connection.json"), ownerOnly: true);
        blockStoreFactory = definition => new FileMediaBlockStore(
            Path.Combine(SourceDirectory(cacheDirectory, definition), "MediaBlocks"),
            SourceSettings(definition),
            RemoteMediaBlockCache.MaxPersistentBytes);
        imageCacheFactory = definition => new RemoteImageCache(
            Path.Combine(SourceDirectory(cacheDirectory, definition), "AlbumArt"),
            SourceSettings(definition));
        indexPathFactory = definition => Path.Combine(
            SourceDirectory(dataDirectory, definition),
            "Index",
            DatabaseConstants.DbFileName);
        createClient = CreateDirectClient;
        deleteStorage = definition =>
        {
            DeleteDirectory(SourceDirectory(dataDirectory, definition));
            DeleteDirectory(SourceDirectory(cacheDirectory, definition));
        };
        nextcloudCatalogPathFactory = definition => Path.Combine(
            SourceDirectory(dataDirectory, definition),
            "Catalog",
            "nextcloud-music.json");
        registry = new LibrarySourceRegistry(
            localServices is null ? null : new LocalLibrarySource(localServices),
            localServices?.Settings,
            localServices?.PhotoIndex);
        appSettings = localServices?.Settings;
        if (appSettings is not null)
        {
            appSettings.AlbumGroupingChanged += OnAlbumGroupingChanged;
        }
        localScanService = localServices?.LibraryScan;
        if (localScanService is not null)
        {
            localScanService.ScanCompleted += OnLocalScanCompleted;
        }
        definitions = LoadDefinitions();
        visibilitySettings = LoadVisibilitySettings();
        BuildSources();
    }

    public RemoteLibraryManager(
        IKeyValueStore deviceState,
        Func<RemoteLibraryDefinition, IMediaBlockStore> blockStoreFactory,
        Func<RemoteLibraryDefinition, RemoteImageCache?> imageCacheFactory,
        ILibrarySource? localSource = null,
        Action<RemoteLibraryDefinition>? deleteStorage = null,
        Func<RemoteLibraryDefinition, string?, RemoteApiClient>? clientFactory = null,
        bool requireForwardingConfirmation = false,
        Func<RemoteLibraryDefinition, string>? indexPathFactory = null,
        IAppSettingsStore? appSettings = null,
        Func<string, Task>? deleteIndexAsync = null,
        Func<Func<string, CancellationToken, Task<Stream>>, IMultiMediaPlatformIO>? mediaProbeFactory = null)
    {
        this.deviceState = deviceState ?? throw new ArgumentNullException(nameof(deviceState));
        this.blockStoreFactory = blockStoreFactory ?? throw new ArgumentNullException(nameof(blockStoreFactory));
        this.imageCacheFactory = imageCacheFactory ?? throw new ArgumentNullException(nameof(imageCacheFactory));
        this.indexPathFactory = indexPathFactory ?? (definition => Path.Combine(
            SourceDirectory(PlatformServices.AppDataPath, definition),
            "Index",
            DatabaseConstants.DbFileName));
        this.deleteStorage = deleteStorage;
        this.deleteIndexAsync = deleteIndexAsync;
        this.mediaProbeFactory = mediaProbeFactory;
        createClient = clientFactory ?? CreateDirectClient;
        this.requireForwardingConfirmation = requireForwardingConfirmation;
        registry = new LibrarySourceRegistry(localSource, appSettings);
        this.appSettings = appSettings;
        if (appSettings is not null)
        {
            appSettings.AlbumGroupingChanged += OnAlbumGroupingChanged;
        }
        definitions = LoadDefinitions();
        visibilitySettings = LoadVisibilitySettings();
        BuildSources();
    }

    public event EventHandler<RemoteLibraryRefreshStatus>? RefreshStatusChanged;

    public IReadOnlyList<RemoteLibraryDefinition> Definitions
    {
        get { lock (gate) { return definitions.ToArray(); } }
    }

    public ILibrarySourceRegistry Registry => registry;

    public RemoteLibraryRefreshStatus RefreshStatus
    {
        get { lock (gate) { return refreshStatus; } }
    }

    public LibraryVisibilitySettings VisibilitySettings
    {
        get { lock (gate) { return visibilitySettings; } }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (gate)
            {
                ThrowIfDisposed();
                if (visibilitySettings == value)
                {
                    return;
                }

                visibilitySettings = value;
                deviceState.Set(
                    VisibilityKey,
                    JsonSerializer.Serialize(value, RemoteStateJsonContext.Default.LibraryVisibilitySettings));
            }

            registry.NotifyDefinitionChanged(LibrarySourceId.Local);
        }
    }

    public async Task ConnectEnabledAsync(CancellationToken cancellationToken = default)
    {
        RemoteLibrarySource[] enabled;
        lock (gate)
        {
            ThrowIfDisposed();
            enabled = definitions
                .Where(definition => definition.ParticipatesInBrowse)
                .Select(definition => sources[definition.Id])
                .ToArray();
        }

        using var concurrency = new SemaphoreSlim(3);
        await Task.WhenAll(enabled.Select(async source =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await source.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }
            finally
            {
                concurrency.Release();
            }
        })).ConfigureAwait(false);
    }

    public async Task<RemoteLibraryDefinition> SaveAsync(
        RemoteLibraryDefinition definition,
        bool allowOffline = false,
        string? authenticationPassword = null,
        bool? rememberPassword = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        definition = NormalizeDefinition(definition);

        RemoteLibraryDefinition? existing;
        lock (gate)
        {
            existing = definitions.FirstOrDefault(item => item.Id == definition.Id);
        }

        var endpointChanged = existing is null ||
            existing.Provider != definition.Provider ||
            !string.Equals(existing.Address, definition.Address, StringComparison.Ordinal) ||
            !string.Equals(existing.ApiProxy, definition.ApiProxy, StringComparison.Ordinal);
        var hasReplacementToken = existing is not null
            && definition.Provider == RemoteLibraryProvider.NextcloudMusic
            && definition.Token.Length > 0
            && !string.Equals(existing.Token, definition.Token, StringComparison.Ordinal);
        if (endpointChanged && existing is not null && authenticationPassword is null && !hasReplacementToken)
        {
            definition = definition with
            {
                Token = string.Empty,
                Username = string.Empty,
                Password = string.Empty,
            };
        }

        string? sessionPassword;
        lock (gate)
        {
            sessionPasswords.TryGetValue(definition.Id, out sessionPassword);
        }

        var canReuseSessionPassword = definition.Provider == RemoteLibraryProvider.WebDav
            && existing?.Provider == RemoteLibraryProvider.WebDav
            && !endpointChanged
            && string.Equals(existing.Username, definition.Username, StringComparison.Ordinal);
        var passwordForProbe = authenticationPassword
            ?? (definition.Password.Length > 0
                ? definition.Password
                : canReuseSessionPassword ? sessionPassword ?? string.Empty : string.Empty);
        var probingDefinition = definition with { Password = passwordForProbe };
        var sourceRequiresSignIn = existing is not null &&
            sources.GetValueOrDefault(existing.Id)?.Status == LibrarySourceStatus.SignInRequired;
        var webDavUsernameChanged = definition.Provider == RemoteLibraryProvider.WebDav
            && existing?.Provider == RemoteLibraryProvider.WebDav
            && !string.Equals(existing.Username, definition.Username, StringComparison.Ordinal);
        var nextcloudTokenChanged = existing is not null
            && definition.Provider == RemoteLibraryProvider.NextcloudMusic
            && !string.Equals(existing.Token, definition.Token, StringComparison.Ordinal);
        var shouldProbe = endpointChanged
            || authenticationPassword is not null
            || sourceRequiresSignIn
            || webDavUsernameChanged
            || nextcloudTokenChanged;
        if (shouldProbe)
        {
            try
            {
                probingDefinition = await AuthenticateAsync(probingDefinition, cancellationToken).ConfigureAwait(false);
                definition = probingDefinition with
                {
                    Password = rememberPassword switch
                    {
                        true => passwordForProbe,
                        false => string.Empty,
                        _ => definition.Password,
                    },
                };
            }
            catch (Exception exception) when (allowOffline && exception is not OperationCanceledException)
            {
                definition = definition with { Token = string.Empty };
            }
        }

        if (existing?.Provider == RemoteLibraryProvider.WebDav
            && definition.Provider != RemoteLibraryProvider.WebDav)
        {
            lock (gate)
            {
                sessionPasswords.Remove(definition.Id);
            }
        }
        else if (definition.Provider == RemoteLibraryProvider.WebDav
            && (authenticationPassword is not null || !canReuseSessionPassword))
        {
            lock (gate)
            {
                if (authenticationPassword is not null
                    && rememberPassword == false
                    && passwordForProbe.Length > 0)
                {
                    sessionPasswords[definition.Id] = passwordForProbe;
                }
                else
                {
                    sessionPasswords.Remove(definition.Id);
                }
            }
        }

        InvalidateNextcloudCatalog(existing, definition);
        var source = CommitDefinition(definition);
        if (definition.Provider == RemoteLibraryProvider.WebDav && authenticationPassword is not null)
        {
            source.ResetConnection();
        }
        _ = ObserveConnectionAsync(source);
        return definition;
    }

    /// <summary>
    /// Saves a Light Player definition after its bearer token was authenticated by the pairing
    /// exchange. The source connects in the background, as it does after a regular save.
    /// </summary>
    public async Task<RemoteLibraryDefinition> SavePairedAsync(
        RemoteLibraryDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        definition = NormalizeDefinition(definition);
        if (definition.Provider != RemoteLibraryProvider.LightPlayer)
        {
            throw new ArgumentException("Only Light Player libraries can be paired.", nameof(definition));
        }

        var serialized = SerializeDefinitionsWith(definition);
        await PersistDefinitionsAsync(serialized, cancellationToken).ConfigureAwait(false);
        var source = CommitDefinition(definition, persist: false);
        _ = ObserveConnectionAsync(source);
        return definition;
    }

    public async Task<RemoteLibraryDefinition> SignInAsync(
        LibrarySourceId sourceId,
        string username,
        string password,
        bool rememberPassword,
        CancellationToken cancellationToken = default)
    {
        RemoteLibraryDefinition definition;
        lock (gate)
        {
            ThrowIfDisposed();
            definition = definitions.FirstOrDefault(item => item.Id == sourceId)
                ?? throw new KeyNotFoundException($"Library source '{sourceId}' is not configured.");
        }

        if (definition.Provider == RemoteLibraryProvider.WebDav)
        {
            var authenticated = await AuthenticateWebDavAsync(
                definition with { Username = username, Password = password },
                cancellationToken).ConfigureAwait(false);
            var updatedWebDav = authenticated with { Password = rememberPassword ? password : string.Empty };
            lock (gate)
            {
                if (rememberPassword || password.Length == 0)
                {
                    sessionPasswords.Remove(sourceId);
                }
                else
                {
                    sessionPasswords[sourceId] = password;
                }
            }

            CommitConnectionDefinition(updatedWebDav);
            sources[sourceId].ResetConnection();
            await sources[sourceId].EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return updatedWebDav;
        }

        if (definition.Provider != RemoteLibraryProvider.LightPlayer)
        {
            throw new NotSupportedException("This remote library uses browser or API-key authentication.");
        }

        var token = await RequestTokenAsync(definition, username, password, cancellationToken).ConfigureAwait(false);
        var updated = definition with
        {
            Token = token,
            Username = username,
            Password = rememberPassword ? password : string.Empty,
        };
        CommitConnectionDefinition(updated);
        await sources[sourceId].EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<RemoteLibraryDefinition> AuthenticateWithBrowserAsync(
        RemoteLibraryDefinition definition,
        Func<Uri, CancellationToken, Task> openBrowser,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(openBrowser);
        definition = NormalizeDefinition(definition);
        if (definition.Provider != RemoteLibraryProvider.NextcloudMusic)
        {
            throw new NotSupportedException("Browser authentication is only available for Nextcloud Music libraries.");
        }

        var authorization = await NextcloudMusicLoginFlow.AuthenticateAsync(
            definition.Address,
            openBrowser,
            cancellationToken).ConfigureAwait(false);
        var authenticated = NormalizeDefinition(definition with
        {
            Address = authorization.ServerAddress,
            Token = authorization.ApiKey,
            Username = authorization.Username,
            Password = string.Empty,
        });
        RemoteLibraryDefinition? existing;
        lock (gate)
        {
            existing = definitions.FirstOrDefault(item => item.Id == authenticated.Id);
        }
        InvalidateNextcloudCatalog(existing, authenticated);
        var source = CommitDefinition(authenticated);
        _ = ObserveConnectionAsync(source);
        return authenticated;
    }

    public async Task RetryAsync(LibrarySourceId sourceId, CancellationToken cancellationToken = default)
    {
        RemoteLibrarySource source;
        lock (gate)
        {
            ThrowIfDisposed();
            source = sources.GetValueOrDefault(sourceId)
                ?? throw new KeyNotFoundException($"Library source '{sourceId}' is not configured.");
        }

        source.ResetConnection();
        await source.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(LibrarySourceId sourceId, CancellationToken cancellationToken = default)
    {
        RemoteLibrarySource source;
        lock (gate)
        {
            ThrowIfDisposed();
            source = sources.GetValueOrDefault(sourceId)
                ?? throw new KeyNotFoundException($"Library source '{sourceId}' is not configured.");
        }

        var services = await source.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await services.LibraryScan.ScanAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            FailRemoteRefresh(sourceId);
            throw;
        }
        registry.NotifyCatalogChanged(sourceId);
    }

    public bool Move(LibrarySourceId sourceId, int newIndex)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var oldIndex = definitions.FindIndex(definition => definition.Id == sourceId);
            if (oldIndex < 0)
            {
                return false;
            }

            newIndex = Math.Clamp(newIndex, 0, definitions.Count - 1);
            if (oldIndex == newIndex)
            {
                return false;
            }

            var definition = definitions[oldIndex];
            definitions.RemoveAt(oldIndex);
            definitions.Insert(newIndex, definition);
            SaveDefinitionsLocked();
        }

        return registry.MoveRemoteSource(sourceId, newIndex);
    }

    public void Remove(LibrarySourceId sourceId, bool deleteCaches)
    {
        RemoteLibraryDefinition? removed;
        lock (gate)
        {
            ThrowIfDisposed();
            var index = definitions.FindIndex(definition => definition.Id == sourceId);
            if (index < 0)
            {
                return;
            }

            removed = definitions[index];
            definitions.RemoveAt(index);
            sources.Remove(sourceId);
            sessionPasswords.Remove(sourceId);
            SaveDefinitionsLocked();
            registry.ReplaceRemoteSources(definitions.Select(item => sources[item.Id]).ToArray());
        }

        RemoveRemoteRefresh(sourceId);
        if (deleteCaches)
        {
            RemoveNextcloudCatalog(removed);
            if (removed.Provider == RemoteLibraryProvider.WebDav && deleteIndexAsync is not null)
                _ = DeleteIndexAsync(indexPathFactory(removed));
            deleteStorage?.Invoke(removed);
        }
    }

    private async Task DeleteIndexAsync(string path)
    {
        try { await deleteIndexAsync!(path).ConfigureAwait(false); }
        catch (Exception exception) { AppLog.Warning("RemoteLibrary", "Could not delete a removed library index.", exception); }
    }

    private void BuildSources()
    {
        foreach (var definition in definitions)
        {
            sources.Add(definition.Id, CreateSource(definition, definition.RefreshAtStartup));
        }

        registry.ReplaceRemoteSources(definitions.Select(definition => sources[definition.Id]).ToArray());
    }

    private RemoteLibrarySource CreateSource(
        RemoteLibraryDefinition definition,
        bool refreshOnConnect) =>
        new(definition, ConnectAsync, refreshOnConnect);

    private async Task<RemoteLibraryConnection> ConnectAsync(
        RemoteLibrarySource source,
        RemoteLibraryDefinition definition,
        long connectionGeneration,
        bool refreshOnConnect,
        CancellationToken cancellationToken)
    {
        if (definition.Provider == RemoteLibraryProvider.WebDav && definition.Password.Length == 0)
        {
            lock (gate)
            {
                if (sessionPasswords.TryGetValue(definition.Id, out var sessionPassword))
                {
                    definition = definition with { Password = sessionPassword };
                }
            }
        }

        var authenticated = await AuthenticateAsync(definition, cancellationToken).ConfigureAwait(false);
        if (authenticated != definition)
        {
            CommitConnectionDefinition(authenticated, source);
            definition = authenticated;
        }

        return definition.Provider switch
        {
            RemoteLibraryProvider.LightPlayer => await ConnectLightPlayerAsync(
                source,
                definition,
                connectionGeneration,
                cancellationToken).ConfigureAwait(false),
            RemoteLibraryProvider.NextcloudMusic => await ConnectNextcloudMusicAsync(
                source,
                definition,
                connectionGeneration,
                refreshOnConnect,
                cancellationToken).ConfigureAwait(false),
            RemoteLibraryProvider.WebDav => await ConnectWebDavAsync(
                source,
                definition,
                connectionGeneration,
                refreshOnConnect,
                cancellationToken).ConfigureAwait(false),
            _ => throw new RemoteLibraryIncompatibleException("The remote library provider is unknown."),
        };
    }

    private async Task<RemoteLibraryConnection> ConnectLightPlayerAsync(
        RemoteLibrarySource source,
        RemoteLibraryDefinition definition,
        long connectionGeneration,
        CancellationToken cancellationToken)
    {
        var client = createClient(definition, definition.Token);
        try
        {
            client.Unauthorized += (_, _) => OnUnauthorized(source, definition, connectionGeneration);
            var imageCache = imageCacheFactory(definition);
            var platform = await RemoteMultimediaPlatformIO.CreateAsync(
                client,
                blockStoreFactory(definition),
                imageCache,
                cancellationToken).ConfigureAwait(false);
            var scan = new RemoteLibraryScanService(client);
            EventHandler<LibraryScanCompletedEventArgs> completed = (_, args) =>
            {
                if (args.HasChanges)
                {
                    registry.NotifyCatalogChanged(source.Id);
                }
            };
            scan.ScanCompleted += completed;
            var stopTrackingRefresh = TrackRemoteRefresh(source.Id, scan);
            var libraryQuery = new RemoteLibraryQueryService(client);
            var services = new LibrarySourceServices(
                libraryQuery,
                new RemoteVideoLibraryQueryService(client, imageCache),
                platform,
                new RemoteSubtitleFileBrowser(client),
                scan,
                platform.SupportsMediaBrowse ? new RemoteVideoFolderBrowser(client) : null,
                platform.MediaBlockCache,
                platform.ImageCache,
                new RemotePhotoSearchService(client),
                new RemoteMusicFolderBrowser(client));
            return new RemoteLibraryConnection(
                services,
                new LibrarySourceCapabilities(false, platform.TranscodeCapabilities),
                () =>
                {
                    stopTrackingRefresh();
                    scan.ScanCompleted -= completed;
                    scan.Dispose();
                    client.Dispose();
                });
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<RemoteLibraryConnection> ConnectNextcloudMusicAsync(
        RemoteLibrarySource source,
        RemoteLibraryDefinition definition,
        long connectionGeneration,
        bool refreshOnConnect,
        CancellationToken cancellationToken)
    {
        var client = new NextcloudMusicClient(definition.Address, definition.Token);
        try
        {
            client.Unauthorized += (_, _) => OnUnauthorized(source, definition, connectionGeneration);
            var catalog = new NextcloudMusicCatalog(
                client,
                LoadNextcloudCatalog(definition),
                state => SaveNextcloudCatalogAsync(definition, state),
                () => appSettings?.AlbumGrouping ?? AlbumGroupingMode.SameFolder);
            var imageCache = imageCacheFactory(definition);
            var platform = new NextcloudMusicPlatformIO(
                client,
                catalog,
                blockStoreFactory(definition),
                imageCache);
            var scan = new NextcloudMusicScanService(catalog);
            var stopTrackingRefresh = TrackRemoteRefresh(source.Id, scan);
            EventHandler<LibraryScanCompletedEventArgs> completed = (_, _) =>
            {
                if (source.IsCurrentGeneration(connectionGeneration))
                {
                    registry.NotifyCatalogChanged(source.Id);
                }
            };
            scan.ScanCompleted += completed;
            var libraryQuery = new NextcloudMusicQueryService(catalog);
            var services = new LibrarySourceServices(
                libraryQuery,
                new NextcloudMusicVideoQueryService(),
                platform,
                SubtitleFiles: null,
                scan,
                VideoFolders: null,
                platform.MediaBlockCache,
                platform.ImageCache,
                MusicFolders: new IndexedMusicFolderBrowser(libraryQuery));
            if (refreshOnConnect)
            {
                try
                {
                    await scan.ScanAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    scan.ScanCompleted -= completed;
                    stopTrackingRefresh();
                    FailRemoteRefresh(source.Id);
                    throw;
                }
            }
            return new RemoteLibraryConnection(
                services,
                new LibrarySourceCapabilities(SupportsFileReveal: false),
                () =>
                {
                    scan.ScanCompleted -= completed;
                    stopTrackingRefresh();
                    client.Dispose();
                });
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<RemoteLibraryConnection> ConnectWebDavAsync(
        RemoteLibrarySource source,
        RemoteLibraryDefinition definition,
        long connectionGeneration,
        bool refreshOnConnect,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = new WebDavClient(definition.Address, definition.Username, definition.Password);
        try
        {
            client.Unauthorized += (_, _) => OnUnauthorized(source, definition, connectionGeneration);
            var imageCache = imageCacheFactory(definition);
            var indexPath = indexPathFactory(definition);
            var platform = new WebDavMultimediaPlatformIO(
                client,
                blockStoreFactory(definition),
                imageCache,
                Path.GetDirectoryName(indexPath) ?? PlatformServices.AppDataPath,
                mediaProbeFactory);
            var indexed = new WebDavIndexedLibrary(
                indexPath,
                client,
                platform,
                () => source.LoadMusic,
                () => source.LoadVideo,
                () => appSettings?.AlbumGrouping ?? AlbumGroupingMode.SameFolder);
            await indexed.InitializeAsync(cancellationToken).ConfigureAwait(false);
            EventHandler<LibraryScanCompletedEventArgs> completed = (_, args) =>
            {
                if (args.HasChanges && source.IsCurrentGeneration(connectionGeneration))
                {
                    registry.NotifyCatalogChanged(source.Id);
                }
            };
            indexed.LibraryScan.ScanCompleted += completed;
            var stopTrackingRefresh = TrackRemoteRefresh(source.Id, indexed.LibraryScan);
            var services = new LibrarySourceServices(
                indexed.LibraryQuery,
                indexed.VideoLibraryQuery,
                platform,
                indexed.SubtitleFiles,
                indexed.LibraryScan,
                indexed.VideoFolders,
                platform.MediaBlockCache,
                platform.ImageCache,
                MusicFolders: indexed.MusicFolders);
            if (refreshOnConnect)
            {
                _ = ObserveInitialWebDavScanAsync(
                    source,
                    connectionGeneration,
                    indexed.LibraryScan);
            }
            return new RemoteLibraryConnection(
                services,
                new LibrarySourceCapabilities(SupportsFileReveal: false),
                () =>
                {
                    stopTrackingRefresh();
                    indexed.LibraryScan.ScanCompleted -= completed;
                    indexed.Dispose();
                    client.Dispose();
                });
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<RemoteLibraryDefinition> AuthenticateAsync(
        RemoteLibraryDefinition definition,
        CancellationToken cancellationToken)
    {
        return definition.Provider switch
        {
            RemoteLibraryProvider.LightPlayer => await AuthenticateLightPlayerAsync(
                definition,
                cancellationToken).ConfigureAwait(false),
            RemoteLibraryProvider.NextcloudMusic => await AuthenticateNextcloudMusicAsync(
                definition,
                cancellationToken).ConfigureAwait(false),
            RemoteLibraryProvider.WebDav => await AuthenticateWebDavAsync(
                definition,
                cancellationToken).ConfigureAwait(false),
            _ => throw new RemoteLibraryIncompatibleException("The remote library provider is unknown."),
        };
    }

    private Task<RemoteLibraryDefinition> AuthenticateLightPlayerAsync(
        RemoteLibraryDefinition definition,
        CancellationToken cancellationToken) =>
        ExecuteProbeAsync(
            token => AuthenticateLightPlayerCoreAsync(definition, token),
            cancellationToken);

    private async Task<RemoteLibraryDefinition> AuthenticateLightPlayerCoreAsync(
        RemoteLibraryDefinition definition,
        CancellationToken cancellationToken)
    {
        using var probe = createClient(definition, null);
        var serverProbe = await probe.ProbeServerAsync(cancellationToken).ConfigureAwait(false);
        var info = serverProbe.Info;
        if (!info.ApiImplemented)
        {
            throw new RemoteLibraryIncompatibleException(
                $"{definition.Address} does not implement the Light Player server API.");
        }
        if (requireForwardingConfirmation && probe.UsesForwardTarget && !serverProbe.ForwardingConfirmed)
        {
            throw new RemoteLibraryIncompatibleException(
                "This browser host does not relay other Light Player targets. Use a proxy-only host for multiple remote libraries.");
        }
        if (!string.Equals(info.Name, "LightPlayer", StringComparison.Ordinal))
        {
            throw new RemoteLibraryIncompatibleException($"{definition.Address} is not a Light Player server.");
        }

        if (info.ProxyOnly && !probe.UsesForwardTarget)
        {
            throw new RemoteLibraryIncompatibleException(
                $"{definition.Address} only relays to other Light Player servers; name the server itself instead.");
        }

        if (!info.AuthenticationRequired)
        {
            return definition.Token.Length == 0 ? definition : definition with { Token = string.Empty };
        }

        if (definition.Token.Length > 0 &&
            await RefreshTokenAsync(definition, cancellationToken).ConfigureAwait(false) is { } refreshed)
        {
            return definition with { Token = refreshed };
        }

        if (definition.Username.Length > 0 && definition.Password.Length > 0)
        {
            var token = await RequestTokenAsync(
                definition,
                definition.Username,
                definition.Password,
                cancellationToken).ConfigureAwait(false);
            return definition with { Token = token };
        }

        throw new RemoteAuthenticationRequiredException(definition.DisplayName);
    }

    private static Task<RemoteLibraryDefinition> AuthenticateNextcloudMusicAsync(
        RemoteLibraryDefinition definition,
        CancellationToken cancellationToken) =>
        ExecuteProbeAsync(
            token => AuthenticateNextcloudMusicCoreAsync(definition, token),
            cancellationToken);

    private static async Task<RemoteLibraryDefinition> AuthenticateNextcloudMusicCoreAsync(
        RemoteLibraryDefinition definition,
        CancellationToken cancellationToken)
    {
        using var client = new NextcloudMusicClient(definition.Address, definition.Token);
        var server = await client.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(server.Type, "nextcloud music", StringComparison.OrdinalIgnoreCase))
        {
            throw new RemoteLibraryIncompatibleException(
                $"{definition.Address} is not a Nextcloud server with the Music app installed.");
        }
        if (!server.Extensions.Contains("apiKeyAuthentication")
            || !server.Extensions.Contains("formPost"))
        {
            throw new RemoteLibraryIncompatibleException(
                "Nextcloud Music must support API-key authentication and form POST requests.");
        }
        if (definition.Token.Length == 0)
        {
            throw new RemoteAuthenticationRequiredException(definition.DisplayName);
        }

        try
        {
            var username = await client.GetUserNameAsync(cancellationToken).ConfigureAwait(false);
            return definition with
            {
                Username = username,
                Password = string.Empty,
            };
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new RemoteAuthenticationRequiredException(definition.DisplayName);
        }
    }

    private static Task<RemoteLibraryDefinition> AuthenticateWebDavAsync(
        RemoteLibraryDefinition definition,
        CancellationToken cancellationToken) =>
        ExecuteProbeAsync(
            token => AuthenticateWebDavCoreAsync(definition, token),
            cancellationToken);

    private static async Task<RemoteLibraryDefinition> AuthenticateWebDavCoreAsync(
        RemoteLibraryDefinition definition,
        CancellationToken cancellationToken)
    {
        using var client = new WebDavClient(definition.Address, definition.Username, definition.Password);
        try
        {
            await client.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return definition;
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new RemoteAuthenticationRequiredException(definition.DisplayName);
        }
    }

    /// <summary>
    /// Exchanges the stored token for one with a fresh expiry, so a library in regular use never has
    /// to sign in again. Null when the server no longer accepts the token.
    /// </summary>
    private async Task<string?> RefreshTokenAsync(
        RemoteLibraryDefinition definition,
        CancellationToken cancellationToken)
    {
        using var client = createClient(definition, definition.Token);
        try
        {
            var result = await client
                .PostAsync<RemoteLoginResult>(RemoteApiDefaults.RefreshPath, cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrEmpty(result.Token)
                ? throw new InvalidOperationException("The server did not return a session token.")
                : result.Token;
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }
    }

    private async Task<string> RequestTokenAsync(
        RemoteLibraryDefinition definition,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var client = createClient(definition, null);
        var result = await client.PostAsync<RemoteLoginRequest, RemoteLoginResult>(
            RemoteApiDefaults.LoginPath,
            new RemoteLoginRequest(username, password),
            cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(result.Token)
            ? throw new InvalidOperationException("The server did not return a session token.")
            : result.Token;
    }

    private void OnUnauthorized(
        RemoteLibrarySource source,
        RemoteLibraryDefinition connectionDefinition,
        long connectionGeneration)
    {
        RemoteLibraryDefinition? updated = null;
        var reauthenticateRemembered = false;
        lock (gate)
        {
            var index = definitions.FindIndex(definition => definition.Id == source.Id);
            if (index < 0
                || !SameConnectionIdentity(definitions[index], connectionDefinition)
                || !source.IsCurrentGeneration(connectionGeneration))
            {
                return;
            }

            var current = definitions[index];
            if (current.Provider == RemoteLibraryProvider.WebDav)
            {
                sessionPasswords.Remove(current.Id);
                updated = current with { Password = string.Empty };
            }
            else
            {
                if (current.Token.Length == 0)
                {
                    return;
                }

                updated = current with { Token = string.Empty };
                reauthenticateRemembered = updated is { Username.Length: > 0, Password.Length: > 0 };
            }

            definitions[index] = updated;
            source.SetDefinitionFromConnection(updated);
            SaveDefinitionsLocked();
        }

        source.MarkSignInRequired();

        if (reauthenticateRemembered)
        {
            _ = ReauthenticateRememberedAsync(source);
        }
    }

    private static async Task ReauthenticateRememberedAsync(RemoteLibrarySource source)
    {
        try
        {
            source.ResetConnection();
            await source.EnsureConnectedAsync().ConfigureAwait(false);
        }
        catch
        {
            source.MarkSignInRequired();
        }
    }

    private void CommitConnectionDefinition(
        RemoteLibraryDefinition definition,
        RemoteLibrarySource? knownSource = null)
    {
        lock (gate)
        {
            var index = definitions.FindIndex(item => item.Id == definition.Id);
            if (index < 0)
            {
                return;
            }

            definitions[index] = definition;
            (knownSource ?? sources[definition.Id]).SetDefinitionFromConnection(definition);
            SaveDefinitionsLocked();
        }
    }

    private RemoteLibrarySource CommitDefinition(RemoteLibraryDefinition definition, bool persist = true)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var index = definitions.FindIndex(item => item.Id == definition.Id);
            RemoteLibrarySource source;
            if (index < 0)
            {
                definitions.Add(definition);
                source = CreateSource(definition, refreshOnConnect: true);
                sources.Add(definition.Id, source);
                registry.ReplaceRemoteSources(definitions.Select(item => sources[item.Id]).ToArray());
            }
            else
            {
                definitions[index] = definition;
                source = sources[definition.Id];
                source.UpdateDefinition(definition);
                registry.NotifyDefinitionChanged(definition.Id);
            }

            if (persist)
            {
                SaveDefinitionsLocked();
            }

            return source;
        }
    }

    private string SerializeDefinitionsWith(RemoteLibraryDefinition definition)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var next = definitions.ToList();
            var index = next.FindIndex(item => item.Id == definition.Id);
            if (index < 0)
            {
                next.Add(definition);
            }
            else
            {
                next[index] = definition;
            }

            return JsonSerializer.Serialize(
                next.ToArray(),
                RemoteStateJsonContext.Default.RemoteLibraryDefinitionArray);
        }
    }

    private async Task PersistDefinitionsAsync(string serialized, CancellationToken cancellationToken)
    {
        if (deviceState is IAsyncKeyValueStore asyncStore)
        {
            await asyncStore.SetAsync(DefinitionsKey, serialized, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            deviceState.Set(DefinitionsKey, serialized);
        }
    }

    private static async Task<T> ExecuteProbeAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var work = operation(attempt.Token);
        var expiry = Task.Delay(ProbeTimeout, attempt.Token);
        var completed = await Task.WhenAny(work, expiry).ConfigureAwait(false);
        attempt.Cancel();
        if (completed == work)
        {
            ObserveFault(expiry);
            return await work.ConfigureAwait(false);
        }

        ObserveFault(work);
        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException($"The remote server did not respond within {ProbeTimeout.TotalSeconds:0} seconds.");
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static bool SameConnectionIdentity(
        RemoteLibraryDefinition first,
        RemoteLibraryDefinition second) =>
        first.Provider == second.Provider
        && string.Equals(first.Address, second.Address, StringComparison.Ordinal)
        && string.Equals(first.ApiProxy, second.ApiProxy, StringComparison.Ordinal)
        && (first.Provider == RemoteLibraryProvider.WebDav
            ? string.Equals(first.Username, second.Username, StringComparison.Ordinal)
            : string.Equals(first.Token, second.Token, StringComparison.Ordinal));

    private async Task ObserveConnectionAsync(RemoteLibrarySource source)
    {
        try
        {
            await source.EnsureConnectedAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task ObserveInitialWebDavScanAsync(
        RemoteLibrarySource source,
        long connectionGeneration,
        ILibraryScanService scan)
    {
        try
        {
            await scan.ScanAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (source.IsCurrentGeneration(connectionGeneration))
            {
                FailRemoteRefresh(source.Id);
            }
            AppLog.Warning("WebDAV.Scan", "The initial WebDAV library scan failed.", exception);
        }
    }

    private Action TrackRemoteRefresh(LibrarySourceId sourceId, ILibraryScanService scan)
    {
        EventHandler<LibraryScanStartedEventArgs> started = (_, _) => BeginRemoteRefresh(sourceId);
        EventHandler<LibraryScanProgressEventArgs> progress = (_, eventArgs) =>
            UpdateRemoteRefreshProgress(sourceId, eventArgs.IndexedCount);
        EventHandler<LibraryScanCompletedEventArgs> completed = (_, eventArgs) =>
            CompleteRemoteRefresh(sourceId, eventArgs.IndexedCount, eventArgs.WarningCount);

        scan.ScanStarted += started;
        scan.ScanProgressChanged += progress;
        scan.ScanCompleted += completed;

        return () =>
        {
            scan.ScanStarted -= started;
            scan.ScanProgressChanged -= progress;
            scan.ScanCompleted -= completed;
            RemoveRemoteRefresh(sourceId);
        };
    }

    private void BeginRemoteRefresh(LibrarySourceId sourceId) =>
        ChangeRemoteRefreshProgress(progress =>
        {
            if (!progress.Values.Any(value => value.IsActive))
            {
                progress.Clear();
            }

            progress[sourceId] = new RemoteRefreshProgress(true, 0, 0, false);
        });

    private void UpdateRemoteRefreshProgress(LibrarySourceId sourceId, int indexedCount) =>
        ChangeRemoteRefreshProgress(progress =>
        {
            if (!progress.TryGetValue(sourceId, out var current) || !current.IsActive)
            {
                if (!progress.Values.Any(value => value.IsActive))
                {
                    progress.Clear();
                }

                current = new RemoteRefreshProgress(true, 0, 0, false);
            }

            progress[sourceId] = current with { IndexedCount = Math.Max(0, indexedCount) };
        });

    private void CompleteRemoteRefresh(
        LibrarySourceId sourceId,
        int indexedCount,
        int warningCount) =>
        ChangeRemoteRefreshProgress(progress =>
        {
            if (!progress.TryGetValue(sourceId, out var current))
            {
                if (!progress.Values.Any(value => value.IsActive))
                {
                    progress.Clear();
                }

                current = new RemoteRefreshProgress(false, 0, 0, false);
            }

            progress[sourceId] = current with
            {
                IsActive = false,
                IndexedCount = Math.Max(0, indexedCount),
                WarningCount = Math.Max(0, warningCount),
            };
        });

    private void FailRemoteRefresh(LibrarySourceId sourceId) =>
        ChangeRemoteRefreshProgress(progress =>
        {
            if (!progress.TryGetValue(sourceId, out var current))
            {
                if (!progress.Values.Any(value => value.IsActive))
                {
                    progress.Clear();
                }

                current = new RemoteRefreshProgress(false, 0, 0, false);
            }

            progress[sourceId] = current with { IsActive = false, Failed = true };
        });

    private void RemoveRemoteRefresh(LibrarySourceId sourceId) =>
        ChangeRemoteRefreshProgress(progress => progress.Remove(sourceId));

    private void ChangeRemoteRefreshProgress(Action<Dictionary<LibrarySourceId, RemoteRefreshProgress>> change)
    {
        RemoteLibraryRefreshStatus updated;
        EventHandler<RemoteLibraryRefreshStatus>? changed;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            change(remoteRefreshProgress);
            updated = new RemoteLibraryRefreshStatus(
                refreshStatus.Revision,
                remoteRefreshProgress.Count > 0,
                remoteRefreshProgress.Values.Any(value => value.IsActive),
                (int)Math.Min(int.MaxValue, remoteRefreshProgress.Values.Sum(value => (long)value.IndexedCount)),
                (int)Math.Min(int.MaxValue, remoteRefreshProgress.Values.Sum(value => (long)value.WarningCount)),
                remoteRefreshProgress.Values.Any(value => value.Failed));
            if (updated == refreshStatus)
            {
                return;
            }

            updated = updated with { Revision = refreshStatus.Revision + 1 };
            refreshStatus = updated;
            changed = RefreshStatusChanged;
        }

        changed?.Invoke(this, updated);
    }

    private RemoteLibraryDefinition NormalizeDefinition(RemoteLibraryDefinition definition)
    {
        var id = IsValidRemoteId(definition.Id)
            ? definition.Id
            : new LibrarySourceId(Guid.NewGuid().ToString("D").ToLowerInvariant());
        if (!Enum.IsDefined(definition.Provider))
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "The remote library provider is unknown.");
        }

        var address = NormalizeAddress(
            definition.Address,
            preservePath: definition.Provider is
                RemoteLibraryProvider.NextcloudMusic or RemoteLibraryProvider.WebDav);
        if (address.Length == 0)
        {
            throw new ArgumentException("The server address is not a valid HTTP or HTTPS origin.", nameof(definition));
        }

        var apiProxy = string.IsNullOrWhiteSpace(definition.ApiProxy)
            ? string.Empty
            : NormalizeAddress(definition.ApiProxy, preservePath: false);
        if (!string.IsNullOrWhiteSpace(definition.ApiProxy) && apiProxy.Length == 0)
        {
            throw new ArgumentException("The API proxy is not a valid HTTP or HTTPS origin.", nameof(definition));
        }

        return definition with
        {
            Id = id,
            Name = definition.Name?.Trim() ?? string.Empty,
            Address = address,
            Token = definition.Provider == RemoteLibraryProvider.WebDav
                ? string.Empty
                : definition.Token ?? string.Empty,
            Username = definition.Username ?? string.Empty,
            Password = definition.Provider is RemoteLibraryProvider.LightPlayer or RemoteLibraryProvider.WebDav
                ? definition.Password ?? string.Empty
                : string.Empty,
            ApiProxy = definition.Provider == RemoteLibraryProvider.LightPlayer ? apiProxy : string.Empty,
            LoadVideo = definition.Provider != RemoteLibraryProvider.NextcloudMusic && definition.LoadVideo,
            RefreshAtStartup = definition.Provider is
                RemoteLibraryProvider.NextcloudMusic or RemoteLibraryProvider.WebDav
                && definition.RefreshAtStartup,
            StorageKey = SafeStorageKey(definition.StorageKey, id),
        };
    }

    private List<RemoteLibraryDefinition> LoadDefinitions()
    {
        try
        {
            var json = deviceState.Get(DefinitionsKey);
            if (!string.IsNullOrEmpty(json))
            {
                var loaded = JsonSerializer.Deserialize(
                    json,
                    RemoteStateJsonContext.Default.RemoteLibraryDefinitionArray) ?? [];
                var sanitized = SanitizeLoaded(loaded);
                if (!loaded.SequenceEqual(sanitized))
                {
                    deviceState.Set(
                        DefinitionsKey,
                        JsonSerializer.Serialize(
                            sanitized.ToArray(),
                            RemoteStateJsonContext.Default.RemoteLibraryDefinitionArray));
                }
                return sanitized;
            }

            var previousJson = deviceState.Get(LegacyDefinitionsKey);
            if (!string.IsNullOrEmpty(previousJson))
            {
                var previous = JsonSerializer.Deserialize(
                    previousJson,
                    RemoteStateJsonContext.Default.LegacyRemoteLibraryDefinitionArray) ?? [];
                var migratedPrevious = SanitizeLoaded(previous.Select(MigrateLegacyDefinition));
                SaveMigratedDefinitions(migratedPrevious, LegacyDefinitionsKey);
                return migratedPrevious;
            }

            var legacyJson = deviceState.Get(LegacyServersKey);
            if (string.IsNullOrEmpty(legacyJson))
            {
                return [];
            }

            var legacy = JsonSerializer.Deserialize(
                legacyJson,
                RemoteStateJsonContext.Default.RemoteServerEntryArray) ?? [];
            if (legacy.Length == 0 || legacy.All(server => string.IsNullOrWhiteSpace(server.Address)))
            {
                var browserLegacy = JsonSerializer.Deserialize(
                    legacyJson,
                    RemoteStateJsonContext.Default.LegacyBrowserServerEntryArray) ?? [];
                legacy = browserLegacy.Select(server => new RemoteServerEntry(
                    server.Id ?? string.Empty,
                    server.Name ?? string.Empty,
                    server.Url ?? string.Empty,
                    server.Token ?? string.Empty)).ToArray();
            }

            var migrated = legacy.Select(server => NormalizeDefinition(MigrateLegacyDefinition(
                new LegacyRemoteLibraryDefinition(
                    default,
                    server.Name,
                    server.Address,
                    server.ForwardTarget,
                    server.Token,
                    server.Username,
                    server.Password,
                    LoadMusic: true,
                    LoadVideo: true,
                    StorageKey: server.Id)))).ToList();
            SaveMigratedDefinitions(migrated, LegacyServersKey);
            return migrated;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static RemoteLibraryDefinition MigrateLegacyDefinition(LegacyRemoteLibraryDefinition definition) =>
        new(
            definition.Id,
            definition.Name,
            string.IsNullOrWhiteSpace(definition.ForwardTarget) ? definition.Address : definition.ForwardTarget,
            string.IsNullOrWhiteSpace(definition.ForwardTarget) ? string.Empty : definition.Address,
            definition.Token,
            definition.Username,
            definition.Password,
            definition.LoadMusic,
            definition.LoadVideo,
            definition.StorageKey);

    private void SaveMigratedDefinitions(List<RemoteLibraryDefinition> migrated, string oldKey)
    {
        deviceState.Set(
            DefinitionsKey,
            JsonSerializer.Serialize(migrated.ToArray(), RemoteStateJsonContext.Default.RemoteLibraryDefinitionArray));
        deviceState.Remove(oldKey);
    }

    private List<RemoteLibraryDefinition> SanitizeLoaded(IEnumerable<RemoteLibraryDefinition> loaded)
    {
        var result = new List<RemoteLibraryDefinition>();
        var ids = new HashSet<LibrarySourceId>();
        foreach (var definition in loaded)
        {
            RemoteLibraryDefinition normalized;
            try
            {
                normalized = NormalizeDefinition(definition);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (ids.Add(normalized.Id))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private LibraryVisibilitySettings LoadVisibilitySettings()
    {
        try
        {
            var json = deviceState.Get(VisibilityKey);
            return string.IsNullOrEmpty(json)
                ? new LibraryVisibilitySettings()
                : JsonSerializer.Deserialize(json, RemoteStateJsonContext.Default.LibraryVisibilitySettings)
                    ?? new LibraryVisibilitySettings();
        }
        catch (JsonException)
        {
            return new LibraryVisibilitySettings();
        }
    }

    private void SaveDefinitionsLocked() => deviceState.Set(DefinitionsKey, SerializeDefinitionsLocked());

    private string SerializeDefinitionsLocked() =>
        JsonSerializer.Serialize(definitions.ToArray(), RemoteStateJsonContext.Default.RemoteLibraryDefinitionArray);

    private IKeyValueStore SourceSettings(RemoteLibraryDefinition definition)
    {
        var prefix = $"source.{definition.Id.Value}.";
        var scoped = new PrefixedKeyValueStore(deviceState, prefix);
        const string migratedKey = "settings.cacheLimitsMigrated";
        if (!scoped.GetBoolean(migratedKey))
        {
            CopySetting("settings.mediaCacheMaxMib");
            CopySetting("settings.albumArtCacheMaxMib");
            scoped.SetBoolean(migratedKey, true);
        }

        return scoped;

        void CopySetting(string key)
        {
            if (scoped.Get(key) is null && deviceState.Get(key) is { } value)
            {
                scoped.Set(key, value);
            }
        }
    }

    private NextcloudMusicCatalogState LoadNextcloudCatalog(RemoteLibraryDefinition definition)
    {
        try
        {
            string? json;
            if (nextcloudCatalogPathFactory is { } pathFactory)
            {
                var path = pathFactory(definition);
                json = File.Exists(path) ? File.ReadAllText(path) : null;
            }
            else
            {
                json = SourceSettings(definition).Get(NextcloudCatalogKey);
            }

            if (string.IsNullOrEmpty(json))
            {
                return NextcloudMusicCatalogState.Empty;
            }

            var state = JsonSerializer.Deserialize(
                    json,
                    NextcloudMusicJsonContext.Default.NextcloudMusicCatalogState);
            return state is { Albums: not null, Songs: not null }
                ? state
                : NextcloudMusicCatalogState.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warning(
                "NextcloudMusic.Cache",
                $"Could not load the cached catalog for remote library {definition.Id}.",
                exception);
            return NextcloudMusicCatalogState.Empty;
        }
    }

    private async Task SaveNextcloudCatalogAsync(
        RemoteLibraryDefinition definition,
        NextcloudMusicCatalogState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(
                state,
                NextcloudMusicJsonContext.Default.NextcloudMusicCatalogState);
            if (nextcloudCatalogPathFactory is { } pathFactory)
            {
                var path = pathFactory(definition);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            }
            else if (deviceState is IAsyncKeyValueStore asyncStore)
            {
                await asyncStore.SetAsync(
                    $"source.{definition.Id.Value}.{NextcloudCatalogKey}",
                    json).ConfigureAwait(false);
            }
            else
            {
                SourceSettings(definition).Set(NextcloudCatalogKey, json);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning(
                "NextcloudMusic.Cache",
                $"Could not save the cached catalog for remote library {definition.Id}.",
                exception);
        }
    }

    private void RemoveNextcloudCatalog(RemoteLibraryDefinition definition)
    {
        try
        {
            if (nextcloudCatalogPathFactory is { } pathFactory)
            {
                var path = pathFactory(definition);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                SourceSettings(definition).Remove(NextcloudCatalogKey);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning(
                "NextcloudMusic.Cache",
                $"Could not remove the cached catalog for remote library {definition.Id}.",
                exception);
        }
    }

    private void InvalidateNextcloudCatalog(
        RemoteLibraryDefinition? existing,
        RemoteLibraryDefinition updated)
    {
        if (existing is not null
            && (existing.Provider == RemoteLibraryProvider.NextcloudMusic
                || updated.Provider == RemoteLibraryProvider.NextcloudMusic)
            && !SameConnectionIdentity(existing, updated))
        {
            RemoveNextcloudCatalog(existing);
        }
    }

    private static bool IsValidRemoteId(LibrarySourceId id) =>
        id != LibrarySourceId.Local && Guid.TryParseExact(id.Value, "D", out _);

    private static string SafeStorageKey(string value, LibrarySourceId id) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !value.Contains(Path.DirectorySeparatorChar) &&
        !value.Contains(Path.AltDirectorySeparatorChar) &&
        value is not "." and not ".."
            ? value
            : id.Value;

    private static string NormalizeAddress(string value, bool preservePath)
    {
        var text = (value ?? string.Empty).Trim().TrimEnd('/');
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            var host = text.Split('/')[0].Split(':')[0];
            text = (IsPrivateHost(host) ? "http://" : "https://") + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) &&
            string.IsNullOrEmpty(uri.UserInfo)
                ? uri.GetLeftPart(preservePath ? UriPartial.Path : UriPartial.Authority).TrimEnd('/')
                : string.Empty;
    }

    private static bool IsPrivateHost(string host)
    {
        if (host is "localhost" || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!System.Net.IPAddress.TryParse(host, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
            bytes[0] == 127 ||
            (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }

    private static RemoteApiClient CreateDirectClient(RemoteLibraryDefinition definition, string? token) =>
        new(
            ApiBaseAddress(definition.HasApiProxy ? definition.ApiProxy : definition.Address),
            token,
            definition.HasApiProxy ? definition.Address : null);

    private static string ApiBaseAddress(string address) => address.TrimEnd('/') + "/api/v1/";

    private static string SourceDirectory(string root, RemoteLibraryDefinition definition) =>
        Path.Combine(root, "Servers", SafeStorageKey(definition.StorageKey, definition.Id));

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private void OnLocalScanCompleted(object? sender, LibraryScanCompletedEventArgs eventArgs)
    {
        if (eventArgs.HasChanges)
        {
            registry.NotifyCatalogChanged(LibrarySourceId.Local);
        }
    }

    private void OnAlbumGroupingChanged(object? sender, EventArgs eventArgs) => registry.NotifyCatalogChanged();

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        if (localScanService is not null)
        {
            localScanService.ScanCompleted -= OnLocalScanCompleted;
        }

        registry.Dispose();
        if (appSettings is not null)
        {
            appSettings.AlbumGroupingChanged -= OnAlbumGroupingChanged;
        }
        sources.Clear();
        sessionPasswords.Clear();
        remoteRefreshProgress.Clear();
    }

    private sealed record RemoteRefreshProgress(
        bool IsActive,
        int IndexedCount,
        int WarningCount,
        bool Failed);
}

public sealed record LegacyBrowserServerEntry(
    string? Id,
    string? Name,
    string? Url,
    string? Token);

internal sealed class RemoteLibrarySource : ILibrarySource
{
    private readonly object gate = new();
    private readonly Func<RemoteLibrarySource, RemoteLibraryDefinition, long, bool, CancellationToken, Task<RemoteLibraryConnection>> connect;
    private RemoteLibraryDefinition definition;
    private RemoteLibraryConnection? connection;
    private Task<LibrarySourceServices>? connectionTask;
    private CancellationTokenSource? connectionCancellation;
    private LibrarySourceStatus runtimeStatus = LibrarySourceStatus.Offline;
    private string? lastError;
    private long connectionGeneration;
    private bool refreshOnNextConnection;
    private bool disposed;

    public RemoteLibrarySource(
        RemoteLibraryDefinition definition,
        Func<RemoteLibrarySource, RemoteLibraryDefinition, long, bool, CancellationToken, Task<RemoteLibraryConnection>> connect,
        bool refreshOnNextConnection)
    {
        this.definition = definition;
        this.connect = connect;
        this.refreshOnNextConnection = refreshOnNextConnection;
    }

    public RemoteLibraryDefinition Definition
    {
        get { lock (gate) { return definition; } }
    }

    public LibrarySourceId Id => Definition.Id;

    public string DisplayName => Definition.DisplayName;

    public bool LoadMusic => Definition.LoadMusic;

    public bool LoadVideo => Definition.LoadVideo;

    public LibrarySourceStatus Status
    {
        get
        {
            lock (gate)
            {
                return definition.ParticipatesInBrowse ? runtimeStatus : LibrarySourceStatus.Disabled;
            }
        }
    }

    public string? LastError
    {
        get { lock (gate) { return lastError; } }
    }

    public LibrarySourceCapabilities Capabilities
    {
        get
        {
            lock (gate)
            {
                return connection?.Capabilities ?? new LibrarySourceCapabilities(SupportsFileReveal: false);
            }
        }
    }

    public LibrarySourceServices? Services
    {
        get { lock (gate) { return connection?.Services; } }
    }

    public event EventHandler? StatusChanged;

    public Task<LibrarySourceServices> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        Task<LibrarySourceServices> pending;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (connection is not null)
            {
                return Task.FromResult(connection.Services);
            }

            if (connectionTask is null)
            {
                connectionCancellation = new CancellationTokenSource();
                var generation = connectionGeneration;
                connectionTask = ConnectCoreAsync(
                    definition,
                    generation,
                    refreshOnNextConnection,
                    connectionCancellation);
            }

            pending = connectionTask;
        }

        return cancellationToken.CanBeCanceled ? pending.WaitAsync(cancellationToken) : pending;
    }

    public void UpdateDefinition(RemoteLibraryDefinition updated)
    {
        bool reconnect;
        lock (gate)
        {
            reconnect = !SameEndpointAndCredentials(definition, updated);
            definition = updated;
            if (reconnect)
            {
                refreshOnNextConnection = true;
            }
        }

        if (reconnect)
        {
            ResetConnection();
        }
        else
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetDefinitionFromConnection(RemoteLibraryDefinition updated)
    {
        lock (gate)
        {
            definition = updated;
        }
    }

    public bool IsCurrentGeneration(long generation)
    {
        lock (gate)
        {
            return !disposed && generation == connectionGeneration;
        }
    }

    public void ResetConnection() => ResetConnection(LibrarySourceStatus.Offline, null);

    public void MarkSignInRequired() =>
        ResetConnection(LibrarySourceStatus.SignInRequired, "Authentication is required.");

    private async Task<LibrarySourceServices> ConnectCoreAsync(
        RemoteLibraryDefinition connectingDefinition,
        long generation,
        bool refreshOnConnect,
        CancellationTokenSource cancellation)
    {
        SetStatus(generation, LibrarySourceStatus.Connecting, null);
        try
        {
            var connected = await connect(
                this,
                connectingDefinition,
                generation,
                refreshOnConnect,
                cancellation.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (disposed || generation != connectionGeneration)
                {
                    connected.Dispose();
                    throw new OperationCanceledException(cancellation.Token);
                }

                connection = connected;
                runtimeStatus = LibrarySourceStatus.Online;
                lastError = null;
                connectionTask = null;
                connectionCancellation = null;
                refreshOnNextConnection = false;
            }

            StatusChanged?.Invoke(this, EventArgs.Empty);
            return connected.Services;
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException)
            {
                AppLog.Warning(
                    "Remote.Library",
                    $"Could not connect to remote library {connectingDefinition.Id}.",
                    exception);
            }
            var status = exception switch
            {
                RemoteAuthenticationRequiredException => LibrarySourceStatus.SignInRequired,
                RemoteLibraryIncompatibleException => LibrarySourceStatus.Incompatible,
                _ => LibrarySourceStatus.Offline,
            };
            SetStatus(generation, status, exception.Message, clearTask: true);
            throw;
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void SetStatus(
        long generation,
        LibrarySourceStatus status,
        string? error,
        bool clearTask = false)
    {
        lock (gate)
        {
            if (disposed || generation != connectionGeneration)
            {
                return;
            }

            runtimeStatus = status;
            lastError = error;
            if (clearTask)
            {
                connectionTask = null;
                connectionCancellation = null;
            }
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetConnection(LibrarySourceStatus status, string? error)
    {
        RemoteLibraryConnection? oldConnection;
        CancellationTokenSource? oldCancellation;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            connectionGeneration++;
            oldConnection = connection;
            oldCancellation = connectionCancellation;
            connection = null;
            connectionTask = null;
            connectionCancellation = null;
            runtimeStatus = status;
            lastError = error;
        }

        oldCancellation?.Cancel();
        oldConnection?.Dispose();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameEndpointAndCredentials(
        RemoteLibraryDefinition first,
        RemoteLibraryDefinition second) =>
        first.Provider == second.Provider &&
        string.Equals(first.Address, second.Address, StringComparison.Ordinal) &&
        string.Equals(first.ApiProxy, second.ApiProxy, StringComparison.Ordinal) &&
        string.Equals(first.Token, second.Token, StringComparison.Ordinal) &&
        string.Equals(first.Username, second.Username, StringComparison.Ordinal) &&
        string.Equals(first.Password, second.Password, StringComparison.Ordinal);

    public void Dispose()
    {
        RemoteLibraryConnection? oldConnection;
        CancellationTokenSource? oldCancellation;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            connectionGeneration++;
            oldConnection = connection;
            oldCancellation = connectionCancellation;
            connection = null;
            connectionTask = null;
            connectionCancellation = null;
        }

        oldCancellation?.Cancel();
        oldConnection?.Dispose();
    }
}

internal sealed class RemoteLibraryConnection : IDisposable
{
    private Action? dispose;

    public RemoteLibraryConnection(
        LibrarySourceServices services,
        LibrarySourceCapabilities capabilities,
        Action dispose)
    {
        Services = services;
        Capabilities = capabilities;
        this.dispose = dispose;
    }

    public LibrarySourceServices Services { get; }

    public LibrarySourceCapabilities Capabilities { get; }

    public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
}