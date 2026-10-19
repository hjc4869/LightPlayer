using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Services;

public sealed record MusicFolderEntry(string Name, string Path);

public sealed record MusicFolderListing(
    IReadOnlyList<MusicFolderEntry> Ancestors,
    IReadOnlyList<MusicFolderEntry> Folders,
    string? TrackFolderPath,
    bool HasTracks)
{
    public static MusicFolderListing Flat { get; } = new([], [], null, true);
}

public interface IMusicFolderBrowser
{
    Task<MusicFolderListing> ListAsync(string? folderPath, CancellationToken cancellationToken = default);
}

public sealed class IndexedMusicFolderBrowser(
    ILibraryQueryService libraryQuery,
    Func<CancellationToken, Task<IReadOnlyList<string>>>? getRoots = null,
    IMultiMediaPlatformIO? platform = null,
    ILibraryFolderAccess? folderAccess = null) : IMusicFolderBrowser
{
    public async Task<MusicFolderListing> ListAsync(string? folderPath, CancellationToken cancellationToken = default)
    {
        var paths = await libraryQuery.GetMediaFolderPathsAsync(cancellationToken).ConfigureAwait(false);
        var configuredRoots = getRoots is null
            ? []
            : await getRoots(cancellationToken).ConfigureAwait(false);
        var roots = new Dictionary<string, MusicFolderEntry>(StringComparer.Ordinal);
        foreach (var configured in configuredRoots)
        {
            var normalized = platform is null
                ? configured
                : await platform.NormalizePathAsync(configured, cancellationToken).ConfigureAwait(false);
            var path = Path.GetDirectoryName(normalized + "/_") ?? normalized;
            var name = folderAccess?.GetDisplayName(normalized);
            roots[path] = new MusicFolderEntry(
                string.IsNullOrWhiteSpace(name) ? VideoFolderTree.LeafName(normalized) : name, path);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (folderPath is null && roots.Count > 0)
        {
            return new MusicFolderListing([], roots.Values.ToArray(), null, false);
        }

        if (folderPath is null && paths.Count == 0)
        {
            return MusicFolderListing.Flat;
        }

        var current = folderPath ?? string.Empty;
        var children = new Dictionary<string, MusicFolderEntry>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (path.Length <= current.Length ||
                (current.Length > 0 && (!path.StartsWith(current, StringComparison.Ordinal) ||
                    (path[current.Length] is not ('/' or '\\') && current[^1] is not ('/' or '\\')))))
            {
                continue;
            }

            var start = current.Length;
            while (start < path.Length && path[start] is '/' or '\\')
            {
                start++;
            }
            var separator = path.IndexOfAny(['/', '\\'], start);
            var childPath = separator < 0 ? path : path[..separator];
            children[childPath] = new MusicFolderEntry(VideoFolderTree.LeafName(childPath), childPath);
        }

        var ancestors = new List<MusicFolderEntry>();
        for (var step = folderPath; !string.IsNullOrEmpty(step); step = ParentOf(step))
        {
            if (roots.TryGetValue(step, out var root))
            {
                ancestors.Add(root);
                break;
            }
            ancestors.Add(new MusicFolderEntry(VideoFolderTree.LeafName(step), step));
        }
        ancestors.Reverse();
        return new MusicFolderListing(ancestors, children.Values.ToArray(), current,
            paths.Contains(current, StringComparer.Ordinal));
    }

    private static string? ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var separator = trimmed.LastIndexOfAny(['/', '\\']);
        if (separator < 0)
        {
            return null;
        }

        return separator == 0 || (separator == 2 && trimmed[1] == ':')
            ? trimmed[..(separator + 1)]
            : trimmed[..separator];
    }
}

public static class MusicFolderTrackResolver
{
    public const int MaximumTrackCount = 1000;
    public const int MaximumDepth = 5;
    private const int MaximumFolderCount = 1000;

    public static async Task<IReadOnlyList<Sourced<DbMediaFile>>> ResolveAsync(
        ILibrarySource source,
        MusicFolderReference folder,
        LibrarySortField sort,
        bool ascending,
        CancellationToken cancellationToken = default)
    {
        var services = await source.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var mediaSort = sort switch
        {
            LibrarySortField.AlbumName => MediaFileSortField.AlbumName,
            LibrarySortField.ArtistName => MediaFileSortField.ArtistName,
            LibrarySortField.Genre => MediaFileSortField.Genre,
            LibrarySortField.Year => MediaFileSortField.Year,
            LibrarySortField.DateAdded => MediaFileSortField.DateAdded,
            LibrarySortField.Duration => MediaFileSortField.Duration,
            _ => MediaFileSortField.Title,
        };
        var tracks = new List<Sourced<DbMediaFile>>(MaximumTrackCount);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visitedFolderCount = 0;
        await VisitAsync(folder.Path, depth: 0).ConfigureAwait(false);
        return tracks;

        async Task VisitAsync(string? path, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tracks.Count >= MaximumTrackCount || visitedFolderCount >= MaximumFolderCount ||
                !visited.Add(path is null ? "\0root" : path))
            {
                return;
            }

            visitedFolderCount++;
            var listing = services.MusicFolders is { } browser
                ? await browser.ListAsync(path, cancellationToken).ConfigureAwait(false)
                : new MusicFolderListing([], [], path, true);

            if (depth < MaximumDepth)
            {
                foreach (var child in listing.Folders.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (tracks.Count >= MaximumTrackCount || visitedFolderCount >= MaximumFolderCount)
                    {
                        break;
                    }

                    await VisitAsync(child.Path, depth + 1).ConfigureAwait(false);
                }
            }

            if (!listing.HasTracks || tracks.Count >= MaximumTrackCount)
            {
                return;
            }

            string? cursor = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageSize = Math.Min(MediaFilePageRequest.MaxPageSize, MaximumTrackCount - tracks.Count);
                var page = await services.LibraryQuery.GetMediaFilesPageAsync(
                    new MediaFilePageRequest(mediaSort, ascending, pageSize, cursor, listing.TrackFolderPath),
                    cancellationToken).ConfigureAwait(false);
                if (page.Items.Length == 0)
                {
                    break;
                }

                tracks.AddRange(page.Items.Select(file => new Sourced<DbMediaFile>(source.Id, file)));
                cursor = page.NextCursor;
            }
            while (cursor is not null && tracks.Count < MaximumTrackCount);
        }
    }
}