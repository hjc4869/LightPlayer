using System;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// A host-owned navigation history the shell mirrors its own navigation into. Only heads whose host
/// has a reloadable address and its own back/forward affordances register one: the browser head
/// keeps the location in the URL hash fragment and each shell navigation in a history entry, so the
/// browser's back, forward and reload drive the app. Entries are identified by a shell-assigned
/// index, which is what makes the direction and size of a jump observable on <see cref="LocationChanged"/>.
/// </summary>
public interface INavigationHistory
{
    /// <summary>The location of the entry the host is on, or null when the host has none.</summary>
    string? CurrentLocation { get; }

    /// <summary>The index recorded on the current entry, or -1 when it carries none.</summary>
    int CurrentIndex { get; }

    /// <summary>Overwrites the current entry, leaving the history depth unchanged.</summary>
    void Replace(string location, int index);

    /// <summary>Appends an entry, discarding any entries ahead of the current one.</summary>
    void Push(string location, int index);

    /// <summary>Moves by <paramref name="delta"/> entries, which raises <see cref="LocationChanged"/>.</summary>
    void Go(int delta);

    /// <summary>Raised with the location and index of the entry the host moved to.</summary>
    event Action<string?, int>? LocationChanged;
}
