#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.Database;

/// <summary>The kinds of encoded thumbnail that share the application's in-memory LRU budget.</summary>
public enum ThumbnailCacheKind
{
    VideoPreview,
    Photo,
}

public interface IThumbnailStore
{
    (long Length, long ModifiedTicks) GetRevision(string path);

    byte[]? Read(ThumbnailCacheKind kind, string path, (long Length, long ModifiedTicks) revision);

    void Write(ThumbnailCacheKind kind, string path, (long Length, long ModifiedTicks) revision, byte[] thumbnail);

    void Clear(ThumbnailCacheKind kind);
}

/// <summary>
/// Holds recently requested encoded thumbnails. Entries of every kind compete for one bounded
/// byte budget, with optional persistent storage for successful previews. Missing thumbnails are
/// remembered in memory so their source is not reopened repeatedly.
/// </summary>
public sealed class ThumbnailMemoryCache
{
    public const long DefaultBudgetInBytes = 16L * 1024 * 1024;

    public static ThumbnailMemoryCache Shared { get; } = new();

    private readonly long budgetInBytes;
    private readonly object gate = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> entries = [];
    private readonly LinkedList<Entry> order = new();
    private readonly int[] clears = new int[Enum.GetValues<ThumbnailCacheKind>().Length];
    private long size;

    public ThumbnailMemoryCache(long budgetInBytes = DefaultBudgetInBytes, IThumbnailStore? persistentStore = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budgetInBytes, 0);
        this.budgetInBytes = budgetInBytes;
        PersistentStore = persistentStore;
    }

    public IThumbnailStore? PersistentStore { get; set; }

    public Task<(bool Found, byte[]? Thumbnail)> TryGetAsync(
        ThumbnailCacheKind kind, string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            bool found = TryGet(kind, path, out byte[]? thumbnail);
            return (found, thumbnail);
        }, cancellationToken);

    public Task SetAsync(
        ThumbnailCacheKind kind, string path, byte[]? thumbnail, CancellationToken cancellationToken = default) =>
        Task.Run(() => Set(kind, path, thumbnail), cancellationToken);

    public bool TryGet(ThumbnailCacheKind kind, string path, out byte[]? thumbnail)
    {
        var key = new Key(kind, path, PersistentStore?.GetRevision(path) ?? default);
        lock (gate)
        {
            if (TryGetMemory(key, out thumbnail))
            {
                return true;
            }
        }

        // Disk reads run outside the lock so one slow volume does not stall every lookup.
        thumbnail = PersistentStore?.Read(kind, path, key.Revision);
        if (thumbnail is not { Length: > 0 })
        {
            return false;
        }

        lock (gate)
        {
            if (TryGetMemory(key, out byte[]? stored))
            {
                thumbnail = stored;
            }
            else
            {
                SetMemory(key, thumbnail);
            }
        }

        return true;
    }

    public void Set(ThumbnailCacheKind kind, string path, byte[]? thumbnail)
    {
        var key = new Key(kind, path, PersistentStore?.GetRevision(path) ?? default);
        int clear;
        lock (gate)
        {
            SetMemory(key, thumbnail);
            clear = clears[(int)kind];
        }

        if (thumbnail is not { Length: > 0 } || PersistentStore is not { } store)
        {
            return;
        }

        store.Write(kind, path, key.Revision, thumbnail);
        bool cleared;
        lock (gate)
        {
            cleared = clear != clears[(int)kind];
        }

        // A clear that ran during the write must not leave the new file behind.
        if (cleared)
        {
            store.Clear(kind);
        }
    }

    public void Clear(ThumbnailCacheKind kind)
    {
        lock (gate)
        {
            for (var node = order.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.Key.Kind == kind)
                {
                    order.Remove(node);
                    entries.Remove(node.Value.Key);
                    size -= node.Value.Thumbnail?.LongLength ?? 0;
                }

                node = next;
            }

            clears[(int)kind]++;
        }

        PersistentStore?.Clear(kind);
    }

    private bool TryGetMemory(Key key, out byte[]? thumbnail)
    {
        if (!entries.TryGetValue(key, out LinkedListNode<Entry>? node))
        {
            thumbnail = null;
            return false;
        }

        order.Remove(node);
        order.AddFirst(node);
        thumbnail = node.Value.Thumbnail;
        return true;
    }

    private void SetMemory(Key key, byte[]? thumbnail)
    {
        if (entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
        {
            order.Remove(existing);
            size -= existing.Value.Thumbnail?.LongLength ?? 0;
        }

        entries[key] = order.AddFirst(new Entry(key, thumbnail));
        size += thumbnail?.LongLength ?? 0;

        // Keep one oversized thumbnail rather than immediately throwing away the requested item.
        while (size > budgetInBytes && order.Count > 1 && order.Last is { } last)
        {
            order.RemoveLast();
            entries.Remove(last.Value.Key);
            size -= last.Value.Thumbnail?.LongLength ?? 0;
        }
    }

    private readonly record struct Key(ThumbnailCacheKind Kind, string Path, (long Length, long ModifiedTicks) Revision);

    private readonly record struct Entry(Key Key, byte[]? Thumbnail);
}