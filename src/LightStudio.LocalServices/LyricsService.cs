using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.MediaLibraryCore.Lyrics;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Loads LRC sidecars and cached lyrics, and manages online searches, downloads, and imports.
/// </summary>
public sealed class LyricsService : ILyricsService, IStorageCache, IMediaResolverAwareLyricsService
{
    private readonly string cacheDirectory = AppPaths.CacheSubdirectory("Lyrics");
    private readonly ILyricSourceService? sources;
    private readonly DirectoryStorageCache cacheStorage;
    private MediaReference? currentMedia;

    public LyricsService(ILyricSourceService? sources = null)
    {
        this.sources = sources;
        cacheStorage = new DirectoryStorageCache(cacheDirectory);
    }

    /// <summary>True when at least one online lyric source is registered.</summary>
    public bool HasSources => sources?.HasSources == true;

    public IClientMediaResolver? MediaResolver { get; set; }

    /// <inheritdoc />
    public Task<StorageCacheUsage> MeasureAsync() => cacheStorage.MeasureAsync();

    /// <summary>Drops every cached lyric. Sidecar files next to the audio are untouched.</summary>
    public Task ClearAsync() => cacheStorage.ClearAsync();

    /// <summary>Finds and parses lyrics for a track, or returns null when none exist.</summary>
    public async Task<ParsedLrc?> LoadAsync(string title, string artist, MediaReference? media)
    {
        currentMedia = media;
        var cache = CachePath(title, artist, media);
        var sidecar = await ResolveSidecarAsync(media).ConfigureAwait(false);
        if (sidecar is not null)
        {
            try
            {
                await using var stream = await sidecar.Value.Platform
                    .OpenMediaFileAsync(sidecar.Value.Path)
                    .ConfigureAwait(false);
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                var content = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    var parsed = LrcParser.Parse(content);
                    parsed.CacheFileName = cache;
                    if (parsed.Sentences.Count > 0)
                    {
                        return parsed;
                    }
                }
            }
            catch
            {
                // Try the app-owned cache next.
            }
        }

        return await Task.Run<ParsedLrc?>(() =>
        {
            if (!File.Exists(cache) || new FileInfo(cache).Length == 0)
            {
                return null;
            }

            try
            {
                var parsed = LrcParser.Parse(File.ReadAllText(cache));
                parsed.CacheFileName = cache;
                return parsed.Sentences.Count > 0 ? parsed : null;
            }
            catch
            {
                return null;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads local lyrics, then downloads the top online result when its title and artist match.
    /// A miss writes an empty cache marker so the track is not searched again.
    /// </summary>
    public async Task<LyricsSearchResult> AutoSearchAsync(
        string title,
        string artist,
        MediaReference? media,
        CancellationToken cancellationToken = default)
    {
        // Local lyrics take precedence over online results.
        cancellationToken.ThrowIfCancellationRequested();
        currentMedia = media;
        var local = await LoadAsync(title, artist, media);
        if (local is not null)
        {
            return new LyricsSearchResult(local, Array.Empty<ExternalLrcInfo>());
        }

        // An empty cache file marks a completed search with no exact match.
        var cache = CachePath(title, artist, media);
        if (File.Exists(cache))
        {
            return new LyricsSearchResult(null, Array.Empty<ExternalLrcInfo>());
        }

        // Leave the track uncached so a source added later can search it.
        if (sources is null || !sources.HasSources)
        {
            return new LyricsSearchResult(null, Array.Empty<ExternalLrcInfo>());
        }

        IReadOnlyList<ExternalLrcInfo> candidates;
        try
        {
            candidates = await sources.LookupAsync(title, artist, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new LyricsSearchResult(null, Array.Empty<ExternalLrcInfo>());
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Count == 0 ||
            !string.Equals(title, candidates[0].Title, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(artist, candidates[0].Artist, StringComparison.OrdinalIgnoreCase))
        {
            WriteNegativeCache(cache);
            return new LyricsSearchResult(null, candidates);
        }

        var downloaded = await DownloadAsync(candidates[0], title, artist, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (downloaded is null)
        {
            WriteNegativeCache(cache);
        }

        return new LyricsSearchResult(downloaded, candidates);
    }

    /// <summary>Searches every source for candidates without touching the cache.</summary>
    public Task<IReadOnlyList<ExternalLrcInfo>> SearchAsync(
        string title,
        string artist,
        CancellationToken cancellationToken = default)
    {
        return sources?.LookupAsync(title, artist, cancellationToken)
            ?? Task.FromResult<IReadOnlyList<ExternalLrcInfo>>(Array.Empty<ExternalLrcInfo>());
    }

    /// <summary>Downloads a chosen candidate, caches it for the track, and parses it.</summary>
    public async Task<ParsedLrc?> DownloadAsync(
        ExternalLrcInfo candidate,
        string title,
        string artist,
        CancellationToken cancellationToken = default)
    {
        if (sources is null)
        {
            return null;
        }

        var download = await sources.DownloadAsync(candidate, cancellationToken);
        if (download is null || string.IsNullOrWhiteSpace(download.Content))
        {
            return null;
        }

        try
        {
            var cache = CachePath(title, artist, currentMedia);
            File.WriteAllText(cache, download.Content);
            var parsed = download.Type == LyricType.Synced
                ? LrcParser.Parse(download.Content)
                : LrcParser.ParsePlain(download.Content);
            parsed.CacheFileName = cache;
            return parsed.Sentences.Count > 0 ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Stores frontend-read LRC content in the cache and returns the parsed result.</summary>
    public async Task<ParsedLrc?> ImportAsync(string title, string artist, string content)
    {
        return await Task.Run<ParsedLrc?>(() =>
        {
            try
            {
                var cache = CachePath(title, artist, currentMedia);
                File.WriteAllText(cache, content);
                var parsed = LrcParser.Parse(content);
                parsed.CacheFileName = cache;
                return parsed.Sentences.Count > 0 ? parsed : null;
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>Persists timing-offset edits back to the cached LRC file.</summary>
    public Task SaveAsync(ParsedLrc lyrics)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrEmpty(lyrics.CacheFileName))
            {
                return;
            }

            try
            {
                File.WriteAllText(lyrics.CacheFileName, lyrics.SaveAsLrc());
            }
            catch
            {
                // Best effort.
            }
        });
    }

    /// <summary>Removes cached lyrics for a track (empties the cache file).</summary>
    public Task ClearAsync(string title, string artist)
    {
        return Task.Run(() =>
        {
            try
            {
                File.WriteAllText(CachePath(title, artist, currentMedia), string.Empty);
            }
            catch
            {
                // Best effort.
            }
        });
    }

    private static void WriteNegativeCache(string path)
    {
        try
        {
            File.WriteAllText(path, string.Empty);
        }
        catch
        {
            // Best effort: a missing marker just means we search again next time.
        }
    }

    private async Task<(IMultiMediaPlatformIO Platform, string Path)?> ResolveSidecarAsync(MediaReference? media)
    {
        if (media is not { Path.Length: > 0 } reference)
        {
            return null;
        }

        try
        {
            var platform = MediaResolver is null
                ? IMultiMediaPlatformIO.Instance
                : await MediaResolver.ResolvePlatformAsync(reference).ConfigureAwait(false);
            var properties = await platform
                .GetMediaFilePropertiesAsync(reference.Path)
                .ConfigureAwait(false);
            if (properties is null)
            {
                return null;
            }

            var sidecarName = Path.GetFileNameWithoutExtension(properties.Name) + ".lrc";
            var sidecar = await platform
                .ResolveFileRelativeToAsync(reference.Path, sidecarName)
                .ConfigureAwait(false);
            return string.IsNullOrEmpty(sidecar) ? null : (platform, sidecar);
        }
        catch
        {
            return null;
        }
    }

    private string CachePath(string title, string artist, MediaReference? media)
    {
        var directory = media is { SourceId: var sourceId } && sourceId != LibrarySourceId.Local
            ? Path.Combine(cacheDirectory, Sanitize(sourceId.Value))
            : cacheDirectory;
        Directory.CreateDirectory(directory);
        var name = Sanitize($"{artist} - {title}") + ".lrc";
        return Path.Combine(directory, name);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
    }
}
