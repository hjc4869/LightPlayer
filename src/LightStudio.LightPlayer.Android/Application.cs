using Android.App;
using Android.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Android;
using Avalonia.Threading;
using LightStudio.LightPlayer.Android.Services;
using LightStudio.LightPlayer.Android.Playback;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightMediaRenderer.Avalonia.Rendering;
using LightStudio.Logging;
using LightStudio.LocalServices;
using LightStudio.Inference;
using LightStudio.RemoteServices;

namespace LightStudio.LightPlayer.Android;

[Application]
public class Application : AvaloniaAndroidApplication<App>
{
    private JsonAppSettingsStore deviceSettings = null!;
    private RemoteLibraryManager remoteLibraries = null!;
    private AndroidMediaStoreProvider nativeLibrary = null!;

    protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
        PlatformServices.AppDataPath = Context.ApplicationInfo.DataDir;
        PlatformServices.CachePath = Context.ApplicationContext.CacheDir.AbsolutePath;
        AppLog.ConfigureClient(AppPaths.DataDirectory);
        AppLog.Information("Android.Startup", "Starting Light Player Android client.");
        AppInit.Initialize(Context);
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Inject Android-specific implementations into the shared application before the framework
        // finishes initializing and the shell / playback controller are built. This keeps every
        // Android dependency inside this head and out of the shared UI library.
        var multimediaPlatform = new AndroidMultimediaPlatformIO(this);
        PlatformServices.MultimediaPlatformIOFactory = () => multimediaPlatform;
        PlatformServices.LibraryFolderAccess = new AndroidLibraryFolderAccess(this, multimediaPlatform);
        nativeLibrary = new AndroidMediaStoreProvider(this, multimediaPlatform);
        PlatformServices.NativeMediaLibrary = nativeLibrary;

        deviceSettings = new JsonAppSettingsStore();
        var deviceShellState = new JsonShellStateStore();
        var localServices = new LocalServices.LocalServices(
            static (content, name) => new MediaLibraryCore.Lyrics.JsDownloadSource(content, name),
            PlatformServices.MultimediaPlatformIOFactory,
            deviceSettings,
            deviceShellState,
            Context.ApplicationInfo.NativeLibraryDir,
            inferenceProvider: new LiteRtInferenceProvider());
        PlatformServices.LocalServicesFactory = () => localServices;
        remoteLibraries = new RemoteLibraryManager(
            AppPaths.DataDirectory,
            AppPaths.CacheDirectory,
            localServices);
        MigrateLegacyRemoteQueueIfNeeded(deviceShellState, remoteLibraries.Definitions);
        PlatformServices.LibrarySources = remoteLibraries.Registry;
        PlatformServices.RemoteLibraryManager = remoteLibraries;
        _ = remoteLibraries.ConnectEnabledAsync();
        PlatformServices.FileRevealService = new AndroidFileRevealService(this, multimediaPlatform);
        PlatformServices.FileShareService = new AndroidFileShareService(this, multimediaPlatform);
        PlatformServices.SupportsAudioOutputSettings = true;
        SystemMediaControlsFactory.Register(() => new AndroidSystemMediaControls(this));
        AAudioPlaybackBackend.Register(this);

        PlatformServices.UpdateAssetName = 
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 || RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? "im.hjc.LightPlayer-Signed.apk"
                : null;

    #if LMR_AVALONIA_FORK
        PlatformServices.SupportsAdvancedColorOutput = SupportsExtendedLinearOutput();
        var colorMode = PlatformServices.SupportsAdvancedColorOutput && deviceSettings.EnableHdrPassthrough
            ? AndroidColorMode.ExtendedLinear
            : AndroidColorMode.Standard;
        VideoOutputDiagnostics.RecordRequest($"Android {colorMode}");
    #else
        VideoOutputDiagnostics.RecordRequest("Android standard color");
    #endif
        Video.AndroidVideoOutput.Install(Context);
        LightMediaRenderer.Contracts.PhotoDecodeAcceleration.Decoder = new Photos.AndroidPhotoDecodeAccelerator(Context);

        return base.CustomizeAppBuilder(builder)
            .AfterSetup(_ => MenuBackdrop.Install(MenuBackdropMode.Default))
            .With(new AndroidPlatformOptions
            {
#if LMR_AVALONIA_FORK
                ColorMode = colorMode,
#endif
            })
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = 512 * 1024 * 1024
            })
            .WithInterFont();
    }

    internal async Task PairRemoteLibraryAsync(RemoteApiPairingInfo pairing)
    {
        RemoteLoginResult login;
        using (var client = new RemoteApiClient(pairing.ServerAddress + "/api/v1/"))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            try
            {
                login = await client.PostAsync<RemotePairingRequest, RemoteLoginResult>(
                    RemoteApiDefaults.PairingPath,
                    new RemotePairingRequest(pairing.Token),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException("The paired server did not answer within 20 seconds.", exception);
            }
        }

        var definition = remoteLibraries.Definitions.FirstOrDefault(existing =>
                string.Equals(existing.Address, pairing.ServerAddress, StringComparison.OrdinalIgnoreCase) &&
                existing.Provider == RemoteLibraryProvider.LightPlayer)
            ?? new RemoteLibraryDefinition(default, string.Empty, pairing.ServerAddress);
        await remoteLibraries.SavePairedAsync(definition with { Token = login.Token }).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            deviceSettings.InitialSetupCompleted = true;
            if (Avalonia.Application.Current is App app)
            {
                app.CompletePairingLaunch();
            }
        });
    }

    /// <summary>
    /// Whether an scRGB surface would reach a panel that can show more than 8 bit sRGB. The
    /// backend falls back on its own, but the setting is only worth offering where it does
    /// something.
    /// </summary>
    private static bool SupportsExtendedLinearOutput()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            return false;
        }

        var configuration = Context?.Resources?.Configuration;
        return configuration?.IsScreenHdr == true || configuration?.IsScreenWideColorGamut == true;
    }

    public override void OnTerminate()
    {
        remoteLibraries?.Dispose();
        remoteLibraries = null!;
        nativeLibrary?.Dispose();
        base.OnTerminate();
    }

    private static void MigrateLegacyRemoteQueueIfNeeded(
        IShellStateStore shellState,
        IReadOnlyList<RemoteLibraryDefinition> definitions)
    {
        if (shellState.PlaybackQueue is not null)
        {
            return;
        }

        var state = new FileKeyValueStore(Path.Combine(AppPaths.DataDirectory, "connection.json"), ownerOnly: true);
        if (state.Get("autoServer") is not { Length: > 0 } legacyId ||
            definitions.FirstOrDefault(definition => definition.StorageKey == legacyId) is not { } definition)
        {
            return;
        }

        var legacy = new RemoteShellStateStore(
            shellState,
            new FileKeyValueStore(Path.Combine(AppPaths.DataDirectory, "Servers", legacyId, "state.json")));
        if (legacy.PlaybackQueue is not { Items.Count: > 0 } queue)
        {
            return;
        }

        shellState.PlaybackQueue = queue with
        {
            Items = queue.Items.Select(item => item with { SourceId = definition.Id.Value }).ToArray(),
        };
        state.Remove("mode");
        state.Remove("autoServer");
    }
}
