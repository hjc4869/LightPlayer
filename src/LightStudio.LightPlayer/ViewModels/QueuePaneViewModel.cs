using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// Backs the right-hand "Now Playing" queue pane: the queue items (owned by the
/// <see cref="PlaybackController"/>), open/closed state (persisted by the shell),
/// edit mode, and item actions.
/// </summary>
public partial class QueuePaneViewModel : ObservableObject
{
    private readonly PlaybackController controller;
    private bool selectAllPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaneWidth))]
    [NotifyPropertyChangedFor(nameof(LayoutWidth))]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    [NotifyPropertyChangedFor(nameof(IsTemporarilyOpen))]
    private bool isOpen = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaneWidth))]
    [NotifyPropertyChangedFor(nameof(IsTemporarilyOpen))]
    private bool isDragHintOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaneWidth))]
    [NotifyPropertyChangedFor(nameof(LayoutWidth))]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    [NotifyPropertyChangedFor(nameof(IsTemporarilyOpen))]
    private bool isOverlayMode;

    [ObservableProperty]
    private bool isEditMode;

    [ObservableProperty]
    private string playlistName = string.Empty;

    public QueuePaneViewModel(PlaybackController controller)
    {
        this.controller = controller;
        controller.PropertyChanged += OnControllerPropertyChanged;
        ToggleOpenCommand = new RelayCommand(() => IsOpen = !IsOpen);
        CloseCommand = new RelayCommand(() => IsOpen = false);
        ToggleEditCommand = new RelayCommand(() =>
        {
            IsEditMode = !IsEditMode;
            if (!IsEditMode)
            {
                selectAllPending = false;
            }
        });
        EnterEditModeCommand = new RelayCommand(() =>
        {
            EnsureOpen?.Invoke();
            IsEditMode = true;
        });
        ExitEditModeCommand = new RelayCommand(() =>
        {
            selectAllPending = false;
            IsEditMode = false;
        });
        SelectAllCommand = new RelayCommand(() =>
        {
            EnsureOpen?.Invoke();
            IsEditMode = true;
            selectAllPending = true;
            SelectAllRequested?.Invoke();
        });
        UnselectAllCommand = new RelayCommand(() => UnselectAllRequested?.Invoke());
        RemoveSelectedCommand = new RelayCommand(() => RemoveSelectedRequested?.Invoke());
        RemoveAllCommand = new RelayCommand(RemoveAll);
        SaveCommand = new RelayCommand(Save);
        PlayItemCommand = new RelayCommand<MusicPlaybackItemModel>(item =>
        {
            if (item is not null)
            {
                controller.PlayQueueItem(item);
            }
        });
        RemoveItemCommand = new RelayCommand<MusicPlaybackItemModel>(item =>
        {
            if (item is not null)
            {
                controller.RemoveItem(item);
            }
        });
        ShowPropertiesCommand = new RelayCommand<MusicPlaybackItemModel>(item =>
        {
            if (item is not null)
            {
                ShowProperties?.Invoke(item);
            }
        });
        OpenFolderCommand = new RelayCommand<MusicPlaybackItemModel>(item =>
        {
            if (item is not null)
            {
                OpenContainingFolder?.Invoke(item);
            }
        });
        OpenAlbumCommand = new RelayCommand<MusicPlaybackItemModel>(item =>
        {
            if (item is not null)
            {
                OpenAlbum?.Invoke(item);
            }
        });
        OpenArtistCommand = new RelayCommand<MusicPlaybackItemModel>(item =>
        {
            if (item is not null)
            {
                OpenArtist?.Invoke(item);
            }
        });
    }

    // Surface the controller's current-track change as our own CurrentItem notification so the
    // view can react (e.g. scroll the queue to the newly playing track).
    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackController.CurrentItem))
        {
            OnPropertyChanged(nameof(CurrentItem));
        }
    }

    /// <summary>Removes the supplied items from the queue in a single batch (edit mode).</summary>
    public void RemoveItems(IReadOnlyList<MusicPlaybackItemModel> items) =>
        controller.RemoveItems(items);

    /// <summary>The now-playing queue, owned and projected by the playback controller.</summary>
    public ObservableCollection<MusicPlaybackItemModel> Items => controller.QueueItems;

    /// <summary>The track currently playing (highlighted in the queue), or null when idle.
    /// Mirrors the controller so the view can react to track changes.</summary>
    public MusicPlaybackItemModel? CurrentItem => controller.CurrentItem;

    public bool IsPinned => IsOpen && !IsOverlayMode;

    public bool IsTemporarilyOpen => (IsDragHintOpen && !IsPinned) || (IsOpen && IsOverlayMode);

    public const double DefaultWidth = 360d;

    /// <summary>Width of the pinned pane. The shell narrows it in the Apple layout.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaneWidth))]
    [NotifyPropertyChangedFor(nameof(LayoutWidth))]
    private double pinnedWidth = DefaultWidth;

    /// <summary>Visible width of the pane; drives the open/close slide animation. Non-zero
    /// whenever the pane is shown, including temporary overlays.</summary>
    public double PaneWidth => IsTemporarilyOpen ? DefaultWidth : IsPinned ? PinnedWidth : 0d;

    /// <summary>Width the pane reserves in the shell layout. Only a pinned pane pushes page
    /// content aside; temporary overlays reserve nothing.</summary>
    public double LayoutWidth => IsPinned ? PinnedWidth : 0d;

    /// <summary>
    /// True while the queue is presented inside the narrow layout's now-playing sheet rather than as
    /// the wide layout's docked side panel. Both views are instantiated at once and share this view
    /// model, so they use this to tell which of them is the live one. Set by the shell.
    /// </summary>
    [ObservableProperty]
    private bool isSheetHosted;

    public Action? EnsureOpen { get; set; }

    /// <summary>
    /// Raised when something outside the queue asks it to bring the currently playing track back
    /// into view -- e.g. the now-playing sheet's "Up next" tab is tapped. This is equivalent to the wide
    /// layout's "Now Playing" header. Only the presentation currently on screen reacts.
    /// </summary>
    public event EventHandler? ScrollToCurrentRequested;

    public event Action? SelectAllRequested;

    public event Action? UnselectAllRequested;

    public event Action? RemoveSelectedRequested;

    public bool ConsumeSelectAllRequest()
    {
        if (!selectAllPending)
        {
            return false;
        }

        selectAllPending = false;
        return true;
    }

    public IRelayCommand ToggleOpenCommand { get; }

    public IRelayCommand CloseCommand { get; }

    public IRelayCommand ToggleEditCommand { get; }

    public IRelayCommand EnterEditModeCommand { get; }

    public IRelayCommand ExitEditModeCommand { get; }

    public IRelayCommand SelectAllCommand { get; }

    public IRelayCommand UnselectAllCommand { get; }

    public IRelayCommand RemoveSelectedCommand { get; }

    public IRelayCommand RemoveAllCommand { get; }

    public IRelayCommand SaveCommand { get; }

    public IRelayCommand<MusicPlaybackItemModel> PlayItemCommand { get; }

    public IRelayCommand<MusicPlaybackItemModel> RemoveItemCommand { get; }

    public IRelayCommand<MusicPlaybackItemModel> ShowPropertiesCommand { get; }

    public IRelayCommand<MusicPlaybackItemModel> OpenFolderCommand { get; }

    public IRelayCommand<MusicPlaybackItemModel> OpenAlbumCommand { get; }

    public IRelayCommand<MusicPlaybackItemModel> OpenArtistCommand { get; }

    /// <summary>
    /// Saves the current queue as a new playlist. Wired by the shell when the
    /// playlist services are available.
    /// </summary>
    public Func<string, Task>? SaveAsPlaylist { get; set; }

    /// <summary>
    /// Resolves a dragged library item (album/artist/playlist/track) to the ordered
    /// playback items to insert. Wired by the shell.
    /// </summary>
    public Func<LibraryInsertPayload, Task<IReadOnlyList<MusicPlaybackItemModel>>>? ResolveInsertItems { get; set; }

    /// <summary>
    /// Builds queue items from dropped external file paths (already ordered by the
    /// caller), expanding CUE sheets into their tracks. Wired by the shell.
    /// </summary>
    public Func<IReadOnlyList<string>, Task<IReadOnlyList<MusicPlaybackItemModel>>>? BuildFileItems { get; set; }

    /// <summary>Shows the file-properties dialog for a queue item. Wired by the shell.</summary>
    public Action<MusicPlaybackItemModel>? ShowProperties { get; set; }

    /// <summary>Opens the folder containing a queue item's file in the OS file manager. Wired by the shell.</summary>
    public Action<MusicPlaybackItemModel>? OpenContainingFolder { get; set; }

    /// <summary>Navigates to the album detail page for a queue item. Wired by the shell.</summary>
    public Action<MusicPlaybackItemModel>? OpenAlbum { get; set; }

    /// <summary>Navigates to the artist detail page for a queue item. Wired by the shell.</summary>
    public Action<MusicPlaybackItemModel>? OpenArtist { get; set; }

    /// <summary>Opens the pane for the duration of a drag without changing the persisted open state.</summary>
    public void BeginDragHint() => IsDragHintOpen = true;

    /// <summary>Reverts the transient drag-open, returning to the persisted open/closed state.</summary>
    public void EndDragHint() => IsDragHintOpen = false;

    /// <summary>
    /// Asks the pane view to scroll the currently playing track to the top of the queue viewport.
    /// </summary>
    public void RequestScrollToCurrent() => ScrollToCurrentRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Reorders dragged queue items to <paramref name="targetIndex"/> (internal reorder drop).</summary>
    public void MoveItems(IReadOnlyList<MusicPlaybackItemModel> items, int targetIndex) =>
        controller.MoveItems(items, targetIndex);

    /// <summary>Resolves and inserts a dragged library item at <paramref name="index"/>.</summary>
    public async Task InsertLibraryPayloadAsync(LibraryInsertPayload payload, int index)
    {
        if (ResolveInsertItems is null)
        {
            return;
        }

        var items = await ResolveInsertItems(payload);
        if (items is { Count: > 0 })
        {
            controller.InsertItems(items, index);
        }
    }

    /// <summary>Inserts dropped external files (already ordered by file name) at <paramref name="index"/>.</summary>
    public async Task InsertFilesAsync(IReadOnlyList<string> orderedPaths, int index)
    {
        if (BuildFileItems is null || orderedPaths.Count == 0)
        {
            return;
        }

        var items = await BuildFileItems(orderedPaths);
        if (items is { Count: > 0 })
        {
            controller.InsertItems(items, index);
        }
    }

    private void RemoveAll()
    {
        if (Items.Count == 0)
        {
            return;
        }

        controller.RemoveItems(Items.ToArray());
        IsEditMode = false;
    }

    private async void Save()
    {
        var name = PlaylistName?.Trim();
        if (SaveAsPlaylist is not null && !string.IsNullOrWhiteSpace(name))
        {
            await SaveAsPlaylist(name);
        }

        PlaylistName = string.Empty;
        IsEditMode = false;
    }
}
