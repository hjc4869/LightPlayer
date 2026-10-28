using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Remote;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Server;

/// <summary>Hosts the browser client and relays its same-origin API calls to a selected server.</summary>
internal sealed class RemoteApiProxyServer : IDisposable
{
    public const string ForwardTargetHeader = "X-LightPlayer-Forward-To";
    public const string ForwardedResponseHeader = "X-LightPlayer-Forwarded";

    private const string KeepAliveWindow = "timeout=10";
    private const int MaximumProbeResponseBytes = 16 * 1024;

    private static readonly TimeSpan ForwardTargetProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ForwardTargetWhitelistLifetime = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
    };

    private readonly string? staticFilesPath;
    private readonly HttpListener listener = new();
    private readonly HttpClient httpClient;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object requestTasksLock = new();
    private readonly HashSet<Task> requestTasks = [];
    private readonly object targetValidationLock = new();
    private readonly Dictionary<ForwardTargetKey, DateTimeOffset> targetWhitelist = [];
    private readonly Dictionary<ForwardTargetKey, Task> targetValidationTasks = [];
    private Task? listenTask;
    private long nextRequestId;
    private bool disposed;

    public RemoteApiProxyServer(string listenerPrefix, string? staticFilesPath)
    {
        this.staticFilesPath = staticFilesPath is null ? null : Path.GetFullPath(staticFilesPath);
        listener.Prefixes.Add(listenerPrefix);
        listener.IgnoreWriteExceptions = false;
        httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
            UseCookies = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (listener.IsListening)
        {
            return;
        }

        listener.Start();
        AppLog.Information(
            "RemoteApi.Proxy",
            $"Listening on {string.Join(", ", listener.Prefixes)}; staticFiles={staticFilesPath ?? "none"}.");
        listenTask = ListenAsync(shutdown.Token);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                var requestTask = Task.Run(
                    () => HandleRequestSafelyAsync(context, cancellationToken),
                    CancellationToken.None);
                lock (requestTasksLock)
                {
                    requestTasks.Add(requestTask);
                }

                _ = requestTask.ContinueWith(
                    completed =>
                    {
                        lock (requestTasksLock)
                        {
                            requestTasks.Remove(completed);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested || !listener.IsListening)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleRequestSafelyAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        long requestId = Interlocked.Increment(ref nextRequestId);
        var elapsed = Stopwatch.StartNew();
        AppLog.Information("RemoteApi.ProxyRequest", $"#{requestId} --> {DescribeRequest(context.Request)}");

        try
        {
            await HandleRequestAsync(context, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Error("RemoteApi.ProxyRequest", $"#{requestId} failed outside proxy handling.", exception);
            TryClose(context.Response);
        }
        finally
        {
            elapsed.Stop();
            AppLog.Information(
                "RemoteApi.ProxyRequest",
                $"#{requestId} <-- status={ResponseStatus(context.Response)} " +
                $"bytes={ResponseLength(context.Response)} elapsedMs={elapsed.Elapsed.TotalMilliseconds:F1}.");
        }
    }

    private async Task HandleRequestAsync(
        HttpListenerContext context,
        long requestId,
        CancellationToken cancellationToken)
    {
        var hijacked = false;
        try
        {
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            if (path.Length == 0)
            {
                path = "/";
            }

            if (context.Request.IsWebSocketRequest && IsApiPath(path))
            {
                hijacked = await ForwardWebSocketAsync(context, requestId, cancellationToken).ConfigureAwait(false);
                return;
            }

            context.Response.Headers["Connection"] = "keep-alive";
            context.Response.Headers["Keep-Alive"] = KeepAliveWindow;

            if (IsApiPath(path))
            {
                if (IsSelfProbe(context.Request, path))
                {
                    await WriteSelfInfoAsync(context).ConfigureAwait(false);
                }
                else
                {
                    await ForwardApiRequestAsync(context, requestId, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await WriteStaticFileAsync(context, path, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ProxyRequestException ex)
        {
            AppLog.Warning(
                "RemoteApi.ProxyRequest",
                $"#{requestId} rejected with {(int)ex.Status} {ex.Code}: {ex.Message}");
            await WriteProblemAsync(context, ex.Status, ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            AppLog.Warning("RemoteApi.ProxyRequest", $"#{requestId} could not reach the upstream server.", ex);
            await WriteProblemAsync(
                context,
                HttpStatusCode.BadGateway,
                "upstream_unavailable",
                ex.GetBaseException().Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppLog.Debug("RemoteApi.ProxyRequest", $"#{requestId} was canceled during shutdown.");
        }
        catch (HttpListenerException ex)
        {
            AppLog.Warning("RemoteApi.ProxyRequest", $"#{requestId} client connection failed.", ex);
        }
        catch (Exception ex)
        {
            AppLog.Error("RemoteApi.ProxyRequest", $"#{requestId} proxy handling failed.", ex);
            await WriteProblemAsync(
                context,
                HttpStatusCode.InternalServerError,
                "proxy_error",
                ex.GetBaseException().Message).ConfigureAwait(false);
        }
        finally
        {
            if (!hijacked)
            {
                TryClose(context.Response);
            }
        }
    }

    /// <summary>
    /// Relays a packet socket to the selected server, copying messages both ways until either end
    /// closes.
    /// </summary>
    /// <remarks>
    /// The target is named in the query string rather than <see cref="ForwardTargetHeader"/> because
    /// a browser sets no headers on a WebSocket handshake. It is checked exactly as the header is:
    /// nothing is relayed to an origin that has not answered a Light Player server probe.
    /// </remarks>
    private async Task<bool> ForwardWebSocketAsync(
        HttpListenerContext context,
        long requestId,
        CancellationToken cancellationToken)
    {
        var targetOrigin = ParseTargetOrigin(
            context.Request.QueryString[RemoteStreamDefaults.ForwardTargetQuery]);
        await EnsureTargetWhitelistedAsync(targetOrigin, cancellationToken).ConfigureAwait(false);

        var destination = new UriBuilder(new Uri(targetOrigin, context.Request.Url?.AbsolutePath ?? "/"))
        {
            Scheme = targetOrigin.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        }.Uri;

        AppLog.Information("RemoteApi.ProxySocket", $"#{requestId} connecting to {destination}.");

        using var upstream = new ClientWebSocket();
        upstream.Options.AddSubProtocol(PacketStreamProtocol.SubProtocol);

        // Connected before the client's own handshake is answered, so a target that refuses can
        // still be reported as an ordinary HTTP failure.
        try
        {
            await upstream.ConnectAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException exception)
        {
            throw new ProxyRequestException(
                HttpStatusCode.BadGateway,
                "upstream_unavailable",
                exception.GetBaseException().Message,
                exception);
        }

        HttpListenerWebSocketContext accepted = await context
            .AcceptWebSocketAsync(PacketStreamProtocol.SubProtocol)
            .ConfigureAwait(false);
        using WebSocket downstream = accepted.WebSocket;
        AppLog.Information("RemoteApi.ProxySocket", $"#{requestId} relay connected.");

        using var bridge = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await Task.WhenAll(
                CopyMessagesAsync(downstream, upstream, bridge),
                CopyMessagesAsync(upstream, downstream, bridge)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Either end going away ends the relay; there is nobody left to tell.
            AppLog.Warning("RemoteApi.ProxySocket", $"#{requestId} relay ended with an error.", exception);
        }

        AppLog.Information(
            "RemoteApi.ProxySocket",
            $"#{requestId} relay closed: clientState={downstream.State}; upstreamState={upstream.State}.");

        return true;
    }

    private static async Task CopyMessagesAsync(
        WebSocket source,
        WebSocket destination,
        CancellationTokenSource bridge)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!bridge.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await source
                    .ReceiveAsync(buffer, bridge.Token)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                await destination.SendAsync(
                    buffer.AsMemory(0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    bridge.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            bridge.Cancel();
        }
    }

    private async Task ForwardApiRequestAsync(
        HttpListenerContext context,
        long requestId,
        CancellationToken cancellationToken)
    {
        var targetOrigin = ParseTargetOrigin(context.Request.Headers[ForwardTargetHeader]);
        await EnsureTargetWhitelistedAsync(targetOrigin, cancellationToken).ConfigureAwait(false);

        var pathAndQuery = context.Request.Url?.PathAndQuery ?? "/api/v1";
        var destination = new Uri(targetOrigin, pathAndQuery);
        AppLog.Information(
            "RemoteApi.ProxyRequest",
            $"#{requestId} forwarding to origin={targetOrigin}; target={DescribeRequestTarget(context.Request)}.");

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), destination);
        if (context.Request.HasEntityBody)
        {
            request.Content = new StreamContent(context.Request.InputStream);
        }

        CopyRequestHeaders(context.Request, request);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        AppLog.Information(
            "RemoteApi.ProxyRequest",
            $"#{requestId} upstream returned {(int)response.StatusCode} {response.StatusCode}; " +
            $"contentType={response.Content.Headers.ContentType?.ToString() ?? "none"}; " +
            $"contentLength={response.Content.Headers.ContentLength?.ToString() ?? "unknown"}.");

        context.Response.StatusCode = (int)response.StatusCode;
        CopyResponseHeaders(response, context.Response);
        context.Response.Headers[ForwardedResponseHeader] = "1";
        AppendVary(context.Response, ForwardTargetHeader);

        if (response.Content.Headers.ContentType?.MediaType == RemoteStreamDefaults.PacketStreamContentType)
        {
            context.Response.KeepAlive = false;
            context.Response.Headers["Connection"] = "close";
            context.Response.Headers.Remove("Keep-Alive");
        }

        if (response.Content.Headers.ContentLength is long contentLength)
        {
            context.Response.ContentLength64 = contentLength;
        }
        else if (HasResponseBody(context.Request.HttpMethod, response.StatusCode))
        {
            context.Response.SendChunked = true;
        }
        else
        {
            context.Response.ContentLength64 = 0;
        }

        if (HasResponseBody(context.Request.HttpMethod, response.StatusCode))
        {
            await response.Content.CopyToAsync(context.Response.OutputStream, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AppendVary(HttpListenerResponse response, string headerName)
    {
        var existing = response.Headers["Vary"];
        if (string.IsNullOrWhiteSpace(existing))
        {
            response.Headers["Vary"] = headerName;
            return;
        }

        if (!existing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(headerName, StringComparer.OrdinalIgnoreCase))
        {
            response.Headers["Vary"] = existing + ", " + headerName;
        }
    }

    private async Task EnsureTargetWhitelistedAsync(Uri targetOrigin, CancellationToken cancellationToken)
    {
        var key = ForwardTargetKey.FromUri(targetOrigin);
        Task validationTask;
        lock (targetValidationLock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expiredKey in targetWhitelist
                .Where(entry => entry.Value <= now)
                .Select(entry => entry.Key)
                .ToArray())
            {
                targetWhitelist.Remove(expiredKey);
            }

            if (targetWhitelist.ContainsKey(key))
            {
                return;
            }

            if (!targetValidationTasks.TryGetValue(key, out validationTask!))
            {
                validationTask = ProbeAndWhitelistTargetAsync(targetOrigin, key);
                targetValidationTasks.Add(key, validationTask);
                _ = validationTask.ContinueWith(
                    completed => RemoveTargetValidation(key, completed),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        await validationTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProbeAndWhitelistTargetAsync(Uri targetOrigin, ForwardTargetKey key)
    {
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        probeTimeout.CancelAfter(ForwardTargetProbeTimeout);

        var probeUri = new Uri(targetOrigin, $"/api/v1/{RemoteApiDefaults.ServerInfoPath}");
        using var request = new HttpRequestMessage(HttpMethod.Get, probeUri);
        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                probeTimeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ProxyRequestException(
                    HttpStatusCode.BadGateway,
                    "forward_target_probe_failed",
                    $"The forward target probe returned HTTP {(int)response.StatusCode}.");
            }

            var info = await ReadProbeResponseAsync(response.Content, probeTimeout.Token).ConfigureAwait(false);
            if (info is null ||
                !string.Equals(info.Name, "LightPlayer", StringComparison.Ordinal) ||
                !string.Equals(info.ApiVersion, "1", StringComparison.Ordinal))
            {
                throw InvalidForwardTarget();
            }

            lock (targetValidationLock)
            {
                targetWhitelist[key] = DateTimeOffset.UtcNow.Add(ForwardTargetWhitelistLifetime);
            }
        }
        catch (JsonException)
        {
            throw InvalidForwardTarget();
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        {
            throw new ProxyRequestException(
                HttpStatusCode.GatewayTimeout,
                "forward_target_probe_timeout",
                "The forward target did not answer its Light Media Center server probe in time.");
        }
    }

    private static async Task<RemoteServerInfo?> ReadProbeResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumProbeResponseBytes)
        {
            throw InvalidForwardTarget();
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaximumProbeResponseBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > MaximumProbeResponseBytes)
        {
            throw InvalidForwardTarget();
        }

        return JsonSerializer.Deserialize(
            buffer.AsSpan(0, length),
            RemoteApiJsonContext.Default.RemoteServerInfo);
    }

    private void RemoveTargetValidation(ForwardTargetKey key, Task completed)
    {
        lock (targetValidationLock)
        {
            if (targetValidationTasks.TryGetValue(key, out var current) && ReferenceEquals(current, completed))
            {
                targetValidationTasks.Remove(key);
            }
        }
    }

    private static ProxyRequestException InvalidForwardTarget() => new(
        HttpStatusCode.BadGateway,
        "invalid_forward_target",
        "The forward target did not return a valid Light Media Center API v1 server response.");

    private static Uri ParseTargetOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ProxyRequestException(
                HttpStatusCode.BadRequest,
                "missing_forward_target",
                $"The {ForwardTargetHeader} header is required.");
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var target) ||
            target.Scheme is not ("http" or "https") ||
            target.UserInfo.Length > 0 ||
            target.AbsolutePath != "/" ||
            target.Query.Length > 0 ||
            target.Fragment.Length > 0)
        {
            throw new ProxyRequestException(
                HttpStatusCode.BadRequest,
                "invalid_forward_target",
                $"The {ForwardTargetHeader} header must contain an HTTP or HTTPS origin.");
        }

        return target;
    }

    private static void CopyRequestHeaders(HttpListenerRequest source, HttpRequestMessage destination)
    {
        foreach (var name in source.Headers.AllKeys)
        {
            if (name is null ||
                HopByHopHeaders.Contains(name) ||
                string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, ForwardTargetHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = source.Headers.GetValues(name);
            if (values is null)
            {
                continue;
            }

            if (!destination.Headers.TryAddWithoutValidation(name, values))
            {
                destination.Content?.Headers.TryAddWithoutValidation(name, values);
            }
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpListenerResponse destination)
    {
        foreach (var header in source.Headers.Concat(source.Content.Headers))
        {
            if (HopByHopHeaders.Contains(header.Key) ||
                string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                destination.Headers.Add(header.Key, value);
            }
        }

        if (source.Content.Headers.ContentType is not null)
        {
            destination.ContentType = source.Content.Headers.ContentType.ToString();
        }
    }

    private async Task WriteStaticFileAsync(
        HttpListenerContext context,
        string requestPath,
        CancellationToken cancellationToken)
    {
        if (requestPath == "/versions")
        {
            context.Response.Headers["Cache-Control"] = "no-store";
        }

        if (context.Request.HttpMethod is not ("GET" or "HEAD"))
        {
            throw new ProxyRequestException(
                HttpStatusCode.MethodNotAllowed,
                "method_not_allowed",
                "Static files support GET and HEAD only.");
        }

        if (staticFilesPath is null || !Directory.Exists(staticFilesPath))
        {
            throw new ProxyRequestException(
                HttpStatusCode.NotFound,
                "web_client_unavailable",
                "This proxy does not have a browser client to serve.");
        }

        var decodedPath = Uri.UnescapeDataString(requestPath);
        var relativePath = decodedPath is "/" or "/diag" ? "index.html" : decodedPath.TrimStart('/');
        var fullPath = Path.GetFullPath(Path.Combine(
            staticFilesPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = staticFilesPath.EndsWith(Path.DirectorySeparatorChar)
            ? staticFilesPath
            : staticFilesPath + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, PathComparison()) || !File.Exists(fullPath))
        {
            throw new ProxyRequestException(HttpStatusCode.NotFound, "not_found", "Static file not found.");
        }

        var contentPath = fullPath;
        string? contentEncoding = null;
        var acceptedEncodings = context.Request.Headers["Accept-Encoding"] ?? string.Empty;
        if (acceptedEncodings.Contains("br", StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath + ".br"))
        {
            contentPath = fullPath + ".br";
            contentEncoding = "br";
        }
        else if (acceptedEncodings.Contains("gzip", StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath + ".gz"))
        {
            contentPath = fullPath + ".gz";
            contentEncoding = "gzip";
        }

        var file = new FileInfo(contentPath);
        var etag = $"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\"";
        context.Response.ContentType = StaticContentType(fullPath);
        context.Response.ContentLength64 = file.Length;
        context.Response.Headers["ETag"] = etag;
        context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        context.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
        context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        context.Response.Headers["Service-Worker-Allowed"] = "/";
        context.Response.Headers["Cache-Control"] = StaticCacheControl(fullPath);
        if (contentEncoding is not null)
        {
            context.Response.Headers["Content-Encoding"] = contentEncoding;
            context.Response.Headers["Vary"] = "Accept-Encoding";
        }

        if (relativePath != "versions" && string.Equals(context.Request.Headers["If-None-Match"], etag, StringComparison.Ordinal))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotModified;
            context.Response.ContentLength64 = 0;
            return;
        }

        if (context.Request.HttpMethod == "HEAD")
        {
            return;
        }

        await using var stream = File.OpenRead(contentPath);
        await stream.CopyToAsync(context.Response.OutputStream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteProblemAsync(
        HttpListenerContext context,
        HttpStatusCode status,
        string code,
        string detail)
    {
        if (!context.Response.OutputStream.CanWrite)
        {
            return;
        }

        var problem = new ProxyProblem(code, status.ToString(), (int)status, detail);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(problem, ProxyJsonContext.Default.ProxyProblem);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/problem+json; charset=utf-8";
        context.Response.ContentLength64 = bytes.LongLength;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static bool IsApiPath(string path) =>
        path == "/api/v1" || path.StartsWith("/api/v1/", StringComparison.Ordinal);

    /// <summary>
    /// Whether this is the browser client asking what its own origin is, rather than a call meant
    /// for a library: the probe is the one route that arrives without a forward target.
    /// </summary>
    private static bool IsSelfProbe(HttpListenerRequest request, string path) =>
        request.HttpMethod is "GET" or "HEAD" &&
        path == $"/api/v1/{RemoteApiDefaults.ServerInfoPath}" &&
        string.IsNullOrWhiteSpace(request.Headers[ForwardTargetHeader]);

    private static async Task WriteSelfInfoAsync(HttpListenerContext context)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new RemoteServerInfo("LightPlayer", "1", ProxyOnly: true),
            RemoteApiJsonContext.Default.RemoteServerInfo);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.LongLength;
        AppendVary(context.Response, ForwardTargetHeader);
        if (context.Request.HttpMethod == "HEAD")
        {
            return;
        }

        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static bool HasResponseBody(string method, HttpStatusCode status) =>
        method != "HEAD" &&
        (int)status >= 200 &&
        status is not (HttpStatusCode.NoContent or HttpStatusCode.NotModified);

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string StaticCacheControl(string path) =>
        Path.GetFileName(path) == "versions" ? "no-store" :
        path.Contains($"{Path.DirectorySeparatorChar}_framework{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && HasContentFingerprint(path)
            ? "public, max-age=31536000, immutable"
            : "no-cache";

    private static bool HasContentFingerprint(string path)
    {
        var fileName = Path.GetFileName(path).AsSpan();
        var extensionSeparator = fileName.LastIndexOf('.');
        if (extensionSeparator < 0)
        {
            return false;
        }

        var name = fileName[..extensionSeparator];
        var fingerprintSeparator = name.LastIndexOf('.');
        var fingerprint = name[(fingerprintSeparator + 1)..];
        if (fingerprintSeparator < 0 || fingerprint.Length != 10)
        {
            return false;
        }

        foreach (var character in fingerprint)
        {
            if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    private static string StaticContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        "" when Path.GetFileName(path) == "versions" => "application/json; charset=utf-8",
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".webmanifest" => "application/manifest+json; charset=utf-8",
        ".wasm" => "application/wasm",
        ".dat" or ".pdb" => "application/octet-stream",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    private static string DescribeRequest(HttpListenerRequest request)
    {
        string target = DescribeRequestTarget(request);
        return $"{request.HttpMethod} {target} HTTP/{request.ProtocolVersion} " +
            $"remote={request.RemoteEndPoint?.ToString() ?? "unknown"} " +
            $"contentType={request.ContentType ?? "none"} contentLength={request.ContentLength64} " +
            $"range={request.Headers["Range"] ?? "none"} origin={request.Headers["Origin"] ?? "none"} " +
            $"userAgent={request.UserAgent ?? "none"} websocket={request.IsWebSocketRequest}.";
    }

    private static string DescribeRequestTarget(HttpListenerRequest request)
    {
        string path = request.Url?.AbsolutePath ?? request.RawUrl?.Split('?', 2)[0] ?? "/";
        if (request.QueryString.Count == 0)
        {
            return path;
        }

        var target = new StringBuilder(path).Append('?');
        bool first = true;
        foreach (string? name in request.QueryString.AllKeys)
        {
            string displayName = name ?? "unnamed";
            string[] values = request.QueryString.GetValues(name) ?? [string.Empty];
            foreach (string value in values)
            {
                if (!first)
                {
                    target.Append('&');
                }

                first = false;
                target.Append(Uri.EscapeDataString(displayName)).Append('=');
                target.Append(IsSensitiveQueryName(displayName)
                    ? "<redacted>"
                    : Uri.EscapeDataString(value.Length <= 512 ? value : value[..512] + "..."));
            }
        }

        return target.ToString();
    }

    private static bool IsSensitiveQueryName(string name)
    {
        string normalized = name.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized.Contains("password", StringComparison.Ordinal) ||
            normalized.Contains("passwd", StringComparison.Ordinal) ||
            normalized.Contains("token", StringComparison.Ordinal) ||
            normalized.Contains("secret", StringComparison.Ordinal) ||
            normalized.Contains("credential", StringComparison.Ordinal) ||
            normalized.Contains("authorization", StringComparison.Ordinal) ||
            normalized.Contains("signature", StringComparison.Ordinal) ||
            normalized.Contains("username", StringComparison.Ordinal) ||
            normalized is "user" or "login" or "key" or "apikey" or "accesskey" or "code" or "cursor";
    }

    private static int ResponseStatus(HttpListenerResponse response)
    {
        try
        {
            return response.StatusCode;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    private static long ResponseLength(HttpListenerResponse response)
    {
        try
        {
            return response.ContentLength64;
        }
        catch (ObjectDisposedException)
        {
            return -1;
        }
    }

    private static void TryClose(HttpListenerResponse response)
    {
        try
        {
            response.Close();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        AppLog.Information("RemoteApi.Proxy", "Stopping proxy server.");
        shutdown.Cancel();
        listener.Close();
        try
        {
            listenTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        Task[] requests;
        lock (requestTasksLock)
        {
            requests = requestTasks.ToArray();
        }

        try
        {
            Task.WhenAll(requests).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        httpClient.Dispose();
        shutdown.Dispose();
    }

    private sealed class ProxyRequestException(
        HttpStatusCode status,
        string code,
        string message,
        Exception? innerException = null)
        : Exception(message, innerException)
    {
        public HttpStatusCode Status { get; } = status;

        public string Code { get; } = code;
    }

    private readonly record struct ForwardTargetKey(string Scheme, string Host, int Port)
    {
        public static ForwardTargetKey FromUri(Uri value) => new(
            value.Scheme.ToLowerInvariant(),
            value.IdnHost.ToLowerInvariant(),
            value.Port);
    }
}

internal sealed record ProxyProblem(string Code, string Title, int Status, string Detail);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProxyProblem))]
internal sealed partial class ProxyJsonContext : JsonSerializerContext;