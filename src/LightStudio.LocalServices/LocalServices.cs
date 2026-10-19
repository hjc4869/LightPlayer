using System;
using System.Collections.Generic;
using LightStudio.LocalServices;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.MediaLibraryCore.Lyrics;
#if PHOTO_INDEXING
using LightStudio.LocalServices.Photos;
#endif

namespace LightStudio.LocalServices;

/// <summary>Local filesystem and database-backed services for installed application heads.</summary>
public sealed class LocalServices : ILocalServices, IPlaybackHistoryServiceFactory
{
    /// <param name="settings">
    /// The head's settings store. Passing the instance the head already uses keeps a single writer
    /// for <c>settings.json</c> when the library can also be served by a remote server.
    /// </param>
    public LocalServices(
        Func<string, string, IJsDownloadSource> lyricSourceFactory,
        Func<IMultiMediaPlatformIO>? multimediaPlatformIOFactory = null,
        IAppSettingsStore? settings = null,
        IShellStateStore? shellState = null,
        string? nativeLibraryDirectory = null,
        IInferenceProvider? inferenceProvider = null)
    {
        Settings = settings ?? new JsonAppSettingsStore();
        ShellState = shellState ?? new JsonShellStateStore();
        LibraryLocation = new AppSettingsLibraryLocationService(Settings);

        var platform = multimediaPlatformIOFactory?.Invoke() ?? new DesktopMultimediaPlatformIO();
        platform.InitializePlatform();
        MultimediaPlatform = platform;
        PlatformServices.QueueIndexDatabaseResetAsync = async () =>
        {
            await platform.PrepareLibraryDatabaseAsync().ConfigureAwait(false);
            await IndexDatabaseReset.QueueAsync(platform.LocalDataPath).ConfigureAwait(false);
        };

        var thumbnails = ThumbnailMemoryCache.Shared;
        var thumbnailStore = new LocalThumbnailStore(AppPaths.CacheSubdirectory("Previews"));
        thumbnails.PersistentStore = thumbnailStore;
        PlatformServices.VideoPreviewCache = thumbnailStore.GetCache(thumbnails, ThumbnailCacheKind.VideoPreview);
        PlatformServices.PhotoPreviewCache = thumbnailStore.GetCache(thumbnails, ThumbnailCacheKind.Photo);

        LibraryScan = new LibraryScanService(LibraryLocation, Settings);
        LibraryQuery = new LibraryQueryService(albumGrouping: () => Settings.AlbumGrouping);
        VideoLibraryQuery = new VideoLibraryQueryService();
        if (inferenceProvider?.AudioSeparation is { } audioSeparation)
        {
            AudioSeparation = new AudioSeparationService(audioSeparation);
            _ = AudioSeparation.InitializeAsync();
        }
#if PHOTO_INDEXING
        if (inferenceProvider is not null)
        {
            PhotoIndex = new PhotoIndexService(platform, LibraryLocation, inferenceProvider,
                nativeLibraryDirectory: nativeLibraryDirectory, nativeLibrary: PlatformServices.NativeMediaLibrary, settingsStore: Settings);
            LibraryScan.ScanCompleted += (_, result) =>
            {
                if (!result.Failed && PhotoIndex.Status.Options.Enabled)
                    _ = PhotoIndex.IndexAsync();
            };
            if (PhotoIndex.Status.Options.Enabled)
                _ = PhotoIndex.InitializeAsync();
        }
#endif
        var playlists = new JsonPlaylistService();
        playlists.InitializeAsync().GetAwaiter().GetResult();
        Playlists = playlists;
        var videoPlaylists = new JsonPlaylistService(PlaylistKind.Video);
        videoPlaylists.InitializeAsync().GetAwaiter().GetResult();
        VideoPlaylists = videoPlaylists;
        LyricSources = new JsonLyricSourceService(lyricSourceFactory);
        Lyrics = new LyricsService(LyricSources);
        FileQueueExpander = new CueFileExpander();
        PlaybackHistory = this;
        UpdateCheck = new GitHubUpdateCheckService(PlatformServices.UpdateAssetName);
    }

    public IAppSettingsStore Settings { get; }

    public IMultiMediaPlatformIO MultimediaPlatform { get; }

    public IShellStateStore ShellState { get; }

    public ILibraryLocationService LibraryLocation { get; }

    public ILibraryScanService LibraryScan { get; }

    public ILibraryQueryService LibraryQuery { get; }

    public IVideoLibraryQueryService VideoLibraryQuery { get; }

    public IAudioSeparationService? AudioSeparation { get; }

#if PHOTO_INDEXING
    public IPhotoIndexService? PhotoIndex { get; }
#endif

    /// <summary>Null: the media is on this device, so its subtitles are picked with the platform's own dialog.</summary>
    public ISubtitleFileBrowser? SubtitleFiles => null;

    public IPlaylistService Playlists { get; }

    public IPlaylistService VideoPlaylists { get; }

    public ILyricSourceService LyricSources { get; }

    public ILyricsService Lyrics { get; }

    public IFileQueueExpander FileQueueExpander { get; }

    public IPlaybackHistoryServiceFactory PlaybackHistory { get; }

    public IUpdateCheckService UpdateCheck { get; }

    public IPlaybackHistoryService Create(Func<bool> isIndexing) =>
        new DevicePlaybackHistoryService(Settings, isIndexing);
}

internal sealed class AppSettingsLibraryLocationService(IAppSettingsStore settingsStore) : ILibraryLocationService
{
    public IReadOnlyList<string> GetLibraryFolderPaths() =>
        settingsStore.NativeLibrary.IsFileSystemEnabled(LibraryMediaCategory.Music) ? settingsStore.LibraryFolderPaths : [];

    public IReadOnlyList<string> GetVideoFolderPaths() =>
        settingsStore.NativeLibrary.IsFileSystemEnabled(LibraryMediaCategory.Video) ? settingsStore.VideoFolderPaths : [];
}