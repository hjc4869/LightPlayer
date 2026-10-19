using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Lyrics;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LocalServices;

/// <summary>
/// Disk-backed lyric source registry. Each script lives as a <c>.js</c> file in
/// the application data <c>Scripts</c> folder; the file name (without extension)
/// is the source name. Bundled sources are embedded resources provisioned on
/// first run. Registered scripts are compiled into <see cref="IJsDownloadSource"/>
/// engines on demand to look up and download lyrics online.
/// </summary>
public sealed class JsonLyricSourceService : ILyricSourceService
{
    private static readonly string[] AllBundledSources = ["lrclib", "netease"];

    private readonly string scriptDirectory;
    private readonly Func<string, string, IJsDownloadSource> sourceFactory;
    private readonly string[] bundledSources;
    private readonly Lazy<IReadOnlyList<BundledLyricSource>> bundledSourceCatalog;
    private readonly object engineLock = new();
    private Dictionary<string, IJsDownloadSource>? engines;
    private bool enginesDirty = true;

    public JsonLyricSourceService(Func<string, string, IJsDownloadSource> sourceFactory)
        : this(sourceFactory, Path.Combine(AppPaths.DataDirectory, "Scripts"))
    {
    }

    /// <param name="bundledSources">
    /// Which of the embedded scripts this head provisions; a head whose network stack cannot reach
    /// a provider names only the ones it can.
    /// </param>
    public JsonLyricSourceService(
        Func<string, string, IJsDownloadSource> sourceFactory,
        string scriptDirectory,
        IReadOnlyList<string>? bundledSources = null)
    {
        this.sourceFactory = sourceFactory;
        this.scriptDirectory = scriptDirectory;
        this.bundledSources = bundledSources is null ? AllBundledSources : [.. bundledSources];
        bundledSourceCatalog = new Lazy<IReadOnlyList<BundledLyricSource>>(LoadBundledSourceCatalog);
        try
        {
            Directory.CreateDirectory(scriptDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Listing degrades to empty if the folder cannot be created.
        }
    }

    /// <summary>
    /// Raised after a script is added or removed. A host whose filesystem does not outlive the page
    /// (the browser's) persists the folder from here.
    /// </summary>
    public event EventHandler? Changed;

    public IReadOnlyList<BundledLyricSource> BundledSources => bundledSourceCatalog.Value;

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

    public Task AddScriptAsync(string name, string content)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.CompletedTask;
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

        return Task.CompletedTask;
    }

    public Task RemoveScriptAsync(string name)
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

        return Task.CompletedTask;
    }

    public async Task ProvisionBundledSourcesAsync()
    {
        foreach (var name in bundledSources)
        {
            await ProvisionBundledSourceAsync(name);
        }
    }

    public async Task ProvisionBundledSourceAsync(string name)
    {
        var bundledName = bundledSources.FirstOrDefault(
            candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
        if (bundledName is null || Contains(bundledName))
        {
            return;
        }

        var content = ReadBundled(typeof(App).Assembly, bundledName);
        if (content is not null)
        {
            await AddScriptAsync(bundledName, content);
        }
    }

    public async Task<IReadOnlyList<ExternalLrcInfo>> LookupAsync(
        string title,
        string artist,
        CancellationToken cancellationToken = default)
    {
        var safeTitle = title ?? string.Empty;
        var safeArtist = artist ?? string.Empty;
        var results = new List<ExternalLrcInfo>();
        foreach (var engine in EnsureEngines().Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.AddRange(await engine.LookupLrcAsync(safeTitle, safeArtist, cancellationToken));
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception ex)
            {
                throw SourceFailure(engine.Name, ex);
            }
        }

        results.Sort(new SimilarityComparer(safeTitle, safeArtist));
        return results;
    }

    public async Task<LyricDownload?> DownloadAsync(
        ExternalLrcInfo candidate,
        CancellationToken cancellationToken = default)
    {
        if (candidate?.Source is not { } sourceName)
        {
            return null;
        }

        if (!EnsureEngines().TryGetValue(sourceName, out var engine))
        {
            return null;
        }

        try
        {
            return await engine.DownloadLrcAsync(candidate, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            throw SourceFailure(sourceName, ex);
        }
    }

    private void InvalidateEngines()
    {
        lock (engineLock)
        {
            enginesDirty = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Dictionary<string, IJsDownloadSource> EnsureEngines()
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

                engines = null;
            }

            var rebuilt = new Dictionary<string, IJsDownloadSource>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in SourceNames)
            {
                try
                {
                    var content = File.ReadAllText(PathFor(name));
                    rebuilt[name] = sourceFactory(content, name);
                }
                catch (Exception ex)
                {
                    foreach (var engine in rebuilt.Values)
                    {
                        engine.Dispose();
                    }

                    throw SourceFailure(name, ex);
                }
            }

            engines = rebuilt;
            enginesDirty = false;
            return engines;
        }
    }

    private static InvalidOperationException SourceFailure(string sourceName, Exception exception) =>
        new($"Lyric source '{sourceName}' failed: {exception.GetBaseException().Message}", exception);

    private IReadOnlyList<BundledLyricSource> LoadBundledSourceCatalog()
    {
        var assembly = typeof(App).Assembly;
        var catalog = new List<BundledLyricSource>();
        foreach (var name in bundledSources)
        {
            var content = ReadBundled(assembly, name);
            if (content is null)
            {
                continue;
            }

            using var source = sourceFactory(content, name);
            catalog.Add(new BundledLyricSource(name, source.GetSourceInfo()));
        }

        return catalog;
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
