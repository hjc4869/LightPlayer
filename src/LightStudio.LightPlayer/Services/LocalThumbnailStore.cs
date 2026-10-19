using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Services;

public sealed class LocalThumbnailStore(string directory) : IThumbnailStore
{
    private const int FormatVersion = 1;
    private const int HeaderLength = sizeof(int) + 2 * sizeof(long) + SHA256.HashSizeInBytes;

    public IStorageCache GetCache(ThumbnailMemoryCache thumbnails, ThumbnailCacheKind kind) =>
        new CacheStorage(thumbnails, kind, DirectoryFor(kind));

    public (long Length, long ModifiedTicks) GetRevision(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return default;
        }

        try
        {
            var source = new FileInfo(path);
            return source.Exists ? (source.Length, source.LastWriteTimeUtc.Ticks) : (-1, 0);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            AppLog.Warning("Cache", $"Could not read preview source metadata for {path}.", exception);
            return (-1, 0);
        }
    }

    public byte[]? Read(ThumbnailCacheKind kind, string path, (long Length, long ModifiedTicks) revision)
    {
        try
        {
            using var stream = File.OpenRead(PathFor(kind, path));
            if (stream.Length <= HeaderLength || stream.Length > HeaderLength + ThumbnailMemoryCache.DefaultBudgetInBytes)
            {
                return null;
            }

            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != FormatVersion ||
                reader.ReadInt64() != revision.Length || reader.ReadInt64() != revision.ModifiedTicks)
            {
                return null;
            }

            byte[] checksum = reader.ReadBytes(SHA256.HashSizeInBytes);
            byte[] thumbnail = reader.ReadBytes((int)(stream.Length - HeaderLength));
            return CryptographicOperations.FixedTimeEquals(checksum, SHA256.HashData(thumbnail))
                ? thumbnail
                : null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            AppLog.Warning("Cache", $"Could not read cached {kind} preview for {path}.", exception);
            return null;
        }
    }

    public void Write(
        ThumbnailCacheKind kind, string path, (long Length, long ModifiedTicks) revision, byte[] thumbnail)
    {
        if (thumbnail.Length == 0 || thumbnail.LongLength > ThumbnailMemoryCache.DefaultBudgetInBytes || revision.Length < 0)
        {
            return;
        }

        string target = PathFor(kind, path);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            Directory.CreateDirectory(DirectoryFor(kind));
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(FormatVersion);
                writer.Write(revision.Length);
                writer.Write(revision.ModifiedTicks);
                writer.Write(SHA256.HashData(thumbnail));
                writer.Write(thumbnail);
            }

            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            AppLog.Warning("Cache", $"Could not store cached {kind} preview for {path}.", exception);
        }
        finally
        {
            Delete(temporary);
        }
    }

    public void Clear(ThumbnailCacheKind kind)
    {
        try
        {
            foreach (string path in Directory.GetFiles(DirectoryFor(kind)))
            {
                Delete(path);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            AppLog.Warning("Cache", $"Could not clear cached {kind} previews.", exception);
        }
    }

    private string DirectoryFor(ThumbnailCacheKind kind) => Path.Combine(directory, kind switch
    {
        ThumbnailCacheKind.VideoPreview => "Video",
        ThumbnailCacheKind.Photo => "Photos",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    });

    private string PathFor(ThumbnailCacheKind kind, string path) =>
        Path.Combine(DirectoryFor(kind), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".preview");

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            AppLog.Warning("Cache", $"Could not delete cached preview {path}.", exception);
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private sealed class CacheStorage(ThumbnailMemoryCache thumbnails, ThumbnailCacheKind kind, string directory) : IStorageCache
    {
        private readonly DirectoryStorageCache storage = new(directory);

        public Task<StorageCacheUsage> MeasureAsync() => storage.MeasureAsync();

        public Task ClearAsync() => Task.Run(() => thumbnails.Clear(kind));
    }
}