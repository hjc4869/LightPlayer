using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Playlists page: an adaptive master-detail view that lists the user's playlists
/// alongside the selected playlist's contents. The panes sit side by side on wide
/// screens and collapse to a single pane (list, then detail with a back button) on
/// narrow screens.
/// </summary>
/// <remarks>
/// Music and video playlists live in stores of their own but share this one list, told apart by
/// their icon and by which detail page the selection opens.
/// </remarks>
public sealed partial class PlaylistsPageViewModel : PageViewModelBase, IInPageBackHandler, IInPageDetailState, IDisposable
{
    private readonly IPlaylistService musicService;
    private readonly IPlaylistService videoService;
    private readonly IPlaylistDialogService dialogs;
    private readonly Func<PlaylistModel, Action, PageViewModelBase> detailFactory;
    private readonly Action<IReadOnlyList<PlaylistItemModel>, bool> play;
    private readonly Action<IReadOnlyList<PlaylistItemModel>> playNext;
    private readonly Action<IReadOnlyList<PlaylistItemModel>> enqueue;
    private bool isCompact;
    private string? pendingDetailKey;
    private bool disposed;

    [ObservableProperty]
    private PlaylistModel? selectedPlaylist;

    [ObservableProperty]
    private PageViewModelBase? selectedDetail;

    public PlaylistsPageViewModel(
        IPlaylistService musicService,
        IPlaylistService videoService,
        IPlaylistDialogService dialogs,
        Func<PlaylistModel, Action, PageViewModelBase> detailFactory,
        Action<IReadOnlyList<PlaylistItemModel>, bool> play,
        Action<IReadOnlyList<PlaylistItemModel>> playNext,
        Action<IReadOnlyList<PlaylistItemModel>> enqueue)
    {
        this.musicService = musicService;
        this.videoService = videoService;
        this.dialogs = dialogs;
        this.detailFactory = detailFactory;
        this.play = play;
        this.playNext = playNext;
        this.enqueue = enqueue;
        TitleKey = nameof(Strings.Resources.Nav_Playlists);

        NewPlaylistCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(NewPlaylistAsync));
        ImportPlaylistCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(ImportPlaylistAsync));

        // The queue actions are music-only: a video playlist has nothing the audio queue can take.
        PlayCommand = new RelayCommand<PlaylistModel>(playlist =>
        {
            if (playlist is { IsMusic: true })
            {
                this.play(playlist.Items, false);
            }
        });
        PlayNextCommand = new RelayCommand<PlaylistModel>(playlist =>
        {
            if (playlist is { IsMusic: true })
            {
                this.playNext(playlist.Items);
            }
        });
        AddToQueueCommand = new RelayCommand<PlaylistModel>(playlist =>
        {
            if (playlist is { IsMusic: true })
            {
                this.enqueue(playlist.Items);
            }
        });
        RenameCommand = new AsyncRelayCommand<PlaylistModel>(
            playlist => AppErrorReporter.GuardAsync(() => RenameAsync(playlist)));
        ExportCommand = new AsyncRelayCommand<PlaylistModel>(
            playlist => AppErrorReporter.GuardAsync(() => ExportAsync(playlist)));
        DeleteCommand = new AsyncRelayCommand<PlaylistModel>(
            playlist => AppErrorReporter.GuardAsync(() => DeleteAsync(playlist)),
            CanDelete);

        SyncPlaylists();
        PageSearch.SetSource(new PageSearchSource(
            () => PageSearchSource.Entries(Playlists), [Playlists], this, itemState: () => Playlists));
        musicService.Playlists.CollectionChanged += OnSourcePlaylistsChanged;
        videoService.Playlists.CollectionChanged += OnSourcePlaylistsChanged;
    }

    /// <summary>Music playlists first, then video ones; each store keeps its own order.</summary>
    public ObservableCollection<PlaylistModel> Playlists { get; } = [];

    public IAsyncRelayCommand NewPlaylistCommand { get; }

    public IAsyncRelayCommand ImportPlaylistCommand { get; }

    public IRelayCommand<PlaylistModel> PlayCommand { get; }

    public IRelayCommand<PlaylistModel> PlayNextCommand { get; }

    public IRelayCommand<PlaylistModel> AddToQueueCommand { get; }

    public IAsyncRelayCommand<PlaylistModel> RenameCommand { get; }

    public IAsyncRelayCommand<PlaylistModel> ExportCommand { get; }

    public IAsyncRelayCommand<PlaylistModel> DeleteCommand { get; }

    public bool ShowEmpty => Playlists.Count == 0;

    public bool ShowList => Playlists.Count > 0;

    public bool ShowDetail => SelectedDetail is not null;

    public bool ShowDetailPlaceholder => SelectedDetail is null;

    // Set by the adaptive view: true in the narrow single-pane layout, false in the wide
    // two-pane layout. Drives whether the inline detail shows its header back button.
    public bool IsCompact
    {
        get => isCompact;
        set
        {
            if (isCompact != value)
            {
                isCompact = value;
                UpdateDetailBackButton();
                OnPropertyChanged(nameof(CanHandleBack));
            }
        }
    }

    public string DetailPlaceholderHeader =>
        Playlists.Count == 0 ? Strings.Resources.Playlists_NoPlaylists : Strings.Resources.Playlists_SelectPlaylistHeader;

    public string DetailPlaceholderDescription =>
        Playlists.Count == 0
            ? Strings.Resources.Playlists_CreateOrImportDescription
            : Strings.Resources.Playlists_ChooseFromListDescription;

    private async Task NewPlaylistAsync()
    {
        if (await dialogs.PickPlaylistKindAsync() is not { } kind)
        {
            return;
        }

        var name = await dialogs.PromptForTitleAsync(
            Strings.Resources.Playlists_NewPlaylist,
            Strings.Resources.Common_Create);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await ServiceFor(kind).CreateAsync(name);
        }
    }

    /// <summary>M3U and WPL are music formats, so an import always lands in the music store.</summary>
    private async Task ImportPlaylistAsync()
    {
        var file = await dialogs.PickImportFileAsync();
        if (file is not null)
        {
            await musicService.ImportAsync(file);
        }
    }

    private async Task RenameAsync(PlaylistModel? playlist)
    {
        if (playlist is null)
        {
            return;
        }

        var name = await dialogs.PromptForTitleAsync(Strings.Resources.PlaylistDetail_RenameDialogHeader, Strings.Resources.Common_Rename, playlist.Title);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await ServiceFor(playlist.Kind).RenameAsync(playlist.Id, name);
        }
    }

    private async Task ExportAsync(PlaylistModel? playlist)
    {
        if (playlist is null)
        {
            return;
        }

        var service = ServiceFor(playlist.Kind);
        await dialogs.SaveExportFileAsync(
            playlist.Title,
            async fileName => (await service.ExportAsync(playlist.Id, fileName))?.Content);
    }

    private async Task DeleteAsync(PlaylistModel? playlist)
    {
        if (playlist is null || !CanDelete(playlist))
        {
            return;
        }

        var confirmed = await dialogs.ConfirmAsync(
            Strings.Resources.PlaylistDetail_DeletePlaylist,
            string.Format(Strings.Resources.PlaylistDetail_DeleteConfirmFormat, playlist.Title),
            Strings.Resources.Common_Delete);
        if (confirmed)
        {
            await ServiceFor(playlist.Kind).DeleteAsync(playlist.Id);
        }
    }

    private IPlaylistService ServiceFor(PlaylistKind kind) =>
        kind == PlaylistKind.Video ? videoService : musicService;

    private static bool CanDelete(PlaylistModel? playlist) => playlist is not null && !playlist.IsBuiltIn;

    partial void OnSelectedPlaylistChanged(PlaylistModel? value)
    {
        var previous = SelectedDetail;
        SelectedDetail = value is null ? null : detailFactory(value, ClearSelection);
        if (!ReferenceEquals(previous, SelectedDetail))
        {
            (previous as IDisposable)?.Dispose();
        }

        OnPropertyChanged(nameof(CanHandleBack));
        OnPropertyChanged(nameof(DetailKey));
    }

    partial void OnSelectedDetailChanged(PageViewModelBase? oldValue, PageViewModelBase? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnDetailPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnDetailPropertyChanged;
        }

        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(ShowDetailPlaceholder));
        UpdateDetailBackButton();
    }

    private void OnDetailPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CanHandleBack))
        {
            OnPropertyChanged(nameof(CanHandleBack));
        }
    }

    // Keep the inline detail's back button in step with the shell layout: whenever the shell shows a
    // back button of its own, the inline back button stays hidden.
    protected override void OnHasShellBackButtonChanged(bool value) => UpdateDetailBackButton();

    // The inline detail shows its header back button only when squeezed into a single pane while the
    // shell has no back button (for example while the queue is docked). The narrow and Apple layouts
    // and Android hide it, deferring to the shell's global back button / platform back gesture.
    private void UpdateDetailBackButton()
    {
        if (SelectedDetail is IInlineDetailPage detail)
        {
            detail.ShowBackButton = !PlatformServices.HostProvidesBackNavigation && IsCompact && !HasShellBackButton;
        }
    }

    private void ClearSelection() => SelectedPlaylist = null;

    /// <inheritdoc />
    public bool CanHandleBack => (SelectedDetail as IInPageBackHandler)?.CanHandleBack == true ||
        IsCompact && SelectedPlaylist is not null;

    public bool CanPreviewBack => IsCompact && SelectedPlaylist is not null &&
        SelectedDetail is not IInPageBackHandler { CanHandleBack: true };

    /// <inheritdoc />
    public string? DetailKey => SelectedPlaylist?.Id;

    /// <inheritdoc />
    public void RestoreDetail(string? key)
    {
        pendingDetailKey = key;
        ApplyPendingDetail();
    }

    // A restored key can arrive before the playlists have loaded, so it is re-applied whenever the
    // collection changes.
    private void ApplyPendingDetail()
    {
        if (pendingDetailKey is null)
        {
            SelectedPlaylist = null;
            return;
        }

        if (Playlists.FirstOrDefault(playlist => playlist.Id == pendingDetailKey) is not { } match)
        {
            return;
        }

        pendingDetailKey = null;
        SelectedPlaylist = match;
    }

    /// <summary>
    /// On the narrow single-pane layout a selected playlist fills the page, so the first back
    /// collapses to the playlist list instead of leaving the Playlists page. On wide layouts both
    /// panes are visible, so back is left to the shell.
    /// </summary>
    public bool TryHandleBack()
    {
        if ((SelectedDetail as IInPageBackHandler)?.TryHandleBack() == true)
        {
            return true;
        }

        if (IsCompact && SelectedPlaylist is not null)
        {
            SelectedPlaylist = null;
            return true;
        }

        return false;
    }

    private void OnSourcePlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncPlaylists();

        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(DetailPlaceholderHeader));
        OnPropertyChanged(nameof(DetailPlaceholderDescription));

        // If the open playlist was deleted elsewhere, fall back to the list.
        if (SelectedPlaylist is not null && !Playlists.Contains(SelectedPlaylist))
        {
            SelectedPlaylist = null;
        }

        if (pendingDetailKey is not null)
        {
            ApplyPendingDetail();
        }
    }

    // Edited in place rather than rebuilt, so the list keeps its selection (and with it the open
    // detail) when the other store changes underneath it.
    private void SyncPlaylists()
    {
        var desired = musicService.Playlists.Concat(videoService.Playlists).ToList();

        for (var index = Playlists.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(Playlists[index]))
            {
                Playlists.RemoveAt(index);
            }
        }

        for (var index = 0; index < desired.Count; index++)
        {
            if (index >= Playlists.Count)
            {
                Playlists.Add(desired[index]);
            }
            else if (!ReferenceEquals(Playlists[index], desired[index]))
            {
                Playlists.Insert(index, desired[index]);
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        PageSearch.Close();
        musicService.Playlists.CollectionChanged -= OnSourcePlaylistsChanged;
        videoService.Playlists.CollectionChanged -= OnSourcePlaylistsChanged;
        if (SelectedDetail is { } detail)
        {
            detail.PropertyChanged -= OnDetailPropertyChanged;
        }

        (SelectedDetail as IDisposable)?.Dispose();
    }
}
