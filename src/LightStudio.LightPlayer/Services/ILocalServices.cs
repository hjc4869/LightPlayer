using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Backend services required by the local application host. Browser and remote
/// hosts can provide the same contracts with RPC-backed implementations.
/// </summary>
public interface ILocalServices
{
    IMultiMediaPlatformIO MultimediaPlatform { get; }

    IAppSettingsStore Settings { get; }

    IShellStateStore ShellState { get; }

    ILibraryLocationService LibraryLocation { get; }

    ILibraryScanService LibraryScan { get; }

    ILibraryQueryService LibraryQuery { get; }

    /// <summary>Read access to the video library, which is indexed and browsed separately from music.</summary>
    IVideoLibraryQueryService VideoLibraryQuery { get; }

    IPhotoIndexService? PhotoIndex => null;

    IAudioSeparationService? AudioSeparation => null;

    /// <summary>
    /// Browses subtitle files where the media lives, or null when that is this device and the
    /// platform's own file picker will do.
    /// </summary>
    ISubtitleFileBrowser? SubtitleFiles { get; }

    IPlaylistService Playlists { get; }

    /// <summary>
    /// The video playlists, stored separately from the music ones so neither kind can leak into
    /// the other's pickers or the music queue.
    /// </summary>
    IPlaylistService VideoPlaylists { get; }

    ILyricSourceService LyricSources { get; }

    ILyricsService Lyrics { get; }

    IFileQueueExpander FileQueueExpander { get; }

    IPlaybackHistoryServiceFactory PlaybackHistory { get; }

    IUpdateCheckService UpdateCheck { get; }
}