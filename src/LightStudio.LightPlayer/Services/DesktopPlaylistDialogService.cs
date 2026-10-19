using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Views.Dialogs;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Desktop implementation of <see cref="IPlaylistDialogService"/>. Hosts the
/// Avalonia dialog windows and routes file import/export through the active
/// window's <see cref="TopLevel.StorageProvider"/>.
/// </summary>
public sealed class DesktopPlaylistDialogService : IPlaylistDialogService
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

    public DesktopPlaylistDialogService(IPlaylistService playlistService, IPlaylistService videoPlaylistService)
    {
        this.playlistService = playlistService;
        this.videoPlaylistService = videoPlaylistService;
    }

    private static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task<string?> PromptForTitleAsync(string header, string confirmLabel, string? initialValue = null)
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        var dialog = new TextPromptDialog(header, confirmLabel, initialValue);
        return await dialog.ShowDialog<string?>(owner);
    }

    public async Task<PlaylistKind?> PickPlaylistKindAsync()
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        var dialog = new Window
        {
            Title = Strings.Resources.Playlists_NewPlaylist,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var music = new Button
        {
            Content = Strings.Resources.Playlists_MusicPlaylist,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            IsDefault = true,
        };
        var video = new Button
        {
            Content = Strings.Resources.Playlists_VideoPlaylist,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        var cancel = new Button
        {
            Content = Strings.Resources.Common_Cancel,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        music.Click += (_, _) => dialog.Close(PlaylistKind.Music);
        video.Click += (_, _) => dialog.Close(PlaylistKind.Video);
        cancel.Click += (_, _) => dialog.Close(null);

        var content = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = Strings.Resources.Playlists_NewPlaylistKindHeader,
            FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
        });
        content.Children.Add(music);
        content.Children.Add(video);
        content.Children.Add(cancel);
        dialog.Content = content;

        return await dialog.ShowDialog<PlaylistKind?>(owner);
    }

    public async Task<PlaylistModel?> PickPlaylistAsync(PlaylistKind kind = PlaylistKind.Music)
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        var service = ServiceFor(kind);
        var snapshot = service.Playlists.ToList();
        var dialog = new PlaylistPickerDialog(snapshot);
        var result = await dialog.ShowDialog<PlaylistPickResult?>(owner);
        if (result is null)
        {
            return null;
        }

        if (!result.CreateNew)
        {
            return result.Existing;
        }

        var name = await PromptForTitleAsync(Strings.Resources.Playlists_NewPlaylist, Strings.Resources.Common_Create);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return await service.CreateAsync(name);
    }

    private IPlaylistService ServiceFor(PlaylistKind kind) =>
        kind == PlaylistKind.Video ? videoPlaylistService : playlistService;

    public async Task<PlaylistFile?> PickImportFileAsync()
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
        if (Owner is not { } owner)
        {
            return false;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
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

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        if (Owner is not { } owner)
        {
            return false;
        }

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var confirmButton = new Button { Content = confirmLabel, IsDefault = true };
        var cancelButton = new Button { Content = Strings.Resources.Common_Cancel };
        confirmButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(confirmButton);

        var content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
        };
        content.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
        });
        content.Children.Add(buttons);
        dialog.Content = content;

        return await dialog.ShowDialog<bool>(owner);
    }
}
