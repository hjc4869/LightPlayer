using System;
using System.Collections.Generic;
using Avalonia.Input;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// In-process drag-and-drop formats used by the now-playing queue. The payloads
/// carry live CLR object references (never serialized), so they only flow within
/// this process — exactly what the queue reorder and library-to-queue drags need.
/// </summary>
public static class QueueDragFormats
{
    /// <summary>Carries the queue items being reordered within the now-playing queue.</summary>
    public static readonly DataFormat<QueueReorderPayload> QueueReorder =
        DataFormat.CreateInProcessFormat<QueueReorderPayload>("LightStudio.LightPlayer.QueueReorder");

    /// <summary>Carries a library item (album/artist/playlist/track) being inserted into the queue.</summary>
    public static readonly DataFormat<LibraryInsertPayload> LibraryInsert =
        DataFormat.CreateInProcessFormat<LibraryInsertPayload>("LightStudio.LightPlayer.LibraryInsert");

    /// <summary>
    /// Audio file extensions accepted when external files are dropped onto the queue.
    /// Mirrors the file picker filter so dropped non-audio files are ignored. Includes
    /// <c>.cue</c> so CUE sheets can be dropped and expanded into their tracks.
    /// </summary>
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg",
        ".opus", ".wma", ".aiff", ".aif", ".alac", ".ape",
        ".cue",
    };
}

/// <summary>Payload for an internal now-playing queue reorder drag.</summary>
public sealed class QueueReorderPayload
{
    public QueueReorderPayload(IReadOnlyList<MusicPlaybackItemModel> items)
    {
        Items = items;
    }

    /// <summary>The queue items being moved, in their current queue order.</summary>
    public IReadOnlyList<MusicPlaybackItemModel> Items { get; }
}

/// <summary>The kind of library item carried by a <see cref="LibraryInsertPayload"/>.</summary>
public enum LibraryInsertKind
{
    Album,
    Artist,
    Track,
    Playlist,
}

/// <summary>
/// Identifies a library item being dragged into the queue. The shell resolves it
/// to the ordered playback items to insert (album/artist tracks come from the
/// database in disc/track order; a playlist uses its stored order; a track is a
/// single item).
/// </summary>
public sealed class LibraryInsertPayload
{
    public LibraryInsertKind Kind { get; init; }

    /// <summary>Album/artist identity (for <see cref="LibraryInsertKind.Album"/> / <see cref="LibraryInsertKind.Artist"/>).</summary>
    public MediaLibraryItemIdentifier? Identifier { get; init; }

    /// <summary>The dragged track row (for <see cref="LibraryInsertKind.Track"/>).</summary>
    public TrackRowModel? Track { get; init; }

    /// <summary>The dragged playlist (for <see cref="LibraryInsertKind.Playlist"/>).</summary>
    public PlaylistModel? Playlist { get; init; }
}
