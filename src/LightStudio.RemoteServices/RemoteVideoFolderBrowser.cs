using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.RemoteServices;

/// <summary>
/// Lists the server's video library folders as they are now, so a file copied onto the server since
/// its last scan can still be browsed to. The server confines every path to the folders it is
/// configured to serve, so nothing here needs to reason about its file system.
/// </summary>
internal sealed class RemoteVideoFolderBrowser(RemoteApiClient client) : IVideoFolderBrowser
{
    public async Task<VideoFolderListing> ListAsync(
        string? folderPath,
        CancellationToken cancellationToken = default)
    {
        var query = string.IsNullOrEmpty(folderPath)
            ? $"?kind={RemoteApiDefaults.MediaBrowseKind}"
            : $"?path={Uri.EscapeDataString(folderPath)}&kind={RemoteApiDefaults.MediaBrowseKind}";
        var result = await client
            .GetAsync<RemoteBrowseResult>($"videos/browse{query}", cancellationToken)
            .ConfigureAwait(false);

        return new VideoFolderListing(
            result.Path,
            result.Ancestors.Select(Convert).ToList(),
            result.Entries.Select(Convert).ToList(),
            result.PreviewPaths);
    }

    private static VideoFolderEntry Convert(RemoteFileEntry entry)
    {
        if (entry.IsFolder)
        {
            return new VideoFolderEntry(
                VideoFolderEntryKind.Folder,
                VideoFolderTree.LeafName(entry.Path),
                entry.Path,
                PreviewPaths: entry.PreviewPaths,
                ItemCount: entry.ItemCount);
        }

        // A picture is told from a video by its name, the same test the server listed it by, so the
        // two need no separate field on the wire.
        return entry.MediaKind == VideoMediaKind.File && MediaContainerFormats.IsImage(entry.Path)
            ? new VideoFolderEntry(
                VideoFolderEntryKind.Photo,
                entry.Name,
                entry.Path,
                SizeInBytes: entry.SizeInBytes,
                LastModifiedUtc: entry.LastModifiedUtc,
                CreatedUtc: entry.CreatedUtc)
            : new VideoFolderEntry(
                VideoFolderEntryKind.Video,
                entry.Name,
                entry.Path,
                entry.ExternalAudioPaths,
                entry.ExternalSubtitlePaths,
                entry.SizeInBytes,
                entry.MediaKind,
                entry.DiscTitle,
                LastModifiedUtc: entry.LastModifiedUtc,
                CreatedUtc: entry.CreatedUtc);
    }
}
