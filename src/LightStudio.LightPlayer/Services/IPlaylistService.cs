using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

/// <summary>A playlist file selected or created by the frontend.</summary>
public sealed record PlaylistFile(string Name, string Content, string? BaseDirectory = null);

/// <summary>
/// A Cross-platform app-native store that owns the user's playlists and their
/// persistence. Mutations update <see cref="Playlists"/> in place (so the UI
/// binds to it directly) and persist to disk.
/// </summary>
public interface IPlaylistService
{
    /// <summary>Raised after a playlist mutation has fully updated the live model.</summary>
    event EventHandler? Changed;

    /// <summary>What this store holds. Music and video playlists are kept in separate stores.</summary>
    PlaylistKind Kind { get; }

    /// <summary>The live set of playlists, ordered by title.</summary>
    ObservableCollection<PlaylistModel> Playlists { get; }

    /// <summary>Loads persisted playlists from disk. Safe to call more than once.</summary>
    Task InitializeAsync();

    /// <summary>
    /// The well-known favorites playlist that backs the Like button (music) or the tile star
    /// (video). Always present and listed among <see cref="Playlists"/> like any other playlist.
    /// </summary>
    PlaylistModel Favorites { get; }

    /// <summary>True when a track with the same source is already in favorites.</summary>
    bool IsFavorite(PlaylistItemModel item);

    /// <summary>Adds the track to favorites if not already present and persists.</summary>
    Task AddToFavoritesAsync(PlaylistItemModel item);

    /// <summary>Removes the track from favorites if present and persists.</summary>
    Task RemoveFromFavoritesAsync(PlaylistItemModel item);

    PlaylistModel? GetById(string id);

    /// <summary>Creates an empty playlist with the given title and persists it.</summary>
    Task<PlaylistModel> CreateAsync(string title);

    /// <summary>Renames a playlist and persists the change.</summary>
    Task RenameAsync(string id, string newTitle);

    /// <summary>Deletes a playlist and its backing file.</summary>
    Task DeleteAsync(string id);

    /// <summary>Appends entries to a playlist and persists it.</summary>
    Task AddItemsAsync(string id, IEnumerable<PlaylistItemModel> items);

    /// <summary>Replaces a playlist's entries (used by reorder and remove edits).</summary>
    Task ReplaceItemsAsync(string id, IEnumerable<PlaylistItemModel> items);

    /// <summary>Imports frontend-read <c>.m3u</c>/<c>.m3u8</c>/<c>.wpl</c> content as a new playlist.</summary>
    Task<PlaylistModel> ImportAsync(PlaylistFile file);

    /// <summary>Creates M3U or WPL content, selected from <paramref name="fileName"/>'s extension.</summary>
    Task<PlaylistFile?> ExportAsync(string id, string fileName);
}
