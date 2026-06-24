using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Playlist detail page: the playlist header (title, item count, total
/// duration), the ordered entries as track rows, and playlist-level actions
/// (play, shuffle, queue, rename, delete, export).
/// </summary>
public sealed partial class PlaylistDetailPageViewModel : PageViewModelBase, IDisposable
{
    private readonly string playlistId;
    private readonly IPlaylistService service;
    private readonly IPlaylistDialogService dialogs;
    private readonly Action<IReadOnlyList<PlaylistItemModel>, bool> play;
    private readonly Action<IReadOnlyList<PlaylistItemModel>> enqueue;
    private readonly Action goBack;
    private readonly TrackActionCallbacks rowActions;
    private readonly Dictionary<TrackRowModel, PlaylistItemModel> rowToItem = new();
    private PlaylistModel? playlist;
    private bool suppressItemsSync;
    private bool disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    private bool hasItems;

    [ObservableProperty]
    private string headerSubtitle = string.Empty;

    // The page's own header back button is hidden when the detail is hosted inline in
    // the adaptive Playlists master-detail view, where the container provides the back
    // affordance. It stays visible when the page is shown on its own route.
    [ObservableProperty]
    private bool showBackButton = true;

    public PlaylistDetailPageViewModel(
        string playlistId,
        IPlaylistService service,
        IPlaylistDialogService dialogs,
        TrackActionCallbacks sharedTrackActions,
        Action<IReadOnlyList<PlaylistItemModel>, bool> play,
        Action<IReadOnlyList<PlaylistItemModel>> enqueue,
        Action goBack)
    {
        this.playlistId = playlistId;
        this.service = service;
        this.dialogs = dialogs;
        this.play = play;
        this.enqueue = enqueue;
        this.goBack = goBack;
        Title = "Playlist";

        rowActions = new TrackActionCallbacks
        {
            Play = sharedTrackActions.Play,
            PlayNext = sharedTrackActions.PlayNext,
            AddToQueue = sharedTrackActions.AddToQueue,
            AddToPlaylist = sharedTrackActions.AddToPlaylist,
            ShowProperties = sharedTrackActions.ShowProperties,
            OpenContainingFolder = sharedTrackActions.OpenContainingFolder,
            RemoveFromPlaylist = RemoveRow,
        };

        BackCommand = new RelayCommand(() => this.goBack());
        PlayCommand = new RelayCommand(() => this.play(CurrentItems(), false), () => HasItems);
        ShuffleCommand = new RelayCommand(() => this.play(CurrentItems(), true), () => HasItems);
        AddToQueueCommand = new RelayCommand(() => this.enqueue(CurrentItems()), () => HasItems);
        RenameCommand = new AsyncRelayCommand(RenameAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => HasItems);

        Load();
    }

    public ObservableCollection<TrackRowModel> Rows { get; } = new();

    public IRelayCommand BackCommand { get; }

    public IRelayCommand PlayCommand { get; }

    public IRelayCommand ShuffleCommand { get; }

    public IRelayCommand AddToQueueCommand { get; }

    public IAsyncRelayCommand RenameCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }

    public IAsyncRelayCommand ExportCommand { get; }

    public bool ShowEmpty => !HasItems;

    public bool ShowContent => HasItems;

    public string EmptyHeader => "This playlist is empty";

    public string EmptyDescription => "Add music from the library or the now-playing queue.";

    private void Load()
    {
        playlist = service.GetById(playlistId);
        if (playlist is null)
        {
            Title = "Playlist";
            HeaderSubtitle = string.Empty;
            UpdateState();
            return;
        }

        playlist.PropertyChanged += OnPlaylistPropertyChanged;
        playlist.Items.CollectionChanged += OnPlaylistItemsChanged;
        Title = playlist.Title;

        foreach (var item in playlist.Items)
        {
            AddRow(item);
        }

        UpdateState();
    }

    private void AddRow(PlaylistItemModel item)
    {
        var row = new TrackRowModel(
            mediaFileId: 0,
            filePath: item.FilePath,
            title: string.IsNullOrWhiteSpace(item.Title) ? DeriveTitle(item.FilePath) : item.Title,
            artist: item.Artist,
            album: item.Album,
            year: string.Empty,
            genre: string.Empty,
            duration: LibrarySortKeys.FormatDuration(item.Duration),
            durationTime: item.Duration,
            discNumber: 0,
            trackNumber: 0,
            dateAdded: default,
            callbacks: rowActions,
            startTimeMs: item.StartTimeMs)
        {
            IsPlaylistItem = true,
            PlaybackContext = () => Rows,
        };

        rowToItem[row] = item;
        Rows.Add(row);
    }

    private async void RemoveRow(TrackRowModel row)
    {
        if (!rowToItem.Remove(row))
        {
            return;
        }

        Rows.Remove(row);
        UpdateState();

        // The replace below mutates the shared playlist collection; suppress the
        // resulting change notification so we don't rebuild the rows we just edited.
        suppressItemsSync = true;
        try
        {
            await service.ReplaceItemsAsync(playlistId, CurrentItems());
        }
        finally
        {
            suppressItemsSync = false;
        }
    }

    private IReadOnlyList<PlaylistItemModel> CurrentItems() =>
        Rows.Select(row => rowToItem[row]).ToList();

    private async Task RenameAsync()
    {
        if (playlist is null)
        {
            return;
        }

        var name = await dialogs.PromptForTitleAsync("Rename playlist", "Rename", playlist.Title);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await service.RenameAsync(playlistId, name);
        }
    }

    private async Task DeleteAsync()
    {
        if (playlist is null)
        {
            return;
        }

        var confirmed = await dialogs.ConfirmAsync(
            "Delete playlist",
            $"Delete \"{playlist.Title}\"? This cannot be undone.",
            "Delete");
        if (confirmed)
        {
            await service.DeleteAsync(playlistId);
            goBack();
        }
    }

    private async Task ExportAsync()
    {
        if (playlist is null)
        {
            return;
        }

        var path = await dialogs.PickExportFileAsync(playlist.Title);
        if (!string.IsNullOrWhiteSpace(path))
        {
            await service.ExportAsync(playlistId, path);
        }
    }

    private void OnPlaylistPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistModel.Title) && playlist is not null)
        {
            Title = playlist.Title;
        }
    }

    private void OnPlaylistItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reflect changes made elsewhere (e.g. the Like button on the playback
        // bar) in real time. Self-initiated edits set the guard to avoid a
        // redundant rebuild.
        if (suppressItemsSync)
        {
            return;
        }

        SyncRowsFromPlaylist();
    }

    private void SyncRowsFromPlaylist()
    {
        Rows.Clear();
        rowToItem.Clear();
        if (playlist is not null)
        {
            foreach (var item in playlist.Items)
            {
                AddRow(item);
            }
        }

        UpdateState();
    }

    private void UpdateState()
    {
        HasItems = Rows.Count > 0;
        HeaderSubtitle = BuildSubtitle();
        PlayCommand.NotifyCanExecuteChanged();
        ShuffleCommand.NotifyCanExecuteChanged();
        AddToQueueCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }

    private string BuildSubtitle()
    {
        var count = Rows.Count;
        var label = count == 1 ? "1 item" : $"{count} items";
        var total = rowToItem.Values.Aggregate(TimeSpan.Zero, (sum, item) => sum + item.Duration);
        return total > TimeSpan.Zero
            ? $"{label}  ·  {LibrarySortKeys.FormatDuration(total)}"
            : label;
    }

    private static string DeriveTitle(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "Unknown title" : Path.GetFileNameWithoutExtension(path);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (playlist is not null)
        {
            playlist.PropertyChanged -= OnPlaylistPropertyChanged;
            playlist.Items.CollectionChanged -= OnPlaylistItemsChanged;
        }
    }
}
