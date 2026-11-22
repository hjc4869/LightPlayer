using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Desktop.Services.BrowserLink;

/// <summary>
/// Runs the browser link listener on 127.0.0.1 as <see cref="BrowserLinkSettings"/> change, keeps
/// the paired browsers, and holds the one redirected session that may exist at a time.
/// </summary>
/// <remarks>
/// The listener answers the WebSocket handshake on a plain socket with small-packet coalescing off:
/// a reply to an append, written while an earlier frame waits for its acknowledgement, would
/// otherwise wait for the peer's delayed acknowledgement.
/// </remarks>
public sealed class BrowserLinkController : IBrowserLinkController, IDisposable
{
    private const string LogCategory = "BrowserLink";
    private const int MaximumConnections = 8;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly object gate = new();
    private readonly IAppSettingsStore settingsStore;
    private readonly Lazy<BrowserLinkCapabilities> capabilities = new(BrowserLinkCapabilities.Detect);
    private readonly HashSet<BrowserLinkConnection> connections = [];
    private Socket? listener;
    private CancellationTokenSource? listening;
    private int listeningPort;
    private IReadOnlyList<BrowserLinkPairing> pairings = [];
    private BrowserLinkSession? activeSession;
    private BrowserLinkState state = BrowserLinkState.Stopped;
    private long nextConnectionId;
    private int pairingPending;

    public BrowserLinkController(IAppSettingsStore settingsStore, string appVersion)
    {
        this.settingsStore = settingsStore;
        AppVersion = appVersion;
    }

    public event EventHandler? StateChanged;

    public IBrowserLinkPresenter? Presenter { get; set; }

    public BrowserLinkState State => state;

    internal string AppVersion { get; }

    /// <summary>What <c>ready</c> reports; hardware decoding only counts while the settings allow it.</summary>
    internal BrowserLinkCapabilities Capabilities => settingsStore.EnableHardwareDecoding
        ? capabilities.Value
        : capabilities.Value.WithoutHardware();

    public void Apply(BrowserLinkSettings settings)
    {
        BrowserLinkConnection[] revoked;
        lock (gate)
        {
            pairings = settings.PairedBrowsers;
            revoked = connections
                .Where(connection => connection.Pairing is { } pairing && !pairings.Contains(pairing))
                .ToArray();
        }

        foreach (BrowserLinkConnection connection in revoked)
        {
            AppLog.Information(LogCategory, $"#{connection.Id} its pairing was revoked.");
            connection.Revoke();
        }

        if (!settings.Enabled)
        {
            StopListening();
            SetState(BrowserLinkState.Stopped);
            return;
        }

        if (listener is not null && listeningPort == settings.Port)
            return;

        StopListening();
        var next = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (OperatingSystem.IsWindows())
                next.ExclusiveAddressUse = true;
            next.Bind(new IPEndPoint(IPAddress.Loopback, settings.Port));
            next.Listen(MaximumConnections);
        }
        catch (SocketException exception)
        {
            AppLog.Error(LogCategory, $"Could not listen on 127.0.0.1:{settings.Port}.", exception);
            next.Dispose();
            SetState(new BrowserLinkState(false, settings.Port, exception.Message));
            return;
        }

        listener = next;
        listeningPort = settings.Port;
        listening = new CancellationTokenSource();
        _ = AcceptAsync(next, listening.Token);
        AppLog.Information(LogCategory, $"Listening on ws://127.0.0.1:{settings.Port}{BrowserLinkProtocol.Path}.");
        SetState(new BrowserLinkState(true, settings.Port, string.Empty));
    }

    public void Dispose() => StopListening();

    internal BrowserLinkPairing? FindPairing(string token)
    {
        IReadOnlyList<BrowserLinkPairing> current;
        lock (gate)
        {
            current = pairings;
        }

        BrowserLinkPairing? match = null;
        foreach (BrowserLinkPairing pairing in current)
        {
            // Every verifier is checked so the time taken does not tell which one matched.
            if (BrowserLinkTokens.Matches(token, pairing.TokenVerifier))
                match ??= pairing;
        }

        return match;
    }

    /// <summary>
    /// Asks the user to confirm a pairing and stores it. Returns the new token, or null when the user
    /// declined, another request is waiting, or <paramref name="cancellationToken"/> fired first.
    /// </summary>
    internal async Task<string?> PairAsync(BrowserLinkPairingRequest request, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref pairingPending, 1, 0) != 0)
            return null;

        Task<string?> confirmation;
        try
        {
            confirmation = Dispatcher.UIThread.InvokeAsync(() => ConfirmAndStoreAsync(request, cancellationToken));
        }
        catch
        {
            Volatile.Write(ref pairingPending, 0);
            throw;
        }

        // A second request is refused for as long as the first one's dialog is up.
        _ = confirmation.ContinueWith(
            _ => Volatile.Write(ref pairingPending, 0),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            return await confirmation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Makes <paramref name="session"/> the redirected session, ending the one it replaces.</summary>
    internal void Activate(BrowserLinkSession session)
    {
        BrowserLinkSession? previous;
        lock (gate)
        {
            previous = activeSession;
            activeSession = session;
        }

        previous?.Terminate(BrowserSessionEndReason.Replaced, BrowserLinkProtocol.Replaced);
        Dispatcher.UIThread.Post(() =>
        {
            if (session.IsEnded)
                return;
            if (Presenter is not { } presenter)
            {
                session.End(BrowserSessionEndReason.Error, "The application is not ready.");
                return;
            }

            _ = OpenAsync(presenter, session);
        });
    }

    internal void Deactivate(BrowserLinkSession session)
    {
        lock (gate)
        {
            if (ReferenceEquals(activeSession, session))
                activeSession = null;
        }
    }

    private static async Task OpenAsync(IBrowserLinkPresenter presenter, BrowserLinkSession session)
    {
        BringToFront();
        try
        {
            await presenter.OpenSessionAsync(session).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppLog.Error(LogCategory, "The redirected video could not be opened.", exception);
            session.End(BrowserSessionEndReason.Error, exception.Message);
        }
    }

    /// <summary>Raises the main window over the browser, where the window system lets an application do so.</summary>
    private static void BringToFront()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            return;

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }

    private async Task<string?> ConfirmAndStoreAsync(BrowserLinkPairingRequest request, CancellationToken cancellationToken)
    {
        if (Presenter is not { } presenter ||
            !await presenter.ConfirmPairingAsync(request, cancellationToken).ConfigureAwait(true))
            return null;
        if (cancellationToken.IsCancellationRequested)
            return null;

        string token = BrowserLinkTokens.Create();
        var pairing = new BrowserLinkPairing(
            string.IsNullOrWhiteSpace(request.Browser) ? request.Origin : request.Browser.Trim(),
            request.Origin,
            BrowserLinkTokens.CreateVerifier(token),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Pairing again from the same extension replaces its earlier token.
        BrowserLinkSettings current = settingsStore.BrowserLinkSettings;
        BrowserLinkSettings next = current with
        {
            PairedBrowsers = current.PairedBrowsers
                .Where(existing => !string.Equals(existing.Origin, request.Origin, StringComparison.OrdinalIgnoreCase))
                .Append(pairing)
                .ToArray(),
        };
        settingsStore.SetBrowserLinkSettings(next);
        Apply(next);
        return token;
    }

    private async Task AcceptAsync(Socket owner, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket accepted = await owner.AcceptAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(accepted, cancellationToken), CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or
            OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Error(LogCategory, "The listener stopped accepting connections.", exception);
        }
    }

    private async Task HandleAsync(Socket accepted, CancellationToken cancellationToken)
    {
        accepted.NoDelay = true;
        var stream = new NetworkStream(accepted, ownsSocket: true);
        BrowserLinkRequest? request;
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            opening.CancelAfter(HandshakeTimeout);
            request = await BrowserLinkHandshake.ReadAsync(stream, opening.Token).ConfigureAwait(false);
            if (request is null)
            {
                await BrowserLinkHandshake.RefuseAsync(stream, HttpStatusCode.BadRequest, opening.Token).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                return;
            }

            string? origin = request.Header("Origin");
            HttpStatusCode? refusal =
                !string.Equals(request.Path.TrimEnd('/'), BrowserLinkProtocol.Path, StringComparison.Ordinal)
                    ? HttpStatusCode.NotFound
                    : !request.IsWebSocketRequest
                        ? HttpStatusCode.UpgradeRequired
                        : !IsExtensionOrigin(origin)
                            ? HttpStatusCode.Forbidden
                            : !BrowserLinkRequest.HasToken(request.Header("Sec-WebSocket-Protocol"), BrowserLinkProtocol.SubProtocol)
                                ? HttpStatusCode.BadRequest
                                : null;
            lock (gate)
            {
                if (refusal is null && connections.Count >= MaximumConnections)
                    refusal = HttpStatusCode.ServiceUnavailable;
            }

            if (refusal is { } status)
            {
                AppLog.Warning(
                    LogCategory,
                    $"Refused {request.Method} {request.Path} from origin {origin ?? "none"}: {(int)status}.");
                await BrowserLinkHandshake.RefuseAsync(stream, status, opening.Token).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                return;
            }

            await BrowserLinkHandshake.AcceptAsync(stream, request, BrowserLinkProtocol.SubProtocol, opening.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            AppLog.Debug(LogCategory, $"A handshake did not complete: {exception.Message}");
            await stream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            SubProtocol = BrowserLinkProtocol.SubProtocol,
            KeepAliveInterval = WebSocket.DefaultKeepAliveInterval,
        });
        BrowserLinkConnection connection;
        lock (gate)
        {
            connection = new BrowserLinkConnection(++nextConnectionId, socket, request.Header("Origin")!, this);
            connections.Add(connection);
        }

        AppLog.Information(LogCategory, $"#{connection.Id} connected from {request.Header("Origin")}.");
        try
        {
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                connections.Remove(connection);
            }

            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Only an extension context may connect: web pages could otherwise reach the listener and raise
    /// pairing prompts. The token remains the authority, since any local process can send any origin.
    /// </summary>
    private static bool IsExtensionOrigin(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme is not ("http" or "https" or "file" or "ws" or "wss" or "data" or "blob" or "about");

    private void StopListening()
    {
        if (listener is null)
            return;

        BrowserLinkConnection[] open;
        lock (gate)
        {
            open = connections.ToArray();
        }

        foreach (BrowserLinkConnection connection in open)
            connection.Shutdown();

        listening?.Cancel();
        listening?.Dispose();
        listening = null;
        listener.Dispose();
        listener = null;
        listeningPort = 0;
        AppLog.Information(LogCategory, "Stopped listening.");
    }

    private void SetState(BrowserLinkState value)
    {
        if (state == value)
            return;
        state = value;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
