using System;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Callbacks a browse page wires to each <see cref="TrackRowModel"/> so its
/// context actions reach the shell. Any unset callback makes the matching
/// command a no-op.
/// </summary>
public sealed class TrackActionCallbacks
{
    public Action<TrackRowModel>? Play { get; init; }

    public Action<TrackRowModel>? PlayNext { get; init; }

    public Action<TrackRowModel>? AddToQueue { get; init; }

    public Action<TrackRowModel>? AddToPlaylist { get; init; }

    public Action<TrackRowModel>? ShowProperties { get; init; }

    public Action<TrackRowModel>? OpenContainingFolder { get; init; }

    /// <summary>Removes this row from the playlist it is shown in (playlist detail only).</summary>
    public Action<TrackRowModel>? RemoveFromPlaylist { get; init; }
}
