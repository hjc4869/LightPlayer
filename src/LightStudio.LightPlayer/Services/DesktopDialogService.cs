using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using LightMediaRenderer.Subtitles;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Dialogs;
using LightStudio.LightPlayer.Views;
using LightStudio.LightPlayer.Views.Dialogs;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Desktop implementation of <see cref="IDialogService"/>. Hosts the About box,
/// media properties viewer, confirmation prompts, and lyric file picker on the
/// active main window. Metadata and file properties are resolved by the active media platform.
/// </summary>
public sealed class DesktopDialogService : IDialogService
{
    private static readonly FilePickerFileType LrcType = new("Lyrics")
    {
        Patterns = new[] { "*.lrc", "*.txt" },
    };

    private static readonly FilePickerFileType SubtitleType = new("Subtitles")
    {
        Patterns = LightMediaRenderer.Subtitles.SubtitleLoader.Patterns,
    };

    // An open settings window disables the main window, so dialogs go over the settings window.
    private static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is { } main
            ? main.OwnedWindows.OfType<SettingsWindow>().FirstOrDefault() ?? main
            : null;

    public async Task ShowAboutAsync()
    {
        if (Owner is { } owner)
        {
            await new AboutDialog().ShowDialog(owner);
        }
    }

    public async Task ShowPropertiesAsync(
        MediaReference media,
        string fallbackTitle,
        IClientMediaResolver mediaResolver)
    {
        if (Owner is not { } owner)
        {
            return;
        }

        var header = string.IsNullOrWhiteSpace(fallbackTitle) ? Strings.Resources.Common_Properties : fallbackTitle;
        var dialog = new MediaPropertiesDialog(header);
        var dialogTask = dialog.ShowDialog(owner);

        var (properties, fileName) = await MediaPropertiesBuilder.BuildAsync(media, mediaResolver);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            header = fileName;
        }

        dialog.SetProperties(header, properties);
        await dialogTask;
    }

    public async Task ShowWarningDetailsAsync(string title, string details)
    {
        var dialog = new Window
        {
            Title = string.IsNullOrWhiteSpace(title) ? Strings.Resources.Shell_WarningDetailsTitle : title,
            Width = 620,
            Height = 420,
            MinWidth = 420,
            MinHeight = 260,
        };

        var copyButton = new Button { Content = Strings.Resources.Common_Copy };
        copyButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(dialog)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(details);
            }
        };

        var closeButton = new Button
        {
            Content = Strings.Resources.Common_Close,
            IsDefault = true,
        };
        closeButton.Click += (_, _) => dialog.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { copyButton, closeButton },
        };

        var content = new DockPanel
        {
            Margin = new Thickness(18),
            LastChildFill = true,
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        content.Children.Add(buttons);
        content.Children.Add(new TextBox
        {
            Text = details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Stretch,
        });
        dialog.Content = content;

        if (Owner is { } owner)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }

    public async Task<StreamLink?> PromptForStreamUrlAsync()
    {
        if (Owner is not { } owner)
            return null;

        var dialog = new TextPromptDialog(
            Strings.Resources.StreamLink_Title,
            Strings.Resources.Common_Open,
            initialValue: null,
            value => StreamUrl.TryParseLink(value, out _),
            Strings.Resources.StreamLink_InvalidAddress,
            Strings.Resources.StreamLink_Address)
        {
            Title = Strings.Resources.StreamLink_Title,
            AcceptsReturn = true,
        };
        string? value = await dialog.ShowDialog<string?>(owner);
        return StreamUrl.TryParseLink(value, out StreamLink? link) ? link : null;
    }

    public async Task<string?> PickLyricContentAsync()
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Resources.Common_ImportLyrics,
            AllowMultiple = false,
            FileTypeFilter = new[] { LrcType, FilePickerFileTypes.All },
        });

        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }

    public async Task<PickedFile?> PickSubtitleFileAsync()
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Resources.VideoPlayer_SubtitlePickerTitle,
            AllowMultiple = false,
            FileTypeFilter = new[] { SubtitleType, FilePickerFileTypes.All },
        });

        return files.Count == 0 ? null : await ReadPickedFileAsync(files[0]);
    }

    internal static async Task<PickedFile> ReadPickedFileAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return new PickedFile(file.Name, buffer.ToArray());
    }

    public async Task<LyricEditResult?> ShowLyricSearchAsync(
        string title,
        string artist,
        ILyricsService lyrics,
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
        var cancel = new Button { Content = Strings.Resources.Common_Cancel };
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

    public async Task<ColorTheme?> ShowThemeEditorAsync(ThemeEditorViewModel viewModel)
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        return await new ThemeEditorDialog(viewModel).ShowDialog<ColorTheme?>(owner);
    }

    public async Task ShowRemoteLibraryEditorAsync(ViewModels.Pages.RemoteLibraryEditorViewModel viewModel)
    {
        if (Owner is { } owner)
        {
            await new RemoteLibraryEditorDialog(viewModel).ShowDialog(owner);
        }
    }
}
