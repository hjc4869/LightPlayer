namespace NativeBindings.Parser;

/// <summary>Decides which header files belong to the selected native libraries rather than to the platform or toolchain.</summary>
internal sealed class Ownership
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly List<string> roots = [];
    private readonly List<string> systemBases = [];
    private readonly HashSet<string> files = new(PathComparer);
    private readonly Dictionary<string, string> canonicalPaths = new(PathComparer);
    private readonly Dictionary<string, bool> decisions = new(PathComparer);

    /// <summary>
    /// A requested header inside a library include root claims that whole root. In a directory the compiler treats as a
    /// system directory, it claims only its own top-level directory, or the file itself when it has none.
    /// </summary>
    public void AddRequest(string header, string resolved, bool system, IEnumerable<string> libraryRoots)
    {
        var path = Canonical(resolved);
        var root = libraryRoots.Select(Canonical).Where(root => path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            .OrderByDescending(root => root.Length).FirstOrDefault();
        if (root is not null && !system)
        {
            AddRoot(root);
            foreach (var directory in Directory.EnumerateDirectories(root).Where(directory => new DirectoryInfo(directory).LinkTarget is not null))
                AddRoot(Canonical(directory));
        }
        else
        {
            var relative = header.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(resolved);
            if (!full.EndsWith(Path.DirectorySeparatorChar + relative, PathComparison))
                throw new InvalidOperationException($"Header {header} resolved to an unexpected path: {resolved}");
            var separator = header.IndexOf('/');
            var systemBase = Canonical(full[..^relative.Length]);
            if (!systemBases.Contains(systemBase, PathComparer))
                systemBases.Add(systemBase);
            if (separator < 0)
                files.Add(path);
            else
                AddRoot(Canonical(Path.Combine(full[..^relative.Length], header[..separator])));
        }
        decisions.Clear();
    }

    /// <summary>
    /// In system directories, a library's own headers include each other with quoted includes, so headers that owned
    /// headers include that way claim their top-level directory (or themselves) in the same system directory.
    /// </summary>
    public void AddQuotedIncludes(IReadOnlyCollection<(string Includer, string Included)> includes)
    {
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (includer, included) in includes)
            {
                if (!Owns(includer) || Owns(included))
                    continue;
                var path = Canonical(included);
                var systemBase = systemBases.Where(directory => path.StartsWith(directory + Path.DirectorySeparatorChar, PathComparison))
                    .OrderByDescending(directory => directory.Length).FirstOrDefault();
                if (systemBase is null)
                    continue;
                var relative = path[(systemBase.Length + 1)..];
                var separator = relative.IndexOf(Path.DirectorySeparatorChar);
                if (separator < 0)
                    files.Add(path);
                else
                    AddRoot(Path.Combine(systemBase, relative[..separator]));
                decisions.Clear();
                changed = true;
            }
        }
    }

    public bool Owns(string file)
    {
        if (file.Length == 0)
            return false;
        if (decisions.TryGetValue(file, out var owned))
            return owned;
        var path = Canonical(file);
        owned = files.Contains(path) || roots.Any(root => path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison));
        decisions[file] = owned;
        return owned;
    }

    private void AddRoot(string root)
    {
        if (!roots.Contains(root, PathComparer))
            roots.Add(root);
    }

    private string Canonical(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (canonicalPaths.TryGetValue(fullPath, out var cached))
            return cached;
        var parent = Path.GetDirectoryName(fullPath);
        string canonical;
        if (parent is null)
            canonical = fullPath;
        else
        {
            FileSystemInfo info = Directory.Exists(fullPath) ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
            var target = info.Exists ? info.ResolveLinkTarget(true) : null;
            canonical = target is not null ? Canonical(target.FullName) : Path.Combine(Canonical(parent), Path.GetFileName(fullPath));
        }
        canonicalPaths[fullPath] = canonical;
        return canonical;
    }
}
