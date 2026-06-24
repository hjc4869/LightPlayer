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
    /// <summary>Prompts for a playlist title. Returns null when cancelled.</summary>
    Task<string?> PromptForTitleAsync(string header, string confirmLabel, string? initialValue = null);

    /// <summary>
    /// Lets the user choose a destination playlist, optionally creating a new
    /// one. Returns the chosen (or newly created) playlist, or null when
    /// cancelled.
    /// </summary>
    Task<PlaylistModel?> PickPlaylistAsync();

    /// <summary>Picks an <c>.m3u</c>/<c>.m3u8</c>/<c>.wpl</c> file to import. Returns null when cancelled.</summary>
    Task<string?> PickImportFileAsync();

    /// <summary>Picks a destination path for export. Returns null when cancelled.</summary>
    Task<string?> PickExportFileAsync(string suggestedName);

    /// <summary>Shows a yes/no confirmation. Returns true when confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel);
}
