using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// What the video library page asks the shell to do on its behalf. Folder navigation goes through
/// the shell rather than staying inside the page so each folder is its own navigation entry, which
/// is what makes Back restore the folder above it (and its scroll position) instead of the page
/// having to unwind itself.
/// </summary>
public sealed record MediaPageCallbacks : IVideoSelectionCallbacks
{
    public Action<LibraryViewSettings>? ChangeViewSettings { get; init; }

    /// <summary>
    /// Opens a video in the player page, with the folder it was picked from as its playlist and an
    /// explicit choice of whether to use its saved position.
    /// </summary>
    public required Action<Sourced<DbVideoFile>, IReadOnlyList<Sourced<DbVideoFile>>, bool> OpenVideo { get; init; }

    /// <summary>Navigates to another video library page for the given folder; null is the library root.</summary>
    public required Action<VideoFolderReference?> OpenFolder { get; init; }

    /// <summary>
    /// Opens a picture in the photo viewer, or null on heads with no local file access. Pictures
    /// are listed from the folder rather than the index, so a path is all this carries. The second
    /// argument is every picture the page is showing, in display order, so the viewer can be paged
    /// sideways through them.
    /// </summary>
    public Action<PhotoFileReference, IReadOnlyList<PhotoFileReference>>? OpenPhoto { get; init; }

    /// <summary>Shows the picture's file properties, or null on heads without a properties dialog.</summary>
    public Action<PhotoFileReference>? ShowPhotoProperties { get; init; }

    /// <summary>Reveals the picture in the platform file manager, or null where that is unsupported.</summary>
    public Action<PhotoFileReference>? OpenPhotoContainingFolder { get; init; }

    /// <summary>
    /// Leaves the current folder for one of its ancestors (a breadcrumb step, Go up); null is the
    /// library root. Separate from <see cref="OpenFolder"/> because moving back up a chain returns
    /// to a page the user came from, which the shell answers with a Back rather than a new entry.
    /// </summary>
    public required Action<VideoFolderReference?> OpenAncestorFolder { get; init; }

    /// <summary>Shows the video's file properties, or null on heads without a properties dialog.</summary>
    public Action<Sourced<DbVideoFile>>? ShowProperties { get; init; }

    /// <summary>Reveals the video in the platform file manager, or null where that is unsupported.</summary>
    public Action<Sourced<DbVideoFile>>? OpenContainingFolder { get; init; }

    /// <summary>Reveals a library folder in the platform file manager, or null where unsupported.</summary>
    public Action<VideoFolderReference>? OpenContainingFolderOfFolder { get; init; }

    /// <summary>
    /// Picks a video playlist and appends the video to it, or null on heads with no playlist
    /// dialogs. Only video playlists are offered.
    /// </summary>
    public Action<Sourced<DbVideoFile>>? AddToPlaylist { get; init; }

    public Func<IReadOnlyList<Sourced<DbVideoFile>>, Task>? AddVideosToPlaylistAsync { get; init; }

    /// <summary>Drops the subtitles imported for the video, along with the choice pointing at them.</summary>
    public Action<Sourced<DbVideoFile>>? ClearSubtitleCache { get; init; }

    /// <summary>Clears the saved display progress of each video while retaining its track preferences.</summary>
    public required Action<IReadOnlyList<Sourced<DbVideoFile>>> ClearPlaybackProgress { get; init; }

    /// <summary>
    /// Records a fully watched marker for each video, in one pass so a folder's worth of them costs
    /// a single write.
    /// </summary>
    public required Action<IReadOnlyList<Sourced<DbVideoFile>>> MarkWatched { get; init; }
}

/// <summary>
/// What the search page needs to show video results alongside the music ones. Null on heads with no
/// video library, which is what keeps the video section off the page there.
/// </summary>
public sealed record VideoSearchServices(
    IUnifiedVideoLibraryQueryService Query,
    VideoPreviewService Previews,
    IVideoPlaybackStateStore PlaybackStates,
    MediaPageCallbacks Callbacks);

public sealed record PhotoSearchServices(
    ILibrarySourceRegistry Sources,
    PhotoFolderService Photos,
    VideoPreviewService Previews,
    MediaPageCallbacks Callbacks,
    Action<string> ShowAll,
    IVideoHistoryStore? History = null);

/// <summary>
/// What the home page needs for its video and picture rows. Null on heads with no video library,
/// which is what keeps those rows off the page there.
/// </summary>
public sealed record HomeMediaServices(
    IUnifiedVideoLibraryQueryService Query,
    VideoPreviewService Previews,
    PhotoFolderService Photos,
    IVideoPlaybackStateStore PlaybackStates,
    ILibrarySourceRegistry Sources,
    MediaPageCallbacks Callbacks,
    IVideoHistoryStore? History = null);

/// <summary>What the home page asks the shell to do on its behalf.</summary>
public sealed record HomePageCallbacks
{
    public required Action<LibraryItemReference> OpenAlbum { get; init; }

    public TileActionCallbacks? AlbumTileActions { get; init; }

    /// <summary>Opens the albums library page, behind the album row's view-all link.</summary>
    public required Action ShowAllAlbums { get; init; }

    /// <summary>Opens the now-playing surface, behind the spotlight card while music is playing.</summary>
    public required Action ShowNowPlaying { get; init; }
}
