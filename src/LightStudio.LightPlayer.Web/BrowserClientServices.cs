using System.Collections.Generic;
using System.Runtime.Versioning;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LocalServices;
using LightStudio.MediaLibraryCore;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.RemoteServices;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal sealed class BrowserClientServices : ILocalServices, IPlaybackHistoryServiceFactory
{
    private BrowserClientServices(IKeyValueStore state)
    {
        Settings = new KeyValueAppSettingsStore(state);
        ShellState = new KeyValueShellStateStore(state);
        Playlists = new KeyValuePlaylistService(state);
        VideoPlaylists = new KeyValuePlaylistService(state, PlaylistKind.Video);

        // Only lrclib is bundled here: netease answers no cross-origin request, so a page cannot
        // reach it however the script is written. The user's own scripts are added from settings.
        Scripts = new JsonLyricSourceService(
            static (content, name) => new BrowserJsDownloadSource(content, name),
            ScriptDirectory,
            ["lrclib"]);
        Lyrics = new LyricsService(Scripts);
        PlaybackHistory = this;

        var platform = new BrowserMultimediaPlatformIO();
        platform.InitializePlatform();
        MultimediaPlatform = platform;
        LibraryLocation = new AppSettingsLibraryLocationService(Settings);

        LibraryScan = new LibraryScanService(LibraryLocation);
        LibraryQuery = new LibraryQueryService(albumGrouping: () => Settings.AlbumGrouping);
        VideoLibraryQuery = new VideoLibraryQueryService();
        FileQueueExpander = new CueFileExpander();
    }

    internal static async Task<BrowserClientServices> CreateAsync(IKeyValueStore state)
    {
        var services = new BrowserClientServices(state);
        await ApplicationServiceBase.App.ConfigureServicesAsync().ConfigureAwait(false);
        await services.Playlists.InitializeAsync().ConfigureAwait(false);
        await services.VideoPlaylists.InitializeAsync().ConfigureAwait(false);
        return services;
    }

    /// <summary>Where the lyric source scripts live in the runtime's filesystem.</summary>
    public static string ScriptDirectory => Path.Combine(AppPaths.DataDirectory, "Scripts");

    public IMultiMediaPlatformIO MultimediaPlatform { get; }
    public IAppSettingsStore Settings { get; }
    public IShellStateStore ShellState { get; }
    public ILibraryLocationService LibraryLocation { get; }
    public ILibraryScanService LibraryScan { get; }
    public ILibraryQueryService LibraryQuery { get; }
    public IVideoLibraryQueryService VideoLibraryQuery { get; }
    public ISubtitleFileBrowser? SubtitleFiles => null;
    public IPlaylistService Playlists { get; }
    public IPlaylistService VideoPlaylists { get; }
    public ILyricSourceService LyricSources => Scripts;

    /// <summary>The registry itself, which the page persists as the user changes it.</summary>
    public JsonLyricSourceService Scripts { get; }
    public ILyricsService Lyrics { get; }
    public IFileQueueExpander FileQueueExpander { get; }
    public IPlaybackHistoryServiceFactory PlaybackHistory { get; }
    public IUpdateCheckService UpdateCheck { get; } = new EmptyUpdateCheckService();

    public IPlaybackHistoryService Create(Func<bool> isIndexing) =>
        new KeyValuePlaybackHistoryService(new BrowserKeyValueStore(), Settings);
}

/// <summary>The folders the user picked in this browser, read back from the device's settings.</summary>
internal sealed class AppSettingsLibraryLocationService(IAppSettingsStore settingsStore) : ILibraryLocationService
{
    public IReadOnlyList<string> GetLibraryFolderPaths() => settingsStore.LibraryFolderPaths;

    public IReadOnlyList<string> GetVideoFolderPaths() => settingsStore.VideoFolderPaths;
}


internal sealed class EmptyUpdateCheckService : IUpdateCheckService
{
    public bool IsSupported => false;
    public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.UpToDate));
}
