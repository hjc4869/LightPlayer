using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.Strings;
using LightStudio.MediaLibraryCore.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.Database;

public static class LibraryHelper
{
    private static IQueryable<MediaLibraryAlbum> ListAlbumsCommon(MediaLibraryDbContext context, string artist)
    {
        var files = context.MediaFiles
            .Where(f => f.Album != null);
        if (artist != null)
        {
            files = files.Where(f => f.AlbumArtist == artist);
        }

        return files
            .GroupBy(f => new { f.Album, Artist = f.AlbumArtist })
            .Select(g => new MediaLibraryAlbum
            {
                FileCount = g.Count(),
                Date = g.First().Date,
                ArtistForQuery = g.First().AlbumArtist,
                Artist = g.First().AlbumArtist ?? (g.Select(f => f.Artist).Distinct().Count() == 1 ? g.Select(f => f.Artist).Distinct().First() : Resources.VariousArtist),
                Title = g.First().Album,
                Genre = g.First().Genre,
                FirstFileInAlbum = g.First().Path,
                DatabaseItemAddedDate = g.First().DatabaseItemAddedDate
            });
    }

    private static IQueryable<MediaLibraryArtist> ListArtistsCommon(MediaLibraryDbContext context)
    {
        return context.MediaFiles
            .Where(f => f.Artist != null)
            .GroupBy(f => f.Artist)
            .Select(g => new MediaLibraryArtist
            {
                Name = g.First().Artist,
                FileCount = g.Count(),
                AlbumCount = g.Select(a => a.Album).Distinct().Count(),
                DatabaseItemAddedDate = g.First().DatabaseItemAddedDate
            });
    }

    private static IQueryable<DbMediaFile> GetMediaFileByAlbumCommon(MediaLibraryDbContext context, string albumName, string artistName)
    {
        return context.MediaFiles
            .Where(f => f.Album == albumName && f.AlbumArtist == artistName)
            .OrderBy(f => f.DiscNumber)
            .ThenBy(f => f.TrackNumber);
    }

    private static IQueryable<DbMediaFile> GetMediaFileByArtistCommon(MediaLibraryDbContext context, string artistName)
    {
        return context.MediaFiles
            .Where(f => f.Artist == artistName || f.AlbumArtist == artistName)
            .OrderBy(f => f.Album)
            .ThenBy(f => f.DiscNumber)
            .ThenBy(f => f.TrackNumber);
    }

    public static List<MediaLibraryAlbum> ListAlbums(string artist = null)
    {
        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            return ListAlbumsCommon(context, artist).ToList();
        }
    }

    public static async Task<List<MediaLibraryAlbum>> ListAlbumsAsync(string artist = null)
    {
        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            return await ListAlbumsCommon(context, artist).ToListAsync();
        }
    }

    public static async Task<List<DbMediaFile>> ListMediaFilesAsync()
    {
        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            return await context.MediaFiles.ToListAsync();
        }
    }

    public static List<MediaLibraryArtist> ListArtists()
    {
        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            return ListArtistsCommon(context).ToList();
        }
    }

    public static async Task<List<MediaLibraryArtist>> ListArtistsAsync()
    {
        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            return await ListArtistsCommon(context).ToListAsync();
        }
    }

    public static async Task<MediaLibraryArtist> GetArtistByNameAsync(string name)
    {
        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            var artist = await ListArtistsCommon(context).Where(a => a.Name == name).FirstOrDefaultAsync();
            if (artist == null)
            {
                throw new ArgumentException($"Artist {name} not found.");
            }

            return artist;
        }
    }

    /// <summary>
    /// Returns whether the library contains an artist with the given name.
    /// Artists are keyed on the track <c>Artist</c> tag, so synthetic album
    /// display names such as <see cref="Resources.VariousArtist"/> resolve to
    /// <see langword="false"/> and therefore have no artist detail page.
    /// </summary>
    public static async Task<bool> ArtistExistsAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            return await context.MediaFiles.AnyAsync(f => f.Artist == name);
        }
    }

    public static async Task<MediaLibraryAlbum> GetAlbumByNameAsync(string albumArtist, string albumName)
    {
        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            var album = await ListAlbumsCommon(context, albumArtist)
                .Where(a => a.Title == albumName)
                .FirstOrDefaultAsync();
            if (album == null)
            {
                throw new ArgumentException($"Album {albumName} from artist {albumArtist} not found.");
            }

            return album;
        }
    }

    public static async Task<MediaLibraryAlbum> GetAlbumByTrackPathAsync(string path)
    {
        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            var file = await context.MediaFiles
                .Where(f => f.Path == path)
                .Select(f => new { f.Album, f.AlbumArtist })
                .FirstOrDefaultAsync();
            if (file == null || string.IsNullOrWhiteSpace(file.Album))
            {
                throw new ArgumentException($"No album found for the file at {path}.");
            }

            var album = await ListAlbumsCommon(context, file.AlbumArtist)
                .Where(a => a.Title == file.Album)
                .FirstOrDefaultAsync();
            if (album == null)
            {
                throw new ArgumentException($"Album {file.Album} not found.");
            }

            return album;
        }
    }

    public static Task<MediaLibraryAlbum> GetAlbumAsync(this MediaLibraryItemIdentifier id)
    {
        if (id.Type != MediaLibraryItemIdentifier.ItemType.Album)
        {
            throw new InvalidOperationException("Item is not an album");
        }

        return GetAlbumByNameAsync(id.ArtistName, id.AlbumName);
    }

    public static Task<MediaLibraryArtist> GetArtistAsync(this MediaLibraryItemIdentifier id)
    {
        if (id.Type != MediaLibraryItemIdentifier.ItemType.Artist)
        {
            throw new InvalidOperationException("Item is not an artist");
        }

        return GetArtistByNameAsync(id.ArtistName);
    }

    public static List<DbMediaFile> GetMediaFileByAlbum(this MediaLibraryAlbum album)
    {
        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            return GetMediaFileByAlbumCommon(context, album.Title, album.ArtistForQuery).ToList();
        }
    }

    public static List<DbMediaFile> GetMediaFileByAlbum(string albumName, string artistName)
    {
        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            return GetMediaFileByAlbumCommon(context, albumName, artistName).ToList();
        }
    }

    public static async Task<List<DbMediaFile>> GetMediaFileByAlbumAsync(this MediaLibraryAlbum album)
    {
        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            return await GetMediaFileByAlbumCommon(context, album.Title, album.ArtistForQuery).ToListAsync();
        }
    }

    public static async Task<List<DbMediaFile>> GetMediaFileByArtistAsync(this MediaLibraryArtist artist)
    {
        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            return await GetMediaFileByArtistCommon(context, artist.Name).ToListAsync();
        }
    }

    public static async Task<List<DbMediaFile>> GetMediaFileByAlbumAsync(string albumName, string artistName)
    {
        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            return await GetMediaFileByAlbumCommon(context, albumName, artistName).ToListAsync();
        }
    }

    /// <summary>
    /// Searches the library for albums, artists, and media files matching the
    /// supplied keyword. An SQL LIKE filter followed by an in-memory similarity
    /// ranking against the keyword.
    /// </summary>
    public static async Task<LibrarySearchResults> SearchAsync(string keyword)
    {
        var results = new LibrarySearchResults();
        var trimmed = keyword?.ToLower().Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return results;
        }

        using (var scope = ApplicationServiceBase.App.GetScope())
        using (var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>())
        {
            var albumQuery = ListAlbumsCommon(context, null)
                .Where(album => EF.Functions.Like(album.Title, $"%{trimmed}%") ||
                                EF.Functions.Like(album.Artist, $"%{trimmed}%") ||
                                EF.Functions.Like(album.Genre, $"%{trimmed}%"));

            var artistQuery = ListArtistsCommon(context)
                .Where(artist => EF.Functions.Like(artist.Name, $"%{trimmed}%"));

            var musicQuery = context.MediaFiles
                .Where(file => EF.Functions.Like(file.Title, $"%{trimmed}%") ||
                               EF.Functions.Like(file.Artist, $"%{trimmed}%") ||
                               EF.Functions.Like(file.AlbumArtist, $"%{trimmed}%") ||
                               EF.Functions.Like(file.Album, $"%{trimmed}%"));

            var albums = await albumQuery.ToListAsync();
            var artists = await artistQuery.ToListAsync();
            var files = await musicQuery.ToListAsync();

            results.Albums = albums
                .OrderByDescending(x => (x.Title ?? string.Empty).ToLower().Similarity(trimmed))
                .ToList();
            results.Artists = artists
                .OrderByDescending(x => (x.Name ?? string.Empty).ToLower().Similarity(trimmed))
                .ToList();
            results.Files = files
                .OrderByDescending(x => (x.Title ?? string.Empty).ToLower().Similarity(trimmed))
                .ToList();
        }

        return results;
    }
}

/// <summary>
/// Result groups returned by <see cref="LibraryHelper.SearchAsync(string)"/>.
/// </summary>
public class LibrarySearchResults
{
    public List<MediaLibraryAlbum> Albums { get; set; } = new List<MediaLibraryAlbum>();

    public List<MediaLibraryArtist> Artists { get; set; } = new List<MediaLibraryArtist>();

    public List<DbMediaFile> Files { get; set; } = new List<DbMediaFile>();
}
