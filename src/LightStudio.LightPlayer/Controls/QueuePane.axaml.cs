using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.ViewModels;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Controls;

public partial class QueuePane : UserControl
{
    private readonly DragInitiator reorderDrag = new();
    private QueuePaneViewModel? subscribedViewModel;
    private MusicPlaybackItemModel? pressedItem;
    private string? loggedOverSignature;
    private bool scrollToCurrentQueued;

    // Fixed queue row height, matching the Border.queueItem styles (compact in the Apple layout's
    // docked pane). Used to scroll the current track to the top by computing its offset directly,
    // without realizing the rows above it.
    private double RowHeight => IsCompact ? 40d : 48d;

    // Matches the Panel.playingSlot styles.
    private double PlayingSlotWidth => IsCompact ? 7d : 10d;

    private bool IsCompact =>
        PaneBorder.Classes.Contains("apple") && !PaneBorder.Classes.Contains("floating") && !IsFullPage;

    /// <summary>
    /// When true the pane stretches to fill its host and drops its own header (used when the queue
    /// is presented as the narrow layout's now-playing sheet page, which supplies the title bar).
    /// When false the side panel keeps a fixed width during its opening and closing animation.
    /// </summary>
    public static readonly StyledProperty<bool> IsFullPageProperty =
        AvaloniaProperty.Register<QueuePane, bool>(nameof(IsFullPage));

    public bool IsFullPage
    {
        get => GetValue(IsFullPageProperty);
        set => SetValue(IsFullPageProperty, value);
    }

    public QueuePane()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;

        // The queue is a drop target for reordering, library items and external files.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, handledEventsToo: true);

        // Reorder drag source. ReleaseSelectingListBox commits selection on pointer release, so
        // these handlers never touch selection directly: they arm/drive the reorder drag and, on
        // release, suppress the ListBox's native selection only when a drag actually happened (the
        // moved rows must stay selected) or when the queue isn't in edit mode (no selection is
        // shown then). Release runs in the tunnel pass so that suppression precedes the ListBoxItem's
        // own bubble-pass selection; move/release also listen for handled events to finish the drag.
        QueueList.AddHandler(PointerPressedEvent, OnQueuePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
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
            subscribedViewModel.ScrollToCurrentRequested -= OnScrollToCurrentRequested;
            subscribedViewModel.SelectAllRequested -= OnSelectAllRequested;
            subscribedViewModel.UnselectAllRequested -= OnUnselectAllRequested;
            subscribedViewModel.RemoveSelectedRequested -= OnRemoveSelectedRequested;
        }

        subscribedViewModel = ViewModel;

        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            subscribedViewModel.ScrollToCurrentRequested += OnScrollToCurrentRequested;
            subscribedViewModel.SelectAllRequested += OnSelectAllRequested;
            subscribedViewModel.UnselectAllRequested += OnUnselectAllRequested;
            subscribedViewModel.RemoveSelectedRequested += OnRemoveSelectedRequested;
        }

        ApplyEditMode();
        ApplyPendingSelection();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(QueuePaneViewModel.IsEditMode):
                ApplyEditMode();
                break;
            case nameof(QueuePaneViewModel.IsSheetHosted):
                ApplyPendingSelection();
                break;

            // Opening the pane jumps to the current track so it's the first thing shown.
            case nameof(QueuePaneViewModel.IsOpen) when ViewModel?.IsOpen == true:
                ScrollCurrentItemToTop();
                break;

            // A track change re-centers on the new track, but only when the pointer isn't
            // in the pane, so a user interacting with the queue isn't scrolled out from under.
            case nameof(QueuePaneViewModel.CurrentItem) when !IsPointerOver:
                ScrollCurrentItemToTop();
                break;
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

    /// <summary>
    /// Scrolls the queue so the currently playing item sits at the top. Wired to the
    /// "Now Playing" header so the user can jump back to the current track at any time.
    /// </summary>
    private void OnNowPlayingHeaderTapped(object? sender, TappedEventArgs e) => ScrollCurrentItemToTop();

    // Something outside the queue asked it to re-center on the current track (the now-playing
    // sheet's "Up next" tab). Both presentations share one view model, so only the one on screen
    // acts on the request.
    private void OnScrollToCurrentRequested(object? sender, EventArgs e)
    {
        if (IsActivePresentation)
        {
            ScrollCurrentItemToTop();
        }
    }

    // The queue has two views alive at once (the wide layout's docked panel and the narrow layout's
    // now-playing sheet page) sharing one view model, so a broadcast request is only acted on by
    // whichever of them is currently on screen.
    private bool IsActivePresentation => IsFullPage == (ViewModel?.IsSheetHosted == true);

    private void OnSelectAllRequested()
    {
        if (IsActivePresentation)
        {
            QueueList.SelectAll();
            ViewModel?.ConsumeSelectAllRequest();
        }
    }

    private void OnUnselectAllRequested()
    {
        if (IsActivePresentation)
        {
            QueueList.UnselectAll();
        }
    }

    private void OnRemoveSelectedRequested()
    {
        if (IsActivePresentation)
        {
            DeleteSelected();
        }
    }

    private void ApplyPendingSelection()
    {
        if (IsActivePresentation
            && ViewModel?.IsEditMode == true
            && ViewModel.ConsumeSelectAllRequest())
        {
            QueueList.SelectAll();
        }
    }

    // When hosted by the now-playing sheet, scroll to the current track as the pane appears so the
    // "Up next" page lands on what's playing, matching the docked pane's open behavior.
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (IsFullPage)
        {
            ScrollCurrentItemToTop();
        }

        ApplyPendingSelection();
    }

    /// <summary>
    /// Requests that the currently playing row be brought to the top of the queue viewport.
    /// The work is deferred and coalesced: a track change frequently coincides with a wholesale
    /// queue replacement (switching albums), whose single reset notification the virtualizing
    /// panel is still reconciling. Scrolling mid-reset leaves stale, overlapping rows, so we let
    /// the reset and its layout settle on a later dispatcher turn before touching the list.
    /// </summary>
    private void ScrollCurrentItemToTop()
    {
        if (scrollToCurrentQueued)
        {
            return;
        }

        scrollToCurrentQueued = true;
        Dispatcher.UIThread.Post(AlignCurrentItemToTop, DispatcherPriority.Background);
    }

    private void AlignCurrentItemToTop()
    {
        scrollToCurrentQueued = false;

        if (ViewModel is not { } vm)
        {
            return;
        }

        var index = -1;
        for (var i = 0; i < vm.Items.Count; i++)
        {
            if (vm.Items[i].IsPlaying)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        // Fixed-height rows, so the current row's top offset is a direct multiply. This
        // deliberately avoids ListBox.ScrollIntoView: for a row that isn't realized yet (common
        // once the queue holds thousands of tracks and the playing track is far down), Avalonia
        // brings it into view by forcing several synchronous full-window layout passes, which
        // made opening the pane take seconds. The ScrollViewer clamps the offset near the end of
        // the list, so the row still lands as close to the top as it can.
        if (QueueList.FindDescendantOfType<ScrollViewer>() is { } scrollViewer)
        {
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, index * RowHeight);
        }
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

    private void OnOpenAlbumClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MusicPlaybackItemModel item })
        {
            ViewModel?.OpenAlbumCommand.Execute(item);
        }
    }

    private void OnOpenArtistClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MusicPlaybackItemModel item })
        {
            ViewModel?.OpenArtistCommand.Execute(item);
        }
    }

    private void OnDeleteItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MusicPlaybackItemModel item })
        {
            ViewModel?.RemoveItemCommand.Execute(item);
        }
    }

    private void OnPropertiesItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MusicPlaybackItemModel item })
        {
            ViewModel?.ShowPropertiesCommand.Execute(item);
        }
    }

    private void OnOpenFolderItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MusicPlaybackItemModel item })
        {
            ViewModel?.OpenFolderCommand.Execute(item);
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

        if (pressedItem is null)
        {
            reorderDrag.Disarm();
            return;
        }

        // Only arm the reorder drag. The press is deliberately left unhandled: selection is applied
        // on release by ReleaseSelectingListBox, so the press mustn't change it (that's what lets a
        // drag begin without disturbing the selection), and leaving it unhandled preserves the
        // native focus-on-press the ListBox needs for Ctrl+A and arrow-key navigation.
        reorderDrag.Arm(e, QueueList);
    }

    private void OnQueuePointerMoved(object? sender, PointerEventArgs e) =>
        reorderDrag.Update(e, QueueList, BuildReorderData, DragDropEffects.Move);

    private void OnQueuePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // A right-click never selects (ReleaseSelectingListBox.ShouldTriggerSelection reacts only to
        // the left button) and never starts a reorder drag, so it must stay unhandled: Avalonia raises
        // ContextRequested from the release only while it is unhandled, and that is what opens a row's
        // context menu. Handling it here is why the menu didn't appear outside edit mode.
        var isRightRelease = e.InitialPressMouseButton == MouseButton.Right;

        // Selection is committed natively on release by ReleaseSelectingListBox. Suppress it when
        // this release ends a reorder drag (the moved rows must stay selected) or when we're not in
        // edit mode (the queue shows no selection outside editing; a click there only plays a row).
        // A plain edit-mode click falls through so the ListBox selects/toggles it natively.
        if (!isRightRelease && (reorderDrag.HasStarted || ViewModel?.IsEditMode != true))
        {
            e.Handled = true;
        }

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
            await AppErrorReporter.GuardAsync(() => vm.InsertLibraryPayloadAsync(library, index));
            return;
        }

        var paths = PlatformServices.SupportsLocalFileAccess
            ? ExtractMediaFilePaths(e.DataTransfer)
            : [];
        if (paths.Count > 0)
        {
            DragDropLog.Write($"QueuePane.Drop files count={paths.Count} index={index}");
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            await AppErrorReporter.GuardAsync(() => vm.InsertFilesAsync(paths, index));
        }
        else
        {
            DragDropLog.Write("QueuePane.Drop no media files resolved from the drop");
        }
    }

    /// <summary>
    /// Resolves dropped external files to local media file paths, ordered by file name. Folders are
    /// ignored (no media extension). Video containers, disc images and pictures are collected alongside
    /// audio: the shell takes them out of the insert and opens them on the video player or the photo
    /// viewer, so dropping one anywhere over the queue matches opening it from the file manager.
    /// Falls back to parsing a <c>text/uri-list</c> surfaced as text when the platform doesn't expose
    /// the files as storage items.
    /// </summary>
    private static System.Collections.Generic.List<string> ExtractMediaFilePaths(IDataTransfer data)
    {
        var result = new System.Collections.Generic.List<string>();

        var files = data.TryGetFiles();
        if (files is not null)
        {
            foreach (var item in files)
            {
                var path = item.TryGetLocalPath();
                DragDropLog.Write($"  file item type={item.GetType().Name} localPath={(path ?? "<null>")}");
                if (IsMediaPath(path))
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
                        if (IsMediaPath(path))
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

    /// <summary>
    /// Whether a dropped path is worth handing to the shell. Extensions are only a first sieve to
    /// keep documents and folders out of a drop meant for media: what a file actually holds -- and so
    /// whether it plays, shows, or queues -- is settled by probing it in the shell.
    /// </summary>
    private static bool IsMediaPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return QueueDragFormats.AudioExtensions.Contains(extension)
            || MediaContainerFormats.Video.Contains(extension)
            || (MediaContainerFormats.DiscImage.Contains(extension) && SupportsDiscImages())
            || MediaContainerFormats.Image.Contains(extension);
    }

    private static bool SupportsDiscImages() =>
        IMultiMediaPlatformIO.Instance is IVideoDiscPlatformIO platform &&
        (platform.SupportsVideoDisc(VideoMediaKind.Dvd) ||
            platform.SupportsVideoDisc(VideoMediaKind.BluRay));

    private static bool IsAcceptable(DragEventArgs e) =>
        e.DataTransfer.Contains(QueueDragFormats.QueueReorder)
        || e.DataTransfer.Contains(QueueDragFormats.LibraryInsert)
        || (PlatformServices.SupportsLocalFileAccess && e.DataTransfer.Contains(DataFormat.File));

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
        DropIndicator.Margin = new Thickness(QueueList.Padding.Left + PlayingSlotWidth, Math.Max(0, y - 1),
            QueueList.Padding.Right, 0);
        DropIndicator.IsVisible = true;
    }

    private void HideDropIndicator() => DropIndicator.IsVisible = false;

    private static MusicPlaybackItemModel? ItemFrom(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as MusicPlaybackItemModel;
}
