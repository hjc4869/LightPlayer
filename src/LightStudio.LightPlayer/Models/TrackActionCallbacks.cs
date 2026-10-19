using System;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Callbacks a browse page wires to each <see cref="TrackRowModel"/> so its
/// context actions reach the shell. Any unset callback makes the matching
/// command a no-op.
/// </summary>
/// <remarks>
/// Pages can use <c>with</c> to specialize individual actions without restating the complete set.
/// </remarks>
public sealed record TrackActionCallbacks
{
    public Action<TrackRowModel>? Play { get; init; }

    public Action<TrackRowModel>? PlayNext { get; init; }

    public Action<TrackRowModel>? AddToQueue { get; init; }

    public Action<TrackRowModel>? AddToPlaylist { get; init; }

    public Action<TrackRowModel>? OpenAlbum { get; init; }

    public Action<TrackRowModel>? OpenArtist { get; init; }

    public Action<TrackRowModel>? ShowProperties { get; init; }

    public Action<TrackRowModel>? OpenContainingFolder { get; init; }

    /// <summary>Removes this row from the playlist it is shown in (playlist detail only).</summary>
    public Action<TrackRowModel>? RemoveFromPlaylist { get; init; }

    public Func<MusicFolderReference, Task>? PlayFolder { get; init; }

    public Func<MusicFolderReference, Task>? PlayFolderNext { get; init; }

    public Func<MusicFolderReference, Task>? AddFolderToQueue { get; init; }

    public Func<MusicFolderReference, Task>? AddFolderToPlaylist { get; init; }
}
