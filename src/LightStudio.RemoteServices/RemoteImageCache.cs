using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.RemoteServices;

/// <summary>
/// Cache for library images downloaded from a remote server: album art, video previews, and
/// photo thumbnails.
/// </summary>
/// <remarks>
/// <para>
/// Entries are keyed by a hash of the media path and evicted least-recently-used once the budget
/// is exceeded. Installed clients store images on disk; browser clients supply persistent storage
/// for responses that the HTTP cache cannot reuse.
/// </para>
/// <para>
/// Concurrent requests for the same image share a single download. Every filesystem operation is
/// best-effort: a cache that cannot be read or written degrades to plain downloads.
/// </para>
/// </remarks>
public sealed class RemoteImageCache : IStorageCache, IStorageCacheLimit
{
    private const long BytesPerMebibyte = 1024 * 1024;

    /// <summary>Bump when preview bytes change so native and browser clients do not reuse stale images.</summary>
    public const string VideoPreviewRepresentationVersion = "5";

    /// <summary>Kept from when this cache held album art alone, so existing budgets carry over.</summary>
    private const string MaxBytesSettingKey = "settings.albumArtCacheMaxMib";

    /// <summary>Smallest budget on offer; a few hundred images.</summary>
    public const long MinBytes = 16 * BytesPerMebibyte;

    /// <summary>Largest budget on offer.</summary>
    public const long MaxAllowedBytes = 4 * 1024 * BytesPerMebibyte;

    /// <summary>Budget applied until the user changes it.</summary>
    public const long DefaultMaxBytes = 256 * BytesPerMebibyte;

    private readonly string directory = string.Empty;
    private readonly IKeyValueStore? settings;
    private readonly IRemoteImageStore? store;
    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<CachedImage>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<CachedImage> recency = new();
    private readonly Dictionary<string, Task<byte[]>> pending = new(StringComparer.Ordinal);

    private bool indexed;
    private long storedBytes;
    private long maxBytes;

    public RemoteImageCache(string directory, IKeyValueStore settings)
    {
        this.directory = directory;
        this.settings = settings;
        var storedMebibytes = settings.GetInt32(MaxBytesSettingKey);
        maxBytes = storedMebibytes > 0
            ? Math.Clamp(storedMebibytes * BytesPerMebibyte, MinBytes, MaxAllowedBytes)
            : DefaultMaxBytes;
    }

    public RemoteImageCache(IRemoteImageStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public long MaxBytes => store?.MaxBytes ?? Interlocked.Read(ref maxBytes);

    public long MinimumBytes => store?.MinimumBytes ?? MinBytes;

    public long MaximumBytes => store?.MaximumBytes ?? MaxAllowedBytes;

    /// <summary>
    /// Returns the artwork of <paramref name="mediaFilePath"/>, downloading it with
    /// <paramref name="fetch"/> only when it is not cached yet.
    /// </summary>
    public Task<byte[]> GetAlbumArtAsync(string mediaFilePath, Func<CancellationToken, Task<byte[]>> fetch) =>
        GetAsync(NameFor(mediaFilePath), fetch);

    /// <summary>
    /// Returns the preview frame of <paramref name="mediaFilePath"/>, downloading it with
    /// <paramref name="fetch"/> only when it is not cached yet.
    /// </summary>
    public Task<byte[]> GetVideoPreviewAsync(string mediaFilePath, Func<CancellationToken, Task<byte[]>> fetch) =>
        GetAsync(NameFor($"preview-v{VideoPreviewRepresentationVersion}\u0000" + mediaFilePath), fetch);

    private Task<byte[]> GetAsync(string name, Func<CancellationToken, Task<byte[]>> fetch)
    {
        TaskCompletionSource<byte[]> completion;
        lock (pending)
        {
            if (pending.TryGetValue(name, out var running))
            {
                return running;
            }

            completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[name] = completion.Task;
        }

        _ = store is null
            ? Task.Run(() => ReadOrDownloadAsync(name, fetch, completion))
            : ReadOrDownloadAsync(name, fetch, completion);
        return completion.Task;
    }

    public Task<StorageCacheUsage> MeasureAsync() => store?.MeasureAsync() ?? Task.Run(() =>
    {
        EnsureIndexed();
        lock (gate)
        {
            return new StorageCacheUsage(entries.Count, storedBytes);
        }
    });

    public Task ClearAsync() => store?.ClearAsync() ?? Task.Run(() =>
    {
        lock (gate)
        {
            entries.Clear();
            recency.Clear();
            storedBytes = 0;
            indexed = true;
        }

        foreach (var path in EnumerateFiles())
        {
            Delete(path);
        }
    });

    public Task SetMaxBytesAsync(long maxBytes) => store?.SetMaxBytesAsync(maxBytes) ?? Task.Run(() =>
    {
        var clamped = Math.Clamp(maxBytes, MinBytes, MaxAllowedBytes);
        Interlocked.Exchange(ref this.maxBytes, clamped);
        settings!.SetInt32(MaxBytesSettingKey, (int)(clamped / BytesPerMebibyte));

        EnsureIndexed();
        foreach (var evicted in Trim())
        {
            Delete(PathFor(evicted));
        }
    });

    private async Task ReadOrDownloadAsync(
        string name,
        Func<CancellationToken, Task<byte[]>> fetch,
        TaskCompletionSource<byte[]> completion)
    {
        try
        {
            var data = store is null
                ? TryRead(name)
                : await store.ReadAsync(name).ConfigureAwait(false);
            if (data is null)
            {
                data = await fetch(CancellationToken.None).ConfigureAwait(false);
                if (data.Length > 0)
                {
                    if (store is null)
                    {
                        Write(name, data);
                    }
                    else
                    {
                        await store.WriteAsync(name, data).ConfigureAwait(false);
                    }
                }
            }

            completion.TrySetResult(data);
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (pending)
            {
                pending.Remove(name);
            }
        }
    }

    private byte[]? TryRead(string name)
    {
        EnsureIndexed();
        lock (gate)
        {
            if (!entries.TryGetValue(name, out var node))
            {
                return null;
            }

            recency.Remove(node);
            recency.AddFirst(node);
        }

        try
        {
            return File.ReadAllBytes(PathFor(name));
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            lock (gate)
            {
                if (entries.Remove(name, out var stale))
                {
                    recency.Remove(stale);
                    storedBytes -= stale.Value.Length;
                }
            }

            return null;
        }
    }

    private void Write(string name, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var target = PathFor(name);
            var temporary = target + ".part";
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return;
        }

        lock (gate)
        {
            if (entries.Remove(name, out var existing))
            {
                recency.Remove(existing);
                storedBytes -= existing.Value.Length;
            }

            entries[name] = recency.AddFirst(new CachedImage(name, data.Length));
            storedBytes += data.Length;
        }

        foreach (var evicted in Trim())
        {
            Delete(PathFor(evicted));
        }
    }

    private void EnsureIndexed()
    {
        lock (gate)
        {
            if (indexed)
            {
                return;
            }

            indexed = true;
            var found = new List<(string Name, long Length, DateTime LastWrite)>();
            foreach (var path in EnumerateFiles())
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Extension == ".part")
                    {
                        File.Delete(path);
                        continue;
                    }

                    found.Add((info.Name, info.Length, info.LastWriteTimeUtc));
                }
                catch (Exception exception) when (IsFileSystemFailure(exception))
                {
                }
            }

            found.Sort((left, right) => right.LastWrite.CompareTo(left.LastWrite));
            foreach (var (name, length, _) in found)
            {
                entries[name] = recency.AddLast(new CachedImage(name, length));
                storedBytes += length;
            }
        }
    }

    private List<string> Trim()
    {
        var evicted = new List<string>();
        lock (gate)
        {
            var budget = Interlocked.Read(ref maxBytes);
            while (storedBytes > budget && recency.Last is { } last)
            {
                recency.RemoveLast();
                entries.Remove(last.Value.Name);
                storedBytes -= last.Value.Length;
                evicted.Add(last.Value.Name);
            }
        }

        return evicted;
    }

    /// <summary>
    /// Album art hashes the bare path, so entries written before previews shared this cache stay
    /// readable; every other kind of image prefixes its own name.
    /// </summary>
    private static string NameFor(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    private string PathFor(string name) => Path.Combine(directory, name);

    private string[] EnumerateFiles()
    {
        try
        {
            return Directory.GetFiles(directory);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return [];
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private readonly record struct CachedImage(string Name, long Length);
}
