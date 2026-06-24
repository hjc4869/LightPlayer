using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaneWidth))]
    [NotifyPropertyChangedFor(nameof(LayoutWidth))]
    private bool isOpen = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaneWidth))]
    private bool isTemporarilyOpen;

    [ObservableProperty]
    private bool isEditMode;

    [ObservableProperty]
    private string playlistName = string.Empty;

    public QueuePaneViewModel(PlaybackController controller)
    {
        this.controller = controller;
        ToggleOpenCommand = new RelayCommand(() => IsOpen = !IsOpen);
        ToggleEditCommand = new RelayCommand(() => IsEditMode = !IsEditMode);
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
    }

    /// <summary>Removes the supplied items from the queue in a single batch (edit mode).</summary>
    public void RemoveItems(IReadOnlyList<MusicPlaybackItemModel> items) =>
        controller.RemoveItems(items);

    /// <summary>The now-playing queue, owned and projected by the playback controller.</summary>
    public ObservableCollection<MusicPlaybackItemModel> Items => controller.QueueItems;

    /// <summary>Visible width of the pane; drives the open/close slide animation. Non-zero
    /// whenever the pane is shown, including a transient drag hint (which overlays content).</summary>
    public double PaneWidth => IsOpen || IsTemporarilyOpen ? 360d : 0d;

    /// <summary>Width the pane reserves in the shell layout. Only a persistent open pushes page
    /// content aside; a transient drag hint reserves nothing so the pane overlays instead.</summary>
    public double LayoutWidth => IsOpen ? 360d : 0d;

    public IRelayCommand ToggleOpenCommand { get; }

    public IRelayCommand ToggleEditCommand { get; }

    public IRelayCommand SaveCommand { get; }

    public IRelayCommand<MusicPlaybackItemModel> PlayItemCommand { get; }

    public IRelayCommand<MusicPlaybackItemModel> RemoveItemCommand { get; }

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

    /// <summary>Opens the pane for the duration of a drag without changing the persisted open state.</summary>
    public void BeginDragHint() => IsTemporarilyOpen = true;

    /// <summary>Reverts the transient drag-open, returning to the persisted open/closed state.</summary>
    public void EndDragHint() => IsTemporarilyOpen = false;

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
