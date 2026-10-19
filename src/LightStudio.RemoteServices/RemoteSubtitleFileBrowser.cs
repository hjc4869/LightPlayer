using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;

namespace LightStudio.RemoteServices;

/// <summary>
/// Browses the server's video library folders for subtitle files. The server confines every path
/// to the folders it is configured to serve, so nothing here needs to reason about the file system.
/// </summary>
internal sealed class RemoteSubtitleFileBrowser(RemoteApiClient client) : ISubtitleFileBrowser
{
    public async Task<SubtitleFileListing> ListAsync(
        string? folderPath,
        CancellationToken cancellationToken = default)
    {
        var query = string.IsNullOrEmpty(folderPath)
            ? string.Empty
            : $"?path={Uri.EscapeDataString(folderPath)}";
        var result = await client
            .GetAsync<RemoteBrowseResult>($"videos/browse{query}", cancellationToken)
            .ConfigureAwait(false);

        return new SubtitleFileListing(
            result.Path,
            result.Ancestors.Select(Convert).ToList(),
            result.Entries.Select(Convert).ToList());
    }

    public Task<byte[]> ReadAsync(string filePath, CancellationToken cancellationToken = default) =>
        client.GetBytesAsync($"videos/subtitle?path={Uri.EscapeDataString(filePath)}", cancellationToken);

    private static SubtitleFileEntry Convert(RemoteFileEntry entry) =>
        new(entry.Name, entry.Path, entry.IsFolder);
}
