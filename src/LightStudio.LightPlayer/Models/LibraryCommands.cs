using System;
using System.Collections.Generic;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// The app-level actions the detail and search pages route through the shell:
/// per-track context actions, collection playback actions, detail navigation,
/// and back navigation. Lets the pages stay decoupled from the shell and the
/// playback queue.
/// </summary>
public sealed class LibraryCommands
{
    /// <summary>Per-row context actions (play, queue, playlist, properties, ...).</summary>
    public TrackActionCallbacks TrackActions { get; init; } = new();

    /// <summary>Open/play/append actions for album tiles shown on detail and search pages.</summary>
    public TileActionCallbacks AlbumTileActions { get; init; } = new();

    /// <summary>Open/play/append actions for artist tiles shown on search pages.</summary>
    public TileActionCallbacks ArtistTileActions { get; init; } = new();

    /// <summary>Replace the queue with these tracks and start playing the first.</summary>
    public Action<IReadOnlyList<TrackRowModel>>? PlayTracks { get; init; }

    /// <summary>Shuffle these tracks into the queue and start playing.</summary>
    public Action<IReadOnlyList<TrackRowModel>>? ShuffleTracks { get; init; }

    /// <summary>Append these tracks to the end of the queue.</summary>
    public Action<IReadOnlyList<TrackRowModel>>? AddTracksToQueue { get; init; }

    /// <summary>Add these tracks to a playlist (wired with the playlists task).</summary>
    public Action<IReadOnlyList<TrackRowModel>>? AddTracksToPlaylist { get; init; }

    /// <summary>Navigate to the album detail page for the supplied identity.</summary>
    public Action<MediaLibraryItemIdentifier>? OpenAlbum { get; init; }

    /// <summary>Navigate to the artist detail page for the supplied identity.</summary>
    public Action<MediaLibraryItemIdentifier>? OpenArtist { get; init; }

    /// <summary>Return to the previous page.</summary>
    public Action? GoBack { get; init; }
}
