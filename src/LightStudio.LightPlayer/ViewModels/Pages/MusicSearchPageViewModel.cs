using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed partial class MusicSearchPageViewModel : PageViewModelBase, IIncrementalLoadingViewModel,
    IScrollPositionPageViewModel, INowPlayingHighlightTarget, IDisposable
{
    private const int BatchSize = 60;
    private readonly LibraryCommands commands;
    private readonly AlbumArtService albumArt;
    private readonly IUnifiedLibraryQueryService libraryQuery;
    private readonly CancellationTokenSource lifetime = new();
    private UnifiedLibrarySearchResults? results;
    private Task? loadMoreTask;
    private bool hasMore = true;
    private MediaItemKey? nowPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool isBusy;

    [ObservableProperty]
    private string errorText = string.Empty;

    public MusicSearchPageViewModel(string query, LibraryItemKind kind, LibraryCommands commands,
        AlbumArtService albumArt, IUnifiedLibraryQueryService libraryQuery,
        UnifiedLibrarySearchResults? results = null, Action? onBack = null)
    {
        Query = query;
        Kind = kind;
        this.commands = commands;
        this.albumArt = albumArt;
        this.libraryQuery = libraryQuery;
        this.results = results;
        TitleKey = kind switch
        {
            LibraryItemKind.Album => nameof(Resources.Nav_Albums),
            LibraryItemKind.Artist => nameof(Resources.Nav_Artists),
            _ => nameof(Resources.Nav_Music),
        };
        BackCommand = new RelayCommand(() => onBack?.Invoke(), () => onBack is not null);
    }

    public string Query { get; }
    public LibraryItemKind Kind { get; }
    public string HeaderText => string.Format(Resources.Search_ResultsForFormat, Query);
    public IRelayCommand BackCommand { get; }
    public bool ShowBackButton => BackCommand.CanExecute(null) && !PlatformServices.HostProvidesBackNavigation && !HasShellBackButton;
    public ObservableCollection<LibraryTileModel> Tiles { get; } = [];
    public ObservableCollection<TrackRowModel> Tracks { get; } = [];
    public bool ShowTiles => Kind != LibraryItemKind.Song;
    public bool ShowTracks => Kind == LibraryItemKind.Song;
    public bool CanLoadMore => hasMore && !IsBusy && !lifetime.IsCancellationRequested;
    public int LoadedItemCount => ShowTiles ? Tiles.Count : Tracks.Count;
    public int TotalItemCount => Kind switch
    {
        LibraryItemKind.Album => results?.Albums.Count ?? 0,
        LibraryItemKind.Artist => results?.Artists.Count ?? 0,
        _ => results?.Files.Count ?? 0,
    };
    public bool ShowEmpty => !IsBusy && LoadedItemCount == 0;
    public double ScrollOffset { get; set; }

    protected override void OnHasShellBackButtonChanged(bool value) => OnPropertyChanged(nameof(ShowBackButton));

    public Task LoadAsync() => loadMoreTask ?? LoadMoreAsync();

    public void SetNowPlaying(MediaItemKey? mediaItem)
    {
        nowPlaying = mediaItem;
        NowPlayingHighlight.Apply(Tracks, nowPlaying);
    }

    public Task LoadMoreAsync() => loadMoreTask is { IsCompleted: false }
        ? loadMoreTask
        : loadMoreTask = LoadMoreCoreAsync();

    private async Task LoadMoreCoreAsync()
    {
        if (!CanLoadMore)
            return;
        IsBusy = true;
        try
        {
            results ??= await libraryQuery.SearchAsync(Query, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            ErrorText = string.Join(Environment.NewLine, results.SourceFailures.Select(failure =>
                $"{failure.SourceName}: {failure.Message}"));
            var offset = LoadedItemCount;
            switch (Kind)
            {
                case LibraryItemKind.Album:
                    foreach (var album in results.Albums.Skip(offset).Take(BatchSize))
                        Tiles.Add(AlbumTileFactory.Create(album, commands.AlbumTileActions, albumArt));
                    break;
                case LibraryItemKind.Artist:
                    foreach (var artist in results.Artists.Skip(offset).Take(BatchSize))
                        Tiles.Add(SearchArtistTileFactory.Create(artist, commands.ArtistTileActions));
                    break;
                default:
                    foreach (var file in results.Files.Skip(offset).Take(BatchSize))
                    {
                        var row = TrackRowFactory.CreateRow(file, commands.TrackActions, () => Tracks);
                        row.AsyncPlaybackContext = LoadPlaybackContextAsync;
                        Tracks.Add(row);
                    }
                    NowPlayingHighlight.Apply(Tracks, nowPlaying);
                    break;
            }
            hasMore = LoadedItemCount < TotalItemCount;
            OnPropertyChanged(nameof(LoadedItemCount));
            OnPropertyChanged(nameof(TotalItemCount));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Warning("Search", $"Could not load {Kind} search results.", exception);
            ErrorText = exception.Message;
            hasMore = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task<IReadOnlyList<TrackRowModel>> LoadPlaybackContextAsync() =>
        Task.FromResult<IReadOnlyList<TrackRowModel>>(results?.Files.Select(file =>
            TrackRowFactory.CreateRow(file, commands.TrackActions)).ToArray() ?? []);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy || lifetime.IsCancellationRequested)
            return;
        foreach (var tile in Tiles)
            tile.ReleaseArt();
        Tiles.Clear();
        Tracks.Clear();
        results = null;
        ScrollOffset = 0;
        hasMore = true;
        await LoadMoreAsync();
    }

    public void Dispose()
    {
        lifetime.Cancel();
        foreach (var tile in Tiles)
            tile.ReleaseArt();
    }
}