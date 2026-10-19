using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.RemoteServices;

/// <summary>
/// Identity of a cached entry: its resource plus a zero-based block index, or -1 for the whole file.
/// </summary>
/// <remarks>
/// The resource id is a hash that already covers the server entity tag, so a file that changed on
/// the server never serves stale blocks from a previous revision, and it is safe to use verbatim as
/// a file name in the block store.
/// </remarks>
public readonly record struct MediaBlockKey(string ResourceId, long BlockIndex)
{
    /// <summary>Name this block is persisted under.</summary>
    public string FileName => $"{ResourceId}-{BlockIndex:x}";
}

/// <summary>
/// Downloads one aligned block of a remote resource.
/// </summary>
public delegate Task<byte[]> RemoteBlockFetch(long offset, int count, CancellationToken cancellationToken);

/// <summary>
/// Retry policy shared by every remote media read. Network hiccups are common on mobile links, so
/// transient failures are retried with exponential back-off and jitter instead of tearing playback
/// down.
/// </summary>
public static class RemoteRetryPolicy
{
    /// <summary>Total number of attempts, including the first one.</summary>
    public const int MaxAttempts = 6;

    /// <summary>
    /// Deadline of the first attempt. Kept short because a request written into a pooled connection
    /// that the server has already dropped produces no response at
    /// all, and the retry lands on a fresh connection.
    /// </summary>
    private static readonly TimeSpan InitialAttemptTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Ceiling for later attempts, so a genuinely slow link still gets a chance.</summary>
    private static readonly TimeSpan MaxAttemptTimeout = TimeSpan.FromSeconds(40);

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying transient failures after a growing delay.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var timeout = InitialAttemptTimeout;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await AttemptAsync(operation, timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < MaxAttempts
                && !cancellationToken.IsCancellationRequested
                && IsTransient(exception))
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                timeout = TimeSpan.FromTicks(Math.Min(timeout.Ticks * 2, MaxAttemptTimeout.Ticks));
            }
        }
    }

    /// <summary>Waits before retrying after failed attempt <paramref name="attempt"/>, counted from 1.</summary>
    internal static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromTicks(Math.Min(InitialDelay.Ticks << Math.Min(attempt - 1, 16), MaxDelay.Ticks));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 120));
        return Task.Delay(delay + jitter, cancellationToken);
    }

    /// <summary>
    /// Runs one attempt, aborting the underlying fetch once <paramref name="timeout"/> elapses.
    /// </summary>
    private static async Task<T> AttemptAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var work = operation(attempt.Token);
        var expiry = Task.Delay(timeout, attempt.Token);
        var completed = await Task.WhenAny(work, expiry).ConfigureAwait(false);

        // Aborting the fetch matters as much as the retry does: the browser allows only a handful
        // of connections per host, so a request left hanging blocks every later one.
        attempt.Cancel();
        if (completed == work)
        {
            Observe(expiry);
            return await work.ConfigureAwait(false);
        }

        Observe(work);
        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"The remote request did not complete within {timeout.TotalSeconds:0} seconds.");
    }

    internal static void Observe(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Decides whether retrying can plausibly succeed. Anything the server rejects deterministically
    /// (bad path, unsatisfiable range, changed file) fails immediately.
    /// </summary>
    internal static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException http => http.StatusCode is not { } status || IsTransientStatus(status),

        WebException web => web.Status is WebExceptionStatus.ConnectFailure
            or WebExceptionStatus.ConnectionClosed
            or WebExceptionStatus.KeepAliveFailure
            or WebExceptionStatus.NameResolutionFailure
            or WebExceptionStatus.ProxyNameResolutionFailure
            or WebExceptionStatus.PipelineFailure
            or WebExceptionStatus.ReceiveFailure
            or WebExceptionStatus.SendFailure
            or WebExceptionStatus.Timeout
            or WebExceptionStatus.RequestCanceled
            or WebExceptionStatus.UnknownError,

        // Short reads and connection resets: the block simply has to be requested again.
        IOException => true,

        // HttpClient surfaces its own timeout as TaskCanceledException.
        TaskCanceledException => true,
        TimeoutException => true,
        _ => false,
    };

    private static bool IsTransientStatus(HttpStatusCode status) =>
        (int)status >= 500
        || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}

/// <summary>
/// LRU cache of blocks and whole files read from the remote media endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Range streams request complete blocks aligned to <see cref="BlockSize"/>, so an
/// arbitrary read pattern from FFmpeg (which seeks constantly while probing and while resolving a
/// seek target) collapses onto a small set of reusable blocks. The tail block of a file is shorter
/// than <see cref="BlockSize"/> but is still complete for that file, so it is cached as well; a
/// response that is shorter than the requested block is treated as a failed read and retried.
/// Whole-file reads use a single entry covering the entire resource.
/// </para>
/// <para>
/// Blocks live in the head's <see cref="IMediaBlockStore"/> (browser storage, or a cache directory
/// on installed heads); only a small <see cref="MaxMemoryBytes"/> window is kept in managed memory
/// so that the several reads FFmpeg performs inside one block do not each cross that boundary.
/// </para>
/// <para>
/// All members are safe to call concurrently. Concurrent requests for the same block share a single
/// load.
/// </para>
/// </remarks>
public sealed class RemoteMediaBlockCache : IStorageCache, IStorageCacheLimit
{
    /// <summary>Size and alignment of blocks requested by range streams.</summary>
    public const int BlockSize = 128 * 1024;

    /// <summary>Default size the persisted blocks may occupy.</summary>
    public const long MaxPersistentBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>Size of the hot window kept in wasm memory in front of the persistent store.</summary>
    public const long MaxMemoryBytes = 4L * 1024 * 1024;

    private readonly object gate = new();
    private readonly Dictionary<MediaBlockKey, LinkedListNode<CacheEntry>> entries = [];
    private readonly Dictionary<MediaBlockKey, PendingFetch> pending = [];

    /// <summary>Blocks being written to the store, readable until the write completes.</summary>
    private readonly Dictionary<MediaBlockKey, PendingWrite> writing = [];

    /// <summary>Most recently used block first, eviction candidate last.</summary>
    private readonly LinkedList<CacheEntry> recency = new();

    private readonly IMediaBlockStore store;
    private long cachedBytes;
    private long nextFetchId;

    public RemoteMediaBlockCache(IMediaBlockStore store) => this.store = store;

    /// <summary>Returns the block for <paramref name="key"/>, loading it if necessary.</summary>
    /// <remarks>
    /// Native callers may wait for a block. Browser callers must use <see cref="GetBlockAsync"/>
    /// for cache misses.
    /// </remarks>
    public byte[] GetBlock(
        MediaBlockKey key,
        long offset,
        int count,
        RemoteBlockFetch fetch,
        CancellationToken cancellationToken)
    {
        if (TryTouch(key, out var cached))
        {
            return cached;
        }

        while (true)
        {
            var download = StartOrJoinFetch(key, offset, count, fetch, cancellationToken);
            try
            {
                return download.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && download.CancellationToken.IsCancellationRequested)
            {
                // This caller joined a fetch owned by a stream that was subsequently closed.
                // Start a fresh request under the live caller instead of inheriting that cancel.
            }
        }
    }

    /// <inheritdoc cref="GetBlock"/>
    public async Task<byte[]> GetBlockAsync(
        MediaBlockKey key,
        long offset,
        int count,
        RemoteBlockFetch fetch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryTouch(key, out var cached))
        {
            return cached;
        }

        while (true)
        {
            var download = StartOrJoinFetch(key, offset, count, fetch, cancellationToken);
            try
            {
                return await download.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && download.CancellationToken.IsCancellationRequested)
            {
                // The shared fetch belonged to a stream that was closed while this caller waited.
            }
        }
    }

    /// <summary>Returns a block held in memory or in the persistent store, without downloading it.</summary>
    public async Task<byte[]?> TryGetBlockAsync(MediaBlockKey key, int count)
    {
        lock (gate)
        {
            if (TryTouchLocked(key, out var cached))
            {
                return cached;
            }

            if (writing.TryGetValue(key, out var write))
            {
                return write.Data;
            }
        }

        try
        {
            if (await store.ReadAsync(key.FileName, count).ConfigureAwait(false) is { } persisted)
            {
                Store(key, persisted);
                return persisted;
            }
        }
        catch (Exception exception)
        {
            AppLog.Warning("Remote.Cache", "Could not read a cached media block.", exception);
        }

        return null;
    }

    /// <summary>Whether a block is held in memory or listed by the persistent store.</summary>
    public async Task<bool> ContainsAsync(MediaBlockKey key)
    {
        lock (gate)
        {
            if (entries.ContainsKey(key) || writing.ContainsKey(key))
            {
                return true;
            }
        }

        try
        {
            return await store.ContainsAsync(key.FileName).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Remote.Cache", "Could not look up a cached media block.", exception);
            return false;
        }
    }

    /// <summary>
    /// Adds a downloaded block. It is readable as soon as this method returns; the task completes
    /// once it is persisted. A block already being persisted is not written again.
    /// </summary>
    public Task AddAsync(MediaBlockKey key, byte[] data)
    {
        TaskCompletionSource completion;
        lock (gate)
        {
            StoreLocked(key, data);
            if (writing.TryGetValue(key, out var running))
            {
                return running.Completion.Task;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            writing[key] = new PendingWrite(data, completion);
        }

        _ = PersistAsync(key, data, completion);
        return completion.Task;
    }

    /// <inheritdoc />
    public Task<StorageCacheUsage> MeasureAsync() => store.MeasureAsync();

    /// <inheritdoc />
    public long MaxBytes => store.MaxBytes;

    /// <inheritdoc />
    public long MinimumBytes => store.MinimumBytes;

    /// <inheritdoc />
    public long MaximumBytes => store.MaximumBytes;

    /// <inheritdoc />
    public Task SetMaxBytesAsync(long maxBytes) => store.SetMaxBytesAsync(maxBytes);

    /// <inheritdoc />
    /// <remarks>
    /// Blocks already being downloaded still land in the cache afterwards; they are exactly the
    /// ones playback is about to read, so re-fetching them would only stall the decoder.
    /// </remarks>
    public async Task ClearAsync()
    {
        lock (gate)
        {
            entries.Clear();
            recency.Clear();
            cachedBytes = 0;
        }

        await store.ClearAsync().ConfigureAwait(false);
    }

    private PendingFetch StartOrJoinFetch(
        MediaBlockKey key,
        long offset,
        int count,
        RemoteBlockFetch fetch,
        CancellationToken cancellationToken)
    {
        PendingFetch started;
        TaskCompletionSource<byte[]> completion;
        lock (gate)
        {
            if (TryTouchLocked(key, out var cached))
            {
                return new PendingFetch(0, Task.FromResult(cached), CancellationToken.None);
            }

            if (pending.TryGetValue(key, out var running))
            {
                if (!running.CancellationToken.IsCancellationRequested)
                {
                    return running;
                }

                pending.Remove(key);
            }

            var fetchId = ++nextFetchId;
            completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            started = new PendingFetch(fetchId, completion.Task, cancellationToken);
            pending[key] = started;
        }

        RemoteRetryPolicy.Observe(started.Task);
        _ = CompleteFetchAsync(key, offset, count, fetch, cancellationToken, started.Id, completion);
        return started;
    }

    private async Task CompleteFetchAsync(
        MediaBlockKey key,
        long offset,
        int count,
        RemoteBlockFetch fetch,
        CancellationToken cancellationToken,
        long fetchId,
        TaskCompletionSource<byte[]> completion)
    {
        try
        {
            completion.TrySetResult(await LoadAsync(
                key, offset, count, fetch, cancellationToken, fetchId).ConfigureAwait(false));
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task<byte[]> LoadAsync(
        MediaBlockKey key,
        long offset,
        int count,
        RemoteBlockFetch fetch,
        CancellationToken cancellationToken,
        long fetchId)
    {
        try
        {
            if (await TryGetBlockAsync(key, count).ConfigureAwait(false) is { } persisted)
            {
                return persisted;
            }

            var data = await RemoteRetryPolicy.ExecuteAsync(
                async token =>
                {
                    var block = await fetch(offset, count, token).ConfigureAwait(false);
                    return block.Length == count
                        ? block
                        : throw new IOException(
                            $"The server returned {block.Length} byte(s) for the {count} byte range at offset {offset}.");
                },
                cancellationToken).ConfigureAwait(false);

            await AddAsync(key, data).ConfigureAwait(false);
            return data;
        }
        finally
        {
            lock (gate)
            {
                if (pending.TryGetValue(key, out var current) && current.Id == fetchId)
                {
                    pending.Remove(key);
                }
            }
        }
    }

    private async Task PersistAsync(MediaBlockKey key, byte[] data, TaskCompletionSource completion)
    {
        try
        {
            await store.WriteAsync(key.FileName, data).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Remote.Cache", "Could not persist a media block.", exception);
        }
        finally
        {
            lock (gate)
            {
                writing.Remove(key);
            }

            completion.SetResult();
        }
    }

    private void Store(MediaBlockKey key, byte[] data)
    {
        lock (gate)
        {
            StoreLocked(key, data);
        }
    }

    private void StoreLocked(MediaBlockKey key, byte[] data)
    {
        if (entries.ContainsKey(key))
        {
            return;
        }

        entries[key] = recency.AddFirst(new CacheEntry(key, data));
        cachedBytes += data.Length;

        while (cachedBytes > MaxMemoryBytes && recency.Last is { } evicted)
        {
            recency.RemoveLast();
            entries.Remove(evicted.Value.Key);
            cachedBytes -= evicted.Value.Data.Length;
        }
    }

    private bool TryTouch(MediaBlockKey key, [NotNullWhen(true)] out byte[]? data)
    {
        lock (gate)
        {
            return TryTouchLocked(key, out data);
        }
    }

    private bool TryTouchLocked(MediaBlockKey key, [NotNullWhen(true)] out byte[]? data)
    {
        if (entries.TryGetValue(key, out var node))
        {
            recency.Remove(node);
            recency.AddFirst(node);
            data = node.Value.Data;
            return true;
        }

        data = null;
        return false;
    }

    private sealed record PendingFetch(
        long Id,
        Task<byte[]> Task,
        CancellationToken CancellationToken);

    private sealed record CacheEntry(MediaBlockKey Key, byte[] Data);

    private sealed record PendingWrite(byte[] Data, TaskCompletionSource Completion);
}
