using System;
using System.Collections.Generic;
using System.Linq;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Models;

/// <summary>One browsable node of the video library folder tree.</summary>
public sealed class VideoFolderNode
{
    /// <summary>Full identifier of the folder, or null for the synthetic library root.</summary>
    public string? Path { get; init; }

    /// <summary>Label shown in the grid and in the breadcrumb.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Immediate subfolders that contain videos somewhere beneath them.</summary>
    public List<VideoFolderNode> Children { get; } = [];

    /// <summary>Videos directly inside this folder.</summary>
    public int DirectVideoCount { get; set; }

    /// <summary>Videos in this folder and everything below it.</summary>
    public int TotalVideoCount { get; set; }
}

/// <summary>
/// Builds a browsable folder tree from the per-folder counts the database reports.
/// </summary>
/// <remarks>
/// The tree is derived in managed code rather than in SQL because a "folder path" is whatever the
/// platform uses to identify one -- a filesystem path on the desktop, a content URI on Android -- and
/// only the separator is common to all of them.
/// </remarks>
public static class VideoFolderTree
{
    /// <summary>
    /// Builds the tree. The returned root is synthetic: its children are the configured video
    /// library folders (or, for any folder outside them, the recorded library root).
    /// </summary>
    public static VideoFolderNode Build(IEnumerable<VideoFolderCount> folders)
    {
        var root = new VideoFolderNode { Name = string.Empty };
        var byPath = new Dictionary<string, VideoFolderNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder.FolderPath))
            {
                continue;
            }

            var libraryRoot = string.IsNullOrEmpty(folder.LibraryRoot) ? folder.FolderPath : folder.LibraryRoot;
            var node = EnsureNode(root, byPath, folder.FolderPath, libraryRoot);
            node.DirectVideoCount += folder.VideoCount;
        }

        Accumulate(root);
        Sort(root);
        return root;
    }

    /// <summary>The chain from the synthetic root down to <paramref name="path"/>, or null.</summary>
    public static List<VideoFolderNode>? FindPath(VideoFolderNode root, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return [root];
        }

        var chain = new List<VideoFolderNode> { root };
        return Descend(root, path, chain) ? chain : null;
    }

    private static bool Descend(VideoFolderNode node, string path, List<VideoFolderNode> chain)
    {
        foreach (var child in node.Children)
        {
            if (string.Equals(child.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                chain.Add(child);
                return true;
            }

            if (child.Path is { } childPath && IsUnder(path, childPath))
            {
                chain.Add(child);
                return Descend(child, path, chain);
            }
        }

        return false;
    }

    private static VideoFolderNode EnsureNode(
        VideoFolderNode root,
        Dictionary<string, VideoFolderNode> byPath,
        string path,
        string libraryRoot)
    {
        if (byPath.TryGetValue(path, out var existing))
        {
            return existing;
        }

        VideoFolderNode parent;
        string name;
        if (string.Equals(path, libraryRoot, StringComparison.OrdinalIgnoreCase) || !IsUnder(path, libraryRoot))
        {
            parent = root;
            name = LeafName(path);
        }
        else
        {
            var parentPath = ParentOf(path, libraryRoot);
            parent = parentPath is null
                ? root
                : EnsureNode(root, byPath, parentPath, libraryRoot);
            name = LeafName(path);
        }

        var node = new VideoFolderNode { Path = path, Name = name };
        parent.Children.Add(node);
        byPath[path] = node;
        return node;
    }

    private static void Accumulate(VideoFolderNode node)
    {
        node.TotalVideoCount = node.DirectVideoCount;
        foreach (var child in node.Children)
        {
            Accumulate(child);
            node.TotalVideoCount += child.TotalVideoCount;
        }
    }

    private static void Sort(VideoFolderNode node)
    {
        node.Children.Sort(static (left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
        foreach (var child in node.Children)
        {
            Sort(child);
        }
    }

    /// <summary>True when <paramref name="path"/> sits somewhere below <paramref name="ancestor"/>.</summary>
    public static bool IsUnder(string path, string ancestor)
    {
        if (path.Length <= ancestor.Length ||
            !path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var next = path[ancestor.Length];
        return next is '/' or '\\' || ancestor.EndsWith('/') || ancestor.EndsWith('\\');
    }

    private static string? ParentOf(string path, string floor)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(['/', '\\']);
        if (index <= 0)
        {
            return null;
        }

        var parent = trimmed[..index];
        return parent.Length < floor.TrimEnd('/', '\\').Length ? floor : parent;
    }

    /// <summary>The folder's own name, without the path leading up to it.</summary>
    public static string LeafName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(['/', '\\']);
        var name = index >= 0 && index < trimmed.Length - 1 ? trimmed[(index + 1)..] : trimmed;
        return Uri.UnescapeDataString(name);
    }
}
