using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// A Cross-platform app-native store that owns the user's playlists and their
/// persistence. Mutations update <see cref="Playlists"/> in place (so the UI
/// binds to it directly) and persist to disk.
/// </summary>
public interface IPlaylistService
{
    /// <summary>The live set of playlists, ordered by title.</summary>
    ObservableCollection<PlaylistModel> Playlists { get; }

    /// <summary>Loads persisted playlists from disk. Safe to call more than once.</summary>
    Task InitializeAsync();

    /// <summary>
    /// The well-known "Favorites" playlist that backs the Like button. Always
    /// present and listed among <see cref="Playlists"/> like any other playlist.
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

    /// <summary>Imports an <c>.m3u</c>/<c>.m3u8</c>/<c>.wpl</c> file as a new playlist.</summary>
    Task<PlaylistModel> ImportAsync(string filePath);

    /// <summary>Exports a playlist to the supplied path as M3U or WPL by extension.</summary>
    Task ExportAsync(string id, string filePath);
}
