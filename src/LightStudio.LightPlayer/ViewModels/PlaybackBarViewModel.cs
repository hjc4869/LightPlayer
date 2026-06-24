using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using PlaybackController = LightStudio.LightPlayer.Services.Playback.PlaybackController;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// Backs the bottom playback bar. Adapts the <see cref="PlaybackController"/>
/// engine into the display strings, icon keys, and transport commands the view
/// binds to. All real playback (decode, output, seek, volume, transport) is
/// delegated to the controller.
/// </summary>
public partial class PlaybackBarViewModel : ObservableObject
{
    private readonly PlaybackController controller;
    private readonly AlbumArtService albumArt;
    private readonly IPlaylistService? playlists;
    private readonly IPlaylistDialogService? playlistDialogService;
    private double volumeBeforeMute = 0.8;
    private MusicPlaybackItemModel? trackedItem;
    private bool isScrubbing;

    [ObservableProperty]
    private IImage? albumImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteIconKey))]
    private bool isFavorite;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyPropertyChangedFor(nameof(RemainingText))]
    private double positionMs;

    public PlaybackBarViewModel(PlaybackController controller, AlbumArtService albumArt, IPlaylistService? playlists = null, IPlaylistDialogService? playlistDialogService = null)
    {
        this.controller = controller;
        this.albumArt = albumArt;
        this.playlists = playlists;
        this.playlistDialogService = playlistDialogService;
        controller.PropertyChanged += OnControllerPropertyChanged;
        TrackCurrentItem(controller.CurrentItem);
        RefreshArt();

        PlayPauseCommand = new RelayCommand(controller.TogglePlayPause);
        NextCommand = new RelayCommand(controller.Next);
        PreviousCommand = new RelayCommand(controller.Previous);
        CycleModeCommand = new RelayCommand(controller.CycleMode);
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync);
        AddToPlaylistCommand = new AsyncRelayCommand(AddToPlaylistAsync);
        ToggleMuteCommand = new RelayCommand(ToggleMute);
        OpenNowPlayingCommand = new RelayCommand(() => OpenNowPlaying?.Invoke());

        if (playlists is not null)
        {
            playlists.Favorites.Items.CollectionChanged += OnFavoritesChanged;
        }

        RefreshFavorite();
    }

    /// <summary>Set by the shell to navigate to the Now Playing page.</summary>
    public Action? OpenNowPlaying { get; set; }

    public IRelayCommand PlayPauseCommand { get; }

    public IRelayCommand NextCommand { get; }

    public IRelayCommand PreviousCommand { get; }

    public IRelayCommand CycleModeCommand { get; }

    public IRelayCommand ToggleFavoriteCommand { get; }

    public IRelayCommand AddToPlaylistCommand { get; }

    public IRelayCommand ToggleMuteCommand { get; }

    public IRelayCommand OpenNowPlayingCommand { get; }

    public MusicPlaybackItemModel? CurrentItem => controller.CurrentItem;

    public bool HasItem => controller.CurrentItem is not null;

    public string Title => controller.CurrentItem?.Title ?? string.Empty;

    public string Artist => controller.CurrentItem?.Artist ?? string.Empty;

    public string Album => controller.CurrentItem?.Album ?? string.Empty;

    public string ArtistAlbum => controller.CurrentItem?.ArtistAlbum ?? string.Empty;

    public string NowPlayingLine =>
        controller.CurrentItem is null ? string.Empty : $"{Title} - {Artist} · {Album}";

    public double DurationMs => controller.DurationMs;

    public bool IsPlaying => controller.State == PlaybackState.Playing;

    public string PlayPauseIconKey => IsPlaying ? "IconPause" : "IconPlay";

    public PlaybackMode Mode => controller.Mode;

    public double Volume
    {
        get => controller.Volume;
        set => controller.SetVolume(value);
    }

    public bool IsMuted => Volume <= 0.0001d;

    public string VolumeIconKey => IsMuted ? "IconVolumeMute" : "IconVolumeHigh";

    public string FavoriteIconKey => IsFavorite ? "IconHeartFilled" : "IconHeartOutline";

    public string ModeIconKey => Mode switch
    {
        PlaybackMode.ListLoop => "IconModeListLoop",
        PlaybackMode.SingleTrackLoop => "IconModeSingleLoop",
        PlaybackMode.Random => "IconModeRandom",
        _ => "IconModeSequential",
    };

    public string ModeLabel => Mode switch
    {
        PlaybackMode.ListLoop => "Repeat all",
        PlaybackMode.SingleTrackLoop => "Repeat one",
        PlaybackMode.Random => "Shuffle",
        _ => "Play in order",
    };

    public string PositionText => FormatTime(PositionMs);

    public string RemainingText => "-" + FormatTime(Math.Max(0d, DurationMs - PositionMs));

    /// <summary>Called by the view when the user starts dragging the progress slider.</summary>
    public void BeginScrub()
    {
        isScrubbing = true;
        controller.BeginScrub();
    }

    /// <summary>Called by the view when the user releases the progress slider.</summary>
    public void EndScrub()
    {
        isScrubbing = false;
        controller.EndScrub(PositionMs);
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlaybackController.CurrentItem):
                TrackCurrentItem(controller.CurrentItem);
                RefreshArt();
                RefreshFavorite();
                OnPropertyChanged(nameof(CurrentItem));
                OnPropertyChanged(nameof(HasItem));
                RaiseMetadataChanged();
                break;
            case nameof(PlaybackController.State):
                OnPropertyChanged(nameof(IsPlaying));
                OnPropertyChanged(nameof(PlayPauseIconKey));
                break;
            case nameof(PlaybackController.Mode):
                OnPropertyChanged(nameof(Mode));
                OnPropertyChanged(nameof(ModeIconKey));
                OnPropertyChanged(nameof(ModeLabel));
                break;
            case nameof(PlaybackController.Volume):
                OnPropertyChanged(nameof(Volume));
                OnPropertyChanged(nameof(IsMuted));
                OnPropertyChanged(nameof(VolumeIconKey));
                break;
            case nameof(PlaybackController.DurationMs):
                OnPropertyChanged(nameof(DurationMs));
                OnPropertyChanged(nameof(RemainingText));
                OnPropertyChanged(nameof(PositionText));
                break;
            case nameof(PlaybackController.PositionMs):
                if (!isScrubbing)
                {
                    PositionMs = controller.PositionMs;
                }

                break;
        }
    }

    private void TrackCurrentItem(MusicPlaybackItemModel? item)
    {
        if (ReferenceEquals(trackedItem, item))
        {
            return;
        }

        if (trackedItem is not null)
        {
            trackedItem.PropertyChanged -= OnCurrentItemPropertyChanged;
        }

        trackedItem = item;

        if (trackedItem is not null)
        {
            trackedItem.PropertyChanged += OnCurrentItemPropertyChanged;
        }
    }

    private void OnCurrentItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The decoder enriches title/duration for file-picked or restored items.
        RaiseMetadataChanged();
    }

    private void OnFavoritesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshFavorite();

    private void RefreshFavorite()
    {
        var item = controller.CurrentItem;
        IsFavorite = item is not null
            && playlists is not null
            && playlists.IsFavorite(PlaylistItemModel.FromPlaybackItem(item));
    }

    private async Task ToggleFavoriteAsync()
    {
        var item = controller.CurrentItem;
        if (item is null)
        {
            return;
        }

        if (playlists is null)
        {
            // No playlist service: reflect the toggle locally without persisting.
            IsFavorite = !IsFavorite;
            return;
        }

        var entry = PlaylistItemModel.FromPlaybackItem(item);
        if (playlists.IsFavorite(entry))
        {
            await playlists.RemoveFromFavoritesAsync(entry);
        }
        else
        {
            await playlists.AddToFavoritesAsync(entry);
        }
    }

    private async Task AddToPlaylistAsync()
    {
        var item = controller.CurrentItem;
        if (item is null || playlists is null || playlistDialogService is null)
        {
            return;
        }

        var target = await playlistDialogService.PickPlaylistAsync();
        if (target is not null)
        {
            await playlists.AddItemsAsync(target.Id, new[] { PlaylistItemModel.FromPlaybackItem(item) });
        }
    }

    private void RaiseMetadataChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(Album));
        OnPropertyChanged(nameof(ArtistAlbum));
        OnPropertyChanged(nameof(NowPlayingLine));
        OnPropertyChanged(nameof(DurationMs));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(PositionText));
    }

    private void RefreshArt()
    {
        AlbumImage = null;
        var item = controller.CurrentItem;
        if (item is not null)
        {
            _ = LoadArtAsync(item);
        }
    }

    private async Task LoadArtAsync(MusicPlaybackItemModel item)
    {
        var bitmap = await albumArt.GetTrackArtAsync(item.FilePath);
        if (bitmap is not null && ReferenceEquals(controller.CurrentItem, item))
        {
            Dispatcher.UIThread.Post(() => AlbumImage = bitmap);
        }
    }

    private void ToggleMute()
    {
        if (IsMuted)
        {
            controller.SetVolume(volumeBeforeMute <= 0.0001d ? 0.8d : volumeBeforeMute);
        }
        else
        {
            volumeBeforeMute = Volume;
            controller.SetVolume(0d);
        }
    }

    private static string FormatTime(double milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds < 0d ? 0d : milliseconds);
        return time.TotalHours >= 1d
            ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes}:{time.Seconds:D2}";
    }
}
