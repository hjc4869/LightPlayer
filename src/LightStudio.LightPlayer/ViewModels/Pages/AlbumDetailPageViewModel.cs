using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Album detail page: album header (art, title, artist, year, genre, track
/// count, total duration) plus the ordered track list and album-level playback
/// actions.
/// </summary>
public sealed partial class AlbumDetailPageViewModel : PageViewModelBase
{
    private readonly MediaLibraryItemIdentifier identifier;
    private readonly LibraryCommands commands;
    private readonly AlbumArtService albumArt;

    // Raw artist name used to open the artist detail page; empty for albums with
    // no attributable artist, which disables the artist link.
    private string artistQueryName = string.Empty;

    // Whether artistQueryName resolves to a real artist in the library. Synthetic
    // display names such as "Various" have no artist page, so the link stays off.
    private bool artistExists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    private bool isLoading = true;

    [ObservableProperty]
    private IImage? coverImage;

    [ObservableProperty]
    private string albumTitle = string.Empty;

    [ObservableProperty]
    private string artistName = string.Empty;

    [ObservableProperty]
    private string metaLine = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<TrackRowModel> tracks = [];

    public AlbumDetailPageViewModel(MediaLibraryItemIdentifier identifier, LibraryCommands commands, AlbumArtService albumArt)
    {
        this.identifier = identifier;
        this.commands = commands;
        this.albumArt = albumArt;
        Title = "Album";

        BackCommand = new RelayCommand(() => this.commands.GoBack?.Invoke());
        PlayCommand = new RelayCommand(() => this.commands.PlayTracks?.Invoke(Tracks), () => Tracks.Count > 0);
        ShuffleCommand = new RelayCommand(() => this.commands.ShuffleTracks?.Invoke(Tracks), () => Tracks.Count > 0);
        AddToQueueCommand = new RelayCommand(() => this.commands.AddTracksToQueue?.Invoke(Tracks), () => Tracks.Count > 0);
        AddToPlaylistCommand = new RelayCommand(() => this.commands.AddTracksToPlaylist?.Invoke(Tracks), () => Tracks.Count > 0);
        OpenArtistCommand = new RelayCommand(OpenArtist, CanOpenArtist);
    }

    public bool ShowContent => !IsLoading;

    public IRelayCommand BackCommand { get; }

    public IRelayCommand PlayCommand { get; }

    public IRelayCommand ShuffleCommand { get; }

    public IRelayCommand AddToQueueCommand { get; }

    public IRelayCommand AddToPlaylistCommand { get; }

    public IRelayCommand OpenArtistCommand { get; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var album = await Task.Run(() => LibraryHelper.GetAlbumByNameAsync(identifier.ArtistName, identifier.AlbumName));
            AlbumTitle = LibrarySortKeys.Fallback(album.Title, "Unknown Album");
            ArtistName = LibrarySortKeys.Fallback(album.Artist, "Unknown Artist");
            artistQueryName = album.Artist ?? string.Empty;
            artistExists = await Task.Run(() => LibraryHelper.ArtistExistsAsync(artistQueryName));
            Title = AlbumTitle;

            var files = await Task.Run(() => LibraryHelper.GetMediaFileByAlbumAsync(album.Title, album.ArtistForQuery));
            var rows = files
                .Select(file => TrackRowFactory.CreateRow(file, commands.TrackActions, () => Tracks))
                .OrderBy(row => row.DiscNumber)
                .ThenBy(row => row.TrackNumber)
                .ToList();
            var total = files.Aggregate(TimeSpan.Zero, (sum, file) => sum + file.Duration);

            Tracks = rows;
            MetaLine = BuildMetaLine(album, rows.Count, total);
            NotifyCommands();

            _ = LoadCoverAsync(album);
        }
        catch (Exception)
        {
            // A missing or renamed album just yields an empty page; the shell logs
            // load failures raised elsewhere.
            Tracks = [];
            NotifyCommands();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadCoverAsync(MediaLibraryAlbum album)
    {
        var artistKey = album.ArtistForQuery ?? album.Artist ?? string.Empty;
        var albumKey = album.Title ?? string.Empty;
        Bitmap? bitmap = await albumArt.GetAlbumArtAsync(artistKey, albumKey, album.FirstFileInAlbum);
        if (bitmap is null)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            CoverImage = bitmap;
        }
        else
        {
            Dispatcher.UIThread.Post(() => CoverImage = bitmap);
        }
    }

    private void NotifyCommands()
    {
        PlayCommand.NotifyCanExecuteChanged();
        ShuffleCommand.NotifyCanExecuteChanged();
        AddToQueueCommand.NotifyCanExecuteChanged();
        AddToPlaylistCommand.NotifyCanExecuteChanged();
        OpenArtistCommand.NotifyCanExecuteChanged();
    }

    private bool CanOpenArtist() =>
        commands.OpenArtist is not null && !string.IsNullOrWhiteSpace(artistQueryName) && artistExists;

    private void OpenArtist()
    {
        if (CanOpenArtist())
        {
            commands.OpenArtist!(new MediaLibraryItemIdentifier
            {
                Type = MediaLibraryItemIdentifier.ItemType.Artist,
                ArtistName = artistQueryName,
            });
        }
    }

    private static string BuildMetaLine(MediaLibraryAlbum album, int trackCount, TimeSpan total)
    {
        var parts = new List<string>();
        var year = AlbumTileFactory.YearLabel(album.Date);
        if (!string.IsNullOrEmpty(year))
        {
            parts.Add(year);
        }

        if (!string.IsNullOrWhiteSpace(album.Genre))
        {
            parts.Add(album.Genre);
        }

        parts.Add(trackCount == 1 ? "1 item" : $"{trackCount} items");

        if (total > TimeSpan.Zero)
        {
            parts.Add(LibrarySortKeys.FormatDuration(total));
        }

        return string.Join("  ·  ", parts);
    }
}
