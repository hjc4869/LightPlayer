using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Remote;
using LightStudio.Logging;

namespace LightStudio.RemoteApi.Streaming;

/// <summary>
/// One client watching one video, for as long as its socket is open.
/// </summary>
/// <remarks>
/// <para>
/// The socket exists so the client can talk back. Packets are only sent against credit the client
/// has granted, which is the only way to bound what a browser holds: its receive buffer is not
/// something script can push back on, so a server that sends as fast as the link allows is a server
/// that fills memory it cannot see.
/// </para>
/// <para>
/// Seeking is a message rather than a new connection, so the containers stay open and stay warm
/// across it. Only one run may read them at a time, so a new run waits for the one it replaces to
/// let go.
/// </para>
/// </remarks>
internal sealed class PacketSocketConnection : IDisposable
{
    /// <summary>
    /// How long a socket may hold its slot without saying what it is for. Nothing has been proved
    /// about the client at that point, so it cannot be allowed to sit there.
    /// </summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly long requestId;
    private readonly WebSocket socket;
    private readonly Func<RemoteStreamHello, Task<StreamSession>> openSession;
    private readonly Func<string?, bool> isAuthorized;
    private readonly CancellationTokenSource lifetime = new();
    private readonly PacketSocketReader reader;
    private readonly PacketSocketWriter writer;
    private readonly PacketCreditGate credit = new();
    private StreamSession? session;
    private CancellationTokenSource? runLifetime;
    private Task runTask = Task.CompletedTask;
    private bool disposed;

    public PacketSocketConnection(
        long requestId,
        WebSocket socket,
        Func<RemoteStreamHello, Task<StreamSession>> openSession,
        Func<string?, bool> isAuthorized)
    {
        this.requestId = requestId;
        this.socket = socket;
        this.openSession = openSession;
        this.isAuthorized = isAuthorized;
        reader = new PacketSocketReader(socket, PacketStreamProtocol.MaximumRequestSize);

        // The writer outlives every run, so it takes the socket's lifetime rather than a run's:
        // canceling a send would abort the connection instead of ending the run.
        writer = new PacketSocketWriter(socket, lifetime.Token);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        AppLog.Information("RemoteApi.Socket", $"#{requestId} packet socket connected.");
        using var stopping = cancellationToken.Register(
            static state => ((CancellationTokenSource)state!).Cancel(),
            lifetime);
        try
        {
            if (!await OpeningMessageAsync(cancellationToken).ConfigureAwait(false))
            {
                await TryCloseAsync(WebSocketCloseStatus.PolicyViolation, "No media named.").ConfigureAwait(false);
                return;
            }

            while (await reader.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                await HandleMessageAsync(reader.Message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Debug("RemoteApi.Socket", $"#{requestId} packet socket was canceled.");
        }
        catch (WebSocketException exception)
        {
            // The client went away without a closing handshake, which is how a closed tab looks.
            AppLog.Information(
                "RemoteApi.Socket",
                $"#{requestId} packet socket ended without a closing handshake: {exception.Message}");
        }
        catch (Exception exception)
        {
            AppLog.Error("RemoteApi.Socket", $"#{requestId} packet socket failed.", exception);
            try
            {
                await writer.WriteErrorAsync(
                    exception is RemoteStreamNegotiationException negotiation ? negotiation.Code : "stream_failed",
                    exception.Message)
                    .ConfigureAwait(false);
            }
            catch
            {
            }

            await TryCloseAsync(WebSocketCloseStatus.InternalServerError, exception.Message).ConfigureAwait(false);
            return;
        }

        await StopRunAsync().ConfigureAwait(false);
        await TryCloseAsync(WebSocketCloseStatus.NormalClosure, "Done.").ConfigureAwait(false);
        AppLog.Information("RemoteApi.Socket", $"#{requestId} packet socket closed.");
    }

    /// <summary>Takes the opening message under a clock. False if it did not name a video.</summary>
    private async Task<bool> OpeningMessageAsync(CancellationToken cancellationToken)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(HandshakeTimeout);

        if (!await reader.ReceiveAsync(handshake.Token).ConfigureAwait(false))
        {
            return false;
        }

        await HandleMessageAsync(reader.Message, cancellationToken).ConfigureAwait(false);
        return session is not null;
    }

    private async Task HandleMessageAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (message.IsEmpty)
        {
            return;
        }

        switch ((PacketStreamRequestType)message.Span[0])
        {
            case PacketStreamRequestType.Hello:
                await OpenAsync(message[1..], cancellationToken).ConfigureAwait(false);
                break;

            case PacketStreamRequestType.Play:
                var (runId, start, granted, tracks) = PacketStreamProtocol.DecodePlay(message.Span);
                await StartRunAsync(runId, start, tracks, granted, cancellationToken).ConfigureAwait(false);
                break;

            case PacketStreamRequestType.Credit:
                credit.Add(PacketStreamProtocol.DecodeCredit(message.Span));
                break;

            default:
                // A message this version does not know about, from a client that knows more.
                break;
        }
    }

    private async Task OpenAsync(ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        if (session is not null)
        {
            throw new InvalidOperationException("This socket already names a video.");
        }

        RemoteStreamHello hello = JsonSerializer.Deserialize(json.Span, JsonTypeInfo<RemoteStreamHello>())
            ?? throw new InvalidOperationException("The opening message carried nothing.");

        AppLog.Information(
            "RemoteApi.Socket",
            $"#{requestId} opening path='{hello.Path}'; transcode={DescribeTranscode(hello.Transcode)}.");

        if (!isAuthorized(hello.Token))
        {
            await writer
                .WriteErrorAsync("unauthorized", "A valid session token is required.")
                .ConfigureAwait(false);
            await TryCloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized.").ConfigureAwait(false);
            return;
        }

        session = await openSession(hello).ConfigureAwait(false);
        AppLog.Information(
            "RemoteApi.Socket",
            $"#{requestId} manifest ready: format={session.Manifest.FormatName}; " +
            $"durationMs={session.Manifest.DurationMilliseconds}; tracks={session.Manifest.Tracks.Length}; " +
            $"seekable={session.Manifest.CanSeek}.");
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(
            session.Manifest,
            JsonTypeInfo<RemoteStreamManifest>());
        await writer.WriteManifestAsync(manifest).ConfigureAwait(false);
    }

    private async Task StartRunAsync(
        uint runId,
        double startSeconds,
        int[] trackIds,
        int grantedCredit,
        CancellationToken cancellationToken)
    {
        StreamSession current = session
            ?? throw new InvalidOperationException("This socket has not named a video yet.");

        AppLog.Information(
            "RemoteApi.Socket",
            $"#{requestId} starting run={runId}; startSeconds={startSeconds:F3}; " +
            $"tracks=[{string.Join(",", trackIds)}]; initialCredit={grantedCredit}.");
        await StopRunAsync().ConfigureAwait(false);

        credit.Reset(grantedCredit);
        await writer.WriteRunStartedAsync(runId).ConfigureAwait(false);

        var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runLifetime = run;
        runTask = Task.Run(() => PumpAsync(current, trackIds, startSeconds, run.Token), CancellationToken.None);
    }

    private async Task PumpAsync(
        StreamSession current,
        int[] trackIds,
        double startSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await current
                .WritePacketsAsync(writer, credit, trackIds, startSeconds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a seek, or the socket is going down.
        }
        catch (Exception exception)
        {
            // The socket survives a failed run: the client may still ask for another.
            AppLog.Error("RemoteApi.Socket", $"#{requestId} packet run failed.", exception);
            try
            {
                await writer
                    .WriteErrorAsync("stream_failed", exception.Message)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task StopRunAsync()
    {
        runLifetime?.Cancel();
        await runTask.ConfigureAwait(false);
        runLifetime?.Dispose();
        runLifetime = null;
        runTask = Task.CompletedTask;
    }

    private async Task TryCloseAsync(WebSocketCloseStatus status, string description)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.CloseAsync(status, Truncate(description), timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    /// <summary>A close reason is capped at 123 bytes on the wire.</summary>
    private static string Truncate(string description) =>
        description.Length <= 100 ? description : description[..100];

    private static string DescribeTranscode(RemoteTranscodeRequest? transcode) =>
        transcode?.Tracks is not { Length: > 0 } tracks
            ? "none"
            : string.Join(
                "; ",
                tracks.Select(track =>
                    $"source={track.SourcePath ?? "main"},stream={track.StreamIndex},codec={track.CodecName}," +
                    $"bitRate={track.BitRate},maxHeight={track.MaxHeight}"));

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
        lifetime.Cancel();
        runLifetime?.Cancel();
        try
        {
            runTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        runLifetime?.Dispose();
        writer.Dispose();
        session?.Dispose();
        socket.Dispose();
        lifetime.Dispose();
    }
}
