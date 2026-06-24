using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.MediaLibraryCore.Lyrics;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Now-playing page: cover hero, current metadata, and a position-synced lyric
/// view. Lyrics are resolved automatically on track change (local cache or an
/// online source), can be searched/imported/cleared manually, and their timing
/// can be nudged earlier or later.
/// </summary>
public sealed partial class NowPlayingPageViewModel : PageViewModelBase, IDisposable
{
    private const long OffsetStepMs = 1000;

    private readonly PlaybackController playback;
    private readonly AlbumArtService albumArt;
    private readonly LyricsService lyrics;
    private readonly IDialogService? dialogService;
    private readonly Action<string>? openArtist;
    private readonly Action<string>? openAlbum;
    private ParsedLrc? current;
    private int lineIndex = -1;
    private bool disposed;
    private IReadOnlyList<ExternalLrcInfo> lastCandidates = Array.Empty<ExternalLrcInfo>();

    [ObservableProperty]
    private string trackTitle = string.Empty;

    [ObservableProperty]
    private string artist = string.Empty;

    [ObservableProperty]
    private string album = string.Empty;

    [ObservableProperty]
    private IImage? albumImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoLyrics))]
    [NotifyPropertyChangedFor(nameof(ShowNoLyricsMessage))]
    private bool hasLyrics;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoLyricsMessage))]
    private bool lrcSearchBusy;

    [ObservableProperty]
    private int currentIndex = -1;

    [ObservableProperty]
    private bool hasUpcoming;

    public NowPlayingPageViewModel(
        PlaybackController playback,
        AlbumArtService albumArt,
        LyricsService lyrics,
        IDialogService? dialogService,
        Action<string>? openArtist,
        Action<string>? openAlbum)
    {
        this.playback = playback;
        this.albumArt = albumArt;
        this.lyrics = lyrics;
        this.dialogService = dialogService;
        this.openArtist = openArtist;
        this.openAlbum = openAlbum;
        Title = "Now Playing";

        Lines = new ObservableCollection<LyricLineModel>();
        Upcoming = new ObservableCollection<LibraryTileModel>();
        SearchLyricsCommand = new AsyncRelayCommand(SearchLyricsAsync, () => playback.CurrentItem is not null && dialogService is not null);
        OffsetEarlierCommand = new RelayCommand(() => AdjustOffset(OffsetStepMs), () => HasLyrics);
        OffsetLaterCommand = new RelayCommand(() => AdjustOffset(-OffsetStepMs), () => HasLyrics);
        OpenArtistCommand = new RelayCommand(OpenArtist, CanOpenArtist);
        OpenAlbumCommand = new RelayCommand(OpenAlbum, CanOpenAlbum);

        playback.PropertyChanged += OnPlaybackChanged;
        playback.QueueItems.CollectionChanged += OnQueueChanged;
        playback.QueueItemsMetadataChanged += OnQueueMetadataChanged;
        RefreshTrack();
    }

    public ObservableCollection<LyricLineModel> Lines { get; }

    public ObservableCollection<LibraryTileModel> Upcoming { get; }

    public bool NoLyrics => !HasLyrics;

    public bool ShowNoLyricsMessage => !HasLyrics && !LrcSearchBusy;

    public string NoLyricsMessage => lyrics.HasSources
        ? "No lyrics found. Use Search or Import to add an .lrc file."
        : "No lyrics found. Add a lyric source in Settings, or import an .lrc file.";

    public IAsyncRelayCommand SearchLyricsCommand { get; }

    public IRelayCommand OffsetEarlierCommand { get; }

    public IRelayCommand OffsetLaterCommand { get; }

    public IRelayCommand OpenArtistCommand { get; }

    public IRelayCommand OpenAlbumCommand { get; }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackController.CurrentItem))
        {
            RefreshTrack();
        }
        else if (e.PropertyName == nameof(PlaybackController.PositionMs))
        {
            SyncLine(playback.PositionMs);
        }
    }

    private void RefreshTrack()
    {
        var item = playback.CurrentItem;
        TrackTitle = item?.Title ?? string.Empty;
        Artist = item?.Artist ?? string.Empty;
        Album = item?.Album ?? string.Empty;
        AlbumImage = null;
        SearchLyricsCommand.NotifyCanExecuteChanged();
        OpenArtistCommand.NotifyCanExecuteChanged();
        OpenAlbumCommand.NotifyCanExecuteChanged();
        _ = LoadArtAsync(item);
        _ = LoadLyricsAsync(item);
        RefreshUpcoming();
    }

    private async Task LoadArtAsync(MusicPlaybackItemModel? item)
    {
        if (item is null)
        {
            return;
        }

        var bitmap = await albumArt.GetTrackArtAsync(item.FilePath);
        if (bitmap is not null && ReferenceEquals(playback.CurrentItem, item))
        {
            Dispatcher.UIThread.Post(() => AlbumImage = bitmap);
        }
    }

    private async Task LoadLyricsAsync(MusicPlaybackItemModel? item)
    {
        SetLyrics(null);
        lastCandidates = Array.Empty<ExternalLrcInfo>();
        if (item is null)
        {
            return;
        }

        LrcSearchBusy = true;
        try
        {
            var result = await lyrics.AutoSearchAsync(item.Title, item.Artist, item.FilePath);
            if (!ReferenceEquals(playback.CurrentItem, item))
            {
                return;
            }

            lastCandidates = result.Candidates;
            SetLyrics(result.Lyrics);
        }
        finally
        {
            if (ReferenceEquals(playback.CurrentItem, item))
            {
                LrcSearchBusy = false;
            }
        }
    }

    private void SetLyrics(ParsedLrc? parsed)
    {
        current = parsed;
        lineIndex = -1;
        CurrentIndex = -1;
        Lines.Clear();
        if (parsed is not null)
        {
            foreach (var sentence in parsed.Sentences)
            {
                Lines.Add(new LyricLineModel(sentence.Time, sentence.Content));
            }
        }

        HasLyrics = Lines.Count > 0;
        OffsetEarlierCommand.NotifyCanExecuteChanged();
        OffsetLaterCommand.NotifyCanExecuteChanged();

        // Highlight the line for the current position right away rather than
        // waiting for the next position tick.
        if (HasLyrics)
        {
            SyncLine(playback.PositionMs);
        }
    }

    private void SyncLine(double positionMs)
    {
        if (current is null || Lines.Count == 0)
        {
            return;
        }

        var index = current.GetPositionFromTime((long)positionMs + current.Offset);
        if (index == lineIndex)
        {
            return;
        }

        if (lineIndex >= 0 && lineIndex < Lines.Count)
        {
            Lines[lineIndex].IsCurrent = false;
        }

        if (index >= 0 && index < Lines.Count)
        {
            Lines[index].IsCurrent = true;
        }

        lineIndex = index;
        CurrentIndex = index;
    }

    private void AdjustOffset(long deltaMs)
    {
        if (current is null)
        {
            return;
        }

        current.Offset += deltaMs;
        SyncLine(playback.PositionMs);
        _ = lyrics.SaveAsync(current);
    }

    private async Task SearchLyricsAsync()
    {
        var item = playback.CurrentItem;
        if (item is null || dialogService is null)
        {
            return;
        }

        var result = await dialogService.ShowLyricSearchAsync(item.Title, item.Artist, lyrics, lastCandidates);
        if (result is { Changed: true } && ReferenceEquals(playback.CurrentItem, item))
        {
            SetLyrics(result.Lyrics);
        }
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshUpcoming();

    private void OnQueueMetadataChanged() => RefreshUpcoming();

    private void RefreshUpcoming()
    {
        Upcoming.Clear();
        var item = playback.CurrentItem;
        var start = item is null ? 0 : playback.QueueItems.IndexOf(item) + 1;
        for (var i = start; i < playback.QueueItems.Count && Upcoming.Count < 15; i++)
        {
            Upcoming.Add(CreateUpcomingTile(playback.QueueItems[i]));
        }

        HasUpcoming = Upcoming.Count > 0;
    }

    private LibraryTileModel CreateUpcomingTile(MusicPlaybackItemModel item)
    {
        var tile = new LibraryTileModel(item.Title, item.Artist, ThumbnailKind.Album)
        {
            OpenCommand = new RelayCommand(() => playback.PlayQueueItem(item)),
        };
        tile.ArtLoader = t => LoadTileArtAsync(t, item.FilePath);

        // The upcoming list is short and uses a plain template, so request artwork eagerly.
        tile.RequestArt();
        return tile;
    }

    private async Task LoadTileArtAsync(LibraryTileModel tile, string filePath)
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

    private bool CanOpenArtist() => openArtist is not null && !string.IsNullOrWhiteSpace(Artist);

    private void OpenArtist()
    {
        if (CanOpenArtist())
        {
            openArtist!(Artist);
        }
    }

    private bool CanOpenAlbum() =>
        openAlbum is not null
        && !string.IsNullOrWhiteSpace(Album)
        && !string.IsNullOrWhiteSpace(playback.CurrentItem?.FilePath);

    private void OpenAlbum()
    {
        var item = playback.CurrentItem;
        if (openAlbum is not null && !string.IsNullOrWhiteSpace(Album) && !string.IsNullOrWhiteSpace(item?.FilePath))
        {
            openAlbum(item!.FilePath);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        playback.PropertyChanged -= OnPlaybackChanged;
        playback.QueueItems.CollectionChanged -= OnQueueChanged;
        playback.QueueItemsMetadataChanged -= OnQueueMetadataChanged;
    }
}
