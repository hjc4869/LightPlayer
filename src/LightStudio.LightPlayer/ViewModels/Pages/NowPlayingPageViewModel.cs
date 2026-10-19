using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
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

using Strings = LightStudio.LightPlayer.Strings;

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
    private readonly ILyricsService lyrics;
    private readonly IDialogService? dialogService;
    private readonly Action<MusicPlaybackItemModel>? openArtist;
    private readonly Action<MusicPlaybackItemModel>? openAlbum;
    private readonly MusicTileRow upcomingRow;
    private ParsedLrc? current;
    private int lineIndex = -1;
    private int artVersion;
    private bool disposed;
    private CancellationTokenSource? lyricSearchCts;
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
    private MediaReference? artworkKey;

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
        ILyricsService lyrics,
        IDialogService? dialogService,
        Action<MusicPlaybackItemModel>? openArtist,
        Action<MusicPlaybackItemModel>? openAlbum)
    {
        this.playback = playback;
        this.albumArt = albumArt;
        this.lyrics = lyrics;
        this.dialogService = dialogService;
        this.openArtist = openArtist;
        this.openAlbum = openAlbum;
        upcomingRow = new MusicTileRow(CreateUpcomingTile);
        TitleKey = nameof(Strings.Resources.Nav_NowPlaying);

        Lines = new ObservableCollection<LyricLineModel>();
        SearchLyricsCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(SearchLyricsAsync), () => playback.CurrentItem is not null && dialogService is not null);
        OffsetEarlierCommand = new RelayCommand(() => AdjustOffset(OffsetStepMs), () => current?.IsSynced == true);
        OffsetLaterCommand = new RelayCommand(() => AdjustOffset(-OffsetStepMs), () => current?.IsSynced == true);
        OpenArtistCommand = new RelayCommand(OpenArtist, CanOpenArtist);
        OpenAlbumCommand = new RelayCommand(OpenAlbum, CanOpenAlbum);

        playback.PropertyChanged += OnPlaybackChanged;
        playback.QueueItems.CollectionChanged += OnQueueChanged;
        playback.QueueItemsMetadataChanged += OnQueueMetadataChanged;
        RefreshTrack();
    }

    public ObservableCollection<LyricLineModel> Lines { get; }

    public ObservableCollection<LibraryTileModel> Upcoming => upcomingRow.Tiles;

    internal MediaReference? CurrentMedia => playback.CurrentItem?.Media;

    public bool NoLyrics => !HasLyrics;

    public bool ShowNoLyricsMessage => !HasLyrics && !LrcSearchBusy;

    public string NoLyricsMessage => lyrics.HasSources
        ? Strings.Resources.NowPlaying_NoLyricsSearchImport
        : Strings.Resources.NowPlaying_NoLyricsAddSource;

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
        lyricSearchCts?.Cancel();
        lyricSearchCts?.Dispose();
        lyricSearchCts = new CancellationTokenSource();

        var item = playback.CurrentItem;
        if (AlbumImage is null)
            ArtworkKey = item?.Media;
        TrackTitle = item?.Title ?? string.Empty;
        Artist = item?.Artist ?? string.Empty;
        Album = item?.Album ?? string.Empty;
        var version = ++artVersion;
        if (item is null)
        {
            ArtworkKey = null;
            AlbumImage = null;
        }

        SearchLyricsCommand.NotifyCanExecuteChanged();
        OpenArtistCommand.NotifyCanExecuteChanged();
        OpenAlbumCommand.NotifyCanExecuteChanged();
        _ = LoadArtAsync(item, version);
        _ = LoadLyricsAsync(item, lyricSearchCts.Token);
        RefreshUpcoming();
    }

    private async Task LoadArtAsync(MusicPlaybackItemModel? item, int version)
    {
        if (item is null)
        {
            return;
        }

        var bitmap = await albumArt.GetHighQualityTrackArtAsync(item.Media).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (disposed || version != artVersion || !ReferenceEquals(playback.CurrentItem, item))
            {
                bitmap?.Dispose();
                return;
            }

            ArtworkKey = bitmap is not null ? item.Media : null;
            AlbumImage = bitmap;
        }, DispatcherPriority.Background);
    }

    partial void OnAlbumImageChanged(IImage? oldValue, IImage? newValue) =>
        (oldValue as IDisposable)?.Dispose();

    private async Task LoadLyricsAsync(MusicPlaybackItemModel? item, CancellationToken cancellationToken)
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
            var result = await lyrics.AutoSearchAsync(item.Title, item.Artist, item.Media, cancellationToken);
            if (!ReferenceEquals(playback.CurrentItem, item))
            {
                return;
            }

            lastCandidates = result.Candidates;
            SetLyrics(result.Lyrics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppErrorReporter.Report(Strings.Resources.NowPlaying_LyricsHeader, ex);
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
        if (HasLyrics && parsed?.IsSynced == true)
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
        if (index < 0)
        {
            return;
        }

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
        _ = AppErrorReporter.GuardAsync(() => lyrics.SaveAsync(current));
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
        var item = playback.CurrentItem;
        var start = item is null ? 0 : playback.QueueItems.IndexOf(item) + 1;
        upcomingRow.Update(playback.QueueItems.Skip(start).Take(16));
        HasUpcoming = Upcoming.Count > 0;
    }

    private LibraryTileModel CreateUpcomingTile(MusicPlaybackItemModel item)
    {
        var tile = new LibraryTileModel(item.Title, item.Artist, ThumbnailKind.Album)
        {
            EnableTileActions = false,
            OpenCommand = new RelayCommand(() => playback.PlayQueueItem(item)),
        };
        tile.ArtLoader = (t, token) => LoadTileArtAsync(t, item.Media, token);

        // The upcoming list is short and uses a plain template, so request artwork eagerly.
        tile.RequestArt();
        return tile;
    }

    private async Task LoadTileArtAsync(
        LibraryTileModel tile,
        MediaReference media,
        CancellationToken cancellationToken)
    {
        var image = await albumArt
            .GetLowQualityTrackArtAsync(media, tile.ArtLoadFifoWindowSize, tile.ArtPixelSize, cancellationToken, tile.ArtCachePool)
            .ConfigureAwait(false);
        if (image is null)
        {
            return;
        }

        AlbumArtService.ApplyOnUiThread(image, art => tile.Image = art, cancellationToken);
    }

    private bool CanOpenArtist() =>
        openArtist is not null && playback.CurrentItem is { Artist.Length: > 0 };

    private void OpenArtist()
    {
        if (CanOpenArtist())
        {
            openArtist!(playback.CurrentItem!);
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
            openAlbum(item!);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lyricSearchCts?.Cancel();
        lyricSearchCts?.Dispose();
        lyricSearchCts = null;
        playback.PropertyChanged -= OnPlaybackChanged;
        playback.QueueItems.CollectionChanged -= OnQueueChanged;
        playback.QueueItemsMetadataChanged -= OnQueueMetadataChanged;

        foreach (var tile in Upcoming)
        {
            tile.ReleaseArt();
        }

        AlbumImage = null;
    }
}
