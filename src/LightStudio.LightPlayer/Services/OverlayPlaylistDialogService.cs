using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Views.Dialogs;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Single-view (Android) implementation of <see cref="IPlaylistDialogService"/>. Renders the
/// name prompt, destination picker, and confirmations as in-shell overlays (mobile platforms
/// cannot open additional windows), and routes import/export through the active top level's
/// <see cref="TopLevel.StorageProvider"/>.
/// </summary>
internal sealed class OverlayPlaylistDialogService : IPlaylistDialogService
{
    private static readonly FilePickerFileType M3uType = new("M3U playlist")
    {
        Patterns = new[] { "*.m3u", "*.m3u8" },
        MimeTypes = new[] { "audio/x-mpegurl" },
    };

    private static readonly FilePickerFileType WplType = new("Windows Media Playlist")
    {
        Patterns = new[] { "*.wpl" },
        MimeTypes = new[] { "application/vnd.ms-wpl" },
    };

    private readonly IPlaylistService playlistService;
    private readonly IPlaylistService videoPlaylistService;

    public OverlayPlaylistDialogService(IPlaylistService playlistService, IPlaylistService videoPlaylistService)
    {
        this.playlistService = playlistService;
        this.videoPlaylistService = videoPlaylistService;
    }

    /// <summary>The shell overlay host. Injected after the shell view model is constructed.</summary>
    public IShellOverlayHost? Host { get; set; }

    private static TopLevel? ActiveTopLevel =>
        (Application.Current?.ApplicationLifetime as ISingleViewApplicationLifetime)?.MainView is { } view
            ? TopLevel.GetTopLevel(view)
            : null;

    public Task<string?> PromptForTitleAsync(string header, string confirmLabel, string? initialValue = null)
    {
        if (Host is not { } host)
        {
            return Task.FromResult<string?>(null);
        }

        string? result = null;
        var input = new TextBox { Text = initialValue ?? string.Empty };

        var buttons = OverlayDialog.ButtonsRow();
        var cancel = new Button { Content = Strings.Resources.Common_Cancel };
        cancel.Click += (_, _) => { result = null; host.CloseOverlay(); };
        var confirm = new Button
        {
            Content = confirmLabel,
            IsDefault = true,
            IsEnabled = !string.IsNullOrWhiteSpace(input.Text),
        };

        void Commit()
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                return;
            }

            result = input.Text!.Trim();
            host.CloseOverlay();
        }

        confirm.Click += (_, _) => Commit();
        input.TextChanged += (_, _) => confirm.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        input.KeyUp += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
            }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        var content = OverlayDialog.BuildContent(header, input, buttons);
        return OverlayDialog.ShowForResultAsync(host, content, () => result);
    }

    public Task<PlaylistKind?> PickPlaylistKindAsync()
    {
        if (Host is not { } host)
        {
            return Task.FromResult<PlaylistKind?>(null);
        }

        PlaylistKind? picked = null;
        var list = new StackPanel { Spacing = 6 };

        var music = new Button
        {
            Content = Strings.Resources.Playlists_MusicPlaylist,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        music.Click += (_, _) => { picked = PlaylistKind.Music; host.CloseOverlay(); };
        var video = new Button
        {
            Content = Strings.Resources.Playlists_VideoPlaylist,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        video.Click += (_, _) => { picked = PlaylistKind.Video; host.CloseOverlay(); };
        list.Children.Add(music);
        list.Children.Add(video);

        var buttons = OverlayDialog.ButtonsRow();
        var cancel = new Button { Content = Strings.Resources.Common_Cancel };
        cancel.Click += (_, _) => { picked = null; host.CloseOverlay(); };
        buttons.Children.Add(cancel);

        var content = OverlayDialog.BuildContent(Strings.Resources.Playlists_NewPlaylistKindHeader, list, buttons);
        return OverlayDialog.ShowForResultAsync(host, content, () => picked);
    }

    public async Task<PlaylistModel?> PickPlaylistAsync(PlaylistKind kind = PlaylistKind.Music)
    {
        if (Host is not { } host)
        {
            return null;
        }

        var service = kind == PlaylistKind.Video ? videoPlaylistService : playlistService;
        PlaylistPickResult? pick = null;
        var list = new StackPanel { Spacing = 6 };

        var newButton = new Button
        {
            Content = Strings.Resources.PlaylistPicker_NewPlaylist,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        newButton.Click += (_, _) => { pick = new PlaylistPickResult(null, CreateNew: true); host.CloseOverlay(); };
        list.Children.Add(newButton);

        foreach (var playlist in service.Playlists.ToList())
        {
            var captured = playlist;
            var button = new Button
            {
                Content = playlist.Title,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => { pick = new PlaylistPickResult(captured, CreateNew: false); host.CloseOverlay(); };
            list.Children.Add(button);
        }

        var buttons = OverlayDialog.ButtonsRow();
        var cancel = new Button { Content = Strings.Resources.Common_Cancel };
        cancel.Click += (_, _) => { pick = null; host.CloseOverlay(); };
        buttons.Children.Add(cancel);

        var content = OverlayDialog.BuildContent(Strings.Resources.Common_AddToPlaylist, list, buttons);
        var chosen = await OverlayDialog.ShowForResultAsync(host, content, () => pick);
        if (chosen is null)
        {
            return null;
        }

        if (!chosen.CreateNew)
        {
            return chosen.Existing;
        }

        var name = await PromptForTitleAsync(Strings.Resources.Playlists_NewPlaylist, Strings.Resources.Common_Create);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return await service.CreateAsync(name);
    }

    public async Task<PlaylistFile?> PickImportFileAsync()
    {
        if (ActiveTopLevel is not { } topLevel)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Resources.Playlists_ImportPlaylist,
            AllowMultiple = false,
            FileTypeFilter = new[] { M3uType, WplType, FilePickerFileTypes.All },
        });

        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var localPath = files[0].TryGetLocalPath();
        return new PlaylistFile(
            files[0].Name,
            await reader.ReadToEndAsync(),
            localPath is null ? null : Path.GetDirectoryName(localPath));
    }

    public async Task<bool> SaveExportFileAsync(string suggestedName, Func<string, Task<string?>> createContentAsync)
    {
        if (ActiveTopLevel is not { } topLevel)
        {
            return false;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Resources.PlaylistDialog_ExportTitle,
            SuggestedFileName = suggestedName,
            DefaultExtension = "m3u8",
            FileTypeChoices = new[] { M3uType, WplType },
        });

        if (file is null || await createContentAsync(file.Name) is not { } content)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek)
        {
            stream.SetLength(0);
        }

        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content);
        return true;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
        Host is { } host
            ? OverlayDialog.ConfirmAsync(host, title, message, confirmLabel)
            : Task.FromResult(false);
}
