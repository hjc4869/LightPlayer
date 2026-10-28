using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightMediaRenderer.Avalonia.Shaders;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;
using LightMediaRenderer.Avalonia.Audio;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Registration point for implementations supplied by a platform head (Desktop / Android).
/// A head populates the relevant hooks before Avalonia framework initialization completes
/// (Android does this in <c>Application.CustomizeAppBuilder</c>); the shared
/// <see cref="AppBootstrapper"/> and UI consult these with safe desktop defaults, so the
/// shared library never depends on any platform-specific type.
/// </summary>
/// <remarks>
/// This mirrors the existing <c>SystemMediaControlsFactory</c> registration pattern and keeps
/// platform-specific dependencies out of the shared application library.
/// </remarks>
public static class PlatformServices
{
    private const string ScaleStageEnvironmentVariable = "LIGHTPLAYER_VIDEO_SCALE_STAGE";
    private static readonly Lazy<FileVideoScaleStage?> configuredStage = new(LoadConfiguredStage);

    static PlatformServices()
    {
        VideoScaleStageConfiguration.StageProvider = packageOverride => string.IsNullOrEmpty(packageOverride)
            ? configuredStage.Value ?? VideoShaderPackages?.GetStage(packageOverride)
            : VideoShaderPackages?.GetStage(packageOverride);
    }

    private static FileVideoScaleStage? LoadConfiguredStage()
    {
        string? manifestPath = Environment.GetEnvironmentVariable(ScaleStageEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(manifestPath))
            return null;

        if (!FileVideoScaleStage.TryLoad(manifestPath, out FileVideoScaleStage? stage, out string error))
        {
            AppLog.Warning("Video.Shader", $"Could not load {ScaleStageEnvironmentVariable}={manifestPath}: {error}");
            return null;
        }

        AppLog.Debug("Video.Shader", $"Loaded pre-scale stage {stage!.Name} from {manifestPath}.");
        return stage;
    }

    /// <summary>
    /// Factory supplied by the application head for persistence, library, playlist, and lyric
    /// services. Local heads register <c>LightStudio.LocalServices</c>; a browser host can register
    /// RPC-backed implementations instead.
    /// </summary>
    public static Func<ILocalServices>? LocalServicesFactory { get; set; }

    /// <summary>Queues a device-local index reset before databases open at the next startup.</summary>
    public static Func<Task>? QueueIndexDatabaseResetAsync { get; set; }

    /// <summary>Refreshes the browser page after an index reset has been queued and the shell stopped.</summary>
    public static Action? ReloadAfterIndexDatabaseReset { get; set; }

    public static string? FfmpegLicenseNotice { get; set; }

    public static int? MaxConcurrentArtworkLoads { get; set; }

    public static INativeMediaLibraryProvider? NativeMediaLibrary { get; set; }

    public static IPhotoIndexService? PhotoIndex { get; set; }

    public static IAudioSeparationService? AudioSeparation { get; set; }

    public static Func<PcmPlaybackRequest, IClientMediaResolver, IPcmPlaybackSource>? PcmPlaybackSourceFactory { get; set; }

    public static Func<string, bool>? TryOpenInNewWindow { get; set; }

    /// <summary>
    /// Whether the host will let a session start making sound right now. A browser keeps audio
    /// output locked until the page has been interacted with, so a video opened onto a freshly
    /// loaded page waits on the play button instead of running silently. Null wherever playback is
    /// never held back.
    /// </summary>
    public static Func<bool>? CanStartPlayback { get; set; }

    /// <summary>Whether this head can pick, drop, and reveal local media files.</summary>
    public static bool SupportsLocalFileAccess { get; set; } = true;

    public static bool UseSimpleLibraryGrouping { get; set; } = OperatingSystem.IsBrowser();

    /// <summary>
    /// Picks media files to open and returns the identifiers the app reads them by. Registered by
    /// the browser head, where a picked file is named by a handle the page was handed rather than
    /// by a path; left null, the shell uses the toolkit's picker and the local paths it returns.
    /// Must be called straight from the click, which is the gesture the browser dialog needs.
    /// </summary>
    public static Func<Task<IReadOnlyList<string>>>? MediaFilePicker { get; set; }

    /// <summary>Whether "Open file" leads anywhere on this head, by either of the two routes above.</summary>
    public static bool CanOpenMediaFiles => SupportsLocalFileAccess || MediaFilePicker is not null;

    /// <summary>Whether this head may change the host's indexed library folders.</summary>
    public static bool CanManageLibraryFolders { get; set; } = true;

    /// <summary>Whether this head shows and persists automatic startup scanning.</summary>
    public static bool SupportsStartupLibraryScan { get; set; } = true;

    /// <summary>Whether this head exposes output sample-rate controls.</summary>
    public static bool SupportsSampleRateSettings { get; set; } = true;

    /// <summary>Whether this head exposes live audio endpoint and backend-option controls.</summary>
    public static bool SupportsAudioOutputSettings { get; set; }

    /// <summary>Whether this head exposes desktop HTTP server configuration.</summary>
    public static bool SupportsRemoteApiSettings { get; set; }

    /// <summary>
    /// Whether the window manager can paint a Mica backdrop behind the app window. Set only by the
    /// Windows desktop head on Windows 11, the one place the effect exists; everywhere else the
    /// shell keeps its opaque page colors and the setting stays hidden.
    /// </summary>
    public static bool SupportsMicaBackdrop { get; set; }

    /// <summary>
    /// Paints the system backdrop behind a native window handle, reporting whether the platform
    /// accepted it. Registered by the Windows desktop head, which asks the compositor for Mica Alt;
    /// left null everywhere else, where the window keeps whatever the toolkit negotiated.
    /// </summary>
    public static Func<IntPtr, bool>? ApplyWindowBackdrop { get; set; }

    /// <summary>
    /// Gives the title bar of a native window handle the height of a toolbar, centering the window
    /// controls on the toolbar the shell draws there, or takes it back. Registered by the macOS
    /// desktop head.
    /// </summary>
    public static Action<IntPtr, bool>? ApplyToolbarTitleBar { get; set; }

    /// <summary>
    /// Observes the window controls the system draws over the top-left corner of a top level, inside
    /// its safe area: Left is the width they take from a row along the top, Top the height they take
    /// from a column along the left. Reports the current value at once and then every change until
    /// disposed. Registered by the iOS head for iPadOS windows.
    /// </summary>
    public static Func<Avalonia.Controls.TopLevel, Action<Avalonia.Thickness>, IDisposable?>? ObserveWindowControls { get; set; }

    /// <summary>
    /// Whether the OS publishes a system accent, offering the Windows or macOS color theme
    /// as the default until the user selects another. Set by the desktop head on those platforms.
    /// </summary>
    public static bool SupportsSystemAccentTheme { get; set; }

    /// <summary>
    /// Whether this head exposes the Wayland backend choice. Set only by the Linux desktop head
    /// when it is running inside a Wayland session, where the choice actually changes anything.
    /// </summary>
    public static bool SupportsWaylandSettings { get; set; }

    public static Action<Avalonia.Controls.Window>? ConfigureWindowChrome { get; set; }

    public static bool SupportsClientSideWindowControls { get; set; }

    /// <summary>
    /// Whether the active windowing backend can create a wide-gamut, extended-range video surface.
    /// Set by application heads whose active backend supports advanced color.
    /// </summary>
    public static bool SupportsAdvancedColorOutput { get; set; }

    public static bool SupportsPhotoDecodePreference { get; set; }

    /// <summary>
    /// Saves the photo viewer's picture in another format or size. Installed heads encode in process; the
    /// browser head registers its media worker, which keeps the work off the page's only thread.
    /// </summary>
    public static IPhotoExportService PhotoExport { get; set; } = new InProcessPhotoExportService();

    /// <summary>
    /// Encodes video screenshots on this process's thread pool. Null where encoding would have to run on
    /// the page's only thread, which hides the feature.
    /// </summary>
    public static IVideoScreenshotEncoder? VideoScreenshotEncoder { get; set; } = new InProcessVideoScreenshotEncoder();

    /// <summary>
    /// Host-owned navigation history the shell mirrors its navigation into, so the host's back,
    /// forward and reload drive the app. Registered by the browser head (URL hash fragment) only;
    /// left null on heads that own their own back stack and startup page.
    /// </summary>
    public static INavigationHistory? NavigationHistory { get; set; }

    /// <summary>
    /// Whether the host already offers back navigation of its own -- Android's system back gesture,
    /// the browser's toolbar -- so the shell and its detail pages leave the affordance to it instead
    /// of drawing their own back buttons.
    /// </summary>
    public static bool HostProvidesBackNavigation => OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser();

    /// <summary>
    /// Whether the shell offers its Apple layout here: a pinned sidebar, a toolbar with the navigation
    /// and view controls, and a floating playback bar. Selected by the native Apple UI style preference.
    /// </summary>
    public static bool SupportsAppleChrome => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ||
        SupportsClientSideWindowControls;

    public static bool IsAndroid => OperatingSystem.IsAndroid();

    /// <summary>
    /// Origin the client was served from, which a browser page is the only thing it may call.
    /// Registered by the browser head; left null on heads that reach a server directly.
    /// </summary>
    public static string? WebHostAddress { get; set; }

    /// <summary>
    /// Whether <see cref="WebHostAddress"/> only relays calls to servers a client names instead of
    /// serving a library of its own. Null when the host does not implement the server API or has
    /// not answered its probe.
    /// </summary>
    public static bool? WebHostProxyOnly { get; set; }

    /// <summary>
    /// Completes once the host has answered its server probe or the probe has failed.
    /// Registered by the browser head; left null on heads that never probe an origin of their own.
    /// </summary>
    public static Task? WebHostProbe { get; set; }

    public static IBrowserUpdateService? BrowserUpdates { get; set; }

    /// <summary>
    /// Presents the add/edit form for a remote library with the host's own controls instead of the
    /// in-app view. Registered by the browser head, whose HTML form the password manager can fill
    /// and save; left null everywhere else, where
    /// <c>Views/Pages/RemoteLibraryEditorView.axaml</c> is shown as usual.
    /// </summary>
    public static Func<ViewModels.Pages.RemoteLibraryEditorViewModel, Task>? RemoteLibraryEditorPresenter { get; set; }

    /// <summary>Desktop-owned controller used to apply HTTP server settings immediately.</summary>
    public static IRemoteApiServerController? RemoteApiServerController { get; set; }

    /// <summary>
    /// Platform observer for proactively staging playback state before host lifecycle events.
    /// Browser registers this because unload handlers cannot call synchronously into managed code.
    /// </summary>
    public static IPlaybackStateObserver? PlaybackStateObserver { get; set; }

    /// <summary>
    /// App data path
    /// </summary>
    public static string AppDataPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// Cache path
    /// </summary>
    public static string CachePath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// Factory for the platform's <see cref="IMultiMediaPlatformIO"/>. When null, the shared
    /// bootstrapper falls back to the desktop filesystem implementation.
    /// </summary>
    public static Func<IMultiMediaPlatformIO>? MultimediaPlatformIOFactory { get; set; }

    /// <summary>
    /// Platform hook for persisting durable access to folders that have no local filesystem
    /// path (Android SAF). Null on desktop, where picked folders always expose a local path.
    /// </summary>
    public static ILibraryFolderAccess? LibraryFolderAccess { get; set; }

    /// <summary>
    /// Platform hook for the "Open containing folder" action. Left null, the shared bootstrapper
    /// falls back to <see cref="DesktopFileRevealService"/>, which shell-executes the directory
    /// path; heads whose media paths are not filesystem paths (Android content URIs) register their
    /// own implementation.
    /// </summary>
    public static IFileRevealService? FileRevealService { get; set; }

    /// <summary>
    /// Platform hook for "Open with" and "Share". Left null, the shared bootstrapper falls back to
    /// <see cref="DesktopFileShareService"/>, which shell-executes the file and has no share sheet
    /// to offer it to; Android registers its own, which goes through an intent chooser.
    /// </summary>
    public static IFileShareService? FileShareService { get; set; }

    /// <summary>
    /// Cache of remotely streamed media blocks this head keeps on the device, surfaced in the
    /// storage settings. Left null by heads that read their media locally.
    /// </summary>
    public static IStorageCache? MediaBlockCache { get; set; }

    /// <summary>
    /// Cache of album art and video previews downloaded from a remote server, surfaced in the
    /// storage settings. Left null when the images are read locally, or when the host's own HTTP
    /// stack caches them (the browser).
    /// </summary>
    public static IStorageCache? ImageCache { get; set; }

    public static IStorageCache? VideoPreviewCache { get; set; }

    public static IStorageCache? PhotoPreviewCache { get; set; }

    /// <summary>
    /// Permanent registry of this device and every configured remote library, local source included.
    /// </summary>
    public static ILibrarySourceRegistry? LibrarySources { get; set; }

    /// <summary>Definition and connection manager backing the remote-libraries settings UI.</summary>
    public static IRemoteLibraryManager? RemoteLibraryManager { get; set; }

    /// <summary>Host-owned source-qualified video resume state; browser supplies IndexedDB storage.</summary>
    public static IVideoPlaybackStateStore? VideoPlaybackStateStore { get; set; }

    /// <summary>Host-owned recently viewed video folders and videos; browser supplies IndexedDB storage.</summary>
    public static IVideoHistoryStore? VideoHistoryStore { get; set; }

    /// <summary>Device-local imported video pre-scale packages and their priority settings.</summary>
    public static VideoShaderPackageCatalog? VideoShaderPackages { get; set; }

    /// <summary>
    /// Puts the host into or out of full screen. Registered by heads whose windowing is not the
    /// classic desktop one (Android); left null, the shell drives the desktop window itself.
    /// </summary>
    public static Action<bool>? FullScreenRequested { get; set; }

    /// <summary>
    /// Reports full screen being entered or left by the host itself rather than by the app -- the
    /// browser's Esc key. Registered by the shell; raised by heads whose host can do this.
    /// </summary>
    public static Action<bool>? FullScreenChanged { get; set; }

    /// <summary>
    /// Detaches video playback from the app's own window: a small always-on-top window on the
    /// desktop, document picture-in-picture in the browser, the shrunken activity on Android. Left
    /// null where the platform cannot do it, which hides the page's button.
    /// </summary>
    public static IPictureInPictureHost? PictureInPicture { get; set; }

    /// <summary>
    /// Asks the platform to orient the screen for a picture of the given width/height ratio, or to
    /// release an earlier request when the ratio is 0. Only meaningful where the OS rotates the
    /// display (Android); left null elsewhere.
    /// </summary>
    public static Action<double>? PreferredOrientationRequested { get; set; }

    /// <summary>
    /// Holds the display awake while video is playing, and releases it again. Every head that has a
    /// screen registers this -- there is no shared way to do it -- and the calls are balanced, so an
    /// implementation may simply follow the flag rather than count requests. Called on the UI thread.
    /// </summary>
    public static Action<bool>? KeepScreenOnRequested { get; set; }

    /// <summary>
    /// Name of the GitHub release asset this build installs (the platform's Flatpak bundle, MSIX
    /// sideload archive, or signed APK). A head sets this only when the running platform and
    /// architecture have a published package; left null, the update check is unsupported and hidden
    /// from the UI. Store releases always return null.
    /// </summary>
#if STORE_RELEASE
    public static string? UpdateAssetName { get => null; set { } }
#else
    public static string? UpdateAssetName { get; set; }
#endif
}
