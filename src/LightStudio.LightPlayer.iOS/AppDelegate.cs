using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.iOS;
using Avalonia.Platform.Storage;
using Foundation;
using LightMediaRenderer.Avalonia.Audio;
using LightMediaRenderer.Avalonia.Rendering;
using LightStudio.FfmpegShim;
using LightStudio.Inference;
using LightStudio.LightPlayer.Desktop.Apple;
using LightStudio.LightPlayer.iOS.Playback;
using LightStudio.LightPlayer.iOS.Services;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.LightPlayer.Strings;
using LightStudio.LocalServices;
using LightStudio.Logging;
using LightStudio.RemoteServices;
using UIKit;

namespace LightStudio.LightPlayer.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<App>
{
    private RemoteLibraryManager? remoteLibraries;
    private IosStorage? storage;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        PlatformServices.AppDataPath = NSFileManager.DefaultManager.GetUrls(
            NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User)[0].Path!;
        PlatformServices.CachePath = NSFileManager.DefaultManager.GetUrls(
            NSSearchPathDirectory.CachesDirectory, NSSearchPathDomain.User)[0].Path!;
        AppLog.ConfigureClient(AppPaths.DataDirectory);
        AppLog.Information("iOS.Startup", "Starting Light Player iOS client.");

        FfmpegLog.Attach();
        SQLitePCL.Batteries_V2.Init();
        IosPhotoVectorExtension.Register();
        AudioOutputDeviceFactory.Register(
            static (format, bufferTarget) => new AudioQueueOutputDevice(format, bufferTarget),
            asyncAudioOutputFactory: AudioQueueOutputDevice.CreateAsync);
        SystemMediaControlsFactory.Register(static () => new IosSystemMediaControls());
        VideoToolboxSurfaceImporter.Register();
        if (OperatingSystem.IsIOSVersionAtLeast(16))
            VideoToolboxVideoScaler.Register();

        var deviceSettings = new JsonAppSettingsStore();
        LocalizedStrings.ApplyLanguage(deviceSettings.InterfaceLanguage);
        storage = new IosStorage(deviceSettings);
        PlatformServices.LibraryFolderAccess = storage;
        PlatformServices.MediaFilePicker = storage.PickMediaAsync;
        PlatformServices.FileShareService = storage;
        PlatformServices.SupportsSystemAccentTheme = true;
        ((IAvaloniaAppDelegate)this).Activated += OnActivated;
        var localServices = new LocalServices.LocalServices(
            static (content, name) => new MediaLibraryCore.Lyrics.JsDownloadSource(content, name),
            PlatformServices.MultimediaPlatformIOFactory,
            deviceSettings,
            new JsonShellStateStore(),
            inferenceProvider: new OnnxInferenceProvider());
        PlatformServices.LocalServicesFactory = () => localServices;
        remoteLibraries = new RemoteLibraryManager(
            AppPaths.DataDirectory, AppPaths.CacheDirectory, localServices);
        PlatformServices.LibrarySources = remoteLibraries.Registry;
        PlatformServices.RemoteLibraryManager = remoteLibraries;
        _ = remoteLibraries.ConnectEnabledAsync();
        PlatformServices.KeepScreenOnRequested = keepOn => UIApplication.SharedApplication.IdleTimerDisabled = keepOn;
        PlatformServices.ObserveWindowControls = IosWindowControls.Observe;

#if LMR_AVALONIA_FORK
        PlatformServices.SupportsAdvancedColorOutput = true;
        if (deviceSettings.EnableHdrPassthrough)
        {
            builder.With(new iOSPlatformOptions
            {
                ColorMode = iOSColorMode.ExtendedLinear,
            });

            VideoOutputDiagnostics.RecordRequest($"iOS {iOSColorMode.ExtendedLinear}");
        }
#endif

        return base.CustomizeAppBuilder(builder)
            .AfterSetup(_ => MenuBackdrop.Install(MenuBackdropMode.Default))
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 512 * 1024 * 1024 })
            .WithInterFont();
    }

    [Export("applicationWillTerminate:")]
    public void WillTerminate(UIApplication application)
    {
        remoteLibraries?.Dispose();
        storage?.Dispose();
        AppLog.Shutdown();
    }

    private async void OnActivated(object? sender, ActivatedEventArgs args)
    {
        if (args is not FileActivatedEventArgs activation || storage is null) return;
        try
        {
            var paths = await storage.ImportMediaAsync(activation.Files.OfType<IStorageFile>().ToArray());
            if (paths.Count == 0 || Avalonia.Application.Current is not App app) return;
            new JsonAppSettingsStore().InitialSetupCompleted = true;
            app.CompletePairingLaunch();
            app.OpenFiles(paths);
        }
        catch (Exception exception)
        {
            AppLog.Error("iOS.Activation", "Could not import the opened media files.", exception);
        }
    }
}