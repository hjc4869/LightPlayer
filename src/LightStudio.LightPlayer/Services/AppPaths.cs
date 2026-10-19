using System;
using System.IO;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Resolves the per-user application data and cache directories. Centralizes the
/// folder names so settings, playlists, and disposable data live under a
/// single, predictable application root.
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "LightStudio.LightPlayer";

    /// <summary>Durable data root (settings, playlists). Created on demand.</summary>
    public static string DataDirectory => EnsureExists(
        Path.Combine(PlatformServices.AppDataPath, AppFolderName));

    /// <summary>Sub-folder of the durable data root, created if missing.</summary>
    public static string DataSubdirectory(string name) =>
        EnsureExists(Path.Combine(DataDirectory, name));

    /// <summary>Local cache root for disposable application data. Created on demand.</summary>
    public static string CacheDirectory => EnsureExists(
        Path.Combine(PlatformServices.CachePath, AppFolderName, "Cache"));

    /// <summary>Sub-folder of the cache root, created if missing.</summary>
    public static string CacheSubdirectory(string name) =>
        EnsureExists(Path.Combine(CacheDirectory, name));

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
