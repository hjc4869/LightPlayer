using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels;

namespace LightStudio.LightPlayer.Controls;

public partial class QueuePane : UserControl
{
    private readonly DragInitiator reorderDrag = new();
    private QueuePaneViewModel? subscribedViewModel;
    private MusicPlaybackItemModel? pressedItem;
    private string? loggedOverSignature;

    public QueuePane()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // The queue is a drop target for reordering, library items and external files.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, handledEventsToo: true);

        // Reorder drag source. ListBoxItem handles the press for selection, so listen even
        // for already-handled pointer events.
        QueueList.AddHandler(PointerPressedEvent, OnQueuePointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        QueueList.AddHandler(PointerMovedEvent, OnQueuePointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        QueueList.AddHandler(PointerReleasedEvent, OnQueuePointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        QueueList.AddHandler(PointerCaptureLostEvent, OnQueuePointerCaptureLost, handledEventsToo: true);
    }

    private QueuePaneViewModel? ViewModel => DataContext as QueuePaneViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        subscribedViewModel = ViewModel;

        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyEditMode();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueuePaneViewModel.IsEditMode))
        {
            ApplyEditMode();
        }
    }

    /// <summary>
    /// A multi-select session while editing (toggle-per-click plus shift-range 
    /// and the native Ctrl+A), and no selection at all otherwise. Selection is
    /// cleared on every mode change so each editing session starts fresh and 
    /// no highlight lingers afterwards.
    /// </summary>
    private void ApplyEditMode()
    {
        var editing = ViewModel?.IsEditMode == true;
        QueueList.UnselectAll();
        QueueList.SelectionMode = editing
            ? SelectionMode.Multiple | SelectionMode.Toggle
            : SelectionMode.Single;
    }

    private void OnItemTapped(object? sender, TappedEventArgs e)
    {
        // Touch users expect a single tap to play; mouse/pen keep the double-tap
        // behavior handled by OnItemDoubleTapped.
        if (e.Pointer.Type == PointerType.Touch)
        {
            TryPlayItem(sender);
        }
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Touch already played on the single tap (see OnItemTapped); don't replay.
        if (e.Pointer.Type != PointerType.Touch)
        {
            TryPlayItem(sender);
        }
    }

    private void TryPlayItem(object? sender)
    {
        // While editing, taps drive selection rather than playback.
        if (ViewModel?.IsEditMode == true)
        {
            return;
        }

        if (sender is Control { DataContext: MusicPlaybackItemModel item })
        {
            ViewModel?.PlayItemCommand.Execute(item);
        }
    }

    private void OnPlayItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MusicPlaybackItemModel item })
        {
            ViewModel?.PlayItemCommand.Execute(item);
        }
    }

    private void OnDeleteItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MusicPlaybackItemModel item })
        {
            ViewModel?.RemoveItemCommand.Execute(item);
        }
    }

    private void OnDeleteSelectedClick(object? sender, RoutedEventArgs e) => DeleteSelected();

    private void OnQueueKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel?.IsEditMode != true)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Delete:
                DeleteSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                ViewModel.IsEditMode = false;
                e.Handled = true;
                break;
        }
    }

    private void DeleteSelected()
    {
        if (ViewModel is not { IsEditMode: true } viewModel)
        {
            return;
        }

        var selected = QueueList.SelectedItems?
            .OfType<MusicPlaybackItemModel>()
            .ToArray();

        if (selected is { Length: > 0 })
        {
            viewModel.RemoveItems(selected);
        }

        // Always leaves edit mode afterwards.
        viewModel.IsEditMode = false;
    }

    // ---- Reorder drag source -------------------------------------------

    private void OnQueuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        pressedItem = ItemFrom(e.Source);
        if (pressedItem is not null)
        {
            reorderDrag.Arm(e, QueueList);
        }
        else
        {
            reorderDrag.Disarm();
        }
    }

    private void OnQueuePointerMoved(object? sender, PointerEventArgs e) =>
        reorderDrag.Update(e, QueueList, BuildReorderData, DragDropEffects.Move);

    private void OnQueuePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        reorderDrag.Disarm();
        pressedItem = null;
    }

    private void OnQueuePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        reorderDrag.Disarm();
        pressedItem = null;
    }

    private IDataTransfer? BuildReorderData()
    {
        if (pressedItem is not { } item || ViewModel is not { } vm)
        {
            return null;
        }

        // In edit mode, dragging one of the selected rows moves the whole selection; otherwise
        // (or when dragging an unselected row) only the pressed row moves.
        var moving = vm.IsEditMode
            && QueueList.SelectedItems?.OfType<MusicPlaybackItemModel>().Contains(item) == true
            ? QueueList.SelectedItems!.OfType<MusicPlaybackItemModel>()
                .OrderBy(vm.Items.IndexOf)
                .ToArray()
            : new[] { item };

        if (moving.Length == 0)
        {
            return null;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(QueueDragFormats.QueueReorder, new QueueReorderPayload(moving)));
        return data;
    }

    // ---- Drop target ---------------------------------------------------

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        loggedOverSignature = null;
        DragDropLog.Write($"QueuePane.DragEnter accepted={IsAcceptable(e)} formats=[{DragDropLog.DescribeFormats(e.DataTransfer)}]");
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Log once per distinct format set while hovering, so DragOver's stream doesn't spam.
        var signature = DragDropLog.DescribeFormats(e.DataTransfer);
        if (signature != loggedOverSignature)
        {
            loggedOverSignature = signature;
            DragDropLog.Write($"QueuePane.DragOver accepted={IsAcceptable(e)} formats=[{signature}]");
        }

        if (!IsAcceptable(e))
        {
            e.DragEffects = DragDropEffects.None;
            HideDropIndicator();
            return;
        }

        ResolveDropIndex(e.GetPosition(QueueList), out var indicatorY);
        ShowDropIndicator(indicatorY);
        e.DragEffects = e.DataTransfer.Contains(QueueDragFormats.QueueReorder)
            ? DragDropEffects.Move
            : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        loggedOverSignature = null;
        HideDropIndicator();
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        loggedOverSignature = null;
        HideDropIndicator();

        DragDropLog.Write($"QueuePane.Drop accepted={IsAcceptable(e)} formats=[{DragDropLog.DescribeFormats(e.DataTransfer)}]");

        if (ViewModel is not { } vm || !IsAcceptable(e))
        {
            return;
        }

        var index = ResolveDropIndex(e.GetPosition(QueueList), out _);

        if (e.DataTransfer.TryGetValue(QueueDragFormats.QueueReorder) is { } reorder)
        {
            DragDropLog.Write($"QueuePane.Drop reorder count={reorder.Items.Count} index={index}");
            vm.MoveItems(reorder.Items, index);
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (e.DataTransfer.TryGetValue(QueueDragFormats.LibraryInsert) is { } library)
        {
            DragDropLog.Write($"QueuePane.Drop library kind={library.Kind} index={index}");
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            await vm.InsertLibraryPayloadAsync(library, index);
            return;
        }

        var paths = ExtractAudioFilePaths(e.DataTransfer);
        if (paths.Count > 0)
        {
            DragDropLog.Write($"QueuePane.Drop files count={paths.Count} index={index}");
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            await vm.InsertFilesAsync(paths, index);
        }
        else
        {
            DragDropLog.Write("QueuePane.Drop no audio files resolved from the drop");
        }
    }

    /// <summary>
    /// Resolves dropped external files to local audio file paths, ordered by file name.
    /// Folders are ignored (no audio extension). Falls back to parsing a <c>text/uri-list</c>
    /// surfaced as text when the platform doesn't expose the files as storage items.
    /// </summary>
    private static System.Collections.Generic.List<string> ExtractAudioFilePaths(IDataTransfer data)
    {
        var result = new System.Collections.Generic.List<string>();

        var files = data.TryGetFiles();
        if (files is not null)
        {
            foreach (var item in files)
            {
                var path = item.TryGetLocalPath();
                DragDropLog.Write($"  file item type={item.GetType().Name} localPath={(path ?? "<null>")}");
                if (IsAudioPath(path))
                {
                    result.Add(path!);
                }
            }
        }
        else
        {
            DragDropLog.Write("  TryGetFiles() returned null");

            // Some providers surface dropped files only as a text/uri-list string.
            var text = data.TryGetText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                DragDropLog.Write($"  fallback text length={text!.Length}");
                foreach (var line in text.Split('\n'))
                {
                    var trimmed = line.Trim().Trim('\r');
                    if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    {
                        continue;
                    }

                    if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile)
                    {
                        var path = uri.LocalPath;
                        if (IsAudioPath(path))
                        {
                            result.Add(path);
                        }
                    }
                }
            }
        }

        result.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static bool IsAudioPath(string? path) =>
        !string.IsNullOrEmpty(path) && QueueDragFormats.AudioExtensions.Contains(Path.GetExtension(path));

    private static bool IsAcceptable(DragEventArgs e) =>
        e.DataTransfer.Contains(QueueDragFormats.QueueReorder)
        || e.DataTransfer.Contains(QueueDragFormats.LibraryInsert)
        || e.DataTransfer.Contains(DataFormat.File);

    /// <summary>
    /// Computes the queue index where dropped content should be inserted from the pointer's
    /// vertical position, using the midpoint of each realized row. Returns the row count
    /// (append) when the pointer is below the last realized row. <paramref name="indicatorY"/>
    /// is the boundary position in <c>QueueList</c> coordinates for the drop indicator.
    /// </summary>
    private int ResolveDropIndex(Point position, out double indicatorY)
    {
        indicatorY = 0;
        var count = ViewModel?.Items.Count ?? 0;
        if (count == 0)
        {
            return 0;
        }

        var lastIndex = -1;
        double lastBottom = 0;
        for (var i = 0; i < count; i++)
        {
            if (QueueList.ContainerFromIndex(i) is not Control container)
            {
                continue;
            }

            var top = container.TranslatePoint(default, QueueList)?.Y ?? 0;
            var height = container.Bounds.Height;
            if (position.Y < top + height / 2)
            {
                indicatorY = top;
                return i;
            }

            lastIndex = i;
            lastBottom = top + height;
        }

        if (lastIndex >= 0)
        {
            indicatorY = lastBottom;
            return lastIndex + 1;
        }

        return 0;
    }

    private void ShowDropIndicator(double y)
    {
        DropIndicator.Margin = new Thickness(22, Math.Max(0, y - 1), 12, 0);
        DropIndicator.IsVisible = true;
    }

    private void HideDropIndicator() => DropIndicator.IsVisible = false;

    private static MusicPlaybackItemModel? ItemFrom(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as MusicPlaybackItemModel;
}
