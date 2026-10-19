using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Services;

public enum VideoFolderEntryKind
{
    Folder,
    Video,
    Photo,
}

/// <param name="Name">Label for the tile: the folder's own name, or the file's name.</param>
/// <param name="Path">Platform identifier the entry is opened or browsed by.</param>
/// <param name="ExternalAudioPaths">Sidecar audio tracks lying beside a video.</param>
/// <param name="ExternalSubtitlePaths">Sidecar subtitle files lying beside a video.</param>
/// <param name="SizeInBytes">File size, where the listing knows it; zero otherwise.</param>
/// <param name="MediaKind">How a video entry is opened; folders and photos use <see cref="VideoMediaKind.File"/>.</param>
/// <param name="DiscTitle">One-based DVD title selected during indexing; zero otherwise.</param>
/// <param name="PreviewPaths">Server-selected descendant media paths for a folder collage.</param>
/// <param name="ItemCount">Number of directly listed items in a folder, when available.</param>
public sealed record VideoFolderEntry(
    VideoFolderEntryKind Kind,
    string Name,
    string Path,
    IReadOnlyList<string>? ExternalAudioPaths = null,
    IReadOnlyList<string>? ExternalSubtitlePaths = null,
    long SizeInBytes = 0,
    VideoMediaKind MediaKind = VideoMediaKind.File,
    int DiscTitle = 0,
    IReadOnlyList<string>? PreviewPaths = null,
    int? ItemCount = null,
    DateTimeOffset LastModifiedUtc = default,
    DateTimeOffset? CreatedUtc = null);

/// <param name="Path">The folder listed, or null for the library roots themselves.</param>
/// <param name="Ancestors">
/// The chain from the containing library root down to <paramref name="Path"/> inclusive, which is
/// what the path bar draws and what going up one level walks back along.
/// </param>
/// <param name="PreviewPaths">Server-selected paths for a collage of the listed folder or library roots.</param>
public sealed record VideoFolderListing(
    string? Path,
    IReadOnlyList<VideoFolderEntry> Ancestors,
    IReadOnlyList<VideoFolderEntry> Entries,
    IReadOnlyList<string>? PreviewPaths = null)
{
    public static VideoFolderListing Empty { get; } = new(null, [], []);
}

/// <summary>
/// Lists a video library folder as it is at this moment, rather than as the index last recorded it.
/// Browsing goes through here so that a file copied in since the last scan is still there to open.
/// </summary>
public interface IVideoFolderBrowser
{
    /// <summary>Lists a folder, or the library roots when <paramref name="folderPath"/> is null.</summary>
    Task<VideoFolderListing> ListAsync(string? folderPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Browses through the head's own file system access, whatever that is: a filesystem path on the
/// desktop, a content URI on Android, a picked directory handle in the browser.
/// </summary>
public sealed class PlatformVideoFolderBrowser(
    IMultiMediaPlatformIO platform,
    ILibraryLocationService location,
    ILibraryFolderAccess? folderAccess = null) : IVideoFolderBrowser
{
    public async Task<VideoFolderListing> ListAsync(
        string? folderPath,
        CancellationToken cancellationToken = default)
    {
        var roots = await RootsAsync(cancellationToken).ConfigureAwait(false);
        var discPlatform = platform as IVideoDiscPlatformIO;
        bool supportsDiscImages = discPlatform is not null &&
            (discPlatform.SupportsVideoDisc(VideoMediaKind.Dvd) ||
                discPlatform.SupportsVideoDisc(VideoMediaKind.BluRay));
        if (string.IsNullOrEmpty(folderPath))
        {
            var found = new List<VideoFolderEntry>(roots.Count);
            foreach (var root in roots)
            {
                try
                {
                    if (await platform.FolderExistsAsync(root, cancellationToken).ConfigureAwait(false))
                    {
                        found.Add(new VideoFolderEntry(
                            VideoFolderEntryKind.Folder,
                            RootDisplayName(root),
                            root));
                    }
                }
                catch (Exception exception) when (FileAccessErrors.IsAccessDenied(exception))
                {
                    AppLog.Warning("Library.Browse", $"Skipping inaccessible root folder {root}.", exception);
                }
            }

            return new VideoFolderListing(null, [], Sorted(found));
        }

        folderPath = await platform.NormalizePathAsync(folderPath, cancellationToken).ConfigureAwait(false);
        var entries = new List<VideoFolderEntry>();
        var folders = await platform.ListFoldersFullNameInPathAsync(folderPath).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        VideoDiscStructureInfo? currentDisc;
        try
        {
            currentDisc = await VideoDiscStructure
                .DetectAsync(platform, folderPath, folders, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (FileAccessErrors.IsAccessDenied(exception))
        {
            AppLog.Warning("Library.Browse", $"Skipping inaccessible disc folder {folderPath}.", exception);
            return new VideoFolderListing(folderPath, VideoFolderPaths.BuildAncestors(folderPath, roots, RootDisplayName), []);
        }
        if (currentDisc is not null)
        {
            if (discPlatform?.SupportsVideoDisc(currentDisc.MediaKind) == true)
            {
                entries.Add(new VideoFolderEntry(
                    VideoFolderEntryKind.Video,
                    VideoFolderTree.LeafName(folderPath),
                    folderPath,
                    MediaKind: currentDisc.MediaKind,
                    LastModifiedUtc: currentDisc.MarkerLastModified));
            }

            return new VideoFolderListing(
                folderPath,
                VideoFolderPaths.BuildAncestors(folderPath, roots, RootDisplayName),
                entries);
        }

        foreach (string child in folders)
        {
            VideoDiscStructureInfo? disc;
            try
            {
                disc = await VideoDiscStructure
                    .DetectAsync(platform, child, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (FileAccessErrors.IsAccessDenied(exception))
            {
                AppLog.Warning("Library.Browse", $"Skipping inaccessible disc folder {child}.", exception);
                continue;
            }
            if (disc is not null && discPlatform?.SupportsVideoDisc(disc.MediaKind) != true)
            {
                continue;
            }

            entries.Add(new VideoFolderEntry(
                disc is null ? VideoFolderEntryKind.Folder : VideoFolderEntryKind.Video,
                VideoFolderTree.LeafName(child),
                child,
                MediaKind: disc?.MediaKind ?? VideoMediaKind.File,
                LastModifiedUtc: disc?.MarkerLastModified ?? default));
        }

        var files = await platform.ListFilesFullNameInPathAsync(folderPath).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var siblings = files.Select(file => (file.Name, file.Path)).ToArray();
        entries.AddRange(files
            .Where(file => MediaContainerFormats.IsVideo(file.Name) &&
                (!MediaContainerFormats.DiscImage.Contains(Path.GetExtension(file.Name)) ||
                    supportsDiscImages))
            .Select(file => new VideoFolderEntry(
                VideoFolderEntryKind.Video,
                file.Name,
                file.Path,
                MediaContainerFormats.FindSidecars(file.Name, siblings, MediaContainerFormats.ExternalAudio),
                MediaContainerFormats.FindSidecars(file.Name, siblings, MediaContainerFormats.ExternalSubtitle),
                SizeInBytes: file.SizeInBytes,
                LastModifiedUtc: file.LastModifiedUtc)));
        entries.AddRange(files
            .Where(file => MediaContainerFormats.IsImage(file.Name))
            .Select(file => new VideoFolderEntry(
                VideoFolderEntryKind.Photo,
                file.Name,
                file.Path,
                SizeInBytes: file.SizeInBytes,
                LastModifiedUtc: file.LastModifiedUtc,
                CreatedUtc: file.CreatedUtc)));

        return new VideoFolderListing(
            folderPath,
            VideoFolderPaths.BuildAncestors(folderPath, roots, RootDisplayName),
            Sorted(entries));
    }

    private string RootDisplayName(string root)
    {
        var displayName = folderAccess?.GetDisplayName(root);
        return string.IsNullOrWhiteSpace(displayName) ? VideoFolderTree.LeafName(root) : displayName;
    }

    private async Task<IReadOnlyList<string>> RootsAsync(CancellationToken cancellationToken)
    {
        var roots = new List<string>();
        foreach (var path in location.GetVideoFolderPaths().Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            roots.Add(await platform.NormalizePathAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<VideoFolderEntry> Sorted(List<VideoFolderEntry> entries)
    {
        entries.Sort(static (left, right) => left.Kind != right.Kind
            ? left.Kind.CompareTo(right.Kind)
            : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
        return entries;
    }
}

/// <summary>
/// Answers listings from the index instead of the file system, for a library that cannot be
/// enumerated: a server too old to offer browsing. New files stay invisible until it is scanned.
/// </summary>
public sealed class IndexedVideoFolderBrowser(IVideoLibraryQueryService videoLibraryQuery) : IVideoFolderBrowser
{
    private static readonly TimeSpan TreeLifetime = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim gate = new(1, 1);
    private VideoFolderNode? tree;
    private DateTime builtAt;

    public async Task<VideoFolderListing> ListAsync(
        string? folderPath,
        CancellationToken cancellationToken = default)
    {
        var root = await GetTreeAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(folderPath))
        {
            return new VideoFolderListing(null, [], Entries(root.Children));
        }

        if (VideoFolderTree.FindPath(root, folderPath) is not { Count: > 1 } chain)
        {
            return VideoFolderListing.Empty;
        }

        var node = chain[^1];
        var entries = new List<VideoFolderEntry>(Entries(node.Children));
        if (node.DirectVideoCount > 0)
        {
            var videos = await videoLibraryQuery
                .GetVideosInFolderAsync(folderPath, cancellationToken)
                .ConfigureAwait(false);
            entries.AddRange(videos.Select(video => new VideoFolderEntry(
                VideoFolderEntryKind.Video,
                video.FileName,
                video.Path,
                SizeInBytes: video.FileSize,
                MediaKind: video.MediaKind,
                DiscTitle: video.DiscTitle,
                LastModifiedUtc: video.FileLastModifiedDate)));
        }

        return new VideoFolderListing(
            folderPath,
            chain.Skip(1)
                .Select(step => new VideoFolderEntry(VideoFolderEntryKind.Folder, step.Name, step.Path!))
                .ToArray(),
            entries);
    }

    private async Task<VideoFolderNode> GetTreeAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (tree is null || DateTime.UtcNow - builtAt > TreeLifetime)
            {
                var counts = await videoLibraryQuery.GetVideoFolderCountsAsync().ConfigureAwait(false);
                tree = VideoFolderTree.Build(counts);
                builtAt = DateTime.UtcNow;
            }

            return tree;
        }
        finally
        {
            gate.Release();
        }
    }

    private static IReadOnlyList<VideoFolderEntry> Entries(IEnumerable<VideoFolderNode> nodes) => nodes
        .Select(node => new VideoFolderEntry(VideoFolderEntryKind.Folder, node.Name, node.Path!))
        .ToArray();
}

public static class VideoFolderPaths
{
    public static async Task<VideoFolderReference?> GetLocationAsync(
        MediaReference media,
        ILibrarySourceRegistry sources,
        string? knownFolderPath = null,
        bool useKnownFolder = false)
    {
        if (!sources.TryGetSource(media.SourceId, out var source))
        {
            return null;
        }

        var services = await source.EnsureConnectedAsync().ConfigureAwait(false);
        var folderPath = knownFolderPath;
        if (!useKnownFolder)
        {
            var path = await services.MultimediaPlatform.NormalizePathAsync(media.Path).ConfigureAwait(false);
            if (!string.Equals(path, media.Path, StringComparison.Ordinal) || string.IsNullOrEmpty(folderPath))
            {
                folderPath = media.SourceId == LibrarySourceId.System
                    ? (await services.MultimediaPlatform.GetMediaFilePropertiesAsync(path).ConfigureAwait(false))?.DirectoryPath
                    : ParentOf(path);
            }
        }

        if (!string.IsNullOrEmpty(folderPath))
        {
            folderPath = await services.MultimediaPlatform.NormalizePathAsync(folderPath).ConfigureAwait(false);
        }

        return new VideoFolderReference(media.SourceId, folderPath, source.DisplayName);
    }

    /// <summary>
    /// The chain from whichever of <paramref name="roots"/> holds <paramref name="folder"/> down to
    /// the folder itself. Walked upwards from the folder so every step is a prefix of the identifier
    /// the platform gave, rather than one this code stitched back together.
    /// </summary>
    public static IReadOnlyList<VideoFolderEntry> BuildAncestors(
        string folder,
        IEnumerable<string> roots,
        Func<string, string?>? getRootDisplayName = null)
    {
        var root = roots.FirstOrDefault(candidate =>
            !string.IsNullOrEmpty(candidate) &&
            (VideoFolderTree.IsUnder(folder, candidate) ||
                string.Equals(
                    folder.TrimEnd('/', '\\'),
                    candidate.TrimEnd('/', '\\'),
                    StringComparison.OrdinalIgnoreCase)));
        var chain = new List<VideoFolderEntry>();
        for (var step = folder; step is not null; step = ParentOf(step))
        {
            var isRoot = root is not null &&
                string.Equals(
                    step.TrimEnd('/', '\\'),
                    root.TrimEnd('/', '\\'),
                    StringComparison.OrdinalIgnoreCase);
            var displayName = isRoot ? getRootDisplayName?.Invoke(root!) : null;
            chain.Add(new VideoFolderEntry(
                VideoFolderEntryKind.Folder,
                string.IsNullOrWhiteSpace(displayName) ? VideoFolderTree.LeafName(step) : displayName,
                step));
            if (root is null || isRoot)
            {
                break;
            }
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>The folder holding <paramref name="path"/>, or null once there is none left.</summary>
    public static string? ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(['/', '\\']);
        return index > 0 ? trimmed[..index] : null;
    }
}
