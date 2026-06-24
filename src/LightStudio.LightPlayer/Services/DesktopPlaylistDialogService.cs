using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Views.Dialogs;

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

    public DesktopPlaylistDialogService(IPlaylistService playlistService)
    {
        this.playlistService = playlistService;
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

    public async Task<PlaylistModel?> PickPlaylistAsync()
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        var snapshot = playlistService.Playlists.ToList();
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

        var name = await PromptForTitleAsync("New playlist", "Create");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return await playlistService.CreateAsync(name);
    }

    public async Task<string?> PickImportFileAsync()
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import playlist",
            AllowMultiple = false,
            FileTypeFilter = new[] { M3uType, WplType, FilePickerFileTypes.All },
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickExportFileAsync(string suggestedName)
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export playlist",
            SuggestedFileName = suggestedName,
            DefaultExtension = "m3u8",
            FileTypeChoices = new[] { M3uType, WplType },
        });

        return file?.TryGetLocalPath();
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
        var cancelButton = new Button { Content = "Cancel" };
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
