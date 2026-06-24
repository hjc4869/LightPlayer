using System;
using System.IO;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Resolves the per-user application data and cache directories. Centralizes the
/// folder names so settings, playlists, thumbnails, and lyrics all live under a
/// single, predictable application root.
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "LightStudio.LightPlayer";

    /// <summary>Roaming data root (settings, playlists). Created on demand.</summary>
    public static string DataDirectory => EnsureExists(
        Path.Combine(Resolve(Environment.SpecialFolder.ApplicationData), AppFolderName));

    /// <summary>Local cache root (thumbnails, downloaded lyrics). Created on demand.</summary>
    public static string CacheDirectory => EnsureExists(
        Path.Combine(Resolve(Environment.SpecialFolder.LocalApplicationData), AppFolderName, "Cache"));

    /// <summary>Sub-folder of the cache root, created if missing.</summary>
    public static string CacheSubdirectory(string name) =>
        EnsureExists(Path.Combine(CacheDirectory, name));

    private static string Resolve(Environment.SpecialFolder folder)
    {
        var root = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        return string.IsNullOrWhiteSpace(root) ? AppContext.BaseDirectory : root;
    }

    private static string EnsureExists(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // Best effort: a missing cache directory degrades to no caching.
        }

        return path;
    }
}
