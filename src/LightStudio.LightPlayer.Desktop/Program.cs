using Avalonia;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LightStudio.LightPlayer.Services;
using System.Runtime.InteropServices;
using LightStudio.FfmpegShim;
using LightStudio.LightPlayer.Desktop;
using LightStudio.LocalServices;
using LightStudio.Inference;
#if WINDOWS
using LightStudio.LightPlayer.Desktop.Windows;
#elif LINUX
using LightStudio.LightPlayer.Desktop.Linux;
#elif MACOS
using LightStudio.LightPlayer.Desktop.Apple;
#endif
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.LightPlayer.Desktop.Services;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Avalonia.Rendering;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.RemoteApi;
using LightStudio.RemoteServices;
#if LINUX
using LightStudio.LightPlayer.Desktop.Services.Mpris;
using LightStudio.LightPlayer.Desktop.Services.SingleInstance;
#endif

namespace LightStudio.LightPlayer;

sealed class Program
{
#if WINDOWS
    // The one instance Avalonia reads: the color mode is chosen at startup, the menu blur clip per menu.
    private static readonly Win32PlatformOptions Win32Options = new();
#endif

    internal static void InitializeSqlite()
    {
#if WINDOWS
        // Windows: the OS-provided winsqlite3.dll.
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
#elif LINUX
        // Linux: use the OS-provided system SQLite. Distributions (and the
        // freedesktop Flatpak runtime) commonly ship only the versioned soname
        // (libsqlite3.so.0) without the unversioned "libsqlite3.so" dev symlink,
        // which the default P/Invoke lookup for "sqlite3" would miss. Map the
        // import to the versioned soname before the provider is used.
        NativeLibrary.SetDllImportResolver(
            typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
            static (name, assembly, searchPath) =>
            {
                if (name == "sqlite3")
                {
                    foreach (var candidate in new[] { "libsqlite3.so.0", "libsqlite3.so" })
                    {
                        if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
                        {
                            return handle;
                        }
                    }
                }

                return IntPtr.Zero;
            });

        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#elif MACOS
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        throw new NotSupportedException();
#endif
#if SQLITE_VEC_STATIC
        LocalServices.Photos.PhotoVectorExtension.RegisterStatic();
#endif
    }

    public static ISingleInstanceService CreateSingleInstanceService(bool usesPackagedIdentity = false)
    {
        try
        {
#if !DEBUG
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                var appId = usesPackagedIdentity ? $"{AppInfo.AppId}.MSIX" : AppInfo.AppId;
                return new WindowsSingleInstanceService(appId);
            }
#endif
#if LINUX
            if (OperatingSystem.IsLinux())
            {
                return new DBusSingleInstanceService(AppInfo.AppId, AppInfo.ObjectPath);
            }
#endif
#if MACOS
            if (OperatingSystem.IsMacOS())
            {
                return new MacSingleInstanceService(AppInfo.AppId);
            }
#endif
#endif
            return new NoopSingleInstanceService();
        }
        catch
        {
            return new NoopSingleInstanceService();
        }
    }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var usesPackagedStorage = false;
#if WINDOWS
        usesPackagedStorage = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) &&
            WindowsApplicationDataPaths.TryConfigure();
#endif
        AppLog.ConfigureConsole();
        AppLog.Information("Desktop.Startup", "Starting Light Player desktop client.");
#if WINDOWS
        if (usesPackagedStorage)
        {
            PlatformServices.MultimediaPlatformIOFactory =
                () => new DesktopMultimediaPlatformIO(AppPaths.DataDirectory);
            AppLog.Information(
                "Desktop.Startup",
                $"Using Windows package storage; data={AppPaths.DataDirectory}; cache={AppPaths.CacheDirectory}.");
        }
#endif
        // Several libav* failures - hardware frame allocation above all - reach managed code as a
        // bare errno, so the native line is the only usable description of the cause.
        FfmpegLog.Attach();

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            WindowsAudioPlaybackBackend.Register();
            PlatformServices.SupportsAudioOutputSettings = true;

            // Lets hardware-decoded video be drawn out of the graphics memory it was decoded into.
            Direct3DSurfaceImporter.Register();
            HardwareDecoderProfiles.Register(new Direct3D11HardwareDecoderProfile());
        }
#elif LINUX
        PulseAudioPlaybackBackend.Register();
        PlatformServices.SupportsAudioOutputSettings = true;
        VaapiSurfaceImporter.Register();
        HardwareDecoderProfiles.Register(new VaapiHardwareDecoderProfile());
#elif MACOS
        VideoSurfaceTransfers.Bt709Video = VideoSurfaceTransfer.Power(1.9609375f);
        CoreAudioPlaybackBackend.Register();
        PlatformServices.SupportsAudioOutputSettings = true;
    VideoToolboxSurfaceImporter.Register();
        VideoToolboxVideoScaler.Register();
#endif

#if WINDOWS
        SystemMediaControlsFactory.Register(() => new WindowsSystemMediaControls());

        // Explorer can open the folder with the track already selected, which shell-executing the
        // directory path (the shared default) cannot do.
        PlatformServices.FileRevealService = new WindowsFileRevealService();
        PlatformServices.KeepScreenOnRequested = WindowsDisplaySleepBlocker.SetKeepScreenOn;

        // Mica arrived with Windows 11 (build 22000); asking for it on Windows 10 leaves the window
        // with no backdrop at all, so the shell keeps its opaque surfaces there.
        PlatformServices.SupportsMicaBackdrop = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        PlatformServices.ApplyWindowBackdrop = WindowsSystemBackdrop.Apply;

        // Windows users pick one accent for the whole desktop and expect apps to follow it, so the
        // app offers a theme derived from it and starts on that rather than on a shipped family.
        PlatformServices.SupportsSystemAccentTheme = true;
#elif LINUX
        var flatpakId = Environment.GetEnvironmentVariable("FLATPAK_ID");
        var suffix = !string.IsNullOrEmpty(flatpakId)
            ? flatpakId
            : $"LightStudioLightPlayer.instance{Environment.ProcessId}";
        SystemMediaControlsFactory.Register(() => new MprisSystemMediaControls("Light Media Center", suffix));
        var screenSaver = new ScreenSaverInhibitor("Light Media Center", "Playing video");
        PlatformServices.KeepScreenOnRequested = screenSaver.SetKeepScreenOn;
#elif MACOS
        SystemMediaControlsFactory.Register(() => new AppleSystemMediaControls());
        PlatformServices.KeepScreenOnRequested = DisplaySleepAssertion.SetKeepScreenOn;
        PlatformServices.ApplyToolbarTitleBar = ToolbarTitleBar.Apply;
        PlatformServices.FileRevealService = new MacFileRevealService();
        PlatformServices.SupportsSystemAccentTheme = true;
        NativeContextMenu.Install(new AppleNativeContextMenu());
#endif
        var activationTargets = FileActivation.ExtractTargets(args);
        PlatformServices.TryOpenInNewWindow = StandaloneFileWindow.TryOpen;
        var standaloneTargets = activationTargets.Where(StandaloneFileWindow.IsVisualFile).ToArray();
        if (standaloneTargets.Length > 0)
        {
            foreach (var target in standaloneTargets.Skip(1))
                StandaloneFileWindow.TryOpen(target);
            var remaining = activationTargets.Except(standaloneTargets).ToArray();
            if (remaining.Length > 0)
                StandaloneFileWindow.StartProcess(remaining);
            var settings = JsonAppSettingsStore.LoadSnapshot();
            Strings.LocalizedStrings.ApplyLanguage(settings.InterfaceLanguage);
            var path = Path.GetFullPath(standaloneTargets[0]);
            App.StandaloneWindowFactory = () => new StandaloneFileWindow(path, settings);
            StartDesktopLifetime(args, settings);
            AppLog.Shutdown();
            return;
        }

        if (args.Contains("--standalone", StringComparer.Ordinal))
        {
            AppLog.Error("Desktop.Activation", "No supported local photo or video was supplied.");
            AppLog.Shutdown();
            return;
        }

        var deviceSettings = new JsonAppSettingsStore();
        Strings.LocalizedStrings.ApplyLanguage(deviceSettings.InterfaceLanguage);
        RemoteLibraryManager? remoteLibraries = null;
        RemoteApiServerController? remoteApi = null;
        App.InitializeDesktopServices = targets =>
        {
            var singleInstance = CreateSingleInstanceService(usesPackagedStorage);
            if (!singleInstance.TryActivate(targets))
            {
                AppLog.Information("Desktop.Startup", "Forwarded activation to the existing instance.");
                singleInstance.Dispose();
                return false;
            }

            App.SingleInstance = singleInstance;
            AppLog.ConfigureClient(AppPaths.DataDirectory);
            InitializeSqlite();
            ConfigureUpdateAsset(usesPackagedStorage);
            var deviceShellState = new JsonShellStateStore();
            var localServices = new LocalServices.LocalServices(
                static (content, name) => new MediaLibraryCore.Lyrics.JsDownloadSource(content, name),
                PlatformServices.MultimediaPlatformIOFactory,
                deviceSettings,
                deviceShellState,
#if WINDOWS
                inferenceProvider: new WindowsInferenceProvider());
#else
                inferenceProvider: new OnnxInferenceProvider());
#endif
            PlatformServices.LocalServicesFactory = () => localServices;
            remoteLibraries = new RemoteLibraryManager(
                AppPaths.DataDirectory, AppPaths.CacheDirectory, localServices);
            MigrateLegacyRemoteQueueIfNeeded(
                Path.Combine(AppPaths.DataDirectory, "connection.json"),
                deviceShellState, remoteLibraries.Definitions);
            PlatformServices.LibrarySources = remoteLibraries.Registry;
            PlatformServices.RemoteLibraryManager = remoteLibraries;
            _ = remoteLibraries.ConnectEnabledAsync();
            remoteApi = new RemoteApiServerController(
                localServices, localServices.MultimediaPlatform,
                hardwareSettings: () => HardwareDecoderProfiles.Compose(deviceSettings),
                tokenSecret: RemoteApiTokenSecret.LoadOrCreate(AppPaths.DataDirectory));
            PlatformServices.SupportsRemoteApiSettings = true;
            PlatformServices.RemoteApiServerController = remoteApi;
            remoteApi.Apply(deviceSettings.RemoteApiSettings);
            return true;
        };

        try
        {
            StartDesktopLifetime(args, deviceSettings);
        }
        finally
        {
            remoteApi?.Dispose();
            remoteLibraries?.Dispose();
            App.SingleInstance?.Dispose();
            AppLog.Information("Desktop.Startup", "Desktop client stopped.");
            AppLog.Shutdown();
        }
    }

    private static void StartDesktopLifetime(string[] args, IAppSettingsStore deviceSettings)
    {
        PlatformServices.SupportsPhotoDecodePreference = true;
        LightStudio.FfmpegShim.Media.PhotoDecodeResources.PreferThroughput = deviceSettings.PreferPhotoDecodePerformance;
        var app = BuildAvaloniaApp();

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            bool supportsAdvancedColorOutput = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
#if LMR_AVALONIA_FORK
            PlatformServices.SupportsAdvancedColorOutput = supportsAdvancedColorOutput;
#endif
            if (supportsAdvancedColorOutput && deviceSettings.EnableHdrPassthrough)
                DesktopVideoBootstrap.EnableWindowsExtendedLinear(Win32Options);
            else
                VideoOutputDiagnostics.RecordRequest("Win32 standard color");
        }
#elif LINUX
        if (OperatingSystem.IsLinux() && IsWaylandSession())
        {
            PlatformServices.SupportsWaylandSettings = true;
            if (deviceSettings.WaylandMode == WaylandMode.Auto)
            {
#if LMR_AVALONIA_FORK
                PlatformServices.SupportsAdvancedColorOutput = true;
#endif
                DesktopVideoBootstrap.EnableWayland(app, deviceSettings.EnableHdrPassthrough);
                PlatformServices.ConfigureWindowChrome = ConfigureWaylandWindowChrome;
            }
            else
            {
                VideoOutputDiagnostics.RecordRequest("nothing (Wayland off)");
            }
        }
        else
        {
            VideoOutputDiagnostics.RecordRequest("nothing (not Wayland)");
        }
#elif MACOS && LMR_AVALONIA_FORK
        PlatformServices.SupportsAdvancedColorOutput = true;
        if (deviceSettings.EnableHdrPassthrough)
        {
            app.With(new AvaloniaNativePlatformOptions
            {
                ColorMode = AvaloniaNativeColorMode.ExtendedLinear,
            });
            VideoOutputDiagnostics.RecordRequest($"macOS {AvaloniaNativeColorMode.ExtendedLinear}");
        }
        else
        {
            VideoOutputDiagnostics.RecordRequest("macOS standard color");
        }
#endif
        
        app.StartWithClassicDesktopLifetime(args);
    }

    private static void MigrateLegacyRemoteQueueIfNeeded(
        string connectionPath,
        IShellStateStore shellState,
        IReadOnlyList<RemoteLibraryDefinition> definitions)
    {
        if (shellState.PlaybackQueue is not null)
        {
            return;
        }

        var state = new FileKeyValueStore(connectionPath, ownerOnly: true);
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

#if LINUX
    private static void ConfigureWaylandWindowChrome(Avalonia.Controls.Window window) =>
        PlatformServices.SupportsClientSideWindowControls =
            window.PlatformImpl is { NeedsManagedDecorations: true } implementation &&
            implementation.TryGetFeature(typeof(Avalonia.Controls.Platform.IWaylandOptionsToplevelImplFeature)) is not null;

    private static bool IsWaylandSession() =>
        string.Equals(
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            "wayland",
            StringComparison.OrdinalIgnoreCase);
#endif

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var app = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .AfterSetup(_ => InstallMenuBackdrop())
#if MACOS
            .AfterSetup(_ => MacSystemTextFont.Register())
#endif
            // EGL first on X11 because it is the only way a hardware-decoded frame can reach the
            // renderer without being read back: GLX has no display to import a dma-buf into.
            // Wayland is always EGL, and GLX stays as the fallback where EGL will not start.
            .With(new X11PlatformOptions
            {
                RenderingMode = [X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software],
            })
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = 512 * 1024 * 1024
            })
            .WithInterFont()
            .LogToTrace();
#if WINDOWS
        app.With(Win32Options);
#endif
        return app;
    }

    private static void InstallMenuBackdrop()
    {
#if WINDOWS
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
#if LMR_AVALONIA_FORK
            bool linearBlending = Win32Options.ColorMode == Win32ColorMode.ExtendedLinear;
#else
            const bool linearBlending = false;
#endif
            MenuBackdrop.Install(
                MenuBackdropMode.WinUI,
                (windowHandle, cornerRadius) => WindowsMenuFrame.Apply(Win32Options, windowHandle, cornerRadius),
                linearBlending);
            return;
        }
#endif
        MenuBackdrop.Install(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? MenuBackdropMode.Native
                : MenuBackdropMode.Overlay);
    }

    // Configure the installable release asset for each desktop package we publish. Packaged and
    // unpackaged Windows installs stay on their respective channels; Linux updates are meaningful
    // only from inside a Flatpak. Unknown platforms and architectures leave the asset name null.
    private static void ConfigureUpdateAsset(bool usesPackagedStorage)
    {
        if (OperatingSystem.IsWindows())
        {
            PlatformServices.UpdateAssetName = usesPackagedStorage
                ? "LightPlayerMSIX.zip"
                : RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "LightPlayerSetup-x64.exe",
                    Architecture.Arm64 => "LightPlayerSetup-arm64.exe",
                    _ => null,
                };
        }
        else if (OperatingSystem.IsLinux() && IsRunningUnderFlatpak())
        {
            PlatformServices.UpdateAssetName = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "im.hjc.LightPlayer-x86_64.flatpak",
                Architecture.Arm64 => "im.hjc.LightPlayer-aarch64.flatpak",
                _ => null,
            };
        }
        else if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            PlatformServices.UpdateAssetName = "LightPlayer-osx-arm64.zip";
        }
    }

    private static bool IsRunningUnderFlatpak() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID"))
        || File.Exists("/.flatpak-info");
}
