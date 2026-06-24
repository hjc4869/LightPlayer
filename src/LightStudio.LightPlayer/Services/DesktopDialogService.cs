using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using LightStudio.FfmpegShim;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Dialogs;
using LightStudio.LightPlayer.Views.Dialogs;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Desktop implementation of <see cref="IDialogService"/>. Hosts the About box,
/// media properties viewer, confirmation prompts, and lyric file picker on the
/// active main window. Metadata is read off the UI thread via the FFmpeg shim.
/// </summary>
public sealed class DesktopDialogService : IDialogService
{
    private static readonly FilePickerFileType LrcType = new("Lyrics")
    {
        Patterns = new[] { "*.lrc", "*.txt" },
    };

    private static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task ShowAboutAsync()
    {
        if (Owner is { } owner)
        {
            await new AboutDialog().ShowDialog(owner);
        }
    }

    public async Task ShowPropertiesAsync(string filePath, string fallbackTitle)
    {
        if (Owner is not { } owner)
        {
            return;
        }

        var properties = await Task.Run(() => BuildProperties(filePath));
        var header = string.IsNullOrEmpty(filePath) ? fallbackTitle : Path.GetFileName(filePath);
        await new MediaPropertiesDialog(header, properties).ShowDialog(owner);
    }

    public async Task<string?> PickLyricFileAsync()
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import lyrics",
            AllowMultiple = false,
            FileTypeFilter = new[] { LrcType, FilePickerFileTypes.All },
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<LyricEditResult?> ShowLyricSearchAsync(
        string title,
        string artist,
        LyricsService lyrics,
        IReadOnlyList<ExternalLrcInfo> initialCandidates)
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        var viewModel = new LyricSearchDialogViewModel(lyrics, title, artist, initialCandidates);
        return await new LyricSearchDialog(viewModel).ShowDialog<LyricEditResult?>(owner);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        if (Owner is not { } owner)
        {
            return false;
        }

        var confirm = new Button { Content = confirmLabel, IsDefault = true };
        var cancel = new Button { Content = "Cancel" };
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap });
        content.Children.Add(buttons);
        dialog.Content = content;

        return await dialog.ShowDialog<bool>(owner);
    }

    private static IReadOnlyList<MediaPropertyItem> BuildProperties(string filePath)
    {
        var items = new List<MediaPropertyItem>();
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return items;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            var info = FfmpegCodec.GetMediaInfoFromStream(stream, close: false);
            Add(items, "Title", info.Title);
            Add(items, "Artist", info.Artist);
            Add(items, "Album", info.Album);
            Add(items, "Album artist", info.AlbumArtist);
            Add(items, "Genre", info.Genre);
            Add(items, "Date", info.Date);
            Add(items, "Track", info.TrackNumber);
            Add(items, "Disc", info.DiscNumber);
            Add(items, "Composer", info.Composer);
            if (info.Duration > TimeSpan.Zero)
            {
                Add(items, "Duration", info.Duration.ToString(@"hh\:mm\:ss"));
            }
        }
        catch
        {
            // Metadata is best-effort; the file rows below always show.
        }

        var file = new FileInfo(filePath);
        Add(items, "File name", file.Name);
        Add(items, "Type", file.Extension.TrimStart('.').ToUpperInvariant());
        Add(items, "Size", $"{file.Length / 1024.0 / 1024.0:0.00} MB");
        Add(items, "Location", file.DirectoryName ?? string.Empty);
        return items;
    }

    private static void Add(List<MediaPropertyItem> items, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            items.Add(new MediaPropertyItem(key, value));
        }
    }
}
