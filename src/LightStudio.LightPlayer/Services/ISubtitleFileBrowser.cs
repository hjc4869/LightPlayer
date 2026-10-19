using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

/// <param name="IsFolder">Whether the entry is browsed into rather than chosen.</param>
public sealed record SubtitleFileEntry(string Name, string Path, bool IsFolder);

/// <param name="Path">The folder listed, or null for the video library roots themselves.</param>
/// <param name="Ancestors">The chain from the containing library root down to the folder listed.</param>
public sealed record SubtitleFileListing(
    string? Path,
    IReadOnlyList<SubtitleFileEntry> Ancestors,
    IReadOnlyList<SubtitleFileEntry> Entries);

/// <summary>
/// Browses the subtitle files sitting beside media this device cannot reach through a file system
/// of its own. Registered only for a remote session; a local one uses the platform's file picker.
/// </summary>
public interface ISubtitleFileBrowser
{
    /// <summary>Lists a folder, or the video library roots when <paramref name="folderPath"/> is null.</summary>
    Task<SubtitleFileListing> ListAsync(string? folderPath, CancellationToken cancellationToken = default);

    /// <summary>Fetches a subtitle file whole for eager decoding.</summary>
    Task<byte[]> ReadAsync(string filePath, CancellationToken cancellationToken = default);
}
