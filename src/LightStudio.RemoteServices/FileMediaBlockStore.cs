using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.RemoteServices;

/// <summary>
/// <see cref="IMediaBlockStore"/> over a cache directory, used by the installed heads when they
/// stream a remote library.
/// </summary>
/// <remarks>
/// The store owns an LRU index over the files it wrote and evicts the least recently used ones once
/// <see cref="MaxBytes"/> is exceeded. The index is rebuilt from the directory on first use, taking
/// last-write times as the recency order, so a restart does not throw the cache away.
/// </remarks>
public sealed class FileMediaBlockStore : IMediaBlockStore
{
    private const long BytesPerMebibyte = 1024 * 1024;

    /// <summary>Key the budget is persisted under, shared with the browser store.</summary>
    private const string MaxBytesSettingKey = "settings.mediaCacheMaxMib";

    private readonly string directory;
    private readonly IKeyValueStore settings;
    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<StoredBlock>> entries = new(StringComparer.Ordinal);

    /// <summary>Most recently used block first, eviction candidate last.</summary>
    private readonly LinkedList<StoredBlock> recency = new();

    private bool indexed;
    private long storedBytes;
    private long maxBytes;

    public FileMediaBlockStore(string directory, IKeyValueStore settings, long defaultMaxBytes)
    {
        this.directory = directory;
        this.settings = settings;
        var storedMebibytes = settings.GetInt32(MaxBytesSettingKey);
        maxBytes = storedMebibytes > 0
            ? Math.Clamp(storedMebibytes * BytesPerMebibyte, MinBytes, MaxAllowedBytes)
            : defaultMaxBytes;
    }

    /// <summary>Smallest budget on offer; streaming needs room for a handful of blocks at least.</summary>
    public const long MinBytes = 64 * BytesPerMebibyte;

    /// <summary>Largest budget on offer.</summary>
    public const long MaxAllowedBytes = 64 * 1024 * BytesPerMebibyte;

    public long MaxBytes => Interlocked.Read(ref maxBytes);

    public long MinimumBytes => MinBytes;

    public long MaximumBytes => MaxAllowedBytes;

    public Task SetMaxBytesAsync(long value) => Task.Run(() =>
    {
        var clamped = Math.Clamp(value, MinBytes, MaxAllowedBytes);
        Interlocked.Exchange(ref maxBytes, clamped);
        settings.SetInt32(MaxBytesSettingKey, (int)(clamped / BytesPerMebibyte));

        EnsureIndexed();
        foreach (var evicted in Trim())
        {
            Delete(evicted);
        }
    });

    public Task<byte[]?> ReadAsync(string name, int expectedLength) => Task.Run<byte[]?>(() =>
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
            var data = File.ReadAllBytes(PathFor(name));
            if (data.Length == expectedLength)
            {
                return data;
            }

            // A truncated leftover from an interrupted write, or a block of another revision.
            Forget(name);
            Delete(name);
            return null;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            Forget(name);
            return null;
        }
    });

    public Task WriteAsync(string name, byte[] data) => Task.Run(() =>
    {
        EnsureIndexed();
        try
        {
            Directory.CreateDirectory(directory);

            // Write through a temporary file so an interrupted write never leaves a short block
            // that a later session would mistake for a complete one.
            var target = PathFor(name);
            var temporary = target + ".part";
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return;
        }

        Remember(name, data.Length);
        foreach (var evicted in Trim())
        {
            Delete(evicted);
        }
    });

    public Task<bool> ContainsAsync(string name)
    {
        lock (gate)
        {
            if (indexed)
            {
                return Task.FromResult(entries.ContainsKey(name));
            }
        }

        return Task.Run(() =>
        {
            EnsureIndexed();
            lock (gate)
            {
                return entries.ContainsKey(name);
            }
        });
    }

    public Task<StorageCacheUsage> MeasureAsync() => Task.Run(() =>
    {
        EnsureIndexed();
        lock (gate)
        {
            return new StorageCacheUsage(entries.Count, storedBytes);
        }
    });

    public Task ClearAsync() => Task.Run(() =>
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
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
            }
        }
    });

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
                entries[name] = recency.AddLast(new StoredBlock(name, length));
                storedBytes += length;
            }
        }
    }

    private void Remember(string name, long length)
    {
        lock (gate)
        {
            if (entries.TryGetValue(name, out var existing))
            {
                storedBytes -= existing.Value.Length;
                recency.Remove(existing);
            }

            entries[name] = recency.AddFirst(new StoredBlock(name, length));
            storedBytes += length;
        }
    }

    private void Forget(string name)
    {
        lock (gate)
        {
            if (entries.Remove(name, out var node))
            {
                recency.Remove(node);
                storedBytes -= node.Value.Length;
            }
        }
    }

    /// <summary>Drops least recently used entries from the index until the budget fits.</summary>
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

    private void Delete(string name)
    {
        try
        {
            File.Delete(PathFor(name));
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
        }
    }

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

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private readonly record struct StoredBlock(string Name, long Length);
}
