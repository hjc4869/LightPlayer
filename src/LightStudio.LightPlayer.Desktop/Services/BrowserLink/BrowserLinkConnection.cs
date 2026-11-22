using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Desktop.Services.BrowserLink;

/// <summary>An appended byte range that arrived as a binary frame.</summary>
internal sealed record AppendMessage(AppendFrame Frame) : BrowserLinkMessage;

/// <summary>
/// One WebSocket from the extension's background context: either a pairing request, or a signed-in
/// socket that may carry one redirected session.
/// </summary>
internal sealed class BrowserLinkConnection
{
    private const string LogCategory = "BrowserLink";
    private const int MaximumTextBytes = 1024 * 1024;
    private const int MaximumBinaryBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan OpeningTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PairingTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Below the 20 seconds the protocol allows, so timer slack cannot push it past.</summary>
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    private readonly long id;
    private readonly WebSocket socket;
    private readonly string origin;
    private readonly BrowserLinkController controller;
    private readonly Channel<Outgoing> outgoing = Channel.CreateUnbounded<Outgoing>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource lifetime = new();
    private BrowserLinkSession? session;
    private int closing;

    public BrowserLinkConnection(long id, WebSocket socket, string origin, BrowserLinkController controller)
    {
        this.id = id;
        this.socket = socket;
        this.origin = origin;
        this.controller = controller;
    }

    public long Id => id;

    /// <summary>The pairing whose token signed this socket in; null before <c>hello</c>.</summary>
    public BrowserLinkPairing? Pairing { get; private set; }

    public async Task RunAsync(CancellationToken stopping)
    {
        using CancellationTokenRegistration stop = stopping.Register(() => Shutdown());
        Task writer = WriteLoopAsync();
        try
        {
            BrowserLinkMessage? first;
            using (var opening = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                opening.CancelAfter(OpeningTimeout);
                first = await ReceiveAsync(opening.Token).ConfigureAwait(false);
            }

            switch (first)
            {
                case null:
                    break;
                case PairMessage pair:
                    await PairAsync(pair).ConfigureAwait(false);
                    break;
                case HelloMessage hello:
                    await SignInAsync(hello).ConfigureAwait(false);
                    break;
                default:
                    throw new BrowserLinkProtocolException("The first message must be 'pair' or 'hello'.");
            }
        }
        catch (BrowserLinkProtocolException exception)
        {
            AppLog.Warning(LogCategory, $"#{id} protocol error: {exception.Message}");
            Close(BrowserLinkProtocol.ProtocolError, exception.Message);
            await DrainAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AppLog.Information(LogCategory, $"#{id} socket timed out or was shut down.");
        }
        catch (WebSocketException exception)
        {
            AppLog.Information(LogCategory, $"#{id} socket ended without a closing handshake: {exception.Message}");
        }
        catch (Exception exception)
        {
            AppLog.Error(LogCategory, $"#{id} socket failed.", exception);
            Close((int)WebSocketCloseStatus.InternalServerError, "Internal error.");
        }
        finally
        {
            session?.EndFromBrowser(BrowserSessionEndReason.Stopped);
            outgoing.Writer.TryComplete();
            try
            {
                await writer.WaitAsync(CloseTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            lifetime.Cancel();
            socket.Abort();
            socket.Dispose();
            lifetime.Dispose();
            AppLog.Information(LogCategory, $"#{id} socket closed.");
        }
    }

    /// <summary>Queues a text frame; frames queued after the socket started closing are dropped.</summary>
    public void Send(byte[] frame)
    {
        if (Volatile.Read(ref closing) == 0)
            outgoing.Writer.TryWrite(new Outgoing(frame, null, null));
    }

    /// <summary>Starts the closing handshake; the socket is dropped if the peer does not answer in time.</summary>
    public void Close(int status, string description)
    {
        if (Interlocked.Exchange(ref closing, 1) != 0)
            return;

        outgoing.Writer.TryWrite(new Outgoing(null, status, description));
        outgoing.Writer.TryComplete();
        try
        {
            lifetime.CancelAfter(CloseTimeout);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Sends <c>close</c> with a reason, then closes the socket with the given status.</summary>
    public void CloseSession(BrowserSessionEndReason reason, string? message, int status)
    {
        Send(BrowserLinkMessageWriter.Close(reason, message));
        Close(status, BrowserLinkProtocol.ReasonName(reason));
    }

    /// <summary>Ends the socket because the listener stops.</summary>
    public void Shutdown()
    {
        if (session is { IsEnded: false } active)
            active.Terminate(BrowserSessionEndReason.Stopped, (int)WebSocketCloseStatus.NormalClosure);
        Close((int)WebSocketCloseStatus.NormalClosure, "Stopped.");
    }

    /// <summary>Ends the socket because its pairing was revoked.</summary>
    public void Revoke()
    {
        if (session is { IsEnded: false } active)
            active.Terminate(BrowserSessionEndReason.Unauthorized, BrowserLinkProtocol.Unauthorized);
        Close(BrowserLinkProtocol.Unauthorized, "Revoked.");
    }

    private async Task PairAsync(PairMessage pair)
    {
        if (pair.Code.Length != 6 || !pair.Code.All(char.IsAsciiDigit))
            throw new BrowserLinkProtocolException("'code' must be six digits.");

        AppLog.Information(LogCategory, $"#{id} pairing requested by {origin} ({pair.Browser} {pair.ExtensionVersion}).");
        var request = new BrowserLinkPairingRequest(origin, pair.Code, pair.Browser, pair.ExtensionVersion);

        // The extension sends nothing while it waits, so anything arriving ends the request.
        Task<BrowserLinkMessage?> peer = ReceiveAsync(lifetime.Token);
        using var expiry = new CancellationTokenSource(PairingTimeout);
        using var abandoned = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token);
        _ = peer.ContinueWith(
            static (_, state) =>
            {
                // The request may have finished and released the source already.
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            abandoned,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        string? token = await controller.PairAsync(request, abandoned.Token).ConfigureAwait(false);
        if (peer.IsCompleted)
        {
            BrowserLinkMessage? message = await peer.ConfigureAwait(false);
            if (message is not null)
                throw new BrowserLinkProtocolException("A message arrived while pairing was pending.");
            return;
        }

        if (token is null)
        {
            AppLog.Information(LogCategory, $"#{id} pairing was rejected or timed out.");
            Close(BrowserLinkProtocol.PairingRejected, "Pairing rejected.");
        }
        else
        {
            AppLog.Information(LogCategory, $"#{id} paired {origin}.");
            Send(BrowserLinkMessageWriter.Paired(token, controller.AppVersion));
            Close((int)WebSocketCloseStatus.NormalClosure, "Paired.");
        }

        try
        {
            await peer.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException
            or BrowserLinkProtocolException)
        {
        }
    }

    private async Task SignInAsync(HelloMessage hello)
    {
        BrowserLinkPairing? pairing = controller.FindPairing(hello.Token);
        if (pairing is null || !string.Equals(pairing.Origin, origin, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warning(
                LogCategory,
                $"#{id} sign-in refused for {origin}: {(pairing is null ? "unknown token" : "origin differs from the pairing")}.");
            Close(BrowserLinkProtocol.Unauthorized, "Unauthorized.");
            await DrainAsync().ConfigureAwait(false);
            return;
        }

        Pairing = pairing;
        AppLog.Information(LogCategory, $"#{id} signed in from {origin} (extension {hello.ExtensionVersion}).");
        Send(BrowserLinkMessageWriter.Ready(controller.AppVersion, controller.Capabilities));

        while (await ReceiveAsync(lifetime.Token).ConfigureAwait(false) is { } message)
        {
            switch (message)
            {
                case OpenMessage open:
                    Open(open);
                    break;
                case CommandMessage command:
                    if (RequireSession() is { IsEnded: false } commanded)
                        commanded.HandleCommand(command.Command);
                    break;
                case SourceMessage source:
                    if (RequireSession() is { IsEnded: false } sourced)
                        sourced.HandleSource(source);
                    break;
                case AppendMessage append:
                    if (RequireSession() is { IsEnded: false } appended)
                        appended.HandleAppend(append.Frame);
                    break;
                case PongMessage:
                    break;
                case CloseMessage close:
                    AppLog.Information(LogCategory, $"#{id} the extension ended the session: {close.Reason}.");
                    session?.EndFromBrowser(BrowserSessionEndReason.Stopped);
                    Close((int)WebSocketCloseStatus.NormalClosure, "Closed.");
                    break;
                default:
                    throw new BrowserLinkProtocolException($"'{message.GetType().Name}' is not allowed after sign-in.");
            }
        }
    }

    private void Open(OpenMessage open)
    {
        if (session is not null)
        {
            if (open.Kind == "source" && !session.IsEnded)
            {
                session.Reopen(open);
                return;
            }

            throw new BrowserLinkProtocolException("A socket carries at most one session.");
        }

        switch (open.Kind)
        {
            case "file":
                OpenFile(open);
                break;
            case "source":
                OpenSource(open);
                break;
            default:
                throw new BrowserLinkProtocolException($"Unknown session kind '{open.Kind}'.");
        }
    }

    private void OpenFile(OpenMessage open)
    {
        if (open.Urls.Count == 0)
            throw new BrowserLinkProtocolException("A file session needs 'urls'.");

        var sources = new List<Uri>();
        foreach (string url in open.Urls)
        {
            if (StreamUrl.TryParse(url, out Uri? uri))
                sources.Add(uri);
        }

        if (sources.Count == 0)
        {
            AppLog.Information(LogCategory, $"#{id} declined a file session without HTTP addresses.");
            CloseSession(BrowserSessionEndReason.Unsupported, null, (int)WebSocketCloseStatus.NormalClosure);
            return;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddHeader(headers, "Referer", open.Referer);
        AddHeader(headers, "Origin", open.Origin);
        AddHeader(headers, "User-Agent", open.UserAgent);
        var link = new StreamLink(sources)
        {
            Headers = headers,
            Title = string.IsNullOrWhiteSpace(open.Title) ? null : open.Title.Trim(),
            StartSeconds = Math.Max(0, open.StartTime),
        };

        AppLog.Information(
            LogCategory,
            $"#{id} file session: sources={sources.Count}; start={link.StartSeconds:0.###}; paused={open.Paused}; " +
            $"rate={open.Rate:0.###}; headers={string.Join(",", headers.Keys)}.");
        var file = new BrowserFileSession(this, controller, open, link);
        session = file;
        controller.Activate(file);
        _ = PingAsync(file);
    }

    private void OpenSource(OpenMessage open)
    {
        AppLog.Information(
            LogCategory,
            $"#{id} source session: start={Math.Max(0, open.StartTime):0.###}; paused={open.Paused}; rate={open.Rate:0.###}; " +
            $"live={open.Live?.ToString() ?? "unknown"}.");
        var source = new BrowserSourceSession(this, controller, open, controller.Capabilities.BudgetBytes);
        session = source;
        controller.Activate(source);
        _ = PingAsync(source);
    }

    private static void AddHeader(Dictionary<string, string> headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl))
            headers[name] = value.Trim();
    }

    private BrowserLinkSession RequireSession() =>
        session ?? throw new BrowserLinkProtocolException("No session is open on this socket.");

    private async Task PingAsync(BrowserLinkSession pinged)
    {
        using var timer = new PeriodicTimer(PingInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false) && !pinged.IsEnded)
                Send(BrowserLinkMessageWriter.Ping());
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Reads until the peer answers the closing handshake or the close timeout drops it.</summary>
    private async Task DrainAsync()
    {
        try
        {
            while (await ReceiveAsync(lifetime.Token).ConfigureAwait(false) is not null)
            {
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException
            or BrowserLinkProtocolException)
        {
        }
    }

    /// <summary>Reads one whole message; null once the peer has closed.</summary>
    private async Task<BrowserLinkMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.GetMemory(16 * 1024), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Answer a closing handshake the peer started.
                Close((int)WebSocketCloseStatus.NormalClosure, "Closed.");
                return null;
            }

            buffer.Advance(result.Count);
            int limit = result.MessageType == WebSocketMessageType.Text ? MaximumTextBytes : MaximumBinaryBytes;
            if (buffer.WrittenCount > limit)
                throw new BrowserLinkProtocolException("A message is too large.");
        }
        while (!result.EndOfMessage);

        return result.MessageType == WebSocketMessageType.Text
            ? BrowserLinkMessageReader.Read(buffer.WrittenMemory)
            : new AppendMessage(AppendFrame.Parse(buffer.WrittenMemory));
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (Outgoing item in outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (item.Frame is { } frame)
                {
                    await socket
                        .SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, lifetime.Token)
                        .ConfigureAwait(false);
                }
                else if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket
                        .CloseOutputAsync((WebSocketCloseStatus)item.Status!.Value, item.Description, lifetime.Token)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException
            or ObjectDisposedException)
        {
            AppLog.Debug(LogCategory, $"#{id} stopped writing: {exception.Message}");
        }
    }

    private readonly record struct Outgoing(byte[]? Frame, int? Status, string? Description);
}
