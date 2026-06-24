using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Home page: when something is playing, shows a now-playing hero with the
/// current cover, metadata, and an upcoming list; otherwise falls back to an
/// all-music grid of every track in shuffled order. Clicking a track plays the
/// whole library from that point.
/// </summary>
public sealed partial class HomePageViewModel : PageViewModelBase, IDisposable
{
    private readonly PlaybackController playback;
    private readonly AlbumArtService albumArt;
    private readonly Action<LibraryTileModel> openAlbum;
    private readonly PlaybackHistoryService? history;
    private bool disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFallback))]
    private bool hasNowPlaying;

    [ObservableProperty]
    private string nowPlayingTitle = string.Empty;

    [ObservableProperty]
    private string nowPlayingArtist = string.Empty;

    [ObservableProperty]
    private string nowPlayingAlbum = string.Empty;

    [ObservableProperty]
    private IImage? albumImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFallback))]
    private bool isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFallback))]
    private bool isEmpty;

    [ObservableProperty]
    private bool hasUpcoming;

    [ObservableProperty]
    private bool hasRecently;

    public HomePageViewModel(
        PlaybackController playback,
        AlbumArtService albumArt,
        Action<LibraryTileModel> openAlbum,
        PlaybackHistoryService? history = null)
    {
        this.playback = playback;
        this.albumArt = albumArt;
        this.openAlbum = openAlbum;
        this.history = history;
        Title = "Home";

        Upcoming = new ObservableCollection<LibraryTileModel>();
        AllMusic = new ObservableCollection<LibraryTileModel>();
        Recently = new ObservableCollection<LibraryTileModel>();

        if (history is not null)
        {
            history.NewEntryAdded += OnHistoryEntryAdded;
        }

        playback.PropertyChanged += OnPlaybackChanged;
        playback.QueueItems.CollectionChanged += OnQueueChanged;
        playback.QueueItemsMetadataChanged += OnQueueMetadataChanged;
        RefreshNowPlaying();
    }

    public ObservableCollection<LibraryTileModel> Upcoming { get; }

    public ObservableCollection<LibraryTileModel> AllMusic { get; }

    public ObservableCollection<LibraryTileModel> Recently { get; }

    /// <summary>Show the all-music grid only when nothing is playing and content is ready.</summary>
    public bool ShowFallback => !HasNowPlaying && !IsLoading;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var tiles = await LoadTrackTilesAsync();
            AllMusic.Clear();
            foreach (var tile in tiles)
            {
                AllMusic.Add(tile);
            }

            IsEmpty = tiles.Count == 0;
        }
        catch
        {
            IsEmpty = AllMusic.Count == 0;
        }
        finally
        {
            IsLoading = false;
        }

        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        if (history is null)
        {
            HasRecently = false;
            return;
        }

        var items = await history.GetHistoryAsync(PlaybackHistoryService.HistoryEntryLimit);
        Recently.Clear();
        foreach (var item in items)
        {
            Recently.Add(CreateHistoryTile(item));
        }

        HasRecently = Recently.Count > 0;
    }

    private LibraryTileModel CreateHistoryTile(MusicPlaybackItemModel item)
    {
        var tile = new LibraryTileModel(item.Title, item.Artist, ThumbnailKind.Album);
        tile.OpenCommand = new RelayCommand(() => PlayHistoryItem(item));
        tile.PlayCommand = new RelayCommand(() => PlayHistoryItem(item));
        tile.AddToQueueCommand = new RelayCommand(() => playback.Enqueue(item));
        tile.ArtLoader = t => LoadTrackTileArtAsync(t, item.FilePath);

        // The history list is short (capped at the limit) and uses a plain template
        // rather than LibraryTile, so request artwork eagerly instead of on realization.
        tile.RequestArt();
        return tile;
    }

    private void OnHistoryEntryAdded(object? sender, MusicPlaybackItemModel item)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (disposed)
            {
                return;
            }

            Recently.Insert(0, CreateHistoryTile(item));
            while (Recently.Count > PlaybackHistoryService.HistoryEntryLimit)
            {
                Recently.RemoveAt(Recently.Count - 1);
            }

            HasRecently = Recently.Count > 0;
        });
    }

    private void PlayHistoryItem(MusicPlaybackItemModel item)
    {
        var existing = playback.QueueItems.FirstOrDefault(
            queued => PathEquals(queued.FilePath, item.FilePath));
        if (existing is not null)
        {
            playback.PlayQueueItem(existing);
        }
        else
        {
            playback.PlayNow(item);
        }
    }

    private static bool PathEquals(string? left, string? right) =>
        string.Equals(left, right, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private async Task<List<LibraryTileModel>> LoadTrackTilesAsync()
    {
        if (GlobalLibraryCache.CachedDbMediaFile is null)
        {
            await Task.Run(GlobalLibraryCache.LoadMediaAsync);
        }

        var files = (GlobalLibraryCache.CachedDbMediaFile ?? []).ToList();
        Shuffle(files);
        var items = files
            .Select(file => new MusicPlaybackItemModel(
                LibrarySortKeys.Fallback(file.Title, DeriveTitle(file.Path)),
                file.Artist ?? string.Empty,
                file.Album ?? string.Empty,
                file.Duration,
                file.Path ?? string.Empty,
                file.StartTime))
            .ToList();

        var tiles = new List<LibraryTileModel>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            tiles.Add(CreateTrackTile(items[i], items, i));
        }

        return tiles;
    }

    private LibraryTileModel CreateTrackTile(MusicPlaybackItemModel item, IReadOnlyList<MusicPlaybackItemModel> all, int index)
    {
        var tile = new LibraryTileModel(item.Title, item.Artist, ThumbnailKind.Album);
        tile.OpenCommand = new RelayCommand(() => playback.PlayAllFrom(all, index));
        tile.PlayCommand = new RelayCommand(() => playback.PlayAllFrom(all, index));
        tile.AddToQueueCommand = new RelayCommand(() => playback.Enqueue(item));
        tile.ArtLoader = t => LoadTrackTileArtAsync(t, item.FilePath);
        return tile;
    }

    private async Task LoadTrackTileArtAsync(LibraryTileModel tile, string filePath)
    {
        var bitmap = await albumArt.GetTrackArtAsync(filePath);
        if (bitmap is null)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            tile.Image = bitmap;
        }
        else
        {
            Dispatcher.UIThread.Post(() => tile.Image = bitmap);
        }
    }

    private static void Shuffle(IList<DbMediaFile> files)
    {
        var rng = Random.Shared;
        for (var i = files.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (files[i], files[j]) = (files[j], files[i]);
        }
    }

    private static string DeriveTitle(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "Unknown title" : System.IO.Path.GetFileNameWithoutExtension(path);

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackController.CurrentItem))
        {
            RefreshNowPlaying();
        }
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshUpcoming();

    private void OnQueueMetadataChanged() => RefreshUpcoming();

    private void RefreshNowPlaying()
    {
        var item = playback.CurrentItem;
        HasNowPlaying = item is not null;
        NowPlayingTitle = item?.Title ?? string.Empty;
        NowPlayingArtist = item?.Artist ?? string.Empty;
        NowPlayingAlbum = item?.Album ?? string.Empty;
        AlbumImage = null;
        if (item is not null)
        {
            _ = LoadHeroArtAsync(item);
        }

        RefreshUpcoming();
    }

    private async Task LoadHeroArtAsync(MusicPlaybackItemModel item)
    {
        var bitmap = await albumArt.GetTrackArtAsync(item.FilePath);
        if (bitmap is not null && ReferenceEquals(playback.CurrentItem, item))
        {
            Dispatcher.UIThread.Post(() => AlbumImage = bitmap);
        }
    }

    private void RefreshUpcoming()
    {
        Upcoming.Clear();
        var current = playback.CurrentItem;
        var start = current is null ? 0 : playback.QueueItems.IndexOf(current) + 1;
        for (var i = start; i < playback.QueueItems.Count && Upcoming.Count < 12; i++)
        {
            Upcoming.Add(CreateUpcomingTile(playback.QueueItems[i]));
        }

        HasUpcoming = Upcoming.Count > 0;
    }

    private LibraryTileModel CreateUpcomingTile(MusicPlaybackItemModel item)
    {
        var tile = new LibraryTileModel(item.Title, item.Artist, ThumbnailKind.Album);
        tile.OpenCommand = new RelayCommand(() => playback.PlayQueueItem(item));
        tile.ArtLoader = t => LoadTrackTileArtAsync(t, item.FilePath);

        // The upcoming list is short (capped at 12) and uses a plain template
        // rather than LibraryTile, so request artwork eagerly instead of on realization.
        tile.RequestArt();
        return tile;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (history is not null)
        {
            history.NewEntryAdded -= OnHistoryEntryAdded;
        }

        playback.PropertyChanged -= OnPlaybackChanged;
        playback.QueueItems.CollectionChanged -= OnQueueChanged;
        playback.QueueItemsMetadataChanged -= OnQueueMetadataChanged;
    }
}
