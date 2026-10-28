using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim.Transcoding;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.RemoteApi.Streaming;

namespace LightStudio.RemoteApi;

/// <summary>
/// HTTP host exposing an <see cref="ILocalServices"/> instance as the remote API, optionally
/// alongside the published browser client. Shared by the desktop head (API only) and the
/// standalone server head (API plus static files).
/// </summary>
public sealed class RemoteApiServer : IDisposable
{
    private const int MaximumRequestBodyBytes = 2 * 1024 * 1024;
    private const int MaximumConcurrentRequests = 8;
    private const int MaximumConcurrentLongPollRequests = 16;
    private const int MaximumFolderPreviewPaths = 9;
    private const int MaximumPreviewFolders = 8;

    /// <summary>Ceiling on a browsed subtitle file, which is read whole into memory to be served.</summary>
    private const long MaximumSubtitleBytes = 128 * 1024 * 1024;

    /// <summary>
    /// Packet sockets live for as long as something is being watched, so they get slots of their own:
    /// counting them against the ordinary request budget would let a few viewers shut the API down.
    /// Each slot holds a video and its sidecars open.
    /// </summary>
    private const int MaximumConcurrentPacketStreams = 8;

    /// <summary>
    /// A media read can stay open for as long as a client reads ahead through it, so media reads
    /// get slots of their own instead of holding the ordinary request budget.
    /// </summary>
    private const int MaximumConcurrentMediaFileRequests = 32;

    /// <summary>Most byte ranges one media request may name.</summary>
    private const int MaximumByteRanges = 64;

    /// <summary>
    /// Idle window advertised on every response.
    /// </summary>
    /// <remarks>
    /// The managed <see cref="HttpListener"/> used on Unix drops a reused connection after 15 s of
    /// idling and tells the client nothing, so a browser that pools connections for far longer
    /// (Firefox: 115 s) eventually writes a request into a socket the server has already dropped and
    /// then waits for a reply that can never arrive. Naming a shorter window makes the client retire
    /// the connection first.
    /// </remarks>
    private const string KeepAliveWindow = "timeout=10";
    private const int CopyBufferSize = 64 * 1024;

    /// <summary>Response headers a cross-origin client is allowed to read; media needs the range ones.</summary>
    private const string ExposedResponseHeaders = "Content-Length, Content-Range, Accept-Ranges, ETag";
    private const string AllowedRequestMethods = "GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS";
    private const string AllowedRequestHeaders = "Authorization, Content-Type, Range, If-None-Match";
    private const string PreflightCacheSeconds = "600";
    private const string PrivateNetworkRequestHeader = "Access-Control-Request-Private-Network";

    private readonly ILocalServices services;
    private readonly IMultiMediaPlatformIO multimediaPlatform;
    private readonly string? staticFilesPath;
    private readonly RemoteApiCredentials? credentials;
    private readonly RemoteApiTokens? tokens;
    private readonly CorsOriginPolicy allowedOrigins;
    private readonly RemoteApiPairingTokens pairingTokens = new();
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim requestSlots = new(MaximumConcurrentRequests);
    private readonly SemaphoreSlim longPollRequestSlots = new(MaximumConcurrentLongPollRequests);
    private readonly SemaphoreSlim packetStreamSlots = new(MaximumConcurrentPacketStreams);
    private readonly SemaphoreSlim mediaFileSlots = new(MaximumConcurrentMediaFileRequests);
    private readonly object requestTasksLock = new();
    private readonly HashSet<Task> requestTasks = [];
    private readonly ScanEventBuffer scanEvents;
    private readonly IndexedTrackCache indexedTracks;
    private readonly IndexedVideoCache indexedVideos;
    private readonly TranscodeOptions transcodeOptions;

    private Task? listenTask;
    private long nextRequestId;
    private bool disposed;

    public RemoteApiServer(
        ILocalServices services,
        IMultiMediaPlatformIO multimediaPlatform,
        RemoteApiHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.multimediaPlatform = multimediaPlatform ?? throw new ArgumentNullException(nameof(multimediaPlatform));
        staticFilesPath = options.StaticFilesPath is null ? null : Path.GetFullPath(options.StaticFilesPath);
        credentials = options.Credentials is { IsUsable: true } value ? value : null;
        tokens = credentials is null
            ? null
            : new RemoteApiTokens(options.TokenSecret ?? RandomNumberGenerator.GetBytes(32), credentials);
        allowedOrigins = CorsOriginPolicy.Create(options.AllowedOrigins);
        listener.Prefixes.Add(options.ListenerPrefix);
        // A write failure is how HttpListener reports that nginx or the browser abandoned a
        // response. Surface it so a superseded media transfer stops copying and releases its slot.
        listener.IgnoreWriteExceptions = false;
        scanEvents = new ScanEventBuffer(services.LibraryScan);
        indexedTracks = new IndexedTrackCache(services.LibraryQuery, services.LibraryScan);
        indexedVideos = new IndexedVideoCache(services.VideoLibraryQuery, services.LibraryScan);
        transcodeOptions = options.Transcoding ?? TranscodeOptions.Disabled;
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
            "RemoteApi.Server",
            $"Listening on {string.Join(", ", listener.Prefixes)}; authentication={credentials is not null}; " +
            $"staticFiles={staticFilesPath ?? "none"}; allowedOrigins={allowedOrigins.Describe()}; " +
            $"transcoding={transcodeOptions.Enabled}; " +
            $"hardware={transcodeOptions.EnableHardware}; encoderInterface={transcodeOptions.HardwareEncoder ?? "auto"}.");
        listenTask = ListenAsync(shutdown.Token);
    }

    internal (string Token, long ExpiresAtUnixSeconds) IssuePairingToken()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!listener.IsListening)
        {
            throw new InvalidOperationException("The remote API server is not running.");
        }

        return pairingTokens.Issue();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);

                // Handlers must not start inline: their synchronous prologue (library lookup, file
                // open) would stall accepting, so a burst of media range reads would serialize and
                // leave later requests (especially a range probe for the next track) queued for as
                // long as the whole burst takes.
                var requestTask = Task.Run(
                    () => HandleRequestInSlotAsync(context, cancellationToken),
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

    private async Task HandleRequestInSlotAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        long requestId = Interlocked.Increment(ref nextRequestId);
        var elapsed = Stopwatch.StartNew();
        AppLog.Information("RemoteApi.Request", $"#{requestId} --> {DescribeRequest(context.Request)}");

        SemaphoreSlim? slots = null;
        var entered = false;
        try
        {
            if (IsPacketSocketRequest(context.Request))
            {
                await HandlePacketSocketInSlotAsync(context, requestId, cancellationToken).ConfigureAwait(false);
                return;
            }

            slots = SlotsFor(context.Request);
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await HandleRequestAsync(context, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryClose(context.Response);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            TryClose(context.Response);
        }
        catch (Exception exception)
        {
            AppLog.Error("RemoteApi.Request", $"#{requestId} failed outside route handling.", exception);
            TryClose(context.Response);
        }
        finally
        {
            if (entered)
            {
                slots!.Release();
            }

            elapsed.Stop();
            AppLog.Information(
                "RemoteApi.Request",
                $"#{requestId} <-- status={ResponseStatus(context.Response)} " +
                $"bytes={ResponseLength(context.Response)} elapsedMs={elapsed.Elapsed.TotalMilliseconds:F1}.");
        }
    }

    /// <summary>
    /// A socket holds a video and its sidecars open for as long as it lives, so a client that cannot
    /// have one is told at once rather than left waiting: it can go and read the file for itself.
    /// </summary>
    private async Task HandlePacketSocketInSlotAsync(
        HttpListenerContext context,
        long requestId,
        CancellationToken cancellationToken)
    {
        // Browsers apply no CORS to a WebSocket handshake. Without a token to check, only native
        // clients and listed origins may open one, so an arbitrary page cannot stream from this host.
        var origin = context.Request.Headers["Origin"];
        if (credentials is null && !string.IsNullOrEmpty(origin) && !allowedOrigins.Allows(origin))
        {
            AppLog.Warning("RemoteApi.Socket", $"#{requestId} rejected the packet socket from origin {origin}.");
            await WriteProblemAsync(
                context,
                HttpStatusCode.Forbidden,
                "origin_not_allowed",
                "This origin may not open a packet stream.").ConfigureAwait(false);
            TryClose(context.Response);
            return;
        }

        if (!await packetStreamSlots.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            AppLog.Warning("RemoteApi.Socket", $"#{requestId} rejected because all packet stream slots are occupied.");
            await WriteProblemAsync(
                context,
                HttpStatusCode.ServiceUnavailable,
                "too_many_streams",
                "This server is already streaming as much as it can.").ConfigureAwait(false);
            TryClose(context.Response);
            return;
        }

        try
        {
            await HandlePacketSocketAsync(context, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Error("RemoteApi.Socket", $"#{requestId} packet socket failed.", exception);
            TryClose(context.Response);
        }
        finally
        {
            packetStreamSlots.Release();
        }
    }

    private async Task HandleRequestAsync(
        HttpListenerContext context,
        long requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            context.Response.Headers["Connection"] = "keep-alive";
            context.Response.Headers["Keep-Alive"] = KeepAliveWindow;
            ApplyCorsHeaders(context);
            if (TryAnswerPreflight(context))
            {
                return;
            }

            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            if (path.Length == 0)
            {
                path = "/";
            }

            if (!IsApiPath(path))
            {
                await WriteStaticFileAsync(context, path, cancellationToken).ConfigureAwait(false);
                return;
            }

            Authorize(context, path);

            if (await TryHandleAuthRouteAsync(context, path, cancellationToken).ConfigureAwait(false) ||
                await TryHandleStreamRouteAsync(context, path, cancellationToken).ConfigureAwait(false) ||
                await TryHandleFixedRouteAsync(context, path, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            throw new RemoteApiException(HttpStatusCode.NotFound, "not_found", "Route not found.");
        }
        catch (RemoteApiException ex)
        {
            AppLog.Warning(
                "RemoteApi.Request",
                $"#{requestId} rejected with {(int)ex.Status} {ex.Code}: {ex.Message}");
            await WriteProblemAsync(context, ex.Status, ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (RemoteStreamNegotiationException ex)
        {
            await WriteProblemAsync(context,
                ex.Code == "source_changed" ? HttpStatusCode.Conflict : HttpStatusCode.UnprocessableEntity,
                ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (LibraryItemNotFoundException ex)
        {
            await WriteProblemAsync(context, HttpStatusCode.NotFound, "item_not_found", ex.Message).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            AppLog.Warning("RemoteApi.Request", $"#{requestId} contained invalid JSON.", ex);
            await WriteProblemAsync(context, HttpStatusCode.BadRequest, "invalid_json", ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("RemoteApi.Request", $"#{requestId} route handling failed.", ex);
            await WriteProblemAsync(
                context,
                HttpStatusCode.InternalServerError,
                "server_error",
                ex.Message).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Names this response as readable to a listed origin. An unlisted one is answered as usual and
    /// the browser, seeing no permission, keeps the answer from the page that asked for it.
    /// </summary>
    private void ApplyCorsHeaders(HttpListenerContext context)
    {
        var origin = context.Request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin))
        {
            return;
        }

        AppendVary(context.Response, "Origin");
        if (!allowedOrigins.Allows(origin))
        {
            return;
        }

        context.Response.Headers["Access-Control-Allow-Origin"] = origin;
        context.Response.Headers["Access-Control-Expose-Headers"] = ExposedResponseHeaders;
    }

    /// <summary>
    /// Answers the preflight a browser sends before any request carrying a token or a byte range.
    /// An unlisted origin is answered too, but without permission, which is what tells the browser
    /// not to send the real request at all.
    /// </summary>
    private bool TryAnswerPreflight(HttpListenerContext context)
    {
        if (context.Request.HttpMethod != "OPTIONS" ||
            string.IsNullOrEmpty(context.Request.Headers["Access-Control-Request-Method"]))
        {
            return false;
        }

        if (allowedOrigins.Allows(context.Request.Headers["Origin"]))
        {
            context.Response.Headers["Access-Control-Allow-Methods"] = AllowedRequestMethods;
            context.Response.Headers["Access-Control-Allow-Headers"] =
                context.Request.Headers["Access-Control-Request-Headers"] is { Length: > 0 } requested
                    ? requested
                    : AllowedRequestHeaders;
            context.Response.Headers["Access-Control-Max-Age"] = PreflightCacheSeconds;
            if (string.Equals(
                context.Request.Headers[PrivateNetworkRequestHeader],
                "true",
                StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            }
        }

        context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        return true;
    }

    private static void AppendVary(HttpListenerResponse response, string value)
    {
        var current = response.Headers["Vary"];
        response.Headers["Vary"] = string.IsNullOrEmpty(current) ? value : $"{current}, {value}";
    }

    /// <summary>
    /// Rejects unauthenticated calls once credentials are configured. The capability probe and the
    /// login route stay open so a client can discover that it needs to sign in and then do so.
    /// </summary>
    private void Authorize(HttpListenerContext context, string path)
    {
        if (credentials is null ||
            path == "/api/v1/" + RemoteApiDefaults.ServerInfoPath ||
            path == "/api/v1/" + RemoteApiDefaults.LoginPath ||
            path == "/api/v1/" + RemoteApiDefaults.PairingPath)
        {
            return;
        }

        if (IsAuthorized(context.Request.Headers["Authorization"]))
        {
            return;
        }

        context.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"LightPlayer\"";
        throw new RemoteApiException(
            HttpStatusCode.Unauthorized,
            "unauthorized",
            "A valid session token is required.");
    }

    private bool IsAuthorized(string? authorization)
    {
        if (tokens is null || string.IsNullOrWhiteSpace(authorization))
        {
            return false;
        }

        var separator = authorization.IndexOf(' ');
        return separator > 0 &&
            string.Equals(authorization[..separator], "Bearer", StringComparison.OrdinalIgnoreCase) &&
            tokens.Validate(authorization[(separator + 1)..].Trim());
    }

    private async Task<bool> TryHandleAuthRouteAsync(
        HttpListenerContext context,
        string path,
        CancellationToken cancellationToken)
    {
        if (path == "/api/v1/" + RemoteApiDefaults.PairingPath)
        {
            if (context.Request.HttpMethod != "POST")
            {
                throw new RemoteApiException(
                    HttpStatusCode.MethodNotAllowed,
                    "method_not_allowed",
                    "Exchange a pairing code with POST.");
            }

            var pairingRequest = await ReadJsonAsync<RemotePairingRequest>(context, cancellationToken).ConfigureAwait(false);
            if (!pairingTokens.TryExchange(
                pairingRequest.Token,
                () => tokens?.Issue() ?? new RemoteLoginResult(string.Empty, 0),
                out var result))
            {
                throw new RemoteApiException(
                    HttpStatusCode.Unauthorized,
                    "invalid_pairing_token",
                    "The pairing link is invalid or expired.");
            }

            context.Response.Headers["Cache-Control"] = "no-store";
            await WriteJsonAsync(context, result).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/v1/" + RemoteApiDefaults.RefreshPath)
        {
            if (context.Request.HttpMethod != "POST")
            {
                throw new RemoteApiException(
                    HttpStatusCode.MethodNotAllowed,
                    "method_not_allowed",
                    "Refresh a session token with POST.");
            }

            if (tokens is null)
            {
                throw new RemoteApiException(
                    HttpStatusCode.NotFound,
                    "authentication_disabled",
                    "This server does not require authentication.");
            }

            context.Response.Headers["Cache-Control"] = "no-store";
            await WriteJsonAsync(context, tokens.Issue()).ConfigureAwait(false);
            return true;
        }

        if (path != "/api/v1/" + RemoteApiDefaults.LoginPath)
        {
            return false;
        }

        if (context.Request.HttpMethod != "POST")
        {
            throw new RemoteApiException(HttpStatusCode.MethodNotAllowed, "method_not_allowed", "Sign in with POST.");
        }

        var loginRequest = await ReadJsonAsync<RemoteLoginRequest>(context, cancellationToken).ConfigureAwait(false);
        if (credentials is null)
        {
            throw new RemoteApiException(
                HttpStatusCode.NotFound,
                "authentication_disabled",
                "This server does not require authentication.");
        }

        if (!credentials.Verify(loginRequest.Username ?? string.Empty, loginRequest.Password ?? string.Empty))
        {
            throw new RemoteApiException(
                HttpStatusCode.Unauthorized,
                "invalid_credentials",
                "The user name or password is incorrect.");
        }

        context.Response.Headers["Cache-Control"] = "no-store";
        await WriteJsonAsync(context, tokens!.Issue()).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Routes of the packet stream: the server demuxes and sends only the tracks a client asked
    /// for, instead of handing over a whole file for the client to demux itself.
    /// </summary>
    /// <remarks>
    /// Playback itself runs over the socket accepted in <see cref="HandlePacketSocketAsync"/>. What
    /// is left here is the one thing a socket cannot serve: a subtitle track has to be walked end to
    /// end, which would leave the containers feeding playback somewhere else entirely.
    /// </remarks>
    private async Task<bool> TryHandleStreamRouteAsync(
        HttpListenerContext context,
        string path,
        CancellationToken cancellationToken)
    {
        if (!path.StartsWith("/api/v1/stream/", StringComparison.Ordinal))
        {
            return false;
        }

        if (context.Request.HttpMethod == "POST" && path == "/api/v1/" + RemoteStreamDefaults.DescribePath)
        {
            var request = await ReadJsonAsync<RemotePathRequest>(context, cancellationToken).ConfigureAwait(false);
            using StreamSession session = await OpenStreamSessionAsync(request.Path).ConfigureAwait(false);
            await WriteJsonAsync(context, session.Manifest with { Transcoding = AdvertisedTranscoding() }).ConfigureAwait(false);
            return true;
        }

        if (context.Request.HttpMethod == "POST" && path == "/api/v1/" + RemoteStreamDefaults.OpenPath)
        {
            var request = await ReadJsonAsync<RemoteStreamHello>(context, cancellationToken).ConfigureAwait(false);
            using StreamSession session = await OpenStreamSessionAsync(request).ConfigureAwait(false);
            await WriteJsonAsync(context, session.Manifest).ConfigureAwait(false);
            return true;
        }

        if (context.Request.HttpMethod == "POST" && path == "/api/v1/" + RemoteStreamDefaults.PacketsPath)
        {
            var request = await ReadJsonAsync<RemotePacketStreamRequest>(context, cancellationToken).ConfigureAwait(false);
            using StreamSession session = await OpenStreamSessionAsync(
                new RemoteStreamHello(request.Path, Transcode: request.Transcode, SourceRevision: request.SourceRevision)).ConfigureAwait(false);

            context.Response.ContentType = RemoteStreamDefaults.PacketStreamContentType;
            context.Response.SendChunked = true;
            context.Response.KeepAlive = false;
            context.Response.Headers["Connection"] = "close";
            context.Response.Headers.Remove("Keep-Alive");
            var writer = new HttpPacketStreamWriter(context.Response.OutputStream, cancellationToken);
            var credit = new PacketCreditGate();
            credit.Reset(long.MaxValue);
            await writer.WritePreambleAsync().ConfigureAwait(false);
            try
            {
                await session
                    .WritePacketsAsync(writer, credit, request.TrackIds, request.StartSeconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (exception is not HttpListenerException)
            {
                AppLog.Error("RemoteApi.PacketStream", "The HTTP packet stream failed.", exception);
                await writer.WriteErrorAsync("stream_failed", exception.Message).ConfigureAwait(false);
            }

            return true;
        }

        if (context.Request.HttpMethod == "POST" && path == "/api/v1/" + RemoteStreamDefaults.SubtitlePath)
        {
            var request = await ReadJsonAsync<RemoteStreamSubtitleRequest>(context, cancellationToken).ConfigureAwait(false);
            using StreamSession session = await OpenStreamSessionAsync(request.Path).ConfigureAwait(false);

            // Reading a subtitle stream walks a container end to end, which must not sit on the
            // thread accepting requests.
            var script = await Task
                .Run(() => session.ReadSubtitleScript(request.TrackId, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(context, new RemoteStreamSubtitle(script)).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private static bool IsPacketSocketRequest(HttpListenerRequest request) =>
        request.IsWebSocketRequest &&
        request.Url?.AbsolutePath.TrimEnd('/') == "/api/v1/" + RemoteStreamDefaults.SocketPath;

    /// <summary>
    /// Accepts a packet socket. The token arrives in the socket's opening message instead of a
    /// header, which a browser cannot set on a WebSocket handshake.
    /// </summary>
    private async Task HandlePacketSocketAsync(
        HttpListenerContext context,
        long requestId,
        CancellationToken cancellationToken)
    {
        HttpListenerWebSocketContext accepted;
        try
        {
            accepted = await context.AcceptWebSocketAsync(PacketStreamProtocol.SubProtocol).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Error("RemoteApi.Socket", $"#{requestId} WebSocket handshake failed.", exception);
            TryClose(context.Response);
            return;
        }

        using var connection = new PacketSocketConnection(
            requestId,
            accepted.WebSocket,
            OpenStreamSessionAsync,
            AuthorizePacketSocket);
        await connection.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool AuthorizePacketSocket(string? token) =>
        tokens is null ||
        (!string.IsNullOrWhiteSpace(token) && tokens.Validate(token));

    /// <summary>
    /// What clients are told this server can re-encode to, or null when it will not re-encode
    /// anything. A client offers the user nothing it cannot actually be given.
    /// </summary>
    private RemoteTranscodeCapabilities? AdvertisedTranscoding()
    {
        if (!transcodeOptions.Enabled)
        {
            return null;
        }

        TranscodeCapabilities capabilities = FfmpegTranscodeCapabilities.Describe(transcodeOptions);
        if (!capabilities.IsAvailable)
        {
            return null;
        }

        return new RemoteTranscodeCapabilities(
            capabilities.VideoEncoders.Select(encoder => encoder.CodecName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            capabilities.AudioEncoders.Select(encoder => encoder.CodecName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            capabilities.IsHardware,
            capabilities.DeviceType);
    }

    /// <summary>
    /// Opens the video at <paramref name="mediaPath"/> together with its sidecars, passing nothing
    /// through a transcoder. Used by the routes that only need to read the media, not stream it.
    /// </summary>
    private Task<StreamSession> OpenStreamSessionAsync(string? mediaPath) =>
        OpenStreamSessionAsync(new RemoteStreamHello(mediaPath ?? string.Empty));

    /// <summary>
    /// Opens the video the socket named together with its sidecars. Being in the library index is
    /// what makes a path readable at all.
    /// </summary>
    private async Task<StreamSession> OpenStreamSessionAsync(RemoteStreamHello hello)
    {
        var video = await indexedVideos.FindAsync(hello.Path).ConfigureAwait(false)
            ?? throw new RemoteApiException(
                HttpStatusCode.Forbidden,
                "media_not_indexed",
                "The requested media path is not present in the library index.");

        var sidecars = video.ExternalAudioList.Concat(video.ExternalSubtitleList).ToArray();
        var revision = $"{video.MediaKind}:{video.DiscTitle}";
        string[] requestedCodecs = (hello.Transcode?.Tracks ?? [])
            .SelectMany(track => string.IsNullOrWhiteSpace(track.CodecName)
                ? track.CodecCandidates ?? []
                : [track.CodecName])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return StreamSession.Create(
            video.Path,
            sidecars,
            revision,
            OpenStreamSource,
            video.MediaKind,
            video.DiscTitle,
            hello.Transcode,
            () => requestedCodecs.Length > 0
                ? FfmpegTranscodeCapabilities.Probe(transcodeOptions, requestedCodecs)
                : TranscodeCapabilities.None,
            transcodeOptions,
            hello.SourceRevision);
    }

    /// <summary>Resolves a library path the index already vouched for to readable bytes.</summary>
    private Stream OpenStreamSource(string path) =>
        multimediaPlatform.OpenMediaFileAsync(path).GetAwaiter().GetResult();

    private async Task<bool> TryHandleFixedRouteAsync(
        HttpListenerContext context,
        string path,
        CancellationToken cancellationToken)
    {
        var method = context.Request.HttpMethod;

        if (method == "GET" && path == "/api/v1/server")
        {
            RemoteTranscodeCapabilities? transcoding = AdvertisedTranscoding();
            var capabilities = new List<string> { RemoteStreamDefaults.PacketStreamCapability, RemoteApiDefaults.MediaBrowseCapability };
            if (transcoding is not null)
                capabilities.Add(RemoteStreamDefaults.TranscodeCapability);
            if (RemotePhotoSearchAvailable)
                capabilities.Add(RemoteApiDefaults.PhotoSearchCapability);
            await WriteJsonAsync(
                context,
                new RemoteServerInfo(
                    "LightPlayer",
                    "1",
                    credentials is not null,
                    capabilities.ToArray(),
                    transcoding))
                .ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/photos/search")
        {
            if (!RemotePhotoSearchAvailable)
            {
                await WriteJsonAsync(context, new PhotoSearchPage([], null, false)).ConfigureAwait(false);
                return true;
            }
            var query = RequiredQuery(context, "q");
            var limitText = context.Request.QueryString["limit"];
            var limit = limitText is null ? 20 : int.TryParse(limitText, out var parsed) ? parsed : 0;
            if (query.Length > 2048 || limit is < 1 or > 100)
                throw new RemoteApiException(HttpStatusCode.BadRequest, "invalid_photo_query", "Photo search accepts up to 2048 query characters and 1-100 results per page.");
            PhotoSearchPage page;
            try
            {
                page = await services.PhotoIndex!.SearchAsync(query, limit, context.Request.QueryString["cursor"], cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                throw new RemoteApiException(HttpStatusCode.BadRequest, "invalid_photo_query", exception.Message);
            }
            page = RemotePhotoSearchAvailable
                ? page with { Items = page.Items.Where(item => FindSearchablePhoto(item.Path) is not null).ToArray() }
                : new PhotoSearchPage([], null, false);
            await WriteJsonAsync(context, page).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/library/folders")
        {
            await WriteJsonAsync(context, services.LibraryLocation.GetLibraryFolderPaths().ToArray()).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/library/video-folders")
        {
            await WriteJsonAsync(context, services.LibraryLocation.GetVideoFolderPaths().ToArray()).ConfigureAwait(false);
            return true;
        }

        if (path is "/api/v1/tracks/index" or "/api/v1/tracks/index/items")
        {
            return await HandleBrowseIndexAsync(context, path,
                services.LibraryQuery as ILibraryBrowseIndexQuery<DbMediaFile>, cancellationToken).ConfigureAwait(false);
        }

        if (path is "/api/v1/albums/index" or "/api/v1/albums/index/items")
        {
            return await HandleBrowseIndexAsync(context, path,
                services.LibraryQuery as ILibraryBrowseIndexQuery<MediaLibraryAlbum>, cancellationToken).ConfigureAwait(false);
        }

        if (path is "/api/v1/artists/index" or "/api/v1/artists/index/items")
        {
            return await HandleBrowseIndexAsync(context, path,
                services.LibraryQuery as ILibraryBrowseIndexQuery<MediaLibraryArtistBrowseItem>, cancellationToken).ConfigureAwait(false);
        }

        if (path is "/api/v1/videos/index" or "/api/v1/videos/index/items")
        {
            return await HandleBrowseIndexAsync(context, path,
                services.VideoLibraryQuery as ILibraryBrowseIndexQuery<DbVideoFile>, cancellationToken).ConfigureAwait(false);
        }

        if (path is "/api/v1/media/index" or "/api/v1/media/index/items")
        {
            return await HandleBrowseIndexAsync(context, path,
                services.VideoLibraryQuery as ILibraryBrowseIndexQuery<IndexedMediaFile>, cancellationToken).ConfigureAwait(false);
        }

        if (method == "GET" && path == "/api/v1/media")
        {
            var page = await ExecutePageQueryAsync(() => LibraryBrowseIndexes.GetMediaItemsPageAsync(
                services.VideoLibraryQuery, ParsePageRequest(context), cancellationToken)).ConfigureAwait(false);
            await WriteJsonAsync(context, page).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/images/by-folder")
        {
            IReadOnlyList<DbImageFile> values = services.VideoLibraryQuery is IMediaLibraryQueryService media
                ? await media.GetImagesInFolderAsync(RequiredQuery(context, "path"), cancellationToken).ConfigureAwait(false)
                : [];
            await WriteJsonAsync(context, values.ToArray()).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/images/search")
        {
            IReadOnlyList<DbImageFile> values = services.VideoLibraryQuery is IMediaLibraryQueryService media
                ? await media.SearchImagesAsync(RequiredQuery(context, "q"), cancellationToken).ConfigureAwait(false)
                : [];
            await WriteJsonAsync(context, values.ToArray()).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/videos")
        {
            var page = await ExecutePageQueryAsync(() => services.VideoLibraryQuery
                .GetVideosPageAsync(ParsePageRequest(context), cancellationToken)).ConfigureAwait(false);
            await WriteJsonAsync(context, page).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/videos/by-folder")
        {
            var values = await services.VideoLibraryQuery
                .GetVideosInFolderAsync(RequiredQuery(context, "path"), cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(context, values.ToArray()).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/videos/picks")
        {
            var limit = ParsePageSize(context);
            if (limit is < 1 or > MediaFilePageRequest.MaxPageSize)
            {
                throw new RemoteApiException(
                    HttpStatusCode.BadRequest,
                    "invalid_limit",
                    $"Pick limit must be from 1 through {MediaFilePageRequest.MaxPageSize}.");
            }

            var values = await services.VideoLibraryQuery
                .GetVideoPicksAsync(ParseVideoPickOrder(context), limit, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(context, values.ToArray()).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/videos/folders")
        {
            var values = await services.VideoLibraryQuery.GetVideoFolderCountsAsync().ConfigureAwait(false);
            await WriteJsonAsync(context, values.ToArray()).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/videos/search")
        {
            var values = await services.VideoLibraryQuery
                .SearchVideosAsync(RequiredQuery(context, "q"), cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(context, values.ToArray()).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/video")
        {
            var value = await services.VideoLibraryQuery
                .GetVideoByPathAsync(RequiredQuery(context, "path"))
                .ConfigureAwait(false);
            await WriteJsonAsync(context, value).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/videos/browse")
        {
            await WriteJsonAsync(
                context,
                await BrowseVideoLibraryAsync(
                    context.Request.QueryString["path"],
                    context.Request.QueryString["kind"] == RemoteApiDefaults.MediaBrowseKind,
                    cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);
            return true;
        }

        if ((method == "GET" || method == "HEAD") && path == "/api/v1/videos/subtitle")
        {
            await WriteSubtitleFileAsync(context, RequiredQuery(context, "path"), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if ((method == "GET" || method == "HEAD") && path == "/api/v1/video/preview")
        {
            await WriteVideoPreviewAsync(context, RequiredQuery(context, "path"), cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (method == "POST" && path == "/api/v1/library/scan")
        {
            // A scan can outlast any request timeout, so it is only started here and its progress
            // and result are reported through scan events after this cursor.
            var cursor = scanEvents.Current().Cursor;
            _ = services.LibraryScan.ScanAsync().ContinueWith(
                scan => AppLog.Warning("RemoteApi.Scan", "A library scan requested by a client failed.", scan.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            context.Response.StatusCode = (int)HttpStatusCode.Accepted;
            await WriteJsonAsync(context, new RemoteScanStarted(cursor)).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/library/scan/events")
        {
            var page = long.TryParse(context.Request.QueryString["after"], out var after)
                ? await scanEvents.WaitAsync(after, cancellationToken).ConfigureAwait(false)
                : scanEvents.Current();
            await WriteJsonAsync(context, page).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/albums")
        {
            var page = await ExecutePageQueryAsync(() => services.LibraryQuery
                .GetAlbumsPageAsync(ParseAlbumPageRequest(context), cancellationToken)).ConfigureAwait(false);
            await WriteJsonAsync(context, page).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/albums/by-artist")
        {
            var values = await services.LibraryQuery
                .GetAlbumsAsync(RequiredQuery(context, "artist"))
                .ConfigureAwait(false);
            await WriteJsonAsync(context, values.ToArray()).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/album")
        {
            var value = await services.LibraryQuery.GetAlbumByNameAsync(
                context.Request.QueryString["artist"],
                context.Request.QueryString["name"],
                context.Request.QueryString["group"]).ConfigureAwait(false);
            await WriteJsonAsync(context, value).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/album/by-track")
        {
            var value = await services.LibraryQuery
                .GetAlbumByTrackPathAsync(RequiredQuery(context, "path"))
                .ConfigureAwait(false);
            await WriteJsonAsync(context, value).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/artists")
        {
            var page = await ExecutePageQueryAsync(() => services.LibraryQuery
                .GetArtistsPageAsync(ParseArtistPageRequest(context), cancellationToken)).ConfigureAwait(false);
            await WriteJsonAsync(context, page).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/artist")
        {
            var value = await services.LibraryQuery
                .GetArtistByNameAsync(context.Request.QueryString["name"])
                .ConfigureAwait(false);
            await WriteJsonAsync(context, value).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/artist/by-track")
        {
            var value = await services.LibraryQuery
                .GetArtistByTrackPathAsync(RequiredQuery(context, "path"))
                .ConfigureAwait(false);
            await WriteJsonAsync(context, value).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/artist/exists")
        {
            var value = await services.LibraryQuery
                .ArtistExistsAsync(context.Request.QueryString["name"])
                .ConfigureAwait(false);
            await WriteJsonAsync(context, value).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/tracks")
        {
            PagedResult<DbMediaFile> page;
            try
            {
                page = await services.LibraryQuery
                    .GetMediaFilesPageAsync(ParseMediaFilePageRequest(context), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                throw new RemoteApiException(
                    HttpStatusCode.BadRequest,
                    "invalid_page",
                    exception.Message);
            }

            await WriteJsonAsync(context, page).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/music/browse")
        {
            var browser = new IndexedMusicFolderBrowser(services.LibraryQuery,
                _ => Task.FromResult(services.LibraryLocation.GetLibraryFolderPaths()),
                services.MultimediaPlatform, PlatformServices.LibraryFolderAccess);
            var listing = await browser.ListAsync(context.Request.QueryString["path"], cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(context, listing).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/tracks/folders")
        {
            var folders = await services.LibraryQuery.GetMediaFolderPathsAsync(cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(context, folders.ToArray()).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/tracks/shuffled")
        {
            if (!long.TryParse(RequiredQuery(context, "seed"), out var seed))
            {
                throw new RemoteApiException(
                    HttpStatusCode.BadRequest,
                    "invalid_seed",
                    "Shuffle seed must be an integer.");
            }

            var pageRequest = ParsePageRequest(context);
            var page = await ExecutePageQueryAsync(() => services.LibraryQuery
                .GetShuffledMediaFilesPageAsync(
                    new ShuffledMediaFilePageRequest(seed, pageRequest.PageSize, pageRequest.Cursor),
                    cancellationToken)).ConfigureAwait(false);
            await WriteJsonAsync(context, page).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/tracks/by-album")
        {
            var values = await services.LibraryQuery.GetMediaFilesByAlbumAsync(
                context.Request.QueryString["album"],
                context.Request.QueryString["artist"],
                context.Request.QueryString["group"]).ConfigureAwait(false);
            await WriteJsonAsync(context, values.ToArray()).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/tracks/by-artist")
        {
            var artist = await services.LibraryQuery
                .GetArtistByNameAsync(context.Request.QueryString["name"])
                .ConfigureAwait(false);
            var values = await services.LibraryQuery.GetMediaFilesByArtistAsync(artist).ConfigureAwait(false);
            await WriteJsonAsync(context, values.ToArray()).ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path == "/api/v1/search")
        {
            var value = await services.LibraryQuery
                .SearchAsync(RequiredQuery(context, "q"))
                .ConfigureAwait(false);
            await WriteJsonAsync(context, value).ConfigureAwait(false);
            return true;
        }

        if (method == "POST" && path == "/api/v1/media/info")
        {
            var request = await ReadJsonAsync<RemotePathRequest>(context, cancellationToken).ConfigureAwait(false);
            var indexed = await RequireStreamablePathAsync(request.Path).ConfigureAwait(false);
            var info = await multimediaPlatform.GetMediaInfoAsync(indexed.Path).ConfigureAwait(false);
            await WriteJsonAsync(context, RemoteMediaInfo.FromModel(info)).ConfigureAwait(false);
            return true;
        }

        if (method == "POST" && path == "/api/v1/media/properties")
        {
            var request = await ReadJsonAsync<RemotePathRequest>(context, cancellationToken).ConfigureAwait(false);
            var indexed = await RequireStreamablePathAsync(request.Path).ConfigureAwait(false);
            var properties = await multimediaPlatform
                .GetMediaFilePropertiesAsync(indexed.Path, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(context, properties).ConfigureAwait(false);
            return true;
        }

        if (method == "POST" && path == "/api/v1/media/inspect")
        {
            var request = await ReadJsonAsync<RemotePathRequest>(context, cancellationToken).ConfigureAwait(false);
            var indexed = await RequireStreamablePathAsync(request.Path).ConfigureAwait(false);
            var report = await multimediaPlatform
                .InspectMediaAsync(indexed.Path, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(context, report).ConfigureAwait(false);
            return true;
        }

        if ((method == "GET" || method == "HEAD") && path == "/api/v1/media/file")
        {
            await WriteMediaAsync(context, RequiredQuery(context, "path"), cancellationToken).ConfigureAwait(false);
            return true;
        }

        if ((method == "GET" || method == "HEAD") && path == "/api/v1/media/artwork")
        {
            await WriteArtworkAsync(context, RequiredQuery(context, "path"), cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<DbMediaFile> RequireIndexedTrackAsync(string requestedPath) =>
        await indexedTracks.FindAsync(requestedPath).ConfigureAwait(false)
        ?? throw new RemoteApiException(
            HttpStatusCode.Forbidden,
            "media_not_indexed",
            "The requested media path is not present in the library index.");

    /// <summary>
    /// Widens the index check used for streaming and for metadata lookups to cover the video
    /// library: a video's own file and the sidecar audio/subtitle files the indexer recorded for it
    /// are all reachable, while anything else on the server's disk stays unreachable.
    /// </summary>
    /// <remarks>
    /// A media file sitting inside a configured video folder is reachable whether or not a scan has
    /// reached it yet, because that is exactly what browsing the library offers the client. What is
    /// served is still confined to media: the roots are shared as a library, not as a file system.
    /// </remarks>
    private async Task<StreamablePath> RequireStreamablePathAsync(string requestedPath)
    {
        if (await indexedTracks.FindAsync(requestedPath).ConfigureAwait(false) is { } track)
        {
            return new StreamablePath(track.Path, track.Id, track.FileLastModifiedDate);
        }

        if (await indexedVideos.FindAsync(requestedPath).ConfigureAwait(false) is { } video)
        {
            return new StreamablePath(requestedPath, video.Id, video.FileLastModifiedDate);
        }

        if ((FindLibraryMediaFile(requestedPath) ?? FindSearchablePhoto(requestedPath)) is { } file)
        {
            return file;
        }

        throw new RemoteApiException(
            HttpStatusCode.Forbidden,
            "media_not_indexed",
            "The requested media path is not present in the library index.");
    }

    /// <summary>
    /// A video or sidecar file inside a configured video folder that the index does not know about,
    /// or null when the path is outside the library, is not media, or does not exist.
    /// </summary>
    private StreamablePath? FindLibraryMediaFile(string requestedPath)
    {
        if (!TryResolveVideoLibraryPath(requestedPath, out var resolved))
        {
            return null;
        }

        var extension = Path.GetExtension(resolved.Path);
        if (!MediaContainerFormats.Video.Contains(extension) &&
            !MediaContainerFormats.Image.Contains(extension) &&
            !MediaContainerFormats.ExternalAudio.Contains(extension) &&
            !MediaContainerFormats.ExternalSubtitle.Contains(extension))
        {
            return null;
        }

        var info = new FileInfo(resolved.Path);
        return info.Exists ? new StreamablePath(resolved.Path, 0, info.LastWriteTimeUtc) : null;
    }

    private bool RemotePhotoSearchAvailable => credentials is not null &&
        services.PhotoIndex?.Status is { IsAvailable: true, Options.AllowRemoteSearch: true };

    private StreamablePath? FindSearchablePhoto(string requestedPath)
    {
        if (!RemotePhotoSearchAvailable || !MediaContainerFormats.IsImage(requestedPath))
            return null;
        try
        {
            var full = Path.GetFullPath(requestedPath);
            foreach (var configured in services.LibraryLocation.GetLibraryFolderPaths().Concat(services.LibraryLocation.GetVideoFolderPaths()))
            {
                var root = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison()))
                    continue;
                var info = new FileInfo(full);
                if (!info.Exists || info.LinkTarget is not null)
                    return null;
                var parent = info.Directory;
                while (parent is not null && !PathsEqual(parent.FullName, root))
                {
                    if (parent.LinkTarget is not null)
                        return null;
                    parent = parent.Parent;
                }
                return new StreamablePath(full, 0, info.LastWriteTimeUtc);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("PhotoSearch", "Could not resolve a photo inside the configured library roots.", exception);
        }
        return null;
    }

    /// <summary>
    /// Resolves media accepted by the preview route but not the raw-file route: a disc image or a
    /// recognized DVD/Blu-ray folder that this server's FFmpeg build can open.
    /// </summary>
    private async Task<StreamablePath?> FindLibraryDiscPreviewSourceAsync(
        string requestedPath,
        CancellationToken cancellationToken)
    {
        if (!TryResolveVideoLibraryPath(requestedPath, out var resolved) ||
            multimediaPlatform is not IVideoDiscPlatformIO discPlatform)
        {
            return null;
        }

        var file = new FileInfo(resolved.Path);
        if (file.Exists && MediaContainerFormats.DiscImage.Contains(file.Extension))
        {
            bool supportsDiscImages = discPlatform.SupportsVideoDisc(VideoMediaKind.Dvd) ||
                discPlatform.SupportsVideoDisc(VideoMediaKind.BluRay);
            return supportsDiscImages
                ? new StreamablePath(file.FullName, 0, file.LastWriteTimeUtc)
                : null;
        }

        if (!Directory.Exists(resolved.Path))
        {
            return null;
        }

        VideoDiscStructureInfo? disc = await VideoDiscStructure
            .DetectAsync(multimediaPlatform, resolved.Path, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return disc is not null && discPlatform.SupportsVideoDisc(disc.MediaKind)
            ? new StreamablePath(resolved.Path, 0, disc.MarkerLastModified)
            : null;
    }

    private readonly record struct StreamablePath(string Path, int Id, DateTimeOffset LastModified)
    {
        public string EntityTag(long length) => $"\"{Id:x}-{LastModified.UtcTicks:x}-{length:x}\"";
    }

    private async Task WriteVideoPreviewAsync(
        HttpListenerContext context,
        string requestedPath,
        CancellationToken cancellationToken)
    {
        StreamablePath source;
        if (await indexedVideos.FindAsync(requestedPath).ConfigureAwait(false) is { } video)
        {
            source = new StreamablePath(video.Path, video.Id, video.FileLastModifiedDate);
        }
        else if ((FindLibraryMediaFile(requestedPath) ?? FindSearchablePhoto(requestedPath)) is { } file)
        {
            source = file;
        }
        else if (await FindLibraryDiscPreviewSourceAsync(requestedPath, cancellationToken)
            .ConfigureAwait(false) is { } disc)
        {
            source = disc;
        }
        else
        {
            throw new RemoteApiException(
                HttpStatusCode.Forbidden,
                "media_not_indexed",
                "The requested media path is not present in the library index.");
        }

        var bytes = MediaContainerFormats.Image.Contains(Path.GetExtension(source.Path))
            ? await CreatePhotoPreviewAsync(source.Path, cancellationToken).ConfigureAwait(false)
            : await services.VideoLibraryQuery
                .GetVideoPreviewAsync(source.Path, cancellationToken)
                .ConfigureAwait(false);
        if (bytes is { Length: > 0 } && VideoPreviewFactory.IsJpegXl(bytes) &&
            !string.Equals(context.Request.QueryString["jxl"], "1", StringComparison.Ordinal))
        {
            bytes = await ConvertPreviewToJpegAsync(bytes, source.Path, cancellationToken).ConfigureAwait(false);
        }

        if (bytes is null || bytes.Length == 0)
        {
            // Nothing could be made of it, which the client draws its placeholder for; not an error
            // worth failing the tile over.
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            context.Response.Headers["Cache-Control"] = "private, max-age=300";
            return;
        }

        var etag = source.EntityTag(bytes.LongLength);
        context.Response.Headers["ETag"] = etag;
        context.Response.Headers["Cache-Control"] = "private, max-age=86400";
        context.Response.ContentType = DetectImageContentType(bytes);
        context.Response.ContentLength64 = bytes.LongLength;
        if (string.Equals(context.Request.Headers["If-None-Match"], etag, StringComparison.Ordinal))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotModified;
            return;
        }

        if (context.Request.HttpMethod != "HEAD")
        {
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]?> CreatePhotoPreviewAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var thumbnails = ThumbnailMemoryCache.Shared;
        var cached = await thumbnails.TryGetAsync(ThumbnailCacheKind.Photo, path, cancellationToken)
            .ConfigureAwait(false);
        if (cached.Found)
        {
            return cached.Thumbnail;
        }

        byte[]? preview;
        try
        {
            preview = await Task.Run(
                async () =>
                {
                    await using var stream = File.OpenRead(path);
                    return await VideoPreviewFactory.CreatePhotoPreviewAsync(stream, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Warning("Photo", $"Could not create preview for {path}.", exception);
            preview = null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await thumbnails.SetAsync(ThumbnailCacheKind.Photo, path, preview, cancellationToken)
            .ConfigureAwait(false);
        return preview;
    }

    /// <summary>
    /// A JPEG XL preview as JPEG with its HDR kept as a gain map, for clients that did not ask for JPEG XL, or null
    /// when it cannot be converted.
    /// </summary>
    private static async Task<byte[]?> ConvertPreviewToJpegAsync(byte[] preview, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => VideoPreviewFactory.ConvertToJpegAsync(preview, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Warning("Preview", $"Could not convert the preview of {path} to JPEG.", exception);
            return null;
        }
    }

    /// <summary>
    /// Lists the subfolders of one video library folder together with either its subtitle files or
    /// its videos, or the library roots themselves when no folder is named. This is the only route
    /// that reads a path the index has never seen, so every path it touches is checked against the
    /// configured roots first.
    /// </summary>
    /// <param name="media">
    /// Whether the client is browsing the library itself, in which case the videos and pictures are
    /// listed with the sidecar tracks lying beside them; otherwise it is picking a subtitle for a
    /// video it has.
    /// </param>
    private async Task<RemoteBrowseResult> BrowseVideoLibraryAsync(
        string? requestedPath,
        bool media,
        CancellationToken cancellationToken)
    {
        var listing = await ListVideoLibraryFolderAsync(requestedPath, media, cancellationToken)
            .ConfigureAwait(false);
        if (!media)
        {
            return listing;
        }

        var listings = new Dictionary<string, RemoteBrowseResult?>
        {
            [listing.Path ?? string.Empty] = listing,
        };

        async Task<RemoteBrowseResult?> ReadFolderAsync(string? path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = path ?? string.Empty;
            if (!listings.TryGetValue(key, out var found))
            {
                try
                {
                    found = await ListVideoLibraryFolderAsync(path, true, cancellationToken).ConfigureAwait(false);
                }
                catch (RemoteApiException exception) when (
                    exception.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                {
                    found = null;
                }

                listings[key] = found;
            }

            return found;
        }

        async Task<string[]> CollectPreviewPathsAsync(string? path)
        {
            var paths = new List<string>(MaximumFolderPreviewPaths);
            var queue = new Queue<string?>();
            queue.Enqueue(path);
            for (var visited = 0; queue.Count > 0 && visited < MaximumPreviewFolders; visited++)
            {
                var current = await ReadFolderAsync(queue.Dequeue()).ConfigureAwait(false);
                if (current is null)
                {
                    continue;
                }

                foreach (var entry in current.Entries)
                {
                    if (entry.IsFolder)
                    {
                        queue.Enqueue(entry.Path);
                    }
                }

                foreach (var entry in current.Entries
                    .Where(entry => !entry.IsFolder)
                    .OrderBy(entry => entry.MediaKind == VideoMediaKind.File &&
                        MediaContainerFormats.IsImage(entry.Path) ? 1 : 0))
                {
                    paths.Add(entry.Path);
                    if (paths.Count == MaximumFolderPreviewPaths)
                    {
                        return paths.ToArray();
                    }
                }
            }

            return paths.ToArray();
        }

        var entries = new RemoteFileEntry[listing.Entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = listing.Entries[index];
            entries[index] = entry.IsFolder
                ? entry with
                {
                    PreviewPaths = await CollectPreviewPathsAsync(entry.Path).ConfigureAwait(false),
                    ItemCount = listings[entry.Path]?.Entries.Length,
                }
                : entry;
        }

        return listing with
        {
            Entries = entries,
            PreviewPaths = await CollectPreviewPathsAsync(listing.Path).ConfigureAwait(false),
        };
    }

    private async Task<RemoteBrowseResult> ListVideoLibraryFolderAsync(
        string? requestedPath,
        bool media,
        CancellationToken cancellationToken)
    {
        var discPlatform = multimediaPlatform as IVideoDiscPlatformIO;
        bool supportsDiscImages = discPlatform is not null &&
            (discPlatform.SupportsVideoDisc(VideoMediaKind.Dvd) ||
                discPlatform.SupportsVideoDisc(VideoMediaKind.BluRay));
        if (string.IsNullOrEmpty(requestedPath))
        {
            var roots = services.LibraryLocation.GetVideoFolderPaths()
                .Select(root => Path.GetFullPath(root))
                .Where(Directory.Exists)
                .Select(root => new RemoteFileEntry(root, root, true))
                .ToArray();
            return new RemoteBrowseResult(null, [], roots);
        }

        var (folder, root) = RequireVideoLibraryPath(requestedPath);
        if (!Directory.Exists(folder))
        {
            throw new RemoteApiException(
                HttpStatusCode.NotFound,
                "folder_not_found",
                "The requested folder no longer exists.");
        }

        var entries = new List<RemoteFileEntry>();
        try
        {
            string[] directories = Directory.EnumerateDirectories(folder).ToArray();
            if (media && await VideoDiscStructure
                .DetectAsync(multimediaPlatform, folder, directories, cancellationToken)
                .ConfigureAwait(false) is { } currentDisc)
            {
                return new RemoteBrowseResult(
                    folder,
                    BuildAncestors(folder, root),
                    discPlatform?.SupportsVideoDisc(currentDisc.MediaKind) == true
                        ? [new RemoteFileEntry(
                            Path.GetFileName(folder),
                            folder,
                            false,
                            MediaKind: currentDisc.MediaKind,
                            LastModifiedUtc: currentDisc.MarkerLastModified)]
                        : []);
            }

            foreach (string child in directories)
            {
                VideoDiscStructureInfo? disc = media
                    ? await VideoDiscStructure
                        .DetectAsync(multimediaPlatform, child, cancellationToken: cancellationToken)
                        .ConfigureAwait(false)
                    : null;
                if (disc is not null && discPlatform?.SupportsVideoDisc(disc.MediaKind) != true)
                {
                    continue;
                }

                entries.Add(new RemoteFileEntry(
                    Path.GetFileName(child),
                    child,
                    disc is null,
                    MediaKind: disc?.MediaKind ?? VideoMediaKind.File,
                    LastModifiedUtc: disc?.MarkerLastModified ?? default));
            }

            // Enumerated as FileInfo rather than paths so a picture's size, which is all its tile
            // has to say about it, comes off the same directory read.
            var files = new DirectoryInfo(folder).EnumerateFiles().ToArray();
            var paths = Array.ConvertAll(files, file => file.FullName);
            entries.AddRange(files
                .Where(file => media
                    ? (MediaContainerFormats.IsVideo(file.FullName) &&
                            (!MediaContainerFormats.DiscImage.Contains(file.Extension) || supportsDiscImages)) ||
                        MediaContainerFormats.IsImage(file.FullName)
                    : MediaContainerFormats.ExternalSubtitle.Contains(file.Extension))
                .Select(file => new RemoteFileEntry(
                    file.Name,
                    file.FullName,
                    false,
                    media && MediaContainerFormats.IsVideo(file.FullName)
                        ? [.. MediaContainerFormats.FindSidecars(file.FullName, paths, MediaContainerFormats.ExternalAudio)]
                        : null,
                    media && MediaContainerFormats.IsVideo(file.FullName)
                        ? [.. MediaContainerFormats.FindSidecars(file.FullName, paths, MediaContainerFormats.ExternalSubtitle)]
                        : null,
                    file.Length,
                    LastModifiedUtc: file.LastWriteTimeUtc,
                    CreatedUtc: file.CreationTimeUtc > DateTime.UnixEpoch ? file.CreationTimeUtc : null)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RemoteApiException(
                HttpStatusCode.Forbidden,
                "folder_unreadable",
                "The requested folder cannot be read.");
        }

        return new RemoteBrowseResult(
            folder,
            BuildAncestors(folder, root),
            entries
                .OrderBy(entry => entry.IsFolder ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray());
    }

    /// <summary>The chain from <paramref name="root"/> down to <paramref name="folder"/> inclusive.</summary>
    private static RemoteFileEntry[] BuildAncestors(string folder, string root)
    {
        var chain = new List<RemoteFileEntry>();
        for (var step = folder; step is not null; step = Path.GetDirectoryName(step))
        {
            chain.Add(new RemoteFileEntry(
                PathsEqual(step, root) ? step : Path.GetFileName(step),
                step,
                true));
            if (PathsEqual(step, root))
            {
                break;
            }
        }

        chain.Reverse();
        return chain.ToArray();
    }

    private async Task WriteSubtitleFileAsync(
        HttpListenerContext context,
        string requestedPath,
        CancellationToken cancellationToken)
    {
        var (file, _) = RequireVideoLibraryPath(requestedPath);
        if (!MediaContainerFormats.ExternalSubtitle.Contains(Path.GetExtension(file)) || !File.Exists(file))
        {
            throw new RemoteApiException(
                HttpStatusCode.NotFound,
                "subtitle_not_found",
                "The requested subtitle file does not exist.");
        }

        if (new FileInfo(file).Length > MaximumSubtitleBytes)
        {
            throw new RemoteApiException(
                HttpStatusCode.RequestEntityTooLarge,
                "subtitle_too_large",
                "The requested subtitle file is too large.");
        }

        var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength64 = bytes.LongLength;
        if (context.Request.HttpMethod != "HEAD")
        {
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves a browsed path and the video library root it belongs to. Normalizing before the
    /// check is what stops a <c>..</c> segment from walking out of the library.
    /// </summary>
    private (string Path, string Root) RequireVideoLibraryPath(string requestedPath) =>
        TryResolveVideoLibraryPath(requestedPath, out var resolved)
            ? resolved
            : throw new RemoteApiException(
                HttpStatusCode.Forbidden,
                "path_outside_library",
                "The requested path is not inside a video library folder.");

    private bool TryResolveVideoLibraryPath(string requestedPath, out (string Path, string Root) resolved)
    {
        string full;
        try
        {
            full = Path.GetFullPath(requestedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            resolved = default;
            return false;
        }

        foreach (var configured in services.LibraryLocation.GetVideoFolderPaths())
        {
            var root = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar);
            if (PathsEqual(full, root) ||
                full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison()))
            {
                resolved = (full, root);
                return true;
            }
        }

        resolved = default;
        return false;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar),
            PathComparison());

    private async Task WriteMediaAsync(
        HttpListenerContext context,
        string requestedPath,
        CancellationToken cancellationToken)
    {
        var indexed = await RequireStreamablePathAsync(requestedPath).ConfigureAwait(false);
        await using var stream = await multimediaPlatform
            .OpenMediaFileAsync(indexed.Path, cancellationToken)
            .ConfigureAwait(false);
        if (!stream.CanSeek)
        {
            throw new RemoteApiException(HttpStatusCode.ServiceUnavailable, "media_unavailable", "The media stream is not seekable.");
        }

        var length = stream.Length;
        var etag = indexed.EntityTag(length);
        var contentType = MediaContentType(indexed.Path);
        context.Response.Headers["Accept-Ranges"] = "bytes";
        context.Response.Headers["ETag"] = etag;
        context.Response.ContentType = contentType;

        if (string.Equals(context.Request.Headers["If-None-Match"], etag, StringComparison.Ordinal))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotModified;
            return;
        }

        List<ByteRange>? ranges;
        try
        {
            ranges = ParseRanges(context.Request.Headers["Range"], length);
        }
        catch (RemoteApiException ex) when (ex.Status == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            context.Response.Headers["Content-Range"] = $"bytes */{length}";
            throw;
        }

        try
        {
            if (ranges is { Count: > 1 })
            {
                await WriteMediaRangesAsync(context, stream, ranges, length, contentType, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var range = ranges?[0];
            var start = range?.Start ?? 0;
            var end = range?.End ?? (length - 1);
            var count = Math.Max(0, end - start + 1);
            if (range is not null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
                context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
            }

            context.Response.ContentLength64 = count;
            if (context.Request.HttpMethod == "HEAD" || count == 0)
            {
                return;
            }

            stream.Seek(start, SeekOrigin.Begin);
            await CopyRangeAsync(stream, context.Response.OutputStream, count, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpListenerException) when (!cancellationToken.IsCancellationRequested)
        {
            // Clients close a media response early whenever they seek elsewhere or stop reading ahead.
            context.Response.Abort();
        }
    }

    /// <summary>Writes ascending, non-overlapping ranges as a <c>multipart/byteranges</c> response.</summary>
    private static async Task WriteMediaRangesAsync(
        HttpListenerContext context,
        Stream stream,
        IReadOnlyList<ByteRange> ranges,
        long length,
        string contentType,
        CancellationToken cancellationToken)
    {
        var boundary = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var partHeaders = new byte[ranges.Count][];
        long contentLength = 0;
        for (var i = 0; i < ranges.Count; i++)
        {
            var range = ranges[i];
            partHeaders[i] = Encoding.ASCII.GetBytes(
                $"{(i == 0 ? "" : "\r\n")}--{boundary}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Range: bytes {range.Start}-{range.End}/{length}\r\n\r\n");
            contentLength += partHeaders[i].Length + range.End - range.Start + 1;
        }

        var closing = Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n");
        contentLength += closing.Length;

        context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
        context.Response.ContentType = $"multipart/byteranges; boundary={boundary}";
        context.Response.ContentLength64 = contentLength;
        if (context.Request.HttpMethod == "HEAD")
        {
            return;
        }

        var output = context.Response.OutputStream;
        for (var i = 0; i < ranges.Count; i++)
        {
            await output.WriteAsync(partHeaders[i], cancellationToken).ConfigureAwait(false);
            stream.Seek(ranges[i].Start, SeekOrigin.Begin);
            await CopyRangeAsync(stream, output, ranges[i].End - ranges[i].Start + 1, cancellationToken)
                .ConfigureAwait(false);
        }

        await output.WriteAsync(closing, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteArtworkAsync(
        HttpListenerContext context,
        string requestedPath,
        CancellationToken cancellationToken)
    {
        var indexed = await RequireIndexedTrackAsync(requestedPath).ConfigureAwait(false);
        byte[]? bytes;
        try
        {
            bytes = await multimediaPlatform
                .GetAlbumCoverAsync(indexed.Path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            bytes = null;
        }

        if (bytes is null || bytes.Length == 0)
        {
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            context.Response.Headers["Cache-Control"] = "private, max-age=300";
            return;
        }

        var etag = CreateEntityTag(indexed, bytes.LongLength);
        context.Response.Headers["ETag"] = etag;
        context.Response.Headers["Cache-Control"] = "private, max-age=86400";
        context.Response.ContentType = DetectImageContentType(bytes);
        context.Response.ContentLength64 = bytes.LongLength;
        if (string.Equals(context.Request.Headers["If-None-Match"], etag, StringComparison.Ordinal))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotModified;
            return;
        }

        if (context.Request.HttpMethod != "HEAD")
        {
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
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
            throw new RemoteApiException(HttpStatusCode.MethodNotAllowed, "method_not_allowed", "Static files support GET and HEAD only.");
        }

        if (staticFilesPath is null || !Directory.Exists(staticFilesPath))
        {
            throw new RemoteApiException(HttpStatusCode.NotFound, "web_client_unavailable", "This server hosts the API only.");
        }

        var decodedPath = Uri.UnescapeDataString(requestPath);
        var relativePath = decodedPath switch
        {
            "/" => "index.html",
            "/diag" => "index.html",
            _ => decodedPath.TrimStart('/'),
        };
        var fullPath = Path.GetFullPath(Path.Combine(
            staticFilesPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = staticFilesPath.EndsWith(Path.DirectorySeparatorChar)
            ? staticFilesPath
            : staticFilesPath + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, PathComparison()) || !File.Exists(fullPath))
        {
            throw new RemoteApiException(HttpStatusCode.NotFound, "not_found", "Static file not found.");
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
            AppendVary(context.Response, "Accept-Encoding");
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

    private static bool IsApiPath(string path) =>
        path == "/api/v1" || path.StartsWith("/api/v1/", StringComparison.Ordinal);

    private static bool IsLongPollRequest(HttpListenerRequest request)
    {
        if (request.HttpMethod != "GET")
        {
            return false;
        }

        var path = request.Url?.AbsolutePath.TrimEnd('/');
        return path is "/api/v1/library/scan/events";
    }

    private SemaphoreSlim SlotsFor(HttpListenerRequest request)
    {
        if (IsLongPollRequest(request))
        {
            return longPollRequestSlots;
        }

        var path = request.Url?.AbsolutePath.TrimEnd('/');
        if (request.HttpMethod is "GET" or "HEAD" && path == "/api/v1/media/file")
        {
            return mediaFileSlots;
        }

        return request.HttpMethod == "POST" &&
            path == "/api/v1/" + RemoteStreamDefaults.PacketsPath
            ? packetStreamSlots
            : requestSlots;
    }

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
        ".js" => "text/javascript; charset=utf-8",
        ".mjs" => "text/javascript; charset=utf-8",
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

    private static async Task CopyRangeAsync(
        Stream source,
        Stream destination,
        long count,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        while (count > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            count -= read;
        }
    }

    /// <summary>
    /// Parses a byte-range header into ascending, non-overlapping ranges, merging adjacent ones.
    /// </summary>
    private static List<ByteRange>? ParseRanges(string? header, long length)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            throw new RemoteApiException(HttpStatusCode.RequestedRangeNotSatisfiable, "invalid_range", "Only byte ranges are supported.");
        }

        var specs = header[6..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (specs.Length == 0)
        {
            throw new RemoteApiException(HttpStatusCode.RequestedRangeNotSatisfiable, "invalid_range", "The byte range is invalid.");
        }

        if (specs.Length > MaximumByteRanges)
        {
            throw new RemoteApiException(HttpStatusCode.RequestedRangeNotSatisfiable, "invalid_range", "Too many byte ranges were requested.");
        }

        var ranges = new List<ByteRange>(specs.Length);
        foreach (var spec in specs)
        {
            var range = ParseRange(spec, length);
            if (ranges.Count > 0 && range.Start <= ranges[^1].End)
            {
                throw new RemoteApiException(
                    HttpStatusCode.RequestedRangeNotSatisfiable,
                    "invalid_range",
                    "Byte ranges must be in ascending order without overlap.");
            }

            if (ranges.Count > 0 && range.Start == ranges[^1].End + 1)
            {
                ranges[^1] = ranges[^1] with { End = range.End };
            }
            else
            {
                ranges.Add(range);
            }
        }

        return ranges;
    }

    private static ByteRange ParseRange(string spec, long length)
    {
        var parts = spec.Split('-', count: 2);
        if (parts.Length != 2)
        {
            throw new RemoteApiException(HttpStatusCode.RequestedRangeNotSatisfiable, "invalid_range", "The byte range is invalid.");
        }

        long start;
        long end;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], out var suffixLength) || suffixLength <= 0)
            {
                throw new RemoteApiException(HttpStatusCode.RequestedRangeNotSatisfiable, "invalid_range", "The byte range is invalid.");
            }

            suffixLength = Math.Min(suffixLength, length);
            start = length - suffixLength;
            end = length - 1;
        }
        else
        {
            if (!long.TryParse(parts[0], out start) || start < 0 || start >= length)
            {
                throw new RemoteApiException(HttpStatusCode.RequestedRangeNotSatisfiable, "invalid_range", "The byte range is outside the media file.");
            }

            if (parts[1].Length == 0)
            {
                end = length - 1;
            }
            else if (!long.TryParse(parts[1], out end) || end < start)
            {
                throw new RemoteApiException(HttpStatusCode.RequestedRangeNotSatisfiable, "invalid_range", "The byte range is invalid.");
            }

            end = Math.Min(end, length - 1);
        }

        return new ByteRange(start, end);
    }

    private static string CreateEntityTag(DbMediaFile file, long length) =>
        $"\"{file.Id:x}-{file.FileLastModifiedDate.UtcTicks:x}-{length:x}\"";

    private static string MediaContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".m4a" or ".mp4" => "audio/mp4",
        ".ogg" or ".oga" => "audio/ogg",
        ".opus" => "audio/opus",
        ".wav" => "audio/wav",
        ".aac" => "audio/aac",
        ".mka" => "audio/x-matroska",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".m4v" => "video/mp4",
        ".ts" or ".m2ts" or ".mts" => "video/mp2t",
        ".flv" => "video/x-flv",
        ".wmv" => "video/x-ms-wmv",
        ".mpg" or ".mpeg" => "video/mpeg",
        ".ogv" => "video/ogg",
        ".rmvb" => "application/vnd.rn-realmedia-vbr",
        ".rv" or ".rvx" => "video/vnd.rn-realvideo",
        ".3gp" => "video/3gpp",
        ".3g2" or ".3gp2" or ".3gpp2" => "video/3gpp2",
        ".dv" => "video/dv",
        ".fli" or ".flc" => "video/fli",
        ".m1u" or ".m4u" or ".mxu" => "video/vnd.mpegurl",
        ".asf" => "video/x-ms-asf",
        ".asx" or ".wax" or ".wmx" or ".wvx" => "video/x-ms-wmx",
        ".ass" or ".ssa" or ".srt" or ".vtt" or ".sub" or ".lrc" => "text/plain; charset=utf-8",

        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".heic" => "image/heic",
        ".heif" or ".hif" => "image/heif",
        ".jxl" => "image/jxl",
        ".jxr" => "image/jxr",
        ".wdp" or ".hdp" => "image/vnd.ms-photo",
        ".dng" => "image/x-adobe-dng",
        ".bmp" or ".dib" => "image/bmp",
        ".gif" => "image/gif",
        ".tif" or ".tiff" => "image/tiff",
        ".ppm" or ".pgm" or ".pnm" => "image/x-portable-pixmap",
        ".tga" => "image/x-targa",
        ".exr" => "image/x-exr",
        ".hdr" => "image/vnd.radiance",
        _ => "application/octet-stream",
    };

    private static string DetectImageContentType(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return "image/webp";
        }

        if (VideoPreviewFactory.IsJpegXl(bytes))
        {
            return "image/jxl";
        }

        return "image/jpeg";
    }

    private static string RequiredQuery(HttpListenerContext context, string name) =>
        context.Request.QueryString[name] is { Length: > 0 } value
            ? value
            : throw new RemoteApiException(HttpStatusCode.BadRequest, "missing_query", $"Query parameter '{name}' is required.");

    private static MediaFilePageRequest ParseMediaFilePageRequest(HttpListenerContext context)
    {
        var sortText = context.Request.QueryString["sort"];
        if (!Enum.TryParse(sortText, ignoreCase: true, out MediaFileSortField sort))
        {
            if (!string.IsNullOrEmpty(sortText))
            {
                throw new RemoteApiException(
                    HttpStatusCode.BadRequest,
                    "invalid_sort",
                    $"Unsupported track sort field '{sortText}'.");
            }

            sort = MediaFileSortField.Title;
        }

        var direction = context.Request.QueryString["direction"];
        var ascending = direction?.ToLowerInvariant() switch
        {
            null or "" or "asc" => true,
            "desc" => false,
            _ => throw new RemoteApiException(
                HttpStatusCode.BadRequest,
                "invalid_direction",
                "Track sort direction must be 'asc' or 'desc'."),
        };

        var limitText = context.Request.QueryString["limit"];
        var pageSize = MediaFilePageRequest.DefaultPageSize;
        if (!string.IsNullOrEmpty(limitText) && !int.TryParse(limitText, out pageSize))
        {
            throw new RemoteApiException(
                HttpStatusCode.BadRequest,
                "invalid_limit",
                "Track page limit must be an integer.");
        }

        var request = new MediaFilePageRequest(
            sort,
            ascending,
            pageSize,
            context.Request.QueryString["cursor"],
            context.Request.QueryString["folder"]);
        try
        {
            request.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new RemoteApiException(
                HttpStatusCode.BadRequest,
                "invalid_page",
                exception.Message);
        }

        return request;
    }

    private static PageRequest ParsePageRequest(HttpListenerContext context)
    {
        var request = new PageRequest(
            ParsePageSize(context),
            context.Request.QueryString["cursor"]);
        ValidatePageRequest(request.Validate);
        return request;
    }

    private static VideoPickOrder ParseVideoPickOrder(HttpListenerContext context)
    {
        var orderText = context.Request.QueryString["order"];
        if (string.IsNullOrEmpty(orderText))
        {
            return VideoPickOrder.RecentlyAdded;
        }

        return Enum.TryParse(orderText, ignoreCase: true, out VideoPickOrder order)
            ? order
            : throw new RemoteApiException(
                HttpStatusCode.BadRequest,
                "invalid_order",
                $"Unsupported video pick order '{orderText}'.");
    }

    private static AlbumPageRequest ParseAlbumPageRequest(HttpListenerContext context)
    {
        var sortText = context.Request.QueryString["sort"];
        if (!Enum.TryParse(sortText, ignoreCase: true, out AlbumSortField sort))
        {
            if (!string.IsNullOrEmpty(sortText))
            {
                throw new RemoteApiException(
                    HttpStatusCode.BadRequest,
                    "invalid_sort",
                    $"Unsupported album sort field '{sortText}'.");
            }
            sort = AlbumSortField.Title;
        }

        var request = new AlbumPageRequest(
            sort,
            ParseDirection(context, "album"),
            ParsePageSize(context),
            context.Request.QueryString["cursor"]);
        ValidatePageRequest(request.Validate);
        return request;
    }

    private static ArtistPageRequest ParseArtistPageRequest(HttpListenerContext context)
    {
        var request = new ArtistPageRequest(
            ParseDirection(context, "artist"),
            ParsePageSize(context),
            context.Request.QueryString["cursor"]);
        ValidatePageRequest(request.Validate);
        return request;
    }

    private static int ParsePageSize(HttpListenerContext context)
    {
        var text = context.Request.QueryString["limit"];
        if (string.IsNullOrEmpty(text))
        {
            return MediaFilePageRequest.DefaultPageSize;
        }
        if (int.TryParse(text, out var value))
        {
            return value;
        }
        throw new RemoteApiException(
            HttpStatusCode.BadRequest,
            "invalid_limit",
            "Page limit must be an integer.");
    }

    private static bool ParseDirection(HttpListenerContext context, string itemType) =>
        context.Request.QueryString["direction"]?.ToLowerInvariant() switch
        {
            null or "" or "asc" => true,
            "desc" => false,
            _ => throw new RemoteApiException(
                HttpStatusCode.BadRequest,
                "invalid_direction",
                $"{itemType} sort direction must be 'asc' or 'desc'."),
        };

    private static void ValidatePageRequest(Action validate)
    {
        try
        {
            validate();
        }
        catch (ArgumentException exception)
        {
            throw new RemoteApiException(HttpStatusCode.BadRequest, "invalid_page", exception.Message);
        }
    }

    private static async Task<bool> HandleBrowseIndexAsync<T>(HttpListenerContext context, string path,
        ILibraryBrowseIndexQuery<T>? query, CancellationToken cancellationToken)
    {
        var batch = path.EndsWith("/items", StringComparison.Ordinal);
        if (context.Request.HttpMethod != (batch ? "POST" : "GET"))
        {
            return false;
        }
        if (query is null)
        {
            throw new RemoteApiException(HttpStatusCode.NotImplemented, "browse_index_unavailable",
                "This library does not provide browse indexes.");
        }
        try
        {
            if (batch)
            {
                var request = await ReadJsonAsync<LibraryBrowseItemsRequest>(context, cancellationToken).ConfigureAwait(false);
                request.Validate();
                await WriteJsonAsync(context, await query.GetBrowseItemsAsync(request, cancellationToken)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            }
            else
            {
                var request = new LibraryBrowseIndexRequest(context.Request.QueryString["folder"]);
                await WriteJsonAsync(context, await query.GetBrowseIndexAsync(request, cancellationToken)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
        catch (LibraryBrowseIndexExpiredException exception)
        {
            throw new RemoteApiException(HttpStatusCode.Gone, "browse_index_expired", exception.Message);
        }
        catch (ArgumentException exception)
        {
            throw new RemoteApiException(HttpStatusCode.BadRequest, "invalid_browse_index", exception.Message);
        }
        return true;
    }

    private static async Task<T> ExecutePageQueryAsync<T>(Func<Task<T>> query)
    {
        try
        {
            return await query().ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            throw new RemoteApiException(HttpStatusCode.BadRequest, "invalid_page", exception.Message);
        }
    }

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

    private static async Task<T> ReadJsonAsync<T>(HttpListenerContext context, CancellationToken cancellationToken)
    {
        // A cross-site form or text/plain post skips the CORS preflight; requiring JSON makes the
        // browser ask first, so only listed origins can send a body at all.
        var mediaType = context.Request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new RemoteApiException(
                HttpStatusCode.UnsupportedMediaType,
                "unsupported_media_type",
                "The request body must be application/json.");
        }

        EnsureRequestBodySize(context.Request);
        using var input = new LimitedReadStream(context.Request.InputStream, MaximumRequestBodyBytes);
        var value = await JsonSerializer.DeserializeAsync(
            input,
            JsonTypeInfo<T>(),
            cancellationToken).ConfigureAwait(false);
        return value ?? throw new RemoteApiException(HttpStatusCode.BadRequest, "empty_body", "A JSON request body is required.");
    }

    private static void EnsureRequestBodySize(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaximumRequestBodyBytes)
        {
            throw new RemoteApiException(HttpStatusCode.RequestEntityTooLarge, "body_too_large", "The request body is too large.");
        }
    }

    private static async Task WriteJsonAsync<T>(HttpListenerContext context, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonTypeInfo<T>());
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.LongLength;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteProblemAsync(
        HttpListenerContext context,
        HttpStatusCode status,
        string code,
        string detail)
    {
        if (context.Response.OutputStream.CanWrite)
        {
            var problem = new RemoteProblem(code, status.ToString(), (int)status, detail);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(problem, RemoteApiJsonContext.Default.RemoteProblem);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/problem+json; charset=utf-8";
            context.Response.ContentLength64 = bytes.LongLength;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
    }

    private static JsonTypeInfo<T> JsonTypeInfo<T>() =>
        (JsonTypeInfo<T>?)RemoteApiJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new InvalidOperationException($"No generated JSON metadata exists for {typeof(T)}.");

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        AppLog.Information("RemoteApi.Server", "Stopping remote API server.");
        scanEvents.Dispose();
        indexedTracks.Dispose();
        indexedVideos.Dispose();
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

        requestSlots.Dispose();
        longPollRequestSlots.Dispose();
        packetStreamSlots.Dispose();
        mediaFileSlots.Dispose();
        shutdown.Dispose();
    }

    private sealed record ByteRange(long Start, long End);

    /// <summary>
    /// Path to track lookup backing the "is this file in the library?" check.
    /// </summary>
    /// <remarks>
    /// Every media range request runs that check, and a single track may require hundreds of small
    /// ranges. The cached listing avoids materializing the whole media file table for each request.
    /// It remains valid until a scan changes it, with a short expiry for edits made outside a scan.
    /// </remarks>
    private sealed class IndexedTrackCache : IDisposable
    {
        private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

        private readonly ILibraryQueryService query;
        private readonly ILibraryScanService scan;
        private readonly SemaphoreSlim gate = new(1, 1);
        private Dictionary<string, DbMediaFile>? tracks;
        private long loadedAt;

        public IndexedTrackCache(ILibraryQueryService query, ILibraryScanService scan)
        {
            this.query = query;
            this.scan = scan;
            scan.ScanCompleted += OnScanCompleted;
        }

        public async Task<DbMediaFile?> FindAsync(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (Volatile.Read(ref tracks) is { } snapshot && !IsExpired())
            {
                return snapshot.GetValueOrDefault(path);
            }

            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var current = Volatile.Read(ref tracks);
                if (current is null || IsExpired())
                {
                    var files = await query.GetMediaFilesAsync().ConfigureAwait(false);
                    current = new Dictionary<string, DbMediaFile>(files.Count, StringComparer.Ordinal);
                    foreach (var file in files)
                    {
                        if (!string.IsNullOrEmpty(file.Path))
                        {
                            current[file.Path] = file;
                        }
                    }

                    Volatile.Write(ref loadedAt, Environment.TickCount64);
                    Volatile.Write(ref tracks, current);
                }

                return current.GetValueOrDefault(path);
            }
            finally
            {
                gate.Release();
            }
        }

        public void Dispose()
        {
            scan.ScanCompleted -= OnScanCompleted;
            gate.Dispose();
        }

        private bool IsExpired() =>
            Environment.TickCount64 - Volatile.Read(ref loadedAt) > MaxAge.TotalMilliseconds;

        private void OnScanCompleted(object? sender, LibraryScanCompletedEventArgs args) =>
            Volatile.Write(ref tracks, null);
    }

    /// <summary>
    /// The video counterpart of <see cref="IndexedTrackCache"/>. Sidecar audio and subtitle files
    /// are indexed alongside their video so that a client can stream all of a video's tracks
    /// without opening up the rest of the server's filesystem.
    /// </summary>
    private sealed class IndexedVideoCache : IDisposable
    {
        private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

        private readonly IVideoLibraryQueryService query;
        private readonly ILibraryScanService scan;
        private readonly SemaphoreSlim gate = new(1, 1);
        private Dictionary<string, DbVideoFile>? videos;
        private long loadedAt;

        public IndexedVideoCache(IVideoLibraryQueryService query, ILibraryScanService scan)
        {
            this.query = query;
            this.scan = scan;
            scan.ScanCompleted += OnScanCompleted;
        }

        public async Task<DbVideoFile?> FindAsync(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (Volatile.Read(ref videos) is { } snapshot && !IsExpired())
            {
                return snapshot.GetValueOrDefault(path);
            }

            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var current = Volatile.Read(ref videos);
                if (current is null || IsExpired())
                {
                    var files = await query.GetVideosAsync().ConfigureAwait(false);
                    current = new Dictionary<string, DbVideoFile>(files.Count, StringComparer.Ordinal);
                    foreach (var file in files)
                    {
                        if (string.IsNullOrEmpty(file.Path))
                        {
                            continue;
                        }

                        current[file.Path] = file;
                        foreach (var sidecar in file.ExternalAudioList)
                        {
                            current.TryAdd(sidecar, file);
                        }

                        foreach (var sidecar in file.ExternalSubtitleList)
                        {
                            current.TryAdd(sidecar, file);
                        }
                    }

                    Volatile.Write(ref loadedAt, Environment.TickCount64);
                    Volatile.Write(ref videos, current);
                }

                return current.GetValueOrDefault(path);
            }
            finally
            {
                gate.Release();
            }
        }

        public void Dispose()
        {
            scan.ScanCompleted -= OnScanCompleted;
            gate.Dispose();
        }

        private bool IsExpired() =>
            Environment.TickCount64 - Volatile.Read(ref loadedAt) > MaxAge.TotalMilliseconds;

        private void OnScanCompleted(object? sender, LibraryScanCompletedEventArgs args) =>
            Volatile.Write(ref videos, null);
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

    private sealed class LimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => bytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Record(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Record(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Record(read);
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            Record(read);
            return read;
        }

        private void Record(int read)
        {
            bytesRead += read;
            if (bytesRead > maximumBytes)
            {
                throw new RemoteApiException(
                    HttpStatusCode.RequestEntityTooLarge,
                    "body_too_large",
                    "The request body is too large.");
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RemoteApiException(HttpStatusCode status, string code, string message) : Exception(message)
    {
        public HttpStatusCode Status { get; } = status;

        public string Code { get; } = code;
    }

    private sealed class ScanEventBuffer : IDisposable
    {
        private const int Capacity = 256;
        private static readonly TimeSpan LongPollTimeout = TimeSpan.FromSeconds(25);

        private readonly object gate = new();
        private readonly ILibraryScanService service;
        private readonly List<RemoteScanEvent> events = [];
        private TaskCompletionSource changed = NewSignal();

        // Seeded from the clock so IDs keep increasing across server restarts and a client cursor
        // from the previous process never hides new events. Stays exact as a JavaScript number.
        private long nextId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

        public ScanEventBuffer(ILibraryScanService service)
        {
            this.service = service;
            service.ScanStarted += OnStarted;
            service.ScanProgressChanged += OnProgress;
            service.ScanWarning += OnWarning;
            service.ScanCompleted += OnCompleted;
        }

        /// <summary>No events, positioned after the newest one, for a client that starts listening now.</summary>
        public RemoteScanEvents Current()
        {
            lock (gate)
            {
                return new RemoteScanEvents([], nextId);
            }
        }

        public async Task<RemoteScanEvents> WaitAsync(long after, CancellationToken cancellationToken)
        {
            Task signal;
            lock (gate)
            {
                var available = AvailableAfter(after);
                if (available.Events.Length > 0)
                {
                    return available;
                }

                signal = changed.Task;
            }

            try
            {
                await signal.WaitAsync(LongPollTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            lock (gate)
            {
                return AvailableAfter(after);
            }
        }

        private RemoteScanEvents AvailableAfter(long after) =>
            new(events.Where(value => value.Id > after).ToArray(), nextId);

        private void OnStarted(object? sender, LibraryScanStartedEventArgs args) =>
            Add(new RemoteScanEvent(0, "started", args.Folders.ToArray()));

        private void OnProgress(object? sender, LibraryScanProgressEventArgs args) =>
            Add(new RemoteScanEvent(0, "progress", IndexedCount: args.IndexedCount));

        private void OnWarning(object? sender, LibraryScanWarningEventArgs args) =>
            Add(new RemoteScanEvent(0, "warning", Warning: args.Warning));

        private void OnCompleted(object? sender, LibraryScanCompletedEventArgs args) =>
            Add(new RemoteScanEvent(
                0,
                "completed",
                IndexedCount: args.IndexedCount,
                WarningCount: args.WarningCount)
            {
                HasChanges = args.HasChanges,
                VideoIndexedCount = args.VideoIndexedCount,
                ImageIndexedCount = args.ImageIndexedCount,
                Failed = args.Failed ? true : null,
            });

        private void Add(RemoteScanEvent value)
        {
            TaskCompletionSource previous;
            lock (gate)
            {
                events.Add(value with { Id = ++nextId });
                if (events.Count > Capacity)
                {
                    events.RemoveRange(0, events.Count - Capacity);
                }

                previous = changed;
                changed = NewSignal();
            }

            previous.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            service.ScanStarted -= OnStarted;
            service.ScanProgressChanged -= OnProgress;
            service.ScanWarning -= OnWarning;
            service.ScanCompleted -= OnCompleted;
            lock (gate)
            {
                changed.TrySetCanceled();
            }
        }
    }
}
