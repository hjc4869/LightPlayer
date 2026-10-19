using System;
using System.IO;

namespace LightStudio.MediaLibraryCore.Tools
{
    public static class PathUtils
    {
        public static bool IsSubPathOf(this string path, string baseDirPath)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(baseDirPath))
            {
                return false;
            }

            // Content-provider identifiers are opaque to System.IO. Tree-scoped Android document
            // URIs retain the tree URI as their prefix, so compare only at a URI path boundary.
            if (path.Contains("://", StringComparison.Ordinal) ||
                baseDirPath.Contains("://", StringComparison.Ordinal))
            {
                try
                {
                    var candidate = Uri.UnescapeDataString(path).TrimEnd('/', '\\');
                    var parent = Uri.UnescapeDataString(baseDirPath).TrimEnd('/', '\\');
                    return string.Equals(candidate, parent, StringComparison.Ordinal) ||
                        candidate.StartsWith(parent + "/", StringComparison.Ordinal);
                }
                catch (UriFormatException)
                {
                    return false;
                }
            }

            try
            {
                var candidate = Path.GetFullPath(path);
                var parent = Path.GetFullPath(baseDirPath);
                var relative = Path.GetRelativePath(parent, candidate);
                return relative == "." ||
                    (!Path.IsPathRooted(relative) &&
                     relative != ".." &&
                     !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                     !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                return false;
            }
        }
    }
}
