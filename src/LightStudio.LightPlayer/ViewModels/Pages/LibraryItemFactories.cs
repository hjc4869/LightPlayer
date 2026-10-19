using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

using Strings = LightStudio.LightPlayer.Strings;

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
        Func<IReadOnlyList<TrackRowModel>>? playbackContext = null,
        LibrarySourceId? sourceId = null)
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
            file.StartTime,
            sourceId)
        {
            PlaybackContext = playbackContext,
        };
    }

    public static TrackRowModel CreateRow(
        Sourced<DbMediaFile> file,
        TrackActionCallbacks callbacks,
        Func<IReadOnlyList<TrackRowModel>>? playbackContext = null) =>
        CreateRow(file.Value, callbacks, playbackContext, file.SourceId);

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
        string.IsNullOrWhiteSpace(path) ? Strings.Resources.Common_UnknownTitle : Path.GetFileNameWithoutExtension(path);
}

/// <summary>
/// Builds album <see cref="LibraryTileModel"/> tiles with lazy artwork loading.
/// Shared by the albums page and the artist detail page so both extract covers
/// the same throttled, UI-thread-safe way.
/// </summary>
internal static class AlbumTileFactory
{
    public static LibraryTileModel Create(MediaLibraryAlbum album, TileActionCallbacks? tileActions, AlbumArtService albumArt)
        => Create(new Sourced<MediaLibraryAlbum>(LibrarySourceId.Local, album), tileActions, albumArt);

    public static LibraryTileModel Create(
        Sourced<MediaLibraryAlbum> sourcedAlbum,
        TileActionCallbacks? tileActions,
        AlbumArtService albumArt)
    {
        var album = sourcedAlbum.Value;
        var title = LibrarySortKeys.Fallback(album.Title, Strings.Resources.Common_UnknownAlbum);
        var artist = LibrarySortKeys.Fallback(album.Artist, Strings.Resources.Common_UnknownArtist);
        var year = YearLabel(album.Date);
        var count = album.FileCount == 1 ? Resources.Common_OneItem : string.Format(Resources.Common_ItemsFormat, album.FileCount);
        var subtitle = string.IsNullOrEmpty(year) ? $"{artist} · {count}" : $"{artist} · {year} · {count}";

        var firstFile = album.FirstFileInAlbum;
        var tile = new LibraryTileModel(title, subtitle, ThumbnailKind.Album)
        {
            ArtistName = artist,
            Year = year,
            Identifier = LibraryItemReference.Album(sourcedAlbum.SourceId, album),
            Media = new MediaReference(sourcedAlbum.SourceId, firstFile ?? string.Empty),
        };
        tile.OpenCommand = new RelayCommand(() => tileActions?.Open?.Invoke(tile));
        tile.PlayCommand = new RelayCommand(() => tileActions?.Play?.Invoke(tile));
        tile.PlayNextCommand = new RelayCommand(() => tileActions?.PlayNext?.Invoke(tile));
        tile.AddToQueueCommand = new RelayCommand(() => tileActions?.AddToQueue?.Invoke(tile));
        tile.AddToPlaylistCommand = new RelayCommand(() => tileActions?.AddToPlaylist?.Invoke(tile));
        tile.OpenArtistCommand = new RelayCommand(() => tileActions?.OpenArtist?.Invoke(tile));
        tile.OpenFolderCommand = new RelayCommand(() => tileActions?.OpenContainingFolder?.Invoke(tile));
        tile.ArtLoader = (t, token) => LoadArtAsync(
            albumArt,
            t,
            new MediaReference(sourcedAlbum.SourceId, firstFile ?? string.Empty),
            token);
        return tile;
    }

    public static string YearLabel(string? date)
    {
        var parsed = LibrarySortKeys.ParseLeadingInt(date);
        return parsed > 0 ? parsed.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private static async Task LoadArtAsync(
        AlbumArtService albumArt,
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
}

internal static class SearchArtistTileFactory
{
    internal static LibraryTileModel Create(UnifiedArtistReference artist, TileActionCallbacks actions)
    {
        var name = LibrarySortKeys.Fallback(artist.DisplayName, Resources.Common_UnknownArtist);
        var subtitle = artist.SourceIds.Count == 1
            ? Resources.RemoteLibraries_LibrarySingular
            : string.Format(CultureInfo.CurrentCulture, Resources.RemoteLibraries_LibrariesFormat, artist.SourceIds.Count);
        var tile = new LibraryTileModel(name, subtitle, ThumbnailKind.Artist)
        {
            ArtistName = name,
            Identifier = LibraryItemReference.Artist(artist),
        };
        tile.OpenCommand = new RelayCommand(() => actions.Open?.Invoke(tile));
        tile.PlayCommand = new RelayCommand(() => actions.Play?.Invoke(tile));
        tile.PlayNextCommand = new RelayCommand(() => actions.PlayNext?.Invoke(tile));
        tile.AddToQueueCommand = new RelayCommand(() => actions.AddToQueue?.Invoke(tile));
        tile.AddToPlaylistCommand = new RelayCommand(() => actions.AddToPlaylist?.Invoke(tile));
        tile.OpenFolderCommand = new RelayCommand(() => actions.OpenContainingFolder?.Invoke(tile));
        return tile;
    }
}
