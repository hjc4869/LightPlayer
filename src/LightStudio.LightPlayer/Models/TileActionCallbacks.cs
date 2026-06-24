using System;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Callbacks a browse page wires to each <see cref="LibraryTileModel"/> so its
/// open and hover-overlay actions reach the shell. Any unset callback makes the
/// matching command a no-op.
/// </summary>
public sealed class TileActionCallbacks
{
    /// <summary>Opens the tile's detail page (album/artist) or activates it.</summary>
    public Action<LibraryTileModel>? Open { get; init; }

    /// <summary>Replaces the queue with the tile's tracks and starts playing.</summary>
    public Action<LibraryTileModel>? Play { get; init; }

    /// <summary>Appends the tile's tracks to the end of the queue.</summary>
    public Action<LibraryTileModel>? AddToQueue { get; init; }
}
