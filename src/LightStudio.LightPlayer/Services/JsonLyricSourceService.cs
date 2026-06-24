using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.Lyrics;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Disk-backed lyric source registry. Each script lives as a <c>.js</c> file in
/// the application data <c>Scripts</c> folder; the file name (without extension)
/// is the source name. Bundled sources are embedded resources provisioned on
/// first run. Registered scripts are compiled into <see cref="JsDownloadSource"/>
/// engines on demand to look up and download lyrics online.
/// </summary>
public sealed class JsonLyricSourceService : ILyricSourceService
{
    private static readonly string[] BundledSources = ["netease", "qqmusic", "xiami"];

    private readonly string scriptDirectory;
    private readonly object engineLock = new();
    private Dictionary<string, JsDownloadSource>? engines;
    private bool enginesDirty = true;

    public JsonLyricSourceService()
        : this(Path.Combine(AppPaths.DataDirectory, "Scripts"))
    {
    }

    public JsonLyricSourceService(string scriptDirectory)
    {
        this.scriptDirectory = scriptDirectory;
        try
        {
            Directory.CreateDirectory(scriptDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Listing degrades to empty if the folder cannot be created.
        }
    }

    public IReadOnlyList<string> SourceNames
    {
        get
        {
            try
            {
                return Directory.EnumerateFiles(scriptDirectory, "*.js")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    public bool HasSources => SourceNames.Count > 0;

    public bool Contains(string name) => File.Exists(PathFor(name));

    public void AddScript(string name, string content)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            File.WriteAllText(PathFor(name), content);
            InvalidateEngines();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: persistence failures leave the registry unchanged.
        }
    }

    public void RemoveScript(string name)
    {
        try
        {
            var path = PathFor(name);
            if (File.Exists(path))
            {
                File.Delete(path);
                InvalidateEngines();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    public void ProvisionBundledSources()
    {
        var assembly = typeof(JsonLyricSourceService).Assembly;
        foreach (var name in BundledSources)
        {
            if (Contains(name))
            {
                continue;
            }

            var content = ReadBundled(assembly, name);
            if (content is not null)
            {
                AddScript(name, content);
            }
        }
    }

    public Task<IReadOnlyList<ExternalLrcInfo>> LookupAsync(string title, string artist)
    {
        var safeTitle = title ?? string.Empty;
        var safeArtist = artist ?? string.Empty;
        return Task.Run<IReadOnlyList<ExternalLrcInfo>>(() =>
        {
            var results = new List<ExternalLrcInfo>();
            foreach (var engine in EnsureEngines().Values)
            {
                try
                {
                    results.AddRange(engine.LookupLrc(safeTitle, safeArtist));
                }
                catch
                {
                    // A failing source (e.g. a Windows-only script) is skipped.
                }
            }

            results.Sort(new SimilarityComparer(safeTitle, safeArtist));
            return results;
        });
    }

    public Task<string?> DownloadAsync(ExternalLrcInfo candidate)
    {
        if (candidate?.Source is not { } sourceName)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.Run<string?>(() =>
        {
            if (!EnsureEngines().TryGetValue(sourceName, out var engine))
            {
                return null;
            }

            try
            {
                return engine.DownloadLrc(candidate);
            }
            catch
            {
                return null;
            }
        });
    }

    private void InvalidateEngines()
    {
        lock (engineLock)
        {
            enginesDirty = true;
        }
    }

    private Dictionary<string, JsDownloadSource> EnsureEngines()
    {
        lock (engineLock)
        {
            if (!enginesDirty && engines is not null)
            {
                return engines;
            }

            if (engines is not null)
            {
                foreach (var engine in engines.Values)
                {
                    try
                    {
                        engine.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal failures.
                    }
                }
            }

            var rebuilt = new Dictionary<string, JsDownloadSource>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in SourceNames)
            {
                try
                {
                    var content = File.ReadAllText(PathFor(name));
                    rebuilt[name] = new JsDownloadSource(content, name, Array.Empty<Assembly>());
                }
                catch
                {
                    // A script that fails to compile is excluded from lookups.
                }
            }

            engines = rebuilt;
            enginesDirty = false;
            return engines;
        }
    }

    private static string? ReadBundled(Assembly assembly, string name)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream($"LightStudio.LightPlayer.Resource.{name}.js");
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string PathFor(string name) =>
        Path.Combine(scriptDirectory, Sanitize(name) + ".js");

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
