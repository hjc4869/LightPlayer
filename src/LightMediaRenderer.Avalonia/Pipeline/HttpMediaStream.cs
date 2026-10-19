using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.Logging;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>Creates an HTTP response stream, adding random access when the server supports ranges.</summary>
public static class HttpMediaStream
{
    private const int MaximumClientErrorAttempts = 3;
    private static readonly TimeSpan InitialClientErrorRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumClientErrorRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpRequestOptionsKey<bool> BrowserResponseStreaming =
        new("WebAssemblyEnableStreamingResponse");

    /// <summary>Limits response size on hosts whose HTTP stack does not apply streaming backpressure.</summary>
    public static bool PreferBoundedResponses { get; set; }

    public static Stream Open(HttpClient client, Uri source, CancellationToken cancellationToken)
    {
        return OpenAsync(client, source, cancellationToken).GetAwaiter().GetResult();
    }

    public static async Task<Stream> OpenAsync(
        HttpClient client,
        Uri source,
        CancellationToken cancellationToken,
        Action<HttpResponseMessage>? validateResponse = null,
        bool readAheadEnabled = true)
    {
        int readAheadBytes = readAheadEnabled ? HttpRangeStream.ReadAheadBytes : HttpRangeStream.BufferSize;
        int? responseBytes = PreferBoundedResponses ? readAheadBytes : null;
        HttpResponseMessage response = await SendProbeAsync(client, source, responseBytes, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                if (IsEmptyResponse(response))
                    break;
                validateResponse?.Invoke(response);
                if (!IsClientError(response.StatusCode))
                    break;

                Uri responseUri = response.RequestMessage?.RequestUri ?? source;
                if (attempt >= MaximumClientErrorAttempts)
                {
                    throw new HttpRequestException(
                        $"The HTTP media server at '{responseUri.Host}' returned " +
                        $"{(int)response.StatusCode} ({response.StatusCode}) after {attempt} attempts.",
                        null,
                        response.StatusCode);
                }

                TimeSpan retryDelay = GetClientErrorRetryDelay(response, attempt);
                AppLog.Warning(
                    "Video.Http",
                    $"The initial media request to '{responseUri.Host}' returned " +
                    $"{(int)response.StatusCode} ({response.StatusCode}) on attempt {attempt} of " +
                    $"{MaximumClientErrorAttempts}; retrying in {retryDelay.TotalSeconds:0.###} seconds.");
                response.Dispose();
                response = null!;
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                response = await SendProbeAsync(client, source, responseBytes, cancellationToken).ConfigureAwait(false);
            }

            bool empty = IsEmptyResponse(response);
            if (!empty)
                response.EnsureSuccessStatusCode();
            Uri resolved = response.RequestMessage?.RequestUri ?? source;
            EnsureHttp(resolved);

            if (response.StatusCode == HttpStatusCode.PartialContent || empty)
            {
                ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
                long length = range?.Length ?? -1;
                if (!empty && (length <= 0 || range?.From != 0 ||
                    !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
                    range.To != Math.Min(responseBytes ?? long.MaxValue, length) - 1))
                {
                    throw new InvalidDataException("The HTTP server returned an invalid byte-range probe.");
                }

                Stream content = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                string? entityTag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
                DateTimeOffset? lastModified = response.Content.Headers.LastModified;
                var rangeStream = new HttpRangeStream(
                    client,
                    source,
                    length,
                    entityTag,
                    lastModified,
                    new HttpResponseBodyStream(response, content, cancellationToken),
                    cancellationToken,
                    validateResponse,
                    readAheadBytes,
                    responseBytes);
                response = null!;
                return rangeStream;
            }

            Stream body = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var result = new HttpResponseBodyStream(response, body, cancellationToken);
            response = null!;
            return result;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static async Task<HttpResponseMessage> SendProbeAsync(
        HttpClient client,
        Uri source,
        int? responseBytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(source, new RangeHeaderValue(0, responseBytes - 1L));
        return await WithNetworkTimeoutAsync(
            token => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token),
            cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<TResult> WithNetworkTimeoutAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        using var pendingLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<TResult> pending = operation(pendingLifetime.Token);
        if (pending.IsCompleted)
            return await pending.ConfigureAwait(false);

        using var deadlineLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task deadline = Task.Delay(NetworkTimeout, deadlineLifetime.Token);
        try
        {
            bool expired = await Task.WhenAny(pending, deadline).ConfigureAwait(false) == deadline;
            if (expired)
                pendingLifetime.Cancel();

            TResult result = await pending.ConfigureAwait(false);
            if (expired)
            {
                (result as IDisposable)?.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("The HTTP media server stopped responding.");
            }

            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The HTTP media server stopped responding.", exception);
        }
        finally
        {
            deadlineLifetime.Cancel();
        }
    }

    private static bool IsEmptyResponse(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
        response.Content.Headers.ContentRange is { Unit: "bytes", Length: 0 };

    private static TimeSpan GetClientErrorRetryDelay(HttpResponseMessage response, int attempt)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return ClampRetryDelay(delta);
        if (retryAfter?.Date is { } date)
            return ClampRetryDelay(date - DateTimeOffset.UtcNow);

        long multiplier = 1L << (attempt - 1);
        return TimeSpan.FromTicks(Math.Min(
            InitialClientErrorRetryDelay.Ticks * multiplier,
            MaximumClientErrorRetryDelay.Ticks));
    }

    private static TimeSpan ClampRetryDelay(TimeSpan delay) =>
        delay <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(Math.Min(delay.Ticks, MaximumClientErrorRetryDelay.Ticks));

    private static bool IsClientError(HttpStatusCode statusCode) =>
        (int)statusCode is >= 400 and <= 499;

    internal static HttpRequestMessage CreateRequest(Uri source, RangeHeaderValue? range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Options.Set(BrowserResponseStreaming, true);
        request.Headers.Range = range;
        return request;
    }

    internal static void EnsureHttp(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new NotSupportedException("The media URL redirected outside HTTP or HTTPS.");
        }
    }
}

/// <summary>Continuous HTTP reader with bounded seek history and read-ahead.</summary>
public sealed class HttpRangeStream : Stream
{
    internal const int BufferSize = 64 * 1024;
    internal const int ReadAheadBytes = 32 * 1024 * 1024;
    private const int HistoryBytes = 32 * 1024 * 1024;
    private const int MaximumForwardReadBytes = 1024 * 1024;
    private const int MaximumReconnectAttempts = 5;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient client;
    private readonly Uri source;
    private readonly long length;
    private readonly string? entityTag;
    private readonly DateTimeOffset? lastModified;
    private readonly Action<HttpResponseMessage>? validateResponse;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken lifetimeToken;
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly object bufferSync = new();
    private readonly LinkedList<BufferedChunk> buffers = new();
    private readonly int readAheadBytes;
    private readonly int? responseBytes;
    private TaskCompletionSource changed = NewSignal();
    private CancellationTokenSource? readerLifetime;
    private Task? readerTask;
    private Exception? readerFault;
    private long generation;
    private long bufferedStart;
    private long bufferedEnd;
    private long bufferedBytes;
    private long readPosition;
    private long position;
    private bool disposed;

    internal HttpRangeStream(
        HttpClient client,
        Uri source,
        long length,
        string? entityTag,
        DateTimeOffset? lastModified,
        Stream response,
        CancellationToken cancellationToken,
        Action<HttpResponseMessage>? validateResponse,
        int readAheadBytes,
        int? responseBytes)
    {
        this.client = client;
        this.source = source;
        this.length = length;
        this.entityTag = entityTag;
        this.lastModified = lastModified;
        this.validateResponse = validateResponse;
        this.readAheadBytes = readAheadBytes;
        this.responseBytes = responseBytes;
        lifetime = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        lifetimeToken = lifetime.Token;
        lock (bufferSync)
            StartReaderLocked(0, response);
    }

    public override bool CanRead => !disposed;

    public override bool CanSeek => !disposed;

    public override bool CanWrite => false;

    public long BufferedStartOffset
    {
        get { lock (bufferSync) return bufferedStart; }
    }

    public long BufferedEndOffset
    {
        get { lock (bufferSync) return bufferedEnd; }
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
            lock (bufferSync)
                return position;
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
            return 0;

        EnterReadGate();
        try
        {
            WaitForDataAsync(lifetimeToken).GetAwaiter().GetResult();
            lock (bufferSync)
            {
                return CopyBufferedLocked(buffer);
            }
        }
        finally
        {
            readGate.Release();
        }
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, cancellationToken);
        linked.Token.ThrowIfCancellationRequested();
        if (buffer.IsEmpty)
            return 0;

        await readGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await WaitForDataAsync(linked.Token).ConfigureAwait(false);
            lock (bufferSync)
                return CopyBufferedLocked(buffer.Span);
        }
        finally
        {
            readGate.Release();
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnterReadGate();
        try
        {
            lock (bufferSync)
            {
                long target = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => checked(position + offset),
                    SeekOrigin.End => checked(length + offset),
                    _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown seek origin."),
                };
                if (target < 0)
                    throw new IOException("Cannot seek before the beginning of the HTTP media.");

                position = target;
                return target;
            }
        }
        finally
        {
            readGate.Release();
        }
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException("The HTTP media stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The HTTP media stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (bufferSync)
            {
                if (!disposed)
                {
                    disposed = true;
                    lifetime.Cancel();
                    ClearBuffersLocked();
                    SignalLocked();
                    CancellationTokenSource? activeLifetime = readerLifetime;
                    _ = readerTask?.ContinueWith(
                        _ => activeLifetime?.Dispose(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    lifetime.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        Dispose();
        Task? pending;
        lock (bufferSync)
            pending = readerTask;
        if (pending is not null)
            await pending.ConfigureAwait(false);
        await readGate.WaitAsync().ConfigureAwait(false);
        readGate.Release();
        GC.SuppressFinalize(this);
    }

    private void EnterReadGate()
    {
        lifetimeToken.ThrowIfCancellationRequested();
        readGate.Wait(lifetimeToken);
    }

    private async Task WaitForDataAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (bufferSync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (position >= length)
                    return;
                if (FindBufferedLocked(position) is not null)
                {
                    if (position >= bufferedStart && position < bufferedEnd)
                    {
                        readPosition = position;
                        SignalLocked();
                    }
                    return;
                }

                if (position < bufferedStart || position < bufferedEnd ||
                    position - bufferedEnd > Math.Min(MaximumForwardReadBytes, readAheadBytes))
                    StartReaderLocked(position);

                readPosition = position;
                SignalLocked();
                if (readerFault is { } fault)
                    throw new IOException("The HTTP media stream could not continue.", fault);
                wait = changed.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private int CopyBufferedLocked(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (position >= length)
            return 0;
        if (OperatingSystem.IsBrowser() && destination.Length > BufferSize)
            destination = destination[..BufferSize];

        int copied = 0;
        while (copied < destination.Length && FindBufferedLocked(position) is { } node)
        {
            BufferedChunk chunk = node.Value;
            bool currentReader = position >= bufferedStart && position < bufferedEnd;
            int inside = checked((int)(position - chunk.Offset));
            int count = Math.Min(chunk.Length - inside, destination.Length - copied);
            chunk.Content.AsSpan(inside, count).CopyTo(destination[copied..]);
            position += count;
            copied += count;
            buffers.Remove(node);
            buffers.AddLast(node);
            if (currentReader)
                readPosition = position;
        }

        TrimHistoryLocked();
        SignalLocked();
        return copied;
    }

    private LinkedListNode<BufferedChunk>? FindBufferedLocked(long offset)
    {
        for (var node = buffers.Last; node is not null; node = node.Previous)
        {
            if (offset >= node.Value.Offset && offset - node.Value.Offset < node.Value.Length)
                return node;
        }
        return null;
    }

    private void StartReaderLocked(long offset, Stream? response = null)
    {
        CancellationTokenSource? previousLifetime = readerLifetime;
        Task? previousTask = readerTask;
        previousLifetime?.Cancel();
        readerLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        CancellationToken cancellationToken = readerLifetime.Token;
        long run = ++generation;
        bufferedStart = bufferedEnd = readPosition = offset;
        TrimHistoryLocked();
        readerFault = null;
        var start = NewSignal();
        readerTask = ReceiveAsync(
            run, offset, response, previousTask, previousLifetime, cancellationToken, start.Task);
        start.SetResult();
        SignalLocked();
    }

    private async Task ReceiveAsync(
        long run,
        long offset,
        Stream? response,
        Task? previousTask,
        CancellationTokenSource? previousLifetime,
        CancellationToken cancellationToken,
        Task start)
    {
        await start.ConfigureAwait(false);
        try
        {
            if (previousTask is not null)
                await previousTask.ConfigureAwait(false);
            previousLifetime?.Dispose();
            previousLifetime = null;

            int attempts = 0;
            long responseEnd = ResponseEnd(offset);
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    int count;
                    Task? wait = null;
                    lock (bufferSync)
                    {
                        if (run != generation || disposed)
                            return;
                        long room = readAheadBytes - (bufferedEnd - readPosition);
                        count = (int)Math.Min(BufferSize, length - offset);
                        if (room <= 0 || bufferedBytes + BufferSize > HistoryBytes + readAheadBytes)
                            wait = changed.Task;
                    }

                    if (wait is not null)
                    {
                        await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (response is null)
                    {
                        responseEnd = ResponseEnd(offset);
                        response = await OpenResponseAsync(offset, responseEnd, cancellationToken).ConfigureAwait(false);
                    }

                    count = (int)Math.Min(count, responseEnd - offset);
                    byte[] content = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        int received = await HttpMediaStream.WithNetworkTimeoutAsync(
                            token => response.ReadAtLeastAsync(
                                content.AsMemory(0, count), count, throwOnEndOfStream: false, token).AsTask(),
                            cancellationToken)
                            .ConfigureAwait(false);
                        if (received == 0)
                            throw new IOException($"The HTTP response ended at byte {offset}, before byte {responseEnd}.");

                        lock (bufferSync)
                        {
                            if (run != generation || disposed)
                                return;
                            buffers.AddLast(new BufferedChunk(offset, content, received));
                            bufferedBytes += content.Length;
                            content = null!;
                            offset += received;
                            bufferedEnd = offset;
                            TrimHistoryLocked();
                            SignalLocked();
                        }

                        attempts = 0;
                    }
                    finally
                    {
                        if (content is not null)
                            ArrayPool<byte>.Shared.Return(content);
                    }

                    if (offset == responseEnd)
                    {
                        response.Dispose();
                        response = null;
                    }
                    if (OperatingSystem.IsBrowser())
                        await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsTransient(exception))
                {
                    response?.Dispose();
                    response = null;
                    if (++attempts > MaximumReconnectAttempts)
                        throw new IOException("The HTTP media connection kept failing.", exception);
                    AppLog.Warning("Video.Http", $"Streaming interrupted at byte {offset}; retry={attempts}.", exception);
                    await Task.Delay(ReconnectDelay * attempts, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (bufferSync)
            {
                if (run == generation && !disposed)
                {
                    readerFault = exception;
                    SignalLocked();
                }
            }
        }
        finally
        {
            response?.Dispose();
            previousLifetime?.Dispose();
        }
    }

    private long ResponseEnd(long offset) => offset + Math.Min(responseBytes ?? long.MaxValue, length - offset);

    private async Task<Stream> OpenResponseAsync(long offset, long end, CancellationToken cancellationToken)
    {
        using var request = HttpMediaStream.CreateRequest(
            source,
            new RangeHeaderValue(offset, responseBytes is null ? null : end - 1));
        if (entityTag is not null && EntityTagHeaderValue.TryParse(entityTag, out EntityTagHeaderValue? tag))
            request.Headers.IfRange = new RangeConditionHeaderValue(tag);
        else if (lastModified is { } modified)
            request.Headers.IfRange = new RangeConditionHeaderValue(modified);

        HttpResponseMessage response = await HttpMediaStream.WithNetworkTimeoutAsync(
            token => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token),
            cancellationToken)
            .ConfigureAwait(false);
        try
        {
            validateResponse?.Invoke(response);
            response.EnsureSuccessStatusCode();
            HttpMediaStream.EnsureHttp(response.RequestMessage?.RequestUri ?? source);

            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent ||
                !string.Equals(range?.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
                range?.From != offset || range.To != end - 1 || range.Length != length)
            {
                throw new InvalidDataException($"The HTTP server did not honor byte range {offset}-{end - 1}.");
            }

            if (entityTag is not null && response.Headers.ETag?.ToString() != entityTag ||
                entityTag is null && lastModified is { } modified &&
                response.Content.Headers.LastModified is { } currentModified && modified != currentModified)
            {
                throw new InvalidDataException("The HTTP media changed while it was being read.");
            }

            Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseBodyStream(response, body, cancellationToken);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        InvalidDataException => false,
        HttpRequestException http => http.StatusCode is null || (int)http.StatusCode >= 500 ||
            http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        IOException or TimeoutException or TaskCanceledException => true,
        _ => false,
    };

    private void TrimHistoryLocked()
    {
        var node = buffers.First;
        while (node is not null && bufferedBytes + BufferSize > HistoryBytes + readAheadBytes)
        {
            var next = node.Next;
            BufferedChunk chunk = node.Value;
            long end = chunk.Offset + chunk.Length;
            bool requested = position >= chunk.Offset && position < end;
            bool upcoming = end > readPosition && chunk.Offset < bufferedEnd && end > bufferedStart;
            if (!requested && !upcoming)
            {
                buffers.Remove(node);
                bufferedBytes -= chunk.Content.Length;
                ArrayPool<byte>.Shared.Return(chunk.Content);
                if (end > bufferedStart && chunk.Offset < bufferedEnd)
                    bufferedStart = Math.Min(end, bufferedEnd);
            }
            node = next;
        }
    }

    private void ClearBuffersLocked()
    {
        foreach (BufferedChunk chunk in buffers)
            ArrayPool<byte>.Shared.Return(chunk.Content);
        buffers.Clear();
        bufferedBytes = 0;
    }

    private void SignalLocked()
    {
        TaskCompletionSource previous = changed;
        changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record BufferedChunk(long Offset, byte[] Content, int Length);
}

/// <summary>Owns an HTTP response whose body can only be read forward.</summary>
internal sealed class HttpResponseBodyStream : Stream
{
    private readonly HttpResponseMessage response;
    private readonly Stream body;
    private readonly CancellationTokenSource lifetime;
    private long position;
    private bool disposed;

    public HttpResponseBodyStream(
        HttpResponseMessage response,
        Stream body,
        CancellationToken cancellationToken)
    {
        this.response = response;
        this.body = body;
        lifetime = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
    }

    public override bool CanRead => !disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => Interlocked.Read(ref position);
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ObjectDisposedException.ThrowIf(disposed, this);
        int read = body
            .ReadAsync(buffer.AsMemory(offset, count), lifetime.Token)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        Interlocked.Add(ref position, read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int read = Read(rented, 0, buffer.Length);
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        int read = await body.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        Interlocked.Add(ref position, read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            if (disposing)
            {
                lifetime.Cancel();
                body.Dispose();
                response.Dispose();
                lifetime.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}