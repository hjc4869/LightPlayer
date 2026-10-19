using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Remote;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.RemoteServices;

/// <summary>
/// Metadata of a remote resource, as reported by a one-byte range probe.
/// </summary>
/// <param name="Length">Total length in bytes, or -1 when the server did not report one.</param>
/// <param name="EntityTag">Entity tag identifying the current revision, when available.</param>
/// <param name="SupportsRangeRequests">Whether the endpoint advertises <c>Accept-Ranges: bytes</c>.</param>
/// <param name="CacheRevision">
/// Best-effort revision metadata for cache separation when a strict entity tag is unavailable.
/// </param>
public readonly record struct RemoteResourceInfo(
    long Length,
    string? EntityTag,
    bool SupportsRangeRequests,
    string? CacheRevision = null);

internal interface IRemoteMediaRangeClient
{
    Task<RemoteResourceInfo> GetResourceInfoAsync(
        string resourceId,
        CancellationToken cancellationToken = default);

    Task<byte[]> GetRangeAsync(
        string resourceId,
        long offset,
        int count,
        string? entityTag = null,
        CancellationToken cancellationToken = default);

    /// <summary>Requests ascending <paramref name="ranges"/> in one response that is read as it arrives.</summary>
    Task<RemoteRangeResponse> OpenRangesAsync(
        string resourceId,
        IReadOnlyList<RemoteByteRange> ranges,
        long length,
        string? entityTag,
        CancellationToken cancellationToken);
}

/// <summary>
/// Typed HTTP client for the Light Player remote API.
/// </summary>
/// <remarks>
/// The browser head cannot reach an arbitrary origin directly (CORS), so it points the client at
/// its own origin and names the real server in <see cref="ForwardTargetHeader"/>, which the hosting
/// proxy forwards to. Installed heads do the same when an API proxy is configured; otherwise they
/// leave <c>forwardTarget</c> null and call the server directly.
/// </remarks>
public sealed class RemoteApiClient : IDisposable, IRemoteMediaRangeClient
{
    /// <summary>Header naming the server a same-origin proxy should forward the request to.</summary>
    public const string ForwardTargetHeader = "X-LightPlayer-Forward-To";

    public const string ForwardedResponseHeader = "X-LightPlayer-Forwarded";

    private static readonly HttpRequestOptionsKey<bool> BrowserResponseStreaming =
        new("WebAssemblyEnableStreamingResponse");

    private readonly HttpClient httpClient;
    private readonly HttpClient streamingClient;
    private readonly RemoteRangeRequests rangeRequests = new();
    private readonly string? token;
    private readonly string? forwardTarget;
    private readonly Uri baseAddress;

    public RemoteApiClient(string apiBaseAddress, string? token = null, string? forwardTarget = null)
    {
        baseAddress = new Uri(apiBaseAddress.TrimEnd('/') + "/", UriKind.Absolute);
        this.token = token;
        this.forwardTarget = forwardTarget;
        httpClient = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(90),
        };
        streamingClient = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        foreach (HttpClient current in new[] { httpClient, streamingClient })
        {
            if (!string.IsNullOrWhiteSpace(forwardTarget))
            {
                current.DefaultRequestHeaders.TryAddWithoutValidation(ForwardTargetHeader, forwardTarget);
            }

            if (!string.IsNullOrEmpty(token))
            {
                current.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
    }

    /// <summary>The token to put in a packet socket's opening message. Sockets carry no headers.</summary>
    public string? Token => token;

    public bool UsesForwardTarget => !string.IsNullOrWhiteSpace(forwardTarget);

    public async Task<(RemoteServerInfo Info, bool ForwardingConfirmed)> ProbeServerAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await GetWithRetryAsync(RemoteApiDefaults.ServerInfoPath, cancellationToken)
            .ConfigureAwait(false);
        var info = await ReadResponseAsync<RemoteServerInfo>(response, cancellationToken).ConfigureAwait(false);
        return (info, response.Headers.Contains(ForwardedResponseHeader));
    }

    /// <summary>
    /// Connects a packet socket. The caller owns it; closing it is what stops the server demuxing.
    /// </summary>
    public async Task<WebSocket> OpenPacketSocketAsync(CancellationToken cancellationToken = default)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(PacketStreamProtocol.SubProtocol);
        try
        {
            await socket.ConnectAsync(BuildSocketUri(), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private Uri BuildSocketUri()
    {
        var builder = new UriBuilder(new Uri(baseAddress, RemoteStreamDefaults.SocketPath))
        {
            Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };

        if (!string.IsNullOrWhiteSpace(forwardTarget))
        {
            builder.Query =
                $"{RemoteStreamDefaults.ForwardTargetQuery}={Uri.EscapeDataString(forwardTarget)}";
        }

        return builder.Uri;
    }

    public async Task<Stream> OpenPacketStreamAsync(
        RemotePacketStreamRequest request,
        CancellationToken cancellationToken = default)
    {
        using var message = CreateJsonRequest(HttpMethod.Post, RemoteStreamDefaults.PacketsPath, request);
        message.Options.Set(BrowserResponseStreaming, true);
        var response = await streamingClient
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new ResponseBodyStream(response, content);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Raised when the server rejected the session token. Nothing inside the app can recover from
    /// that, so the host returns the user to the server picker to sign in again.
    /// </summary>
    public event EventHandler? Unauthorized;

    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        using var response = await GetWithRetryAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<TRequest, TResponse>(HttpMethod.Post, path, request, cancellationToken);

    public async Task<TResponse> PostAsync<TResponse>(
        string path,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync(path, content: null, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<TResponse> PutAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<TRequest, TResponse>(HttpMethod.Put, path, request, cancellationToken);

    public Task<TResponse> PatchAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<TRequest, TResponse>(HttpMethod.Patch, path, request, cancellationToken);

    public Task<TResponse> DeleteAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<TRequest, TResponse>(HttpMethod.Delete, path, request, cancellationToken);

    public async Task SendAsync<TRequest>(
        HttpMethod method,
        string path,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        using var message = CreateJsonRequest(method, path, request);
        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(method, path);
        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(method, path);
        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResponse> SendTextAsync<TResponse>(
        HttpMethod method,
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/plain"),
        };
        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendTextAsync(
        HttpMethod method,
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/plain"),
        };
        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> GetBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await GetWithRetryAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a resource into native memory. Returns null when the body is empty.</summary>
    public async Task<ArtworkBuffer?> GetArtworkBufferAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await GetWithRetryAsync(path, cancellationToken, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        // A length is only usable when it describes the bytes the stream returns, not an encoded body.
        if (response.Content.Headers.ContentLength is not { } length || response.Content.Headers.ContentEncoding.Count > 0)
        {
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return content.Length > 0 ? ArtworkBuffer.Copy(content) : null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ArtworkBuffer.ReadAsync(stream, length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the length, entity tag and range capability of a resource with a one-byte range probe.
    /// </summary>
    public async Task<RemoteResourceInfo> GetResourceInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Headers = { Range = new RangeHeaderValue(0, 0) },
        };
        using var response = await httpClient
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var contentRange = response.Content.Headers.ContentRange;
        var supportsRanges = response.StatusCode == HttpStatusCode.PartialContent
            && contentRange is not null
            && string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            && contentRange.From == 0
            && contentRange.To == 0
            && contentRange.Length > 0;
        if (supportsRanges)
        {
            var probe = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (probe.Length != 1)
            {
                throw new IOException($"The remote API returned {probe.Length} bytes for a one-byte range probe.");
            }
        }

        return new RemoteResourceInfo(
            contentRange?.Length ?? response.Content.Headers.ContentLength ?? -1,
            response.Headers.ETag?.ToString(),
            supportsRanges);
    }

    /// <summary>
    /// Downloads <paramref name="count"/> bytes starting at <paramref name="offset"/>.
    /// </summary>
    /// <param name="entityTag">
    /// Entity tag the caller expects. A mismatch means the file was replaced mid-read and the
    /// received bytes cannot be combined with previously read ones.
    /// </param>
    public async Task<byte[]> GetRangeAsync(
        string path,
        long offset,
        int count,
        string? entityTag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        using var message = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Headers = { Range = new RangeHeaderValue(offset, offset + count - 1) },
        };
        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new InvalidDataException($"The remote API ignored the byte range request for '{path}'.");
        }

        if (entityTag is { Length: > 0 }
            && response.Headers.ETag?.ToString() is { Length: > 0 } received
            && !string.Equals(received, entityTag, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The remote file '{path}' changed while it was being read.");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    Task<RemoteResourceInfo> IRemoteMediaRangeClient.GetResourceInfoAsync(
        string resourceId,
        CancellationToken cancellationToken) =>
        GetResourceInfoAsync(MediaFilePath(resourceId), cancellationToken);

    Task<byte[]> IRemoteMediaRangeClient.GetRangeAsync(
        string resourceId,
        long offset,
        int count,
        string? entityTag,
        CancellationToken cancellationToken) =>
        GetRangeAsync(MediaFilePath(resourceId), offset, count, entityTag, cancellationToken);

    Task<RemoteRangeResponse> IRemoteMediaRangeClient.OpenRangesAsync(
        string resourceId,
        IReadOnlyList<RemoteByteRange> ranges,
        long length,
        string? entityTag,
        CancellationToken cancellationToken) =>
        rangeRequests.OpenAsync(
            ranges,
            length,
            (range, token) => SendRangesAsync(MediaFilePath(resourceId), range, entityTag, token),
            cancellationToken);

    private async Task<HttpResponseMessage> SendRangesAsync(
        string path,
        RangeHeaderValue range,
        string? entityTag,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Headers = { Range = range },
        };
        message.Options.Set(BrowserResponseStreaming, true);
        var response = await streamingClient
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            if (entityTag is { Length: > 0 }
                && response.Headers.ETag?.ToString() is { Length: > 0 } received
                && !string.Equals(received, entityTag, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The remote file '{path}' changed while it was being read.");
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(
        string path,
        CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            return await httpClient.GetAsync(path, completion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && RemoteRetryPolicy.IsTransient(exception))
        {
            return await httpClient.GetAsync(path, completion, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var message = CreateJsonRequest(method, path, request);
        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateJsonRequest<TRequest>(HttpMethod method, string path, TRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonTypeInfo<TRequest>());
        return new HttpRequestMessage(method, path)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new("application/json") },
            },
        };
    }

    private async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var value = await JsonSerializer.DeserializeAsync(stream, JsonTypeInfo<T>(), cancellationToken).ConfigureAwait(false);
            return value!;
        }
        catch (JsonException exception)
        {
            // A body this client cannot read is a failed request (a captive portal, a proxy error
            // page, another server on the address), so report it like any other transport failure
            // instead of surfacing a parser error callers do not handle.
            throw new HttpRequestException(
                $"Remote API returned an unreadable response for {response.RequestMessage?.RequestUri}.",
                exception,
                response.StatusCode);
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // The session token expired or the server's credentials changed.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            Unauthorized?.Invoke(this, EventArgs.Empty);
        }

        string? detail = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            detail = (await JsonSerializer.DeserializeAsync(
                stream,
                RemoteApiJsonContext.Default.RemoteProblem,
                cancellationToken).ConfigureAwait(false))?.Detail;
        }
        catch (JsonException)
        {
        }

        throw new HttpRequestException(
            detail ?? $"Remote API returned {(int)response.StatusCode} {response.ReasonPhrase}.",
            null,
            response.StatusCode);
    }

    private static JsonTypeInfo<T> JsonTypeInfo<T>() =>
        (JsonTypeInfo<T>?)RemoteApiJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new InvalidOperationException($"No generated JSON metadata exists for {typeof(T)}.");

    private static string MediaFilePath(string resourceId) =>
        $"media/file?path={Uri.EscapeDataString(resourceId)}";

    private sealed class ResponseBodyStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        httpClient.Dispose();
        streamingClient.Dispose();
    }
}
