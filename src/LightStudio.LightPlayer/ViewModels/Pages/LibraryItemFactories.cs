using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Builds <see cref="TrackRowModel"/> rows from database media files. Shared by
/// the music page and the album/artist/search pages so every list projects the
/// same columns and identity.
/// </summary>
internal static class TrackRowFactory
{
    public static TrackRowModel CreateRow(
        DbMediaFile file,
        TrackActionCallbacks callbacks,
        Func<IReadOnlyList<TrackRowModel>>? playbackContext = null)
    {
        var title = LibrarySortKeys.Fallback(file.Title, DeriveTitle(file.Path));
        return new TrackRowModel(
            file.Id,
            file.Path ?? string.Empty,
            title,
            file.Artist ?? string.Empty,
            file.Album ?? string.Empty,
            file.Date ?? string.Empty,
            file.Genre ?? string.Empty,
            LibrarySortKeys.FormatDuration(file.Duration),
            file.Duration,
            LibrarySortKeys.ParseLeadingInt(file.DiscNumber),
            LibrarySortKeys.ParseLeadingInt(file.TrackNumber),
            file.DatabaseItemAddedDate,
            callbacks,
            file.StartTime)
        {
            PlaybackContext = playbackContext,
        };
    }

    /// <summary>
    /// Orders an artist's tracks by album, then by natural disc/track order
    /// within each album, so playback and the detail list queue album-by-album
    /// in on-disc order.
    /// </summary>
    public static List<TrackRowModel> OrderByAlbumThenTrack(IEnumerable<TrackRowModel> rows) =>
        rows
            .OrderBy(row => row.Album, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.DiscNumber)
            .ThenBy(row => row.TrackNumber)
            .ToList();

    private static string DeriveTitle(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "Unknown title" : Path.GetFileNameWithoutExtension(path);
}

/// <summary>
/// Builds album <see cref="LibraryTileModel"/> tiles with lazy artwork loading.
/// Shared by the albums page and the artist detail page so both extract covers
/// the same throttled, UI-thread-safe way.
/// </summary>
internal static class AlbumTileFactory
{
    public static LibraryTileModel Create(MediaLibraryAlbum album, TileActionCallbacks? tileActions, AlbumArtService albumArt)
    {
        var title = LibrarySortKeys.Fallback(album.Title, "Unknown Album");
        var artist = LibrarySortKeys.Fallback(album.Artist, "Unknown Artist");
        var year = YearLabel(album.Date);
        var count = album.FileCount == 1 ? "1 item" : $"{album.FileCount} items";
        var subtitle = string.IsNullOrEmpty(year) ? $"{artist} · {count}" : $"{artist} · {year} · {count}";

        var artistKey = album.ArtistForQuery ?? artist;
        var albumKey = album.Title ?? title;
        var firstFile = album.FirstFileInAlbum;

        var tile = new LibraryTileModel(title, subtitle, ThumbnailKind.Album)
        {
            ArtistName = artist,
            Year = year,
            Identifier = album,
        };
        tile.OpenCommand = new RelayCommand(() => tileActions?.Open?.Invoke(tile));
        tile.PlayCommand = new RelayCommand(() => tileActions?.Play?.Invoke(tile));
        tile.AddToQueueCommand = new RelayCommand(() => tileActions?.AddToQueue?.Invoke(tile));
        tile.ArtLoader = t => LoadArtAsync(albumArt, t, artistKey, albumKey, firstFile);
        return tile;
    }

    public static string YearLabel(string? date)
    {
        var parsed = LibrarySortKeys.ParseLeadingInt(date);
        return parsed > 0 ? parsed.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private static async Task LoadArtAsync(AlbumArtService albumArt, LibraryTileModel tile, string artistKey, string albumKey, string? firstFilePath)
    {
        var bitmap = await albumArt.GetAlbumArtAsync(artistKey, albumKey, firstFilePath);
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
}
