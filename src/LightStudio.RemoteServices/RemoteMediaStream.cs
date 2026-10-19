using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.Logging;

namespace LightStudio.RemoteServices;

/// <summary>
/// Seekable, read-only <see cref="Stream"/> over the remote <c>media/file</c> endpoint.
/// </summary>
/// <remarks>
/// <para>
/// The stream never materializes the whole file: reads are served from
/// <see cref="RemoteMediaBlockCache"/>, which keeps aligned blocks in the head's block store. That
/// bounds memory usage for large lossless files and makes seeking cheap once a region has been
/// visited, even across sessions.
/// </para>
/// <para>
/// With read-ahead enabled, a cache miss starts one forward request naming every uncached block
/// from the missed one onward, so cached regions are skipped within the same response. The response
/// is consumed block by block, at most a bounded window ahead of the reader; a miss that the request
/// will not reach soon replaces it. Browser fetch keeps receiving a response that is not being read,
/// so browsers request one window at a time. Without read-ahead, a miss fetches only the missing block.
/// </para>
/// <para>
/// Browser reads await cache misses; synchronous browser reads require an already cached block.
/// </para>
/// <para>The instance is safe to use from multiple threads; reads and seeks are serialized.</para>
/// </remarks>
internal sealed class RemoteMediaStream : Stream
{
    /// <summary>Largest window the forward request runs ahead of the reader.</summary>
    private const long MaximumReadAheadBytes = 32L * 1024 * 1024;

    /// <summary>
    /// No stream may reserve more than this fraction of the persistent block-store budget, so the
    /// back of its read-ahead window cannot evict unread blocks at the front.
    /// </summary>
    private const int ReadAheadCacheBudgetDivisor = 2;

    /// <summary>Furthest a miss may lie ahead of the forward request before a new request starts at it.</summary>
    private const long MaximumForwardWaitBytes = 1024 * 1024;

    /// <summary>Most ranges named by one request; later gaps are requested after it completes.</summary>
    private const int MaximumRequestRanges = 16;

    /// <summary>Longest wait for response headers or the next block while the reader has room.</summary>
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(15);

    private readonly IRemoteMediaRangeClient client;
    private readonly RemoteMediaBlockCache cache;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object disposalSync = new();
    private readonly object forwardSync = new();
    private readonly CancellationTokenSource lifetime;
    private readonly RemoteBlockFetch fetch;
    private readonly string resourcePath;
    private readonly string resourceId;
    private readonly string? entityTag;
    private readonly long length;
    private readonly bool readAheadEnabled;
    private long position;
    private bool disposed;
    private Task? disposal;

    // Forward request state, guarded by forwardSync.
    private TaskCompletionSource changed = NewSignal();
    private CancellationTokenSource? forwardLifetime;
    private Task forwardTask = Task.CompletedTask;
    private long forwardGeneration;
    private bool forwardRunning;
    private Exception? forwardFault;
    private long forwardOffset;
    private IReadOnlyList<RemoteByteRange>? forwardRanges;
    private long windowOffset;

    private RemoteMediaStream(
        IRemoteMediaRangeClient client,
        RemoteMediaBlockCache cache,
        string resourcePath,
        string resourceId,
        string? entityTag,
        long length,
        CancellationToken cancellationToken,
        bool readAheadEnabled)
    {
        this.client = client;
        this.cache = cache;
        this.resourcePath = resourcePath;
        this.resourceId = resourceId;
        this.entityTag = entityTag;
        this.length = length;
        this.readAheadEnabled = readAheadEnabled;
        lifetime = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        fetch = FetchBlockAsync;
    }

    /// <summary>
    /// Probes the remote file and opens a range-backed stream or caches the whole file before opening it.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The endpoint did not report a length or does not serve byte ranges; the caller should fall
    /// back to downloading the whole file.
    /// </exception>
    public static async Task<Stream> CreateAsync(
        IRemoteMediaRangeClient client,
        RemoteMediaBlockCache cache,
        string mediaPath,
        CancellationToken cancellationToken = default,
        string? cacheIdentity = null,
        bool readAheadEnabled = true,
        bool downloadWholeFile = false)
    {
        var info = await RemoteRetryPolicy.ExecuteAsync(
            token => client.GetResourceInfoAsync(mediaPath, token),
            cancellationToken).ConfigureAwait(false);

        if (info.Length < 0 || !info.SupportsRangeRequests)
        {
            throw new NotSupportedException(
                $"The remote endpoint does not serve byte ranges for '{mediaPath}'.");
        }

        // The strongest available revision signal keeps blocks from different file versions apart.
        // The hash doubles as the block file name in browser storage.
        var identity = Encoding.UTF8.GetBytes(
            $"{cacheIdentity ?? mediaPath}\n{info.EntityTag ?? info.CacheRevision ?? info.Length.ToString()}");
        var resourceId = Convert.ToHexString(SHA256.HashData(identity))[..32];
        if (downloadWholeFile)
        {
            if (info.Length == 0)
            {
                return new MemoryStream([], writable: false);
            }

            var bytes = await cache.GetBlockAsync(
                new MediaBlockKey(resourceId, -1),
                0,
                checked((int)info.Length),
                (offset, count, token) => client.GetRangeAsync(mediaPath, offset, count, info.EntityTag, token),
                cancellationToken).ConfigureAwait(false);
            return new MemoryStream(bytes, writable: false);
        }

        return new RemoteMediaStream(
            client,
            cache,
            mediaPath,
            resourceId,
            info.EntityTag,
            info.Length,
            cancellationToken,
            readAheadEnabled);
    }

    public override bool CanRead => !disposed;

    public override bool CanSeek => !disposed;

    public override bool CanWrite => false;

    internal long BufferedStartOffset
    {
        get
        {
            var current = Math.Clamp(Interlocked.Read(ref position), 0, length);
            return current >= length ? length : BlockStart(current);
        }
    }

    internal long BufferedEndOffset
    {
        get
        {
            var current = Math.Clamp(Interlocked.Read(ref position), 0, length);
            if (current >= length)
            {
                return length;
            }

            var windowStart = BlockStart(current);
            lock (forwardSync)
            {
                return forwardRunning
                    && forwardOffset >= windowStart
                    && forwardOffset <= ReadAheadWindowEnd(windowStart)
                    ? Math.Max(current, forwardOffset)
                    : current;
            }
        }
    }

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return length;
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return Interlocked.Read(ref position);
        }

        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        EnterGate();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var remaining = (int)Math.Min(buffer.Length, Math.Max(0, length - position));
            if (OperatingSystem.IsBrowser())
                remaining = Math.Min(remaining, RemoteMediaBlockCache.BlockSize - (int)(position % RemoteMediaBlockCache.BlockSize));
            var copied = 0;
            while (copied < remaining)
            {
                var offset = position + copied;
                var block = readAheadEnabled
                    ? GetForwardBlockAsync(offset, lifetime.Token).GetAwaiter().GetResult()
                    : cache.GetBlock(
                        KeyFor(offset),
                        BlockStart(offset),
                        BlockLength(offset),
                        fetch,
                        lifetime.Token);
                copied += CopyFromBlock(block, offset, buffer[copied..remaining]);
            }

            position += copied;
            MoveWindow(position);
            return copied;
        }
        finally
        {
            gate.Release();
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            cancellationToken);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var remaining = (int)Math.Min(buffer.Length, Math.Max(0, length - position));
            if (OperatingSystem.IsBrowser())
                remaining = Math.Min(remaining, 64 * 1024);
            var copied = 0;
            while (copied < remaining)
            {
                var offset = position + copied;
                var block = readAheadEnabled
                    ? await GetForwardBlockAsync(offset, linked.Token).ConfigureAwait(false)
                    : await cache.GetBlockAsync(
                        KeyFor(offset),
                        BlockStart(offset),
                        BlockLength(offset),
                        fetch,
                        linked.Token).ConfigureAwait(false);
                copied += CopyFromBlock(block, offset, buffer.Span[copied..remaining]);
            }

            position += copied;
            MoveWindow(position);
            return copied;
        }
        finally
        {
            gate.Release();
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        EnterGate();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown seek origin."),
            };

            if (target < 0)
            {
                throw new IOException("Cannot seek before the beginning of the media file.");
            }

            position = target;
            MoveWindow(Math.Min(target, length));
            return target;
        }
        finally
        {
            gate.Release();
        }
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException("The remote media stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The remote media stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _ = ObserveDisposalAsync();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        lock (disposalSync)
            return new ValueTask(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        disposed = true;
        lifetime.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Task forward;
            lock (forwardSync)
            {
                forward = forwardTask;
            }

            await forward.ConfigureAwait(false);
            forwardLifetime?.Dispose();
            lifetime.Dispose();
        }
        finally
        {
            gate.Release();
        }
        GC.SuppressFinalize(this);
    }

    private async Task ObserveDisposalAsync()
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            AppLog.Warning("Remote.Media", "Could not close the remote media stream.", exception);
        }
    }

    /// <summary>Copies the part of <paramref name="block"/> that starts at <paramref name="offset"/>.</summary>
    private static int CopyFromBlock(byte[] block, long offset, Span<byte> destination)    {
        var insideBlock = (int)(offset - BlockStart(offset));
        var available = Math.Min(block.Length - insideBlock, destination.Length);
        block.AsSpan(insideBlock, available).CopyTo(destination);
        return available;
    }

    private static long BlockStart(long offset) =>
        offset / RemoteMediaBlockCache.BlockSize * RemoteMediaBlockCache.BlockSize;

    /// <summary>Length of the block containing <paramref name="offset"/>, clamped at end of file.</summary>
    private int BlockLength(long offset) =>
        (int)Math.Min(RemoteMediaBlockCache.BlockSize, length - BlockStart(offset));

    private MediaBlockKey KeyFor(long offset) =>
        new(resourceId, offset / RemoteMediaBlockCache.BlockSize);

    /// <summary>
    /// Returns the block containing <paramref name="offset"/> from the cache, or from the forward
    /// request, which is started or replaced when it would not deliver the block soon.
    /// </summary>
    private async Task<byte[]> GetForwardBlockAsync(long offset, CancellationToken cancellationToken)
    {
        var blockStart = BlockStart(offset);
        var key = KeyFor(blockStart);
        var count = BlockLength(blockStart);
        MoveWindow(blockStart);
        long awaitedGeneration = -1;
        while (true)
        {
            long observedGeneration;
            long observedOffset;
            lock (forwardSync)
            {
                observedGeneration = forwardGeneration;
                observedOffset = forwardOffset;
            }

            if (await cache.TryGetBlockAsync(key, count).ConfigureAwait(false) is { } block)
            {
                return block;
            }

            Task wait;
            lock (forwardSync)
            {
                if (awaitedGeneration == forwardGeneration && forwardFault is { } fault)
                {
                    forwardFault = null;
                    throw new IOException("The remote media request could not continue.", fault);
                }

                // The forward request may have delivered the block after the lookup above.
                if (observedGeneration == forwardGeneration
                    && observedOffset <= blockStart
                    && forwardOffset > blockStart)
                {
                    continue;
                }

                if (!WillDeliverLocked(blockStart))
                {
                    StartForwardLocked(blockStart);
                }

                awaitedGeneration = forwardGeneration;
                wait = changed.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool WillDeliverLocked(long blockStart)
    {
        if (!forwardRunning
            || blockStart < forwardOffset
            || blockStart - forwardOffset > MaximumForwardWaitBytes)
        {
            return false;
        }

        // Between requests, the next one starts at forwardOffset and names every uncached block.
        if (forwardRanges is null)
        {
            return true;
        }

        foreach (var range in forwardRanges)
        {
            if (blockStart >= range.Start && blockStart < range.End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Replaces the forward request with one starting at <paramref name="offset"/>.</summary>
    private void StartForwardLocked(long offset)
    {
        var previousTask = forwardTask;
        var previousLifetime = forwardLifetime;
        previousLifetime?.Cancel();
        forwardLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var generation = ++forwardGeneration;
        forwardRunning = true;
        forwardFault = null;
        forwardOffset = offset;
        forwardRanges = null;

        // The request starts after this lock is released.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        forwardTask = RunForwardAsync(
            generation,
            offset,
            previousTask,
            previousLifetime,
            forwardLifetime.Token,
            start.Task);
        start.SetResult();
        SignalLocked();
    }

    private async Task RunForwardAsync(
        long generation,
        long offset,
        Task previousTask,
        CancellationTokenSource? previousLifetime,
        CancellationToken cancellationToken,
        Task start)
    {
        await start.ConfigureAwait(false);
        try
        {
            // Only one request runs at a time: the replaced one finishes cancelling first.
            await previousTask.ConfigureAwait(false);
            previousLifetime?.Dispose();
            previousLifetime = null;

            await ForwardAsync(generation, offset, cancellationToken).ConfigureAwait(false);
            lock (forwardSync)
            {
                if (generation == forwardGeneration)
                {
                    forwardRunning = false;
                    forwardRanges = null;
                    SignalLocked();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Warning("Remote.Media", $"The media request starting at byte {offset} failed.", exception);
            lock (forwardSync)
            {
                if (generation == forwardGeneration)
                {
                    forwardRunning = false;
                    forwardRanges = null;
                    forwardFault = exception;
                    SignalLocked();
                }
            }
        }
        finally
        {
            previousLifetime?.Dispose();
        }
    }

    /// <summary>
    /// Requests uncached blocks from <paramref name="start"/> to the end of the file, reconnecting
    /// after transient failures at the first block not yet received.
    /// </summary>
    private async Task ForwardAsync(long generation, long start, CancellationToken cancellationToken)
    {
        var bounded = OperatingSystem.IsBrowser();
        var next = start;
        var failures = 0;
        while (next < length)
        {
            // A bounded response is received whole, so each one waits until half a window is free.
            var windowEnd = await WaitForRoomAsync(
                next,
                bounded ? ReadAheadBytes() / 2 : 1,
                cancellationToken).ConfigureAwait(false);
            var end = bounded ? windowEnd : length;

            // The block that started this request is unreadable from the cache even if the store lists it.
            var ranges = await PlanAsync(next, end, start, cancellationToken).ConfigureAwait(false);
            if (ranges.Count == 0)
            {
                next = end;
                Publish(generation, next, null);
                continue;
            }

            Publish(generation, ranges[0].Start, ranges);
            try
            {
                next = await ReceiveAsync(generation, ranges, start, cancellationToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                && RemoteRetryPolicy.IsTransient(exception))
            {
                var resume = DeliveredOffset(generation, ranges[0].Start);
                failures = resume > ranges[0].Start ? 1 : failures + 1;
                if (failures >= RemoteRetryPolicy.MaxAttempts)
                {
                    throw;
                }

                AppLog.Warning(
                    "Remote.Media",
                    $"The media request was interrupted at byte {resume}; reconnecting (attempt {failures + 1}).",
                    exception);
                await RemoteRetryPolicy.DelayBeforeRetryAsync(failures, cancellationToken).ConfigureAwait(false);
                next = resume;
            }

            Publish(generation, next, null);
        }
    }

    /// <summary>Lists runs of uncached blocks in <c>[start, end)</c>, up to the per-request range limit.</summary>
    private async Task<List<RemoteByteRange>> PlanAsync(
        long start,
        long end,
        long demanded,
        CancellationToken cancellationToken)
    {
        var ranges = new List<RemoteByteRange>();
        long runStart = -1;
        for (var offset = start; offset < end; offset += RemoteMediaBlockCache.BlockSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset == demanded || !await cache.ContainsAsync(KeyFor(offset)).ConfigureAwait(false))
            {
                if (runStart < 0)
                {
                    runStart = offset;
                }
            }
            else if (runStart >= 0)
            {
                ranges.Add(new RemoteByteRange(runStart, offset));
                runStart = -1;
                if (ranges.Count == MaximumRequestRanges)
                {
                    return ranges;
                }
            }
        }

        if (runStart >= 0)
        {
            ranges.Add(new RemoteByteRange(runStart, end));
        }

        return ranges;
    }

    /// <summary>
    /// Receives one response, adding each complete block to the cache no further than the read-ahead
    /// window ahead of the reader.
    /// </summary>
    /// <returns>The end of the last requested range.</returns>
    private async Task<long> ReceiveAsync(
        long generation,
        IReadOnlyList<RemoteByteRange> ranges,
        long demanded,
        CancellationToken cancellationToken)
    {
        // Armed only while network data is awaited; waiting for the reader to make room never expires.
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = watchdog.Token;
        var demandedReceived = false;
        try
        {
            watchdog.CancelAfter(NetworkTimeout);
            using var response = await client
                .OpenRangesAsync(resourcePath, ranges, length, entityTag, token)
                .ConfigureAwait(false);
            while (true)
            {
                watchdog.CancelAfter(NetworkTimeout);
                if (!await response.MoveNextPartAsync(token).ConfigureAwait(false))
                {
                    break;
                }

                watchdog.CancelAfter(Timeout.InfiniteTimeSpan);
                Advance(generation, response.PartStart);
                var offset = response.PartStart;
                while (offset < response.PartEnd)
                {
                    var blockStart = BlockStart(offset);
                    var blockEnd = blockStart + BlockLength(blockStart);
                    if (offset != blockStart || response.PartEnd < blockEnd)
                    {
                        // A part that does not cover a whole block cannot complete it.
                        var skipped = Math.Min(blockEnd, response.PartEnd) - offset;
                        watchdog.CancelAfter(NetworkTimeout);
                        await response.SkipAsync(skipped, token).ConfigureAwait(false);
                        watchdog.CancelAfter(Timeout.InfiniteTimeSpan);
                        offset += skipped;
                        continue;
                    }

                    await WaitForRoomAsync(blockStart, 1, cancellationToken).ConfigureAwait(false);
                    var key = KeyFor(blockStart);
                    var keep = blockStart == demanded || !await cache.ContainsAsync(key).ConfigureAwait(false);
                    var persisted = Task.CompletedTask;
                    watchdog.CancelAfter(NetworkTimeout);
                    if (keep)
                    {
                        var block = new byte[blockEnd - blockStart];
                        await response.ReadExactlyAsync(block, token).ConfigureAwait(false);
                        persisted = cache.AddAsync(key, block);
                        demandedReceived |= blockStart == demanded;
                    }
                    else
                    {
                        await response.SkipAsync(blockEnd - blockStart, token).ConfigureAwait(false);
                    }

                    watchdog.CancelAfter(Timeout.InfiniteTimeSpan);
                    offset = blockEnd;
                    Advance(generation, offset);
                    await persisted.ConfigureAwait(false);
                    if (OperatingSystem.IsBrowser())
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested && watchdog.IsCancellationRequested)
        {
            throw new TimeoutException("The remote media server stopped responding.", exception);
        }

        // Requesting the block again would only repeat the same response.
        if (ranges[0].Start == demanded && !demandedReceived)
        {
            throw new InvalidDataException($"The server did not return the requested bytes at offset {demanded}.");
        }

        return ranges[^1].End;
    }

    /// <summary>Waits until the read-ahead window has <paramref name="room"/> bytes past <paramref name="offset"/>.</summary>
    /// <returns>The end of the read-ahead window.</returns>
    private async Task<long> WaitForRoomAsync(long offset, long room, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (forwardSync)
            {
                var windowEnd = ReadAheadWindowEnd(BlockStart(windowOffset));
                if (windowEnd - offset >= Math.Min(room, length - offset))
                {
                    return windowEnd;
                }

                wait = changed.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Moves the read-ahead window to the block containing <paramref name="offset"/>.</summary>
    private void MoveWindow(long offset)
    {
        lock (forwardSync)
        {
            var moved = BlockStart(offset) != BlockStart(windowOffset);
            windowOffset = offset;
            if (moved && forwardRunning)
            {
                SignalLocked();
            }
        }
    }

    private void Publish(long generation, long offset, IReadOnlyList<RemoteByteRange>? ranges)
    {
        lock (forwardSync)
        {
            if (generation == forwardGeneration)
            {
                forwardOffset = offset;
                forwardRanges = ranges;
                SignalLocked();
            }
        }
    }

    private void Advance(long generation, long offset)
    {
        lock (forwardSync)
        {
            if (generation == forwardGeneration && offset > forwardOffset)
            {
                forwardOffset = offset;
                SignalLocked();
            }
        }
    }

    private long DeliveredOffset(long generation, long fallback)
    {
        lock (forwardSync)
        {
            return generation == forwardGeneration ? forwardOffset : fallback;
        }
    }

    private void SignalLocked()
    {
        var previous = changed;
        changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long ReadAheadBytes() =>
        Math.Min(
            MaximumReadAheadBytes,
            Math.Max(RemoteMediaBlockCache.BlockSize, cache.MaxBytes / ReadAheadCacheBudgetDivisor));

    private long ReadAheadWindowEnd(long windowStart) =>
        windowStart + Math.Min(ReadAheadBytes(), length - windowStart);

    private Task<byte[]> FetchBlockAsync(long offset, int count, CancellationToken cancellationToken) =>
        client.GetRangeAsync(resourcePath, offset, count, entityTag, cancellationToken);

    private void EnterGate()
    {
        gate.Wait();
    }
}
