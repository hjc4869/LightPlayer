using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LightMediaRenderer.Subtitles;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Dialogs;
using LightStudio.LightPlayer.Views.Dialogs;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Single-view (Android) implementation of <see cref="IDialogService"/>. Mobile platforms cannot
/// open additional <see cref="Window"/> instances, so every dialog is rendered as an in-shell
/// overlay through <see cref="IShellOverlayHost"/>. Metadata for the properties view is read via
/// the platform I/O abstraction so it works with content:// URIs.
/// </summary>
internal sealed class OverlayDialogService : IDialogService
{
    private const string GitHubProjectUrl = "https://github.com/hjc4869/LightPlayer";

    private static readonly FilePickerFileType LrcType = new("Lyrics")
    {
        Patterns = new[] { "*.lrc", "*.txt" },
    };

    private static readonly FilePickerFileType SubtitleType = new("Subtitles")
    {
        Patterns = LightMediaRenderer.Subtitles.SubtitleLoader.Patterns,
    };

    /// <summary>The shell overlay host. Injected after the shell view model is constructed.</summary>
    public IShellOverlayHost? Host { get; set; }

    private static TopLevel? ActiveTopLevel =>
        (Application.Current?.ApplicationLifetime as ISingleViewApplicationLifetime)?.MainView is { } view
            ? TopLevel.GetTopLevel(view)
            : null;

    public Task ShowAboutAsync()
    {
        if (Host is null)
        {
            return Task.CompletedTask;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "2.0.0.0";
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(CenteredText(Strings.Resources.About_ProductName, 22, FontWeight.SemiBold));
        body.Children.Add(CenteredSecondary(string.Format(Strings.Resources.About_VersionFormat, version, BuildInfo.SourceVersion), 13, wrap: true));
        body.Children.Add(CenteredSecondary(Strings.Resources.About_Tagline, 12, wrap: true));
        body.Children.Add(BuildGitHubLink());

        var notices = LicenseNotices.LoadCombinedText();
        if (!string.IsNullOrEmpty(notices))
        {
            body.Children.Add(BuildNoticesView(notices));
        }

        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(OperatingSystem.IsBrowser()
            ? BuildBrowserDiagnosticsButton()
            : BuildExportLogsButton());
        if (PlatformServices.QueueIndexDatabaseResetAsync is not null)
        {
            var reset = new Button { Content = Strings.Resources.About_ResetIndexDatabase };
            reset.Click += async (_, _) =>
            {
                if (Application.Current is not App app)
                    throw new InvalidOperationException("The application is not initialized.");
                await AppErrorReporter.GuardAsync(app.ResetIndexDatabaseAsync, Strings.Resources.About_ResetIndexDatabase);
            };
            buttons.Children.Add(reset);
        }
        var close = new Button { Content = Strings.Resources.Common_Close, IsDefault = true };
        close.Click += (_, _) => Host.CloseOverlay();
        buttons.Children.Add(close);
        return Host.ShowOverlayAsync(OverlayDialog.BuildContent(Strings.Resources.Common_About, body, buttons));
    }

    public Task<StreamLink?> PromptForStreamUrlAsync()
    {
        if (Host is not { } host)
            return Task.FromResult<StreamLink?>(null);

        StreamLink? result = null;
        var input = new TextBox
        {
            PlaceholderText = Strings.Resources.StreamLink_Address,
            AcceptsReturn = true,
            Height = 120,
            MaxWidth = 520,
            MaxHeight = 120,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextWrapping = TextWrapping.NoWrap,
            [ScrollViewer.HorizontalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
            [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
        };
        var validation = new TextBlock
        {
            Text = Strings.Resources.StreamLink_InvalidAddress,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        var body = new StackPanel
        {
            Spacing = 8,
            Children = { input, validation },
        };

        var buttons = OverlayDialog.ButtonsRow();
        var cancel = new Button { Content = Strings.Resources.Common_Cancel };
        cancel.Click += (_, _) => host.CloseOverlay();
        var confirm = new Button
        {
            Content = Strings.Resources.Common_Open,
            IsEnabled = false,
        };

        void Commit()
        {
            if (!StreamUrl.TryParseLink(input.Text, out StreamLink? link))
            {
                validation.IsVisible = true;
                return;
            }

            result = link;
            host.CloseOverlay();
        }

        confirm.Click += (_, _) => Commit();
        input.TextChanged += (_, _) =>
        {
            validation.IsVisible = false;
            confirm.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        };
        input.KeyUp += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                Commit();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        return OverlayDialog.ShowForResultAsync(
            host,
            OverlayDialog.BuildContent(Strings.Resources.StreamLink_Title, body, buttons),
            () => result);
    }

    private static Control BuildGitHubLink()
    {
        var link = new Button
        {
            Classes = { "link" },
            Content = Strings.Resources.About_GitHubProject,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        link.Click += async (_, _) =>
        {
            if (ActiveTopLevel?.Launcher is { } launcher)
            {
                await launcher.LaunchUriAsync(new Uri(GitHubProjectUrl));
            }
        };
        return link;
    }

    private static Button BuildExportLogsButton()
    {
        var button = new Button
        {
            Content = Strings.Resources.About_ExportLogs,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsEnabled = AppLog.CurrentFilePath is not null,
        };
        button.Click += async (_, _) =>
            await AppErrorReporter.GuardAsync(
                () => LogExportService.ExportAsync(ActiveTopLevel?.StorageProvider),
                Strings.Resources.About_ExportLogs);
        return button;
    }

    private static Button BuildBrowserDiagnosticsButton()
    {
        var button = new Button
        {
            Content = Strings.Resources.About_BrowserDiagnostics,
        };
        button.Click += async (_, _) =>
        {
            if (ActiveTopLevel?.Launcher is { } launcher &&
                PlatformServices.WebHostAddress is { Length: > 0 } host)
            {
                await launcher.LaunchUriAsync(new Uri($"{host.TrimEnd('/')}/diag"));
            }
        };
        return button;
    }

    // Frames the embedded LICENSE + THIRDPARTY notices. The card's own ScrollViewer (from
    // OverlayDialog.BuildContent) scrolls the whole body, so this block only needs a border and
    // selectable, wrapping text rather than a nested fixed-height scroll region.
    private static Control BuildNoticesView(string notices)
    {
        var text = new SelectableTextBlock
        {
            Text = notices,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 10),
        };
        ApplyDynamicForeground(text, "AppPrimaryTextBrush");

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 4, 0, 0),
            Child = text,
        };
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("AppDividerBrush"));
        return border;
    }

    public async Task ShowPropertiesAsync(
        MediaReference media,
        string fallbackTitle,
        IClientMediaResolver mediaResolver)
    {
        if (Host is null)
        {
            return;
        }

        var header = string.IsNullOrWhiteSpace(fallbackTitle) ? Strings.Resources.Common_Properties : fallbackTitle;
        var progress = new ProgressBar { IsIndeterminate = true, Width = 140, Height = 4 };
        var body = new ContentControl
        {
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = Strings.Resources.Common_Loading,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    progress,
                },
            },
        };
        var overlayTask = ShowInfoAsync(header, body, out var headerText);

        var (rows, fileName) = await MediaPropertiesBuilder
            .BuildAsync(media, mediaResolver).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            header = fileName;
            headerText.Text = header;
        }

        var list = new StackPanel { Spacing = 2 };
        foreach (var row in rows)
        {
            list.Children.Add(BuildPropertyRow(row));
        }

        progress.IsIndeterminate = false;
        body.Content = list;
        await overlayTask;
    }

    public Task ShowWarningDetailsAsync(string title, string details)
    {
        if (Host is null)
        {
            return Task.CompletedTask;
        }

        var box = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            FontSize = 13,
        };

        return ShowInfoAsync(
            string.IsNullOrWhiteSpace(title) ? Strings.Resources.Shell_WarningDetailsTitle : title,
            box,
            extraButton: (Strings.Resources.Common_Copy, () => CopyToClipboard(details)));
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
        Host is { } host
            ? OverlayDialog.ConfirmAsync(host, title, message, confirmLabel)
            : Task.FromResult(false);

    public async Task<ColorTheme?> ShowThemeEditorAsync(ThemeEditorViewModel viewModel)
    {
        if (Host is not { } host)
        {
            return null;
        }

        ColorTheme? result = null;
        void OnClose(ColorTheme? theme)
        {
            result = theme;
            host.CloseOverlay();
        }

        viewModel.RequestClose += OnClose;

        // The view brings its own title, form, and Save/Cancel buttons and manages its own
        // scrolling, so it is hosted directly like the lyric search view.
        var view = new ThemeEditorView { DataContext = viewModel };
        try
        {
            return await OverlayDialog.ShowForResultAsync(host, view, () => result);
        }
        finally
        {
            viewModel.RequestClose -= OnClose;
        }
    }

    public async Task ShowRemoteLibraryEditorAsync(ViewModels.Pages.RemoteLibraryEditorViewModel viewModel)
    {
        // The browser head draws this form in HTML so its password manager can take part; the two
        // forms are twins (see Web/wwwroot/remote-library-editor.js) and change together.
        if (PlatformServices.RemoteLibraryEditorPresenter is { } present)
        {
            await present(viewModel);
            return;
        }

        if (Host is not { } host)
        {
            return;
        }

        void OnClose() => host.CloseOverlay();
        viewModel.RequestClose += OnClose;

        // Like the theme editor, the view carries its own title and buttons and scrolls itself.
        var view = new Views.Pages.RemoteLibraryEditorView { DataContext = viewModel };
        try
        {
            await host.ShowOverlayAsync(view);
        }
        finally
        {
            viewModel.RequestClose -= OnClose;
        }
    }

    public async Task<string?> PickLyricContentAsync()
    {
        if (ActiveTopLevel is not { } topLevel)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
        if (ActiveTopLevel is not { } topLevel)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Resources.VideoPlayer_SubtitlePickerTitle,
            AllowMultiple = false,
            FileTypeFilter = new[] { SubtitleType, FilePickerFileTypes.All },
        });

        return files.Count == 0 ? null : await DesktopDialogService.ReadPickedFileAsync(files[0]);
    }

    // Reuses the same view model and view as the desktop lyric search window, hosted in the overlay
    // instead of a native window. Completion (download / import / clear / cancel) is signaled by the
    // view model, which also delegates external-file picking back to this service.
    public async Task<LyricEditResult?> ShowLyricSearchAsync(
        string title,
        string artist,
        ILyricsService lyrics,
        IReadOnlyList<ExternalLrcInfo> initialCandidates)
    {
        if (Host is not { } host)
        {
            return null;
        }

        var viewModel = new LyricSearchDialogViewModel(lyrics, title, artist, initialCandidates)
        {
            LyricContentPicker = PickLyricContentAsync,
        };

        LyricEditResult? result = null;
        viewModel.RequestClose += r =>
        {
            result = r;
            host.CloseOverlay();
        };

        // The view brings its own header, search box, candidate list, and buttons, so it is hosted
        // directly (it manages its own scrolling) rather than wrapped in a title/body card.
        var view = new LyricSearchView { DataContext = viewModel };
        try
        {
            return await OverlayDialog.ShowForResultAsync(host, view, () => result);
        }
        finally
        {
            viewModel.Dispose();
        }
    }

    private Task ShowInfoAsync(
        string title,
        Control body,
        (string Label, Action OnClick)? extraButton = null)
    {
        return ShowInfoAsync(title, body, out _, extraButton);
    }

    private Task ShowInfoAsync(
        string title,
        Control body,
        out TextBlock header,
        (string Label, Action OnClick)? extraButton = null)
    {
        var host = Host!;
        var buttons = OverlayDialog.ButtonsRow();

        if (extraButton is { } extra)
        {
            var extraBtn = new Button { Content = extra.Label };
            extraBtn.Click += (_, _) => extra.OnClick();
            buttons.Children.Add(extraBtn);
        }

        var close = new Button { Content = Strings.Resources.Common_Close, IsDefault = true };
        close.Click += (_, _) => host.CloseOverlay();
        buttons.Children.Add(close);

        return host.ShowOverlayAsync(OverlayDialog.BuildContent(title, body, buttons, out header));
    }

    private static Control BuildPropertyRow(MediaPropertyItem item)
    {
        if (item.IsSection)
        {
            var heading = new TextBlock
            {
                Text = item.Key,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 14, 0, 2),
            };
            ApplyDynamicForeground(heading, "AppAccentBrush");
            return heading;
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            Margin = new Thickness(0, 5),
        };

        var key = new TextBlock { Text = item.Key, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        ApplyDynamicForeground(key, "AppSecondaryTextBrush");
        Grid.SetColumn(key, 0);

        var value = new TextBlock { Text = item.Value, TextWrapping = TextWrapping.Wrap };
        ApplyDynamicForeground(value, "AppPrimaryTextBrush");
        Grid.SetColumn(value, 1);

        grid.Children.Add(key);
        grid.Children.Add(value);
        return grid;
    }

    private static TextBlock CenteredText(string text, double size, FontWeight weight) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };

    private static TextBlock CenteredSecondary(string text, double size, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        ApplyDynamicForeground(block, "AppSecondaryTextBrush");
        return block;
    }

    private static void ApplyDynamicForeground(TextBlock text, string resourceKey) =>
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(resourceKey));

    private static async void CopyToClipboard(string text)
    {
        try
        {
            if (ActiveTopLevel?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch
        {
            // Clipboard access is best-effort.
        }
    }
}
