using System;
using System.IO;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// <see cref="IStorageCache"/> over a cache directory whose files are all disposable.
/// </summary>
/// <remarks>
/// Every operation is best-effort: a file that vanishes or cannot be touched while the directory is
/// walked is skipped rather than failing the whole measurement or clear.
/// </remarks>
public sealed class DirectoryStorageCache(string directory) : IStorageCache
{
    public Task<StorageCacheUsage> MeasureAsync() => Task.Run(() =>
    {
        var count = 0;
        var bytes = 0L;

        foreach (var path in EnumerateFiles())
        {
            try
            {
                bytes += new FileInfo(path).Length;
                count++;
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
            }
        }

        return new StorageCacheUsage(count, bytes);
    });

    public Task ClearAsync() => Task.Run(() =>
    {
        foreach (var path in EnumerateFiles())
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
            }
        }
    });

    /// <summary>Snapshots the directory, so entries may be deleted while it is walked.</summary>
    private string[] EnumerateFiles()
    {
        try
        {
            return Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return [];
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}
