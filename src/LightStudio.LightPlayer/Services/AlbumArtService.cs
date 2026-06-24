using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using LightStudio.FfmpegShim;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Loads album/track artwork from a track's embedded cover (via the FFmpeg shim),
/// falling back to a folder/cover image next to the file. Decoded bitmaps are
/// cached in memory per album so navigating back to a page is instant, and a
/// persistent JPEG thumbnail cache on disk survives restarts. Extractions are
/// throttled so a large library does not saturate the thread pool.
/// </summary>
public sealed class AlbumArtService
{
    private const int DecodeWidth = 256;

    private static readonly string[] FolderImageNames =
    [
        "cover.jpg", "cover.jpeg", "folder.jpg", "folder.jpeg", "front.jpg", "front.jpeg",
        "cover.png", "folder.png", "front.png",
    ];

    private readonly ConcurrentDictionary<string, Task<Bitmap?>> cache = new();
    private readonly SemaphoreSlim throttle = new(initialCount: 3);
    private readonly string thumbnailDirectory = AppPaths.CacheSubdirectory("Thumbnails");

    /// <summary>Loads artwork for an album, keyed by artist+album so all tracks share one image.</summary>
    public Task<Bitmap?> GetAlbumArtAsync(string artistKey, string albumKey, string? firstFilePath)
    {
        var key = $"{artistKey}\u0000{albumKey}";
        return cache.GetOrAdd(key, _ => LoadThrottledAsync(key, firstFilePath));
    }

    /// <summary>Loads artwork for a single track, keyed by file path (used by now-playing).</summary>
    public Task<Bitmap?> GetTrackArtAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return Task.FromResult<Bitmap?>(null);
        }

        return cache.GetOrAdd(TrackCacheKey(filePath), _ => LoadThrottledAsync(filePath, filePath));
    }

    /// <summary>
    /// Ensures a track's artwork is decoded and cached to disk, returning the local
    /// thumbnail file path (or null when the track has no artwork). Shares the
    /// in-memory cache and on-disk thumbnail with <see cref="GetTrackArtAsync"/>.
    /// Used to hand a file path to OS media controls, which need an artwork URI.
    /// </summary>
    public async Task<string?> GetTrackArtFilePathAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        var bitmap = await GetTrackArtAsync(filePath).ConfigureAwait(false);
        if (bitmap is null)
        {
            return null;
        }

        // The on-disk thumbnail is keyed by the file path itself (the cache key
        // GetTrackArtAsync hands to the loader), not the in-memory "track\0..." key.
        var thumbnailPath = ThumbnailPathFor(filePath);
        return File.Exists(thumbnailPath) ? thumbnailPath : null;
    }

    private static string TrackCacheKey(string filePath) => $"track\u0000{filePath}";

    private async Task<Bitmap?> LoadThrottledAsync(string cacheKey, string? firstFilePath)
    {
        await throttle.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => LoadAlbumArt(cacheKey, firstFilePath)).ConfigureAwait(false);
        }
        finally
        {
            throttle.Release();
        }
    }

    private Bitmap? LoadAlbumArt(string cacheKey, string? filePath)
    {
        var thumbnailPath = ThumbnailPathFor(cacheKey);

        // A previously decoded thumbnail on disk loads far faster than re-probing
        // the source file and survives application restarts.
        try
        {
            if (File.Exists(thumbnailPath))
            {
                using var cached = File.OpenRead(thumbnailPath);
                return new Bitmap(cached);
            }
        }
        catch
        {
            // Corrupt cache entry: fall through and re-extract.
        }

        try
        {
            var bytes = ExtractEmbeddedCover(filePath) ?? ReadFolderImage(filePath);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = Bitmap.DecodeToWidth(stream, DecodeWidth);
            SaveThumbnail(thumbnailPath, bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private string ThumbnailPathFor(string cacheKey)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(cacheKey));
        var name = Convert.ToHexString(bytes) + ".jpg";
        return Path.Combine(thumbnailDirectory, name);
    }

    private static void SaveThumbnail(string path, Bitmap bitmap)
    {
        try
        {
            using var file = File.Create(path);
            bitmap.Save(file);
        }
        catch
        {
            // Best effort: failing to cache only means the next request re-decodes.
        }
    }

    private static byte[]? ExtractEmbeddedCover(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var fileStream = File.OpenRead(filePath);
            using var coverStream = FfmpegCodec.GetAlbumCoverFromStream(fileStream, close: false);
            if (coverStream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            coverStream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReadFolderImage(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            // Match case-insensitively so folder/cover art is found on case-sensitive file systems.
            var byLowerName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                byLowerName.TryAdd(Path.GetFileName(path).ToLowerInvariant(), path);
            }

            foreach (var name in FolderImageNames)
            {
                if (byLowerName.TryGetValue(name, out var match))
                {
                    return File.ReadAllBytes(match);
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
