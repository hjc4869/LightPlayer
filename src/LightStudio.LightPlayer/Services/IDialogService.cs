using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Dialogs;
using LightStudio.LightPlayer.ViewModels.Pages;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LightPlayer.Services;

/// <param name="Content">
/// The file's bytes rather than its text: what encoding a subtitle is in is for the loader to work
/// out, and the picked file may not have a path this device can open twice.
/// </param>
public sealed record PickedFile(string Name, byte[] Content);

/// <summary>HTTP media sources entered together, in playback preference order.</summary>
public sealed record StreamLink(IReadOnlyList<Uri> Sources);

/// <summary>
/// Hosts the application's modal dialogs (About, file properties, confirmations,
/// lyric import and search). Implementations resolve the active top-level window
/// so callers stay platform-agnostic.
/// </summary>
public interface IDialogService
{
    Task ShowAboutAsync();

    Task ShowPropertiesAsync(
        MediaReference media,
        string fallbackTitle,
        IClientMediaResolver mediaResolver);

    /// <summary>
    /// Shows the full text of a warning/error banner so the user can read and copy it. The title
    /// is the short banner caption; <paramref name="details"/> is the multi-line message.
    /// </summary>
    Task ShowWarningDetailsAsync(string title, string details);

    /// <summary>Prompts for one or more absolute HTTP or HTTPS media URLs.</summary>
    Task<StreamLink?> PromptForStreamUrlAsync();

    Task<bool> ConfirmAsync(string title, string message, string confirmLabel);

    /// <summary>
    /// Shows the custom color theme editor. Returns the saved theme, or null when
    /// the user cancels.
    /// </summary>
    Task<ColorTheme?> ShowThemeEditorAsync(ThemeEditorViewModel viewModel);

    /// <summary>
    /// Shows the add/edit form for a remote library. The view model saves itself, so the call only
    /// completes once the form is closed.
    /// </summary>
    Task ShowRemoteLibraryEditorAsync(RemoteLibraryEditorViewModel viewModel);

    Task<string?> PickLyricContentAsync();

    /// <summary>Picks a subtitle file off this device, or returns null when nothing was chosen.</summary>
    Task<PickedFile?> PickSubtitleFileAsync();

    /// <summary>
    /// Shows the lyric search/manage dialog seeded with the track's metadata and
    /// any candidates already found by the auto-search. Returns the user's change
    /// (download, import, or clear), or null when the dialog is canceled.
    /// </summary>
    Task<LyricEditResult?> ShowLyricSearchAsync(
        string title,
        string artist,
        ILyricsService lyrics,
        IReadOnlyList<ExternalLrcInfo> initialCandidates);
}

internal static class StreamUrl
{
    public static bool TryParseLink(string? value, [NotNullWhen(true)] out StreamLink? link)
    {
        var sources = new List<Uri>();
        foreach (string line in (value ?? string.Empty).Split(
                 ['\r', '\n'],
                 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParse(line, out Uri? uri))
            {
                link = null;
                return false;
            }

            sources.Add(uri);
        }

        link = sources.Count > 0 ? new StreamLink(sources) : null;
        return link is not null;
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out uri) &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        uri = null;
        return false;
    }
}
