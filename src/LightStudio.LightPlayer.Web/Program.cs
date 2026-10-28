using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using LightMediaRenderer.Subtitles;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;
using LightMediaRenderer.Avalonia.Rendering;
using LightMediaRenderer.Avalonia.Shaders;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.RemoteServices;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal static class Program
{
    private static RemoteLibraryManager? remoteLibraries;
    private static BrowserVideoShaderPackageStore? shaderPackageStore;
    private static BrowserLyricSourceStore? lyricSourceStore;

    private static async Task Main(string[] args)
    {
        try
        {
            await StartApplicationAsync();
        }
        catch (Exception exception)
        {
            AppLog.Error("Web.Startup", "The browser client could not start.", exception);
            throw;
        }
    }

    private static async Task StartApplicationAsync()
    {
        AppLog.ConfigureConsole();
        AppLog.Information("Web.Startup", "Starting Light Player browser client.");
        BrowserMainThread.Capture();
        await BrowserSqliteConnection.RegisterAsync();
        await BrowserAssets.ImportAsync("WebAudio", "web-audio.js");
        await BrowserAssets.ImportAsync("BrowserStorage", "browser-storage.js");
        await BrowserAssets.ImportAsync("BrowserLifecycle", "browser-lifecycle.js");
        await BrowserAssets.ImportAsync("BlockStore", "block-store.js");
        await BrowserAssets.ImportAsync("BrowserNavigation", "browser-navigation.js");
        await BrowserAssets.ImportAsync("BrowserPairingLaunch", "browser-pairing-launch.js");
        await BrowserAssets.ImportAsync("BrowserFullScreen", "browser-fullscreen.js");
        await BrowserAssets.ImportAsync("BrowserPictureInPicture", "browser-picture-in-picture.js");
        await BrowserAssets.ImportAsync("BrowserWakeLock", "browser-wake-lock.js");
        await BrowserAssets.ImportAsync("MediaSession", "media-session.js");
        await BrowserAssets.ImportAsync("RemoteLibraryEditor", "remote-library-editor.js");
        await BrowserAssets.ImportAsync("BrowserFonts", "browser-fonts.js");
        await BrowserAssets.ImportAsync("LyricSources", "lyric-sources.js");
        await BrowserStorage.InitializeAsync();
        await BrowserSqliteConnection.ApplyPendingIndexDatabaseResetAsync();
        BrowserPairingRequest? pairingRequest = null;
        Exception? pairingLaunchError = null;
        try
        {
            pairingRequest = await BrowserPairingLaunch.TakeAsync();
        }
        catch (Exception exception)
        {
            pairingLaunchError = exception;
            AppLog.Warning("Web.Pairing", "The pairing launch could not be read.", exception);
            await BrowserPairingLaunch.CompleteAsync();
        }

        await BrowserFileSystem.InitializeAsync();
        PlatformServices.LibraryFolderAccess = new BrowserLibraryFolderAccess();

        await BrowserVideoBootstrap.RegisterVideoPipelineAsync();
        _ = LoadFfmpegLicenseNoticeAsync();
        ImageDecoding.Decoder = new BrowserImageDecoder();
        PlatformServices.MaxConcurrentArtworkLoads = BackgroundMediaJobs.Count;
        PlatformServices.PcmPlaybackSourceFactory = (request, resolver) => new BrowserPcmPlaybackSource(request, resolver);
        var deviceState = new BrowserKeyValueStore();

        PlatformServices.AppDataPath = "/LightPlayer";
        PlatformServices.CachePath = "/LightPlayer/Cache";
        string shaderPackagePath = AppPaths.DataSubdirectory("video-shader-packages");
        shaderPackageStore = new BrowserVideoShaderPackageStore(shaderPackagePath);
        await shaderPackageStore.RestoreAsync();
        var shaderPackages = new VideoShaderPackageCatalog(shaderPackagePath);
        shaderPackageStore.Attach(shaderPackages);
        PlatformServices.VideoShaderPackages = shaderPackages;

        // Ahead of the services: the built-in playlists take their titles from the resources as
        // they are constructed, so the language has to be in place first.
        Strings.LocalizedStrings.ApplyLanguage(new KeyValueAppSettingsStore(deviceState).InterfaceLanguage);
        await BrowserUpdates.RegisterAsync();

        var clientServices = await BrowserClientServices.CreateAsync(deviceState);
        PlatformServices.QueueIndexDatabaseResetAsync = BrowserSqliteConnection.QueueIndexDatabaseResetAsync;
        PlatformServices.ReloadAfterIndexDatabaseReset = BrowserLifecycleInterop.ReloadAfterIndexDatabaseReset;
        RemoteImageCache? CreateImageCache(RemoteLibraryDefinition definition) =>
            definition.Provider == RemoteLibraryProvider.NextcloudMusic
                ? new RemoteImageCache(new BrowserBlockStore(
                    $"{definition.Id.Value}-album-art",
                    new PrefixedKeyValueStore(deviceState, $"source.{definition.Id.Value}."),
                    RemoteImageCache.DefaultMaxBytes,
                    "settings.albumArtCacheMaxMib",
                    RemoteImageCache.MinBytes,
                    RemoteImageCache.MaxAllowedBytes))
                : null;

        remoteLibraries = new RemoteLibraryManager(
            deviceState,
            definition => new BrowserBlockStore(
                definition.Id.Value,
                new PrefixedKeyValueStore(deviceState, $"source.{definition.Id.Value}."),
                RemoteMediaBlockCache.MaxPersistentBytes),
            CreateImageCache,
            localSource: new LocalLibrarySource(clientServices),
            deleteStorage: definition =>
            {
                var settings = new PrefixedKeyValueStore(deviceState, $"source.{definition.Id.Value}.");
                _ = new BrowserBlockStore(
                    definition.Id.Value,
                    settings,
                    RemoteMediaBlockCache.MaxPersistentBytes).ClearAsync();
                if (CreateImageCache(definition) is { } imageCache)
                {
                    _ = imageCache.ClearAsync();
                }
            },
            requireForwardingConfirmation: true,
            appSettings: clientServices.Settings,
            deleteIndexAsync: BrowserSqliteConnection.DeleteDatabaseAsync,
            mediaProbeFactory: open => new BrowserMultimediaPlatformIO(open));
        PlatformServices.LocalServicesFactory = () => clientServices;
        PlatformServices.LibrarySources = remoteLibraries.Registry;
        PlatformServices.RemoteLibraryManager = remoteLibraries;
        if (pairingRequest is not null)
        {
            try
            {
                await PairRemoteLibraryAsync(pairingRequest);
                await BrowserPairingLaunch.CompleteAsync();
            }
            catch (Exception exception)
            {
                pairingLaunchError = exception;
                AppLog.Warning("Web.Pairing", "The paired remote library could not be added.", exception);
                if (pairingRequest.ExpiresAtUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    await BrowserPairingLaunch.CompleteAsync();
                }
            }
        }

        PlatformServices.WebHostAddress = BrowserStorage.GetOrigin().TrimEnd('/');
        PlatformServices.WebHostProbe = ProbeWebHostAsync();
        PlatformServices.VideoPlaybackStateStore = new KeyValueVideoPlaybackStateStore(deviceState);
        PlatformServices.VideoHistoryStore = new KeyValueVideoHistoryStore(deviceState, clientServices.Settings);
        MigrateLegacyRemoteQueueIfNeeded(deviceState, clientServices.ShellState, remoteLibraries.Definitions);

        // Browsers that already picked a library did so before setup ran here; do not send them back to it.
        if (remoteLibraries.Definitions.Count > 0 || clientServices.Settings.LibraryFolderPaths.Count > 0)
        {
            clientServices.Settings.InitialSetupCompleted = true;
        }

        // The runtime's filesystem starts empty every load, so the scripts are laid down again:
        // whichever set this browser kept, and the bundled one on the first load that has none.
        lyricSourceStore = new BrowserLyricSourceStore(BrowserClientServices.ScriptDirectory);
        bool restoredLyricSources = await lyricSourceStore.RestoreAsync();
        lyricSourceStore.Attach(clientServices.Scripts);
        if (!restoredLyricSources && clientServices.Settings.EnableThirdPartyLyrics)
        {
            await clientServices.LyricSources.ProvisionBundledSourcesAsync();
        }

        BrowserStorage.Remove("server.auto");
        BrowserStorage.Remove("server.baseUrl");
        BrowserStorage.Remove("server.token");
        BrowserStorage.Remove("server.name");
        _ = remoteLibraries.ConnectEnabledAsync();

        // Local media only exists here once the user has picked a folder the browser will keep.
        PlatformServices.SupportsLocalFileAccess = false;

        // Picking single files needs none of that: the page is handed the file itself, so "Open
        // file" works even in a browser that cannot keep a library folder.
        PlatformServices.MediaFilePicker = PickMediaFilesAsync;
        PlatformServices.CanManageLibraryFolders = BrowserFileSystem.IsSupported;

        // A restored folder is only readable again after the user re-grants it, so a scan the app
        // starts on its own could do nothing but raise warnings.
        PlatformServices.SupportsStartupLibraryScan = false;
        PlatformServices.SupportsSampleRateSettings = false;
        PlatformServices.PhotoExport = new BrowserPhotoExportService();
        PlatformServices.VideoScreenshotEncoder = null;
    #if LMR_AVALONIA_FORK
        PlatformServices.SupportsAdvancedColorOutput = true;
    #endif
        PlatformServices.PlaybackStateObserver = new BrowserLifecycle();
        PlatformServices.NavigationHistory = await BrowserNavigationHistory.CreateAsync();
        WebAudioPlaybackBackend.Register();
        PlatformServices.CanStartPlayback = WebAudioInterop.IsUnlocked;
        BrowserFullScreen.Register();
        BrowserPictureInPicture.Register();
        BrowserWakeLock.Register();
        BrowserSystemMediaControls.Register();
        BrowserRemoteLibraryEditor.Register();

        await BrowserFonts.LoadAsync();

        // FontFallbacks below only covers Avalonia's own text; subtitles are drawn through Skia's
        // font manager, which in the browser knows no families at all.
        foreach (byte[] face in BrowserFonts.Faces)
        {
            SubtitleFontFallbacks.Add(() => new MemoryStream(face, writable: false));
        }

        // Initialize the cached metadata on the JavaScript-owning thread before Avalonia starts
        // Avoid deadlock due to lazy initialize
        _ = clientServices.LyricSources.BundledSources;
        await BuildAvaloniaApp().StartBrowserAppAsync("out", new BrowserPlatformOptions
        {
#if LMR_AVALONIA_FORK
            PreferHdr = true
#endif
        });
        BrowserPageTheme.Register();

        if (pairingRequest is not null || pairingLaunchError is not null)
        {
            var succeeded = pairingLaunchError is null;
            var message = succeeded
                ? string.Format(Strings.Resources.RemoteApiPairing_WebSuccess, pairingRequest!.ServerAddress)
                : string.Format(Strings.Resources.RemoteApiPairing_WebFailed, pairingLaunchError!.Message);
            await BrowserPairingLaunch.ShowResultAsync(succeeded, message);
        }

        // After the app exists, so a launch that arrives immediately reaches it. The files
        // themselves were claimed by the page long before this point.
        await BrowserFileActivation.StartAsync();
    }

    /// <summary>
    /// Opens the browser's file picker for media the app can play, and returns the identifiers the
    /// picked files are read by. They are adopted for this page only: no folder was granted, so
    /// nothing here reaches the library.
    /// </summary>
    private static Task<IReadOnlyList<string>> PickMediaFilesAsync() =>
        BrowserFileSystem.PickFilesAsync(
            Strings.Resources.Shell_OpenMediaFilesPickerTitle,
            string.Join(',', QueueDragFormats.AudioExtensions),
            string.Join(',', MediaContainerFormats.Video),
            string.Join(',', MediaContainerFormats.Image));

    private static async Task PairRemoteLibraryAsync(BrowserPairingRequest request)
    {
        var login = await ExchangePairingAsync(request);
        var definition = remoteLibraries!.Definitions.FirstOrDefault(existing =>
                string.Equals(existing.Address, request.ServerAddress, StringComparison.OrdinalIgnoreCase) &&
                existing.Provider == RemoteLibraryProvider.LightPlayer)
            ?? new RemoteLibraryDefinition(default, string.Empty, request.ServerAddress);
        await remoteLibraries.SavePairedAsync(definition with { Token = login.Token });
    }

    private static async Task<RemoteLoginResult> ExchangePairingAsync(BrowserPairingRequest request)
    {
        using var client = new RemoteApiClient(request.ServerAddress + "/api/v1/");
        using var attempt = new CancellationTokenSource();
        var work = client.PostAsync<RemotePairingRequest, RemoteLoginResult>(
            RemoteApiDefaults.PairingPath,
            new RemotePairingRequest(request.Token),
            attempt.Token);
        var expiry = Task.Delay(TimeSpan.FromSeconds(20), attempt.Token);
        var completed = await Task.WhenAny(work, expiry);
        if (completed == work)
        {
            attempt.Cancel();
            ObserveFault(expiry);
            return await work;
        }

        attempt.Cancel();
        ObserveFault(work);
        throw new TimeoutException("The paired server did not answer within 20 seconds.");
    }

    /// <summary>
    /// Asks the page's own origin what it is, so the library editor knows whether this host has a
    /// library to offer or only relays to the address the user types.
    /// </summary>
    private static async Task ProbeWebHostAsync()
    {
        try
        {
            using var client = new RemoteApiClient(BrowserStorage.GetOrigin().TrimEnd('/') + "/api/v1/");
            using var attempt = new CancellationTokenSource();
            var work = client.ProbeServerAsync(attempt.Token);
            var expiry = Task.Delay(TimeSpan.FromSeconds(15), attempt.Token);
            var completed = await Task.WhenAny(work, expiry);
            attempt.Cancel();
            if (completed != work)
            {
                ObserveFault(work);
                await expiry;
                throw new TimeoutException("The hosting origin did not answer within 15 seconds.");
            }

            ObserveFault(expiry);
            var probe = await work;
            PlatformServices.WebHostProxyOnly = probe.Info.ApiImplemented ? probe.Info.ProxyOnly : null;
        }
        catch (Exception exception)
        {
            AppLog.Warning("Web.Startup", "The hosting origin did not answer its server probe.", exception);
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Reads the notice from a background media worker without holding up startup.</summary>
    private static async Task LoadFfmpegLicenseNoticeAsync()
    {
        try
        {
            PlatformServices.FfmpegLicenseNotice = await BrowserMediaOperations.ReadLicenseNoticeAsync();
        }
        catch (Exception exception)
        {
            AppLog.Warning("Web.Media", "Failed to load FFmpeg license.", exception);
        }
    }

    private static void MigrateLegacyRemoteQueueIfNeeded(
        IKeyValueStore state,
        IShellStateStore shellState,
        IReadOnlyList<RemoteLibraryDefinition> definitions)
    {
        if (shellState.PlaybackQueue is not null ||
            state.Get("server.baseUrl") is not { Length: > 0 } legacyAddress ||
            definitions.FirstOrDefault(definition =>
                string.Equals(
                    definition.Address,
                    legacyAddress,
                    StringComparison.OrdinalIgnoreCase)) is not { } definition)
        {
            return;
        }

        var storageKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(legacyAddress)))[..16];
        var legacy = new KeyValueShellStateStore(new PrefixedKeyValueStore(state, $"server.{storageKey}."));
        if (legacy.PlaybackQueue is not { Items.Count: > 0 } queue)
        {
            return;
        }

        shellState.PlaybackQueue = queue with
        {
            Items = queue.Items.Select(item => item with { SourceId = definition.Id.Value }).ToArray(),
        };
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .AfterSetup(_ =>
            {
                MenuBackdrop.Install(MenuBackdropMode.Overlay);
                BrowserFonts.Register();
            })
            .WithInterFont()
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = 512 * 1024 * 1024
            })
            .With(new FontManagerOptions
            {
                FontFallbacks =
                [
                    new FontFallback { FontFamily = new FontFamily(BrowserFonts.CjkFontFamily) },
                ],
            });
}