#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.IO;

public sealed record VideoDiscStructureInfo(
    VideoMediaKind MediaKind,
    DateTimeOffset MarkerLastModified);

public readonly record struct VideoDiscMeasurement(long SizeInBytes, DateTimeOffset LastModified);

/// <summary>Recognizes and measures standard DVD-Video and Blu-ray directory structures.</summary>
public static class VideoDiscStructure
{
    public static async Task<VideoDiscStructureInfo?> DetectAsync(
        IMultiMediaPlatformIO platform,
        string rootPath,
        IReadOnlyList<string>? childFolders = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentException.ThrowIfNullOrEmpty(rootPath);

        childFolders ??= await platform.ListFoldersFullNameInPathAsync(rootPath).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        string? bdmv = FindFolder(childFolders, "BDMV");
        if (bdmv is not null)
        {
            IFile? bluRayMarker = await FindMarkerAsync(
                platform,
                bdmv,
                "index.bdmv",
                ["INDX0100", "INDX0200", "INDX0300"],
                cancellationToken).ConfigureAwait(false)
                ?? await FindMarkerAsync(
                    platform,
                    bdmv,
                    "MovieObject.bdmv",
                    ["MOBJ0100", "MOBJ0200", "MOBJ0300"],
                    cancellationToken).ConfigureAwait(false);
            if (bluRayMarker is not null)
            {
                return new VideoDiscStructureInfo(
                    VideoMediaKind.BluRay,
                    bluRayMarker.LastModifiedUtc);
            }
        }

        string? videoTs = FindFolder(childFolders, "VIDEO_TS");
        IFile? dvdMarker = videoTs is null
            ? null
            : await FindMarkerAsync(
                platform,
                videoTs,
                "VIDEO_TS.IFO",
                ["DVDVIDEO-VMG"],
                cancellationToken).ConfigureAwait(false);
        return dvdMarker is null
            ? null
            : new VideoDiscStructureInfo(
                VideoMediaKind.Dvd,
                dvdMarker.LastModifiedUtc);
    }

    public static async Task<VideoDiscMeasurement> MeasureAsync(
        IMultiMediaPlatformIO platform,
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentException.ThrowIfNullOrEmpty(rootPath);

        long size = 0;
        DateTimeOffset lastModified = DateTimeOffset.MinValue;
        var pending = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string folder = pending.Pop();
            if (!seen.Add(folder))
            {
                continue;
            }

            foreach (IFile file in await platform.ListFilesFullNameInPathAsync(folder).ConfigureAwait(false))
            {
                size = file.SizeInBytes > long.MaxValue - size ? long.MaxValue : size + file.SizeInBytes;
                if (file.LastModifiedUtc > lastModified)
                {
                    lastModified = file.LastModifiedUtc;
                }
            }

            foreach (string child in await platform.ListFoldersFullNameInPathAsync(folder).ConfigureAwait(false))
            {
                pending.Push(child);
            }
        }

        return new VideoDiscMeasurement(size, lastModified);
    }

    private static string? FindFolder(IEnumerable<string> folders, string name) =>
        folders.FirstOrDefault(folder => string.Equals(LeafName(folder), name, StringComparison.OrdinalIgnoreCase));

    private static async Task<IFile?> FindMarkerAsync(
        IMultiMediaPlatformIO platform,
        string folder,
        string markerName,
        IReadOnlyList<string> signatures,
        CancellationToken cancellationToken)
    {
        IFile? marker = (await platform.ListFilesFullNameInPathAsync(folder).ConfigureAwait(false))
            .FirstOrDefault(file => string.Equals(file.Name, markerName, StringComparison.OrdinalIgnoreCase));
        if (marker is null)
        {
            return null;
        }

        await using Stream stream = await platform
            .OpenMediaFileAsync(marker.Path, cancellationToken)
            .ConfigureAwait(false);
        var buffer = new byte[12];
        int read = 0;
        while (read < buffer.Length)
        {
            int count = await stream
                .ReadAsync(buffer.AsMemory(read), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        string header = Encoding.ASCII.GetString(buffer, 0, read);
        return signatures.Any(signature => header.StartsWith(signature, StringComparison.Ordinal))
            ? marker
            : null;
    }

    private static string LeafName(string path)
    {
        string encoded = Path.GetFileName(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string decoded = Uri.UnescapeDataString(encoded)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int separator = decoded.LastIndexOfAny(['/', '\\', ':']);
        return separator >= 0 ? decoded[(separator + 1)..] : decoded;
    }
}