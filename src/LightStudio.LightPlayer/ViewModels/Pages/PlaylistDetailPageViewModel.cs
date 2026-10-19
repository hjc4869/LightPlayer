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

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Playlist detail page: the playlist header (title, item count, total
/// duration), the ordered entries as track rows, and playlist-level actions
/// (play, shuffle, queue, rename, delete, export).
/// </summary>
public sealed partial class PlaylistDetailPageViewModel : SelectionPageViewModelBase, IDisposable, IInlineDetailPage, INowPlayingHighlightTarget
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
    private MediaItemKey? nowPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    private bool hasItems;

    [ObservableProperty]
    private string headerSubtitle = string.Empty;

    // Its own header back button. Shown only when the page is on its own route and the shell has no
    // back button of its own. The narrow layout, the Apple layout, and hosts with their own back
    // navigation (Android, the browser) hide it in favor of a single global back affordance. When
    // hosted inline in the adaptive Playlists master-detail view, the host drives this instead (see
    // PlaylistsPageViewModel).
    [ObservableProperty]
    private bool showBackButton = !PlatformServices.HostProvidesBackNavigation;

    public PlaylistDetailPageViewModel(
        string playlistId,
        IPlaylistService service,
        IPlaylistDialogService dialogs,
        TrackActionCallbacks sharedTrackActions,
        Action<IReadOnlyList<PlaylistItemModel>, bool> play,
        Action<IReadOnlyList<PlaylistItemModel>> enqueue,
        Action goBack,
        LibraryCommands? selectionCommands = null)
    {
        this.playlistId = playlistId;
        this.service = service;
        this.dialogs = dialogs;
        this.play = play;
        this.enqueue = enqueue;
        this.goBack = goBack;
        TitleKey = nameof(Strings.Resources.Common_Playlist);

        // Take the shell's actions wholesale and add only the playlist-specific one, so an action
        // added to the shared set later cannot be silently dropped here.
        rowActions = sharedTrackActions with { RemoveFromPlaylist = RemoveRow };

        BackCommand = new RelayCommand(() =>
        {
            if (!TryHandleBack())
            {
                this.goBack();
            }
        });
        PlayCommand = new RelayCommand(() => this.play(CurrentItems(), false), () => HasItems);
        ShuffleCommand = new RelayCommand(() => this.play(CurrentItems(), true), () => HasItems);
        AddToQueueCommand = new RelayCommand(() => this.enqueue(CurrentItems()), () => HasItems);
        RenameCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(RenameAsync));
        DeleteCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(DeleteAsync));
        ExportCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(ExportAsync), () => HasItems);

        MultiSelectionActions.AddMusicActions(Selection, selectionCommands ?? new LibraryCommands
        {
            PlayTracks = tracks => play(tracks.Select(row => rowToItem[row]).ToArray(), false),
            ShuffleTracks = tracks => play(tracks.Select(row => rowToItem[row]).ToArray(), true),
            AddTracksToQueue = tracks => enqueue(tracks.Select(row => rowToItem[row]).ToArray()),
        });
        Selection.AddAction(nameof(Strings.Resources.Common_RemoveFromPlaylist), RemoveRowsAsync);
        PageSearch.SetSource(new PageSearchSource(
            () => PageSearchSource.Entries(Rows).Prepend(
                new PageSearchEntry(this, string.Join('\n', Title, HeaderSubtitle))), [Rows], this));
        Load();
    }

    // A standalone (route-hosted) detail page hides its own back button while the shell shows one.
    // Inline-hosted copies never receive shell layout changes (their host owns ShowBackButton), so
    // this stays inert there.
    protected override void OnHasShellBackButtonChanged(bool value) =>
        ShowBackButton = !PlatformServices.HostProvidesBackNavigation && !value;

    public ObservableCollection<TrackRowModel> Rows { get; } = new();

    /// <inheritdoc />
    public void SetNowPlaying(MediaItemKey? mediaItem)
    {
        nowPlaying = mediaItem;
        NowPlayingHighlight.Apply(Rows, nowPlaying);
    }

    public IRelayCommand BackCommand { get; }

    public IRelayCommand PlayCommand { get; }

    public IRelayCommand ShuffleCommand { get; }

    public IRelayCommand AddToQueueCommand { get; }

    public IAsyncRelayCommand RenameCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }

    public IAsyncRelayCommand ExportCommand { get; }

    public bool ShowEmpty => !HasItems;

    public bool ShowContent => HasItems;

    public bool CanDelete => playlist is not null && !playlist.IsBuiltIn;

    public string EmptyHeader => Strings.Resources.PlaylistDetail_EmptyHeader;

    public string EmptyDescription => Strings.Resources.PlaylistDetail_EmptyDescription;

    private void Load()
    {
        playlist = service.GetById(playlistId);
        if (playlist is null)
        {
            // Leave the generic localized "Playlist" title from the constructor as a fallback.
            HeaderSubtitle = string.Empty;
            UpdateState();
            return;
        }

        Title = playlist.Title;

        playlist.PropertyChanged += OnPlaylistPropertyChanged;
        playlist.Items.CollectionChanged += OnPlaylistItemsChanged;

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
            startTimeMs: item.StartTimeMs,
            sourceId: item.SourceId)
        {
            IsPlaylistItem = true,
            PlaybackContext = () => Rows,
        };

        rowToItem[row] = item;
        Rows.Add(row);
    }

    private async void RemoveRow(TrackRowModel row) =>
        await AppErrorReporter.GuardAsync(() => RemoveRowsAsync([row]));

    private async Task RemoveRowsAsync(IReadOnlyList<SelectionItemModel> selected)
    {
        if (disposed || suppressItemsSync)
        {
            return;
        }

        var removed = selected.Cast<TrackRowModel>().ToHashSet();
        var remaining = Rows.Where(row => !removed.Contains(row)).Select(row => rowToItem[row]).ToArray();

        suppressItemsSync = true;
        try
        {
            await service.ReplaceItemsAsync(playlistId, remaining);
            foreach (var row in removed)
            {
                rowToItem.Remove(row);
                Rows.Remove(row);
            }

            UpdateState();
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

        var name = await dialogs.PromptForTitleAsync(Strings.Resources.PlaylistDetail_RenameDialogHeader, Strings.Resources.Common_Rename, playlist.Title);
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
            Strings.Resources.PlaylistDetail_DeletePlaylist,
            string.Format(Strings.Resources.PlaylistDetail_DeleteConfirmFormat, playlist.Title),
            Strings.Resources.Common_Delete);
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

        await dialogs.SaveExportFileAsync(
            playlist.Title,
            async fileName => (await service.ExportAsync(playlistId, fileName))?.Content);
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
        Selection.Exit();
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
        Selection.SetItems(Rows);
        NowPlayingHighlight.Apply(Rows, nowPlaying);
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
        var label = count == 1 ? Strings.Resources.Common_OneItem : string.Format(Strings.Resources.Common_ItemsFormat, count);
        var total = rowToItem.Values.Aggregate(TimeSpan.Zero, (sum, item) => sum + item.Duration);
        return total > TimeSpan.Zero
            ? $"{label}  ·  {LibrarySortKeys.FormatDuration(total)}"
            : label;
    }

    private static string DeriveTitle(string? path) =>
        string.IsNullOrWhiteSpace(path) ? Strings.Resources.Common_UnknownTitle : Path.GetFileNameWithoutExtension(path);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        PageSearch.Close();
        Selection.Dispose();
        if (playlist is not null)
        {
            playlist.PropertyChanged -= OnPlaylistPropertyChanged;
            playlist.Items.CollectionChanged -= OnPlaylistItemsChanged;
        }
    }
}
