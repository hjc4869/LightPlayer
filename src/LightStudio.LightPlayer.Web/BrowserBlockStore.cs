using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.RemoteServices;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// Persistent backing store for cached media blocks, kept in the Origin Private File System.
/// </summary>
/// <remarks>
/// <para>
/// WebAssembly still runs in 32-bit mode, so a large in-memory cache would compete with the runtime
/// heap. OPFS keeps the blocks on disk, outside the wasm heap, and survives page reloads. It is not
/// reachable through <see cref="System.IO"/>: the runtime only mounts an in-memory filesystem, so
/// the store goes through a small JavaScript module instead.
/// </para>
/// <para>
/// The store owns an LRU index over the persisted files and evicts the least recently used ones
/// once <see cref="MaxBytes"/> is exceeded. Every operation is best-effort: if browser storage is
/// unavailable, denied or full, the store disables itself and the caller falls back to the network.
/// </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class BrowserBlockStore : IMediaBlockStore, IRemoteImageStore
{
    /// <summary>Browser storage key holding the user's budget, in mebibytes.</summary>
    private const string MaxBytesSettingKey = "settings.mediaCacheMaxMib";

    private const long BytesPerMebibyte = 1024 * 1024;

    /// <summary>Smallest budget on offer; streaming needs room for a handful of blocks at least.</summary>
    public const long MinBytes = 64 * BytesPerMebibyte;

    /// <summary>Largest budget on offer; browser quotas sit well below this in practice.</summary>
    public const long MaxAllowedBytes = 16 * 1024 * BytesPerMebibyte;

    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<StoredBlock>> entries = [];
    private readonly string storeId;
    private readonly IKeyValueStore settings;
    private readonly string maxBytesSettingKey;

    /// <summary>Most recently used block first, eviction candidate last.</summary>
    private readonly LinkedList<StoredBlock> recency = new();

    private Task<bool>? initialization;
    private long maxBytes;
    private long storedBytes;
    private volatile bool disabled;

    public BrowserBlockStore(long defaultMaxBytes)
        : this("legacy", new BrowserKeyValueStore(), defaultMaxBytes)
    {
    }

    public BrowserBlockStore(string storeId, IKeyValueStore settings, long defaultMaxBytes)
        : this(storeId, settings, defaultMaxBytes, MaxBytesSettingKey, MinBytes, MaxAllowedBytes)
    {
    }

    public BrowserBlockStore(
        string storeId,
        IKeyValueStore settings,
        long defaultMaxBytes,
        string maxBytesSettingKey,
        long minimumBytes,
        long maximumBytes)
    {
        this.storeId = storeId;
        this.settings = settings;
        this.maxBytesSettingKey = maxBytesSettingKey;
        MinimumBytes = minimumBytes;
        MaximumBytes = maximumBytes;
        maxBytes = LoadMaxBytes(defaultMaxBytes);
    }

    /// <summary>Total size the persisted blocks may occupy before eviction starts.</summary>
    public long MaxBytes => Interlocked.Read(ref maxBytes);

    /// <inheritdoc />
    public long MinimumBytes { get; }

    /// <inheritdoc />
    public long MaximumBytes { get; }

    /// <summary>Applies a new budget, remembers it for later sessions, and evicts what no longer fits.</summary>
    public async Task SetMaxBytesAsync(long value)
    {
        var clamped = Math.Clamp(value, MinimumBytes, MaximumBytes);
        Interlocked.Exchange(ref maxBytes, clamped);
        settings.SetInt32(maxBytesSettingKey, (int)(clamped / BytesPerMebibyte));

        if (!await EnsureReadyAsync().ConfigureAwait(false))
        {
            return;
        }

        try
        {
            foreach (var evicted in Trim())
            {
                await BrowserMainThread.InvokeAsync(
                    () => BlockStoreInterop.RemoveAsync(storeId, evicted)).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Storage revoked: the index already reflects the smaller budget.
        }
    }

    private long LoadMaxBytes(long defaultMaxBytes)
    {
        var storedMebibytes = settings.GetInt32(maxBytesSettingKey);
        return storedMebibytes > 0
            ? Math.Clamp(storedMebibytes * BytesPerMebibyte, MinimumBytes, MaximumBytes)
            : defaultMaxBytes;
    }

    public async Task<byte[]?> ReadAsync(string name)
    {
        if (!await EnsureReadyAsync().ConfigureAwait(false))
        {
            return null;
        }

        int expectedLength;
        lock (gate)
        {
            if (!entries.TryGetValue(name, out var node) || node.Value.Size is <= 0 or > int.MaxValue)
            {
                return null;
            }

            expectedLength = (int)node.Value.Size;
        }

        return await ReadAsync(name, expectedLength).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a persisted block, or returns <see langword="null"/> when it is absent, unreadable or
    /// does not have the expected length (a truncated leftover from an interrupted write).
    /// </summary>
    public async Task<byte[]?> ReadAsync(string name, int expectedLength)
    {
        if (!await EnsureReadyAsync().ConfigureAwait(false) || !Contains(name))
        {
            return null;
        }

        try
        {
            // Source-generated interop cannot marshal an array out of a Task, so the block is read
            // asynchronously into a JavaScript slot and then copied across synchronously.
            var size = await BrowserMainThread.InvokeAsync(
                () => BlockStoreInterop.ReadBeginAsync(storeId, name, expectedLength)).ConfigureAwait(false);
            if (size < 0)
            {
                return null;
            }

            if (size != expectedLength)
            {
                // A truncated leftover from an interrupted write, or a block of another revision.
                await BrowserMainThread.InvokeAsync(
                    () => BlockStoreInterop.ReadDiscardAsync(storeId, name)).ConfigureAwait(false);
                await ForgetAsync(name).ConfigureAwait(false);
                return null;
            }

            var data = new byte[size];
            await BrowserMainThread.InvokeAsync(
                () => BlockStoreInterop.ReadEndAsync(storeId, name, data)).ConfigureAwait(false);
            Touch(name);
            return data;
        }
        catch (Exception)
        {
            // A failed cache lookup must never surface to the decoder; the block gets downloaded.
            return null;
        }
    }

    /// <summary>Persists a block and evicts least recently used ones to stay inside the budget.</summary>
    public async Task WriteAsync(string name, byte[] data)
    {
        if (data.Length > MaxBytes || !await EnsureReadyAsync().ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (!await BrowserMainThread.InvokeAsync(
                () => BlockStoreInterop.WriteAsync(storeId, name, data)).ConfigureAwait(false))
            {
                return;
            }

            foreach (var evicted in Add(name, data.Length))
            {
                await BrowserMainThread.InvokeAsync(
                    () => BlockStoreInterop.RemoveAsync(storeId, evicted)).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Out of quota or storage revoked: keep playing without persistence.
        }
    }

    /// <summary>Whether the index lists a persisted block.</summary>
    public async Task<bool> ContainsAsync(string name) =>
        await EnsureReadyAsync().ConfigureAwait(false) && Contains(name);

    /// <summary>Number of persisted blocks and the space they occupy.</summary>
    public async Task<StorageCacheUsage> MeasureAsync()
    {
        if (!await EnsureReadyAsync().ConfigureAwait(false))
        {
            return default;
        }

        lock (gate)
        {
            return new StorageCacheUsage(entries.Count, storedBytes);
        }
    }

    /// <summary>Removes every persisted block.</summary>
    public async Task ClearAsync()
    {
        if (!await EnsureReadyAsync().ConfigureAwait(false))
        {
            return;
        }

        List<string> names;
        lock (gate)
        {
            names = [.. entries.Keys];
            entries.Clear();
            recency.Clear();
            storedBytes = 0;
        }

        foreach (var name in names)
        {
            try
            {
                await BrowserMainThread.InvokeAsync(
                    () => BlockStoreInterop.RemoveAsync(storeId, name)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Storage revoked mid-clear: the index is empty either way, so the leftover file
                // is adopted again (and evicted in LRU order) on the next start.
            }
        }
    }

    private Task<bool> EnsureReadyAsync()
    {
        if (disabled)
        {
            return Task.FromResult(false);
        }

        lock (gate)
        {
            return initialization ??= InitializeAsync();
        }
    }

    private async Task<bool> InitializeAsync()
    {
        try
        {
            if (!await BrowserMainThread.InvokeAsync(
                () => BlockStoreInterop.OpenAsync(storeId)).ConfigureAwait(false))
            {
                disabled = true;
                return false;
            }

            // Blocks written by earlier sessions are adopted rather than discarded, so a track that
            // was played yesterday still starts without touching the network.
            LoadIndex(await BrowserMainThread.InvokeAsync(
                () => BlockStoreInterop.ListAsync(storeId)).ConfigureAwait(false));
            foreach (var evicted in Trim())
            {
                await BrowserMainThread.InvokeAsync(
                    () => BlockStoreInterop.RemoveAsync(storeId, evicted)).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception)
        {
            disabled = true;
            return false;
        }
    }

    private void LoadIndex(string listing)
    {
        var stored = new List<(string Name, long Size, long Modified)>();
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length == 3
                && long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                && long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var modified))
            {
                stored.Add((fields[0], size, modified));
            }
        }

        // Oldest first, so inserting each at the head leaves the newest block most recently used.
        stored.Sort(static (left, right) => left.Modified.CompareTo(right.Modified));

        lock (gate)
        {
            foreach (var (name, size, _) in stored)
            {
                if (entries.ContainsKey(name))
                {
                    continue;
                }

                entries[name] = recency.AddFirst(new StoredBlock(name, size));
                storedBytes += size;
            }
        }
    }

    private bool Contains(string name)
    {
        lock (gate)
        {
            return entries.ContainsKey(name);
        }
    }

    private void Touch(string name)
    {
        lock (gate)
        {
            if (entries.TryGetValue(name, out var node))
            {
                recency.Remove(node);
                recency.AddFirst(node);
            }
        }
    }

    private List<string> Add(string name, long size)
    {
        lock (gate)
        {
            if (entries.TryGetValue(name, out var existing))
            {
                storedBytes -= existing.Value.Size;
                recency.Remove(existing);
            }

            entries[name] = recency.AddFirst(new StoredBlock(name, size));
            storedBytes += size;
            return Trim();
        }
    }

    /// <summary>Drops least recently used entries from the index until the budget holds.</summary>
    private List<string> Trim()
    {
        var evicted = new List<string>();
        lock (gate)
        {
            while (storedBytes > MaxBytes && recency.Last is { } last)
            {
                recency.RemoveLast();
                entries.Remove(last.Value.Name);
                storedBytes -= last.Value.Size;
                evicted.Add(last.Value.Name);
            }
        }

        return evicted;
    }

    private async Task ForgetAsync(string name)
    {
        lock (gate)
        {
            if (entries.Remove(name, out var node))
            {
                recency.Remove(node);
                storedBytes -= node.Value.Size;
            }
        }

        await BrowserMainThread.InvokeAsync(
            () => BlockStoreInterop.RemoveAsync(storeId, name)).ConfigureAwait(false);
    }

    private sealed record StoredBlock(string Name, long Size);
}

[SupportedOSPlatform("browser")]
internal static partial class BlockStoreInterop
{
    [JSImport("open", "BlockStore")]
    internal static partial Task<bool> OpenAsync(string storeId);

    [JSImport("list", "BlockStore")]
    internal static partial Task<string> ListAsync(string storeId);

    /// <summary>Returns the file size or -1; mismatched entries are not staged.</summary>
    [JSImport("readBegin", "BlockStore")]
    internal static partial Task<int> ReadBeginAsync(string storeId, string name, int expectedLength = -1);

    /// <summary>Copies the slot prepared by <see cref="ReadBeginAsync"/> into managed memory.</summary>
    [JSImport("readEnd", "BlockStore")]
    internal static partial Task ReadEndAsync(
        string storeId,
        string name,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> buffer);

    /// <summary>Releases a slot prepared by <see cref="ReadBeginAsync"/> without reading it.</summary>
    [JSImport("readDiscard", "BlockStore")]
    internal static partial Task ReadDiscardAsync(string storeId, string name);

    [JSImport("write", "BlockStore")]
    internal static partial Task<bool> WriteAsync(string storeId, string name, byte[] data);

    [JSImport("remove", "BlockStore")]
    internal static partial Task RemoveAsync(string storeId, string name);
}
