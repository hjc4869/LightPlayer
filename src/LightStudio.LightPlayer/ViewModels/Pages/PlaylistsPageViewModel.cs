using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Playlists page: an adaptive master-detail view that lists the user's playlists
/// alongside the selected playlist's contents. The panes sit side by side on wide
/// screens and collapse to a single pane (list, then detail with a back button) on
/// narrow screens.
/// </summary>
public sealed partial class PlaylistsPageViewModel : PageViewModelBase, IDisposable
{
    private readonly IPlaylistService service;
    private readonly IPlaylistDialogService dialogs;
    private readonly Func<string, Action, PlaylistDetailPageViewModel> detailFactory;
    private bool isCompact;
    private bool disposed;

    [ObservableProperty]
    private PlaylistModel? selectedPlaylist;

    [ObservableProperty]
    private PlaylistDetailPageViewModel? selectedDetail;

    public PlaylistsPageViewModel(
        IPlaylistService service,
        IPlaylistDialogService dialogs,
        Func<string, Action, PlaylistDetailPageViewModel> detailFactory)
    {
        this.service = service;
        this.dialogs = dialogs;
        this.detailFactory = detailFactory;
        Title = "Playlists";

        NewPlaylistCommand = new AsyncRelayCommand(NewPlaylistAsync);
        ImportPlaylistCommand = new AsyncRelayCommand(ImportPlaylistAsync);

        service.Playlists.CollectionChanged += OnPlaylistsChanged;
    }

    public ObservableCollection<PlaylistModel> Playlists => service.Playlists;

    public IAsyncRelayCommand NewPlaylistCommand { get; }

    public IAsyncRelayCommand ImportPlaylistCommand { get; }

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
            }
        }
    }

    public string DetailPlaceholderHeader =>
        Playlists.Count == 0 ? "No playlists yet" : "Select a playlist";

    public string DetailPlaceholderDescription =>
        Playlists.Count == 0
            ? "Create a playlist or import an M3U or WPL file to get started."
            : "Choose a playlist from the list to see its items.";

    private async Task NewPlaylistAsync()
    {
        var name = await dialogs.PromptForTitleAsync("New playlist", "Create");
        if (!string.IsNullOrWhiteSpace(name))
        {
            await service.CreateAsync(name);
        }
    }

    private async Task ImportPlaylistAsync()
    {
        var path = await dialogs.PickImportFileAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await service.ImportAsync(path);
        }
    }

    partial void OnSelectedPlaylistChanged(PlaylistModel? value)
    {
        var previous = SelectedDetail;
        SelectedDetail = value is null ? null : detailFactory(value.Id, ClearSelection);
        if (!ReferenceEquals(previous, SelectedDetail))
        {
            previous?.Dispose();
        }
    }

    partial void OnSelectedDetailChanged(PlaylistDetailPageViewModel? value)
    {
        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(ShowDetailPlaceholder));
        UpdateDetailBackButton();
    }

    // The inline detail shows its header back button only in the narrow single-pane
    // layout; on wide screens both panes are visible so no back affordance is needed.
    private void UpdateDetailBackButton()
    {
        if (SelectedDetail is not null)
        {
            SelectedDetail.ShowBackButton = IsCompact;
        }
    }

    private void ClearSelection() => SelectedPlaylist = null;

    private void OnPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(DetailPlaceholderHeader));
        OnPropertyChanged(nameof(DetailPlaceholderDescription));

        // If the open playlist was deleted elsewhere, fall back to the list.
        if (SelectedPlaylist is not null && !Playlists.Contains(SelectedPlaylist))
        {
            SelectedPlaylist = null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        service.Playlists.CollectionChanged -= OnPlaylistsChanged;
        SelectedDetail?.Dispose();
    }
}

