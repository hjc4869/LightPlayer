using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels;

namespace LightStudio.LightPlayer.Views;

public partial class ShellView : UserControl
{
    private static readonly FilePickerFileType AudioFiles = new("Audio files")
    {
        Patterns = new[]
        {
            "*.mp3", "*.flac", "*.wav", "*.m4a", "*.aac", "*.ogg",
            "*.opus", "*.wma", "*.aiff", "*.aif", "*.alac", "*.ape", "*.cue",
        },
        MimeTypes = new[] { "audio/*" },
    };

    // Tracks how deeply the drag is nested in drop targets across the window, so the
    // now-playing pane is only revealed while a queue drag is actually over the window.
    private int dragHintDepth;

    public ShellView()
    {
        InitializeComponent();

        // Enable window-wide drag detection (AllowDrop inherits to every descendant) so the
        // now-playing pane can be revealed the moment a queue drag enters the window, wherever
        // it lands. The pane itself handles the real drop; these handlers only toggle the hint.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnShellDragEnter, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnShellDragLeave, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnShellDrop, handledEventsToo: true);
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void OnShellDragEnter(object? sender, DragEventArgs e)
    {
        DragDropLog.Write($"ShellView.DragEnter isQueueDrag={IsQueueDrag(e)} depth={dragHintDepth} formats=[{DragDropLog.DescribeFormats(e.DataTransfer)}]");

        if (!IsQueueDrag(e))
        {
            return;
        }

        dragHintDepth++;
        Shell?.Queue.BeginDragHint();
    }

    private void OnShellDragLeave(object? sender, DragEventArgs e)
    {
        if (dragHintDepth == 0)
        {
            return;
        }

        dragHintDepth--;
        DragDropLog.Write($"ShellView.DragLeave depth={dragHintDepth}");
        if (dragHintDepth != 0)
        {
            return;
        }

        // A drag moving between two child drop targets raises DragLeave (old) before
        // DragEnter (new), so the depth dips to zero mid-move. Defer the close and re-check:
        // a following DragEnter will have restored the depth, leaving the pane open. The depth
        // only stays at zero once the drag has truly left the window.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (dragHintDepth == 0)
                {
                    Shell?.Queue.EndDragHint();
                }
            },
            DispatcherPriority.Background);
    }

    private void OnShellDrop(object? sender, DragEventArgs e)
    {
        DragDropLog.Write($"ShellView.Drop depth={dragHintDepth} formats=[{DragDropLog.DescribeFormats(e.DataTransfer)}]");
        dragHintDepth = 0;
        Shell?.Queue.EndDragHint();
    }

    private static bool IsQueueDrag(DragEventArgs e) =>
        e.DataTransfer.Contains(QueueDragFormats.QueueReorder)
        || e.DataTransfer.Contains(QueueDragFormats.LibraryInsert)
        || e.DataTransfer.Contains(DataFormat.File);


    private async void OnOpenFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open audio files",
            AllowMultiple = true,
            FileTypeFilter = new[] { AudioFiles, FilePickerFileTypes.All },
        });

        var paths = new List<string>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
        }

        if (paths.Count > 0)
        {
            shell.AddFilesToQueue(paths, play: true);
        }
    }

    private void OnExpandAndFocusSearch(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.Navigation.IsExpanded = true;
        }

        // Focus after the search box has been made visible and laid out.
        Dispatcher.UIThread.Post(() => RailSearchBox?.FocusInput(), DispatcherPriority.Loaded);
    }

    private async void OnViewWarningsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell || string.IsNullOrWhiteSpace(shell.WarningDetailsText))
        {
            return;
        }

        var details = shell.WarningDetailsText;
        var dialog = new Window
        {
            Title = string.IsNullOrWhiteSpace(shell.WarningText) ? "Details" : shell.WarningText,
            Width = 620,
            Height = 420,
            MinWidth = 420,
            MinHeight = 260,
        };

        var copyButton = new Button { Content = "Copy" };
        copyButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(dialog)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(details);
            }
        };

        var closeButton = new Button
        {
            Content = "Close",
            IsDefault = true,
        };
        closeButton.Click += (_, _) => dialog.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new global::Avalonia.Thickness(0, 12, 0, 0),
            Children = { copyButton, closeButton },
        };

        var content = new DockPanel
        {
            Margin = new global::Avalonia.Thickness(18),
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

        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }

        // The error has now been viewed; clear the banner so it does not linger.
        shell.ClearWarnings();
    }
}