using System;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Surfaces the playlist-related dialogs and file pickers. The desktop
/// implementation hosts Avalonia windows and uses
/// <c>TopLevel.StorageProvider</c>; this seam keeps the view models free of
/// view and platform types.
/// </summary>
public interface IPlaylistDialogService
{
    /// <summary>Prompts for a playlist title. Returns null when canceled.</summary>
    Task<string?> PromptForTitleAsync(string header, string confirmLabel, string? initialValue = null);

    /// <summary>
    /// Lets the user choose a destination playlist of the given kind, optionally
    /// creating a new one. Only playlists of that kind are offered, and a new one
    /// is created in that kind's store. Returns the chosen (or newly created)
    /// playlist, or null when canceled.
    /// </summary>
    Task<PlaylistModel?> PickPlaylistAsync(PlaylistKind kind = PlaylistKind.Music);

    /// <summary>Asks which kind of playlist to create. Returns null when canceled.</summary>
    Task<PlaylistKind?> PickPlaylistKindAsync();

    /// <summary>Picks and reads an <c>.m3u</c>/<c>.m3u8</c>/<c>.wpl</c> file. Returns null when canceled.</summary>
    Task<PlaylistFile?> PickImportFileAsync();

    /// <summary>Picks an export file and writes content created for its selected name.</summary>
    Task<bool> SaveExportFileAsync(string suggestedName, Func<string, Task<string?>> createContentAsync);

    /// <summary>Shows a yes/no confirmation. Returns true when confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel);
}
