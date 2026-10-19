using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Manages the registry of third-party lyric source scripts and runs them to
/// look up and download lyrics. Scripts are stored on disk so the user's
/// selection survives restarts; the JavaScript engines are built on demand and
/// cached for the lifetime of the service.
/// </summary>
public interface ILyricSourceService
{
    /// <summary>The lyric sources bundled with this application head.</summary>
    IReadOnlyList<BundledLyricSource> BundledSources { get; }

    /// <summary>The names of every registered lyric source, ordered alphabetically.</summary>
    IReadOnlyList<string> SourceNames { get; }

    /// <summary>True when at least one lyric source is registered.</summary>
    bool HasSources { get; }

    /// <summary>True when a source with the given name is registered.</summary>
    bool Contains(string name);

    /// <summary>Adds or replaces a script, persisting it under the application data folder.</summary>
    Task AddScriptAsync(string name, string content);

    /// <summary>Removes a registered script and deletes it from disk.</summary>
    Task RemoveScriptAsync(string name);

    /// <summary>Provisions the bundled third-party sources, skipping any already present.</summary>
    Task ProvisionBundledSourcesAsync();

    /// <summary>Provisions one bundled source, skipping it when already present or unavailable.</summary>
    Task ProvisionBundledSourceAsync(string name);

    /// <summary>
    /// Queries every registered source for lyric candidates, ordered by how
    /// closely each result matches the requested title and artist.
    /// </summary>
    Task<IReadOnlyList<ExternalLrcInfo>> LookupAsync(
        string title,
        string artist,
        CancellationToken cancellationToken = default);

    /// <summary>Downloads typed lyric content for a candidate, or null when unavailable.</summary>
    Task<LyricDownload?> DownloadAsync(
        ExternalLrcInfo candidate,
        CancellationToken cancellationToken = default);
}

    /// <summary>A bundled lyric source and the information supplied by its script.</summary>
public sealed class BundledLyricSource
{
    public BundledLyricSource(string name, IReadOnlyList<LyricSourceInfoItem> information)
    {
        Name = name;
        Information = information;
    }

    public string Name { get; }

    public IReadOnlyList<LyricSourceInfoItem> Information { get; }
}
