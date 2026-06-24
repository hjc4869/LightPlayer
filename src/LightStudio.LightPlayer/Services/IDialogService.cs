using System.Collections.Generic;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Hosts the application's modal dialogs (About, file properties, confirmations,
/// lyric import and search). Implementations resolve the active top-level window
/// so callers stay platform-agnostic.
/// </summary>
public interface IDialogService
{
    Task ShowAboutAsync();

    Task ShowPropertiesAsync(string filePath, string fallbackTitle);

    Task<bool> ConfirmAsync(string title, string message, string confirmLabel);

    Task<string?> PickLyricFileAsync();

    /// <summary>
    /// Shows the lyric search/manage dialog seeded with the track's metadata and
    /// any candidates already found by the auto-search. Returns the user's change
    /// (download, import, or clear), or null when the dialog is cancelled.
    /// </summary>
    Task<LyricEditResult?> ShowLyricSearchAsync(
        string title,
        string artist,
        LyricsService lyrics,
        IReadOnlyList<ExternalLrcInfo> initialCandidates);
}
