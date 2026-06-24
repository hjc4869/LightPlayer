using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.Lyrics;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LightPlayer.Services;

/// <summary>The outcome of an auto-search: the resolved lyrics (if any) and the
/// online candidates discovered along the way, so the manual picker can reuse
/// them without searching again.</summary>
public sealed record LyricsSearchResult(ParsedLrc? Lyrics, IReadOnlyList<ExternalLrcInfo> Candidates);

/// <summary>
/// Loads, imports, searches, downloads, and clears LRC lyrics. Parsing lives in
/// the cross-platform <see cref="LrcParser"/>; this service owns the desktop
/// cache path, the sidecar-file lookup, and the online search/download flow via
/// <see cref="ILyricSourceService"/>. Lyrics are matched first to a <c>.lrc</c>
/// next to the audio file, then to the per-user cache so manual imports and
/// downloads survive restarts.
/// </summary>
public sealed class LyricsService
{
    private readonly string cacheDirectory = AppPaths.CacheSubdirectory("Lyrics");
    private readonly ILyricSourceService? sources;

    public LyricsService(ILyricSourceService? sources = null)
    {
        this.sources = sources;
    }

    /// <summary>True when at least one online lyric source is registered.</summary>
    public bool HasSources => sources?.HasSources == true;

    /// <summary>Finds and parses lyrics for a track, or returns null when none exist.</summary>
    public Task<ParsedLrc?> LoadAsync(string title, string artist, string? filePath)
    {
        return Task.Run<ParsedLrc?>(() =>
        {
            var sidecar = SidecarPath(filePath);
            var cache = CachePath(title, artist);

            foreach (var path in new[] { sidecar, cache })
            {
                if (path is not null && File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    try
                    {
                        var parsed = LrcParser.Parse(File.ReadAllText(path));
                        parsed.CacheFileName = cache;
                        return parsed.Sentences.Count > 0 ? parsed : null;
                    }
                    catch
                    {
                        // Try the next candidate.
                    }
                }
            }

            return null;
        });
    }

    /// <summary>
    /// Resolves lyrics for a track the way the original now-playing view did:
    /// local sidecar/cache first, then an automatic online search that downloads
    /// the top result only when it matches the track exactly. A miss writes an
    /// empty cache marker so the same track is not searched again.
    /// </summary>
    public async Task<LyricsSearchResult> AutoSearchAsync(string title, string artist, string? filePath)
    {
        // 1. Local sidecar or a previously cached/downloaded file wins outright.
        var local = await LoadAsync(title, artist, filePath);
        if (local is not null)
        {
            return new LyricsSearchResult(local, Array.Empty<ExternalLrcInfo>());
        }

        // 2. An existing (possibly empty) cache file means we already searched.
        var cache = CachePath(title, artist);
        if (File.Exists(cache))
        {
            return new LyricsSearchResult(null, Array.Empty<ExternalLrcInfo>());
        }

        // 3. Without a source we cannot search; leave the track uncached so it
        //    retries once the user adds a source or imports lyrics.
        if (sources is null || !sources.HasSources)
        {
            return new LyricsSearchResult(null, Array.Empty<ExternalLrcInfo>());
        }

        // 4. Query every source and only auto-download an exact title/artist match.
        IReadOnlyList<ExternalLrcInfo> candidates;
        try
        {
            candidates = await sources.LookupAsync(title, artist);
        }
        catch
        {
            candidates = Array.Empty<ExternalLrcInfo>();
        }

        if (candidates.Count == 0 ||
            !string.Equals(title, candidates[0].Title, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(artist, candidates[0].Artist, StringComparison.OrdinalIgnoreCase))
        {
            WriteNegativeCache(cache);
            return new LyricsSearchResult(null, candidates);
        }

        var downloaded = await DownloadAsync(candidates[0], title, artist);
        if (downloaded is null)
        {
            WriteNegativeCache(cache);
        }

        return new LyricsSearchResult(downloaded, candidates);
    }

    /// <summary>Searches every source for candidates without touching the cache.</summary>
    public Task<IReadOnlyList<ExternalLrcInfo>> SearchAsync(string title, string artist)
    {
        return sources?.LookupAsync(title, artist)
            ?? Task.FromResult<IReadOnlyList<ExternalLrcInfo>>(Array.Empty<ExternalLrcInfo>());
    }

    /// <summary>Downloads a chosen candidate, caches it for the track, and parses it.</summary>
    public async Task<ParsedLrc?> DownloadAsync(ExternalLrcInfo candidate, string title, string artist)
    {
        if (sources is null)
        {
            return null;
        }

        var text = await sources.DownloadAsync(candidate);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var cache = CachePath(title, artist);
            File.WriteAllText(cache, text);
            var parsed = LrcParser.Parse(text);
            parsed.CacheFileName = cache;
            return parsed.Sentences.Count > 0 ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Copies an LRC file into the cache for this track and returns the parsed result.</summary>
    public async Task<ParsedLrc?> ImportAsync(string title, string artist, string sourcePath)
    {
        return await Task.Run<ParsedLrc?>(() =>
        {
            try
            {
                var text = File.ReadAllText(sourcePath);
                var cache = CachePath(title, artist);
                File.WriteAllText(cache, text);
                var parsed = LrcParser.Parse(text);
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
                File.WriteAllText(CachePath(title, artist), string.Empty);
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

    private static string? SidecarPath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(filePath);
        return directory is null ? null : Path.Combine(directory, Path.GetFileNameWithoutExtension(filePath) + ".lrc");
    }

    private string CachePath(string title, string artist)
    {
        var name = Sanitize($"{artist} - {title}") + ".lrc";
        return Path.Combine(cacheDirectory, name);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
    }
}
