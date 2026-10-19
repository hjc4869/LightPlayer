#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.MediaLibraryCore.Database;

/// <summary>One folder that directly contains indexed videos.</summary>
public sealed class VideoFolderCount
{
    /// <summary>The configured video library folder this folder lives under.</summary>
    public string? LibraryRoot { get; set; }

    /// <summary>Full identifier of the folder.</summary>
    public string? FolderPath { get; set; }

    /// <summary>Number of videos directly inside this folder (not counting subfolders).</summary>
    public int VideoCount { get; set; }
}

/// <summary>How a bounded set of videos is picked out of the library.</summary>
public enum VideoPickOrder
{
    /// <summary>Most recently indexed first.</summary>
    RecentlyAdded,

    /// <summary>An arbitrary sample, drawn again on every call.</summary>
    Random,
}

/// <summary>
/// Read access to the video library. Deliberately separate from <see cref="ILibraryQueryService"/>
/// so that nothing which consumes the music library can accidentally surface videos.
/// </summary>
public interface IVideoLibraryQueryService
{
    /// <summary>Every indexed video, ordered by folder then file name. Previews are not loaded.</summary>
    Task<IReadOnlyList<DbVideoFile>> GetVideosAsync();

    /// <summary>One page of all indexed videos in display-name order. Previews are not loaded.</summary>
    Task<PagedResult<DbVideoFile>> GetVideosPageAsync(
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// At most <paramref name="limit"/> videos in the requested order, for the shortcut rows that
    /// show a sample of the library rather than browse it. Previews are not loaded.
    /// </summary>
    Task<IReadOnlyList<DbVideoFile>> GetVideoPicksAsync(
        VideoPickOrder order,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Videos directly inside <paramref name="folderPath"/>. Previews are not loaded.</summary>
    Task<IReadOnlyList<DbVideoFile>> GetVideosInFolderAsync(
        string folderPath,
        CancellationToken cancellationToken = default);

    /// <summary>Per-folder video counts, enough to build the browsable folder tree.</summary>
    Task<IReadOnlyList<VideoFolderCount>> GetVideoFolderCountsAsync();

    /// <summary>
    /// Videos whose title tag or file name contains <paramref name="keyword"/>. Deliberately no
    /// deeper than that: videos carry none of the tag metadata the music search spans.
    /// Previews are not loaded.
    /// </summary>
    Task<IReadOnlyList<DbVideoFile>> SearchVideosAsync(
        string keyword,
        CancellationToken cancellationToken = default);

    /// <summary>Looks a video up by its identifier.</summary>
    Task<DbVideoFile?> GetVideoByPathAsync(string path);

    /// <summary>
    /// A preview image for a video, or null when none could be produced. Preview frames are made
    /// when a tile asks for one rather than stored by the scan, so the library carries no image
    /// blobs and a scan costs one probe per file instead of a decode.
    /// </summary>
    Task<byte[]?> GetVideoPreviewAsync(string path, CancellationToken cancellationToken = default);
}
