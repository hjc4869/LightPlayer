using System;
using System.Net.WebSockets;
using System.Threading;
using Avalonia.Threading;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Desktop.Services.BrowserLink;

/// <summary>
/// One redirected video on a signed-in socket: the page's element commands reach the player and the
/// player's state goes back. Each session kind accepts the media messages that belong to it; anything
/// else is a protocol error.
/// </summary>
internal abstract class BrowserLinkSession : IBrowserVideoSession
{
    private readonly BrowserLinkController controller;
    private readonly object stateGate = new();
    private BrowserElementState? lastState;
    private long lastCommand;
    private int ended;

    protected BrowserLinkSession(BrowserLinkConnection connection, BrowserLinkController controller, OpenMessage open)
    {
        Connection = connection;
        this.controller = controller;
        ApplyOpen(open);
    }

    public BrowserLinkConnection Connection { get; }

    public bool IsEnded => Volatile.Read(ref ended) != 0;

    public abstract StreamLink? File { get; }

    public abstract AppendMediaSourceProvider? TakeSource();

    public string? Title { get; private set; }

    public double StartSeconds { get; private set; }

    public bool StartPaused { get; private set; }

    public double StartRate { get; private set; }

    public long StartCommand { get; private set; }

    public event EventHandler<BrowserElementCommand>? CommandReceived;

    public event EventHandler<BrowserSessionEndReason>? Ended;

    public event EventHandler? SourceReset;

    public event EventHandler? SourceReplaced;

    public void HandleCommand(BrowserElementCommand command)
    {
        if (command.Number <= lastCommand)
            throw new BrowserLinkProtocolException("Command numbers must increase.");

        lastCommand = command.Number;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsEnded)
                CommandReceived?.Invoke(this, command);
        });
    }

    public virtual void HandleSource(SourceMessage message) =>
        throw new BrowserLinkProtocolException($"'{message.GetType().Name}' needs a source session.");

    public virtual void HandleAppend(AppendFrame frame) =>
        throw new BrowserLinkProtocolException("Appends need a source session.");

    /// <summary>A repeated <c>open</c> of kind <c>source</c>.</summary>
    public virtual void Reopen(OpenMessage open) =>
        throw new BrowserLinkProtocolException("This session cannot be opened again.");

    public void ReportState(BrowserElementState state)
    {
        lock (stateGate)
        {
            if (IsEnded || state == lastState)
                return;
            lastState = state;
        }

        Connection.Send(BrowserLinkMessageWriter.State(state));
    }

    void IBrowserVideoSession.End(BrowserSessionEndReason reason, string? message) => End(reason, message);

    /// <summary>The app ended the session: tells the browser why and closes the socket.</summary>
    public void End(BrowserSessionEndReason reason, string? message)
    {
        if (!TryMarkEnded())
            return;

        AppLog.Information("BrowserLink", $"#{Connection.Id} the app ended the session: {reason}.");
        Connection.CloseSession(reason, message, (int)WebSocketCloseStatus.NormalClosure);
        OnClosed();
    }

    /// <summary>
    /// Something other than the player ended the session: a newer session, a revocation, shutdown, or
    /// media the session cannot play.
    /// </summary>
    public void Terminate(BrowserSessionEndReason reason, int status, string? message = null)
    {
        if (!TryMarkEnded())
            return;

        AppLog.Information("BrowserLink", $"#{Connection.Id} session terminated: {reason}.");
        Connection.CloseSession(reason, message, status);
        OnClosed();
        Dispatcher.UIThread.Post(() => Ended?.Invoke(this, reason));
    }

    /// <summary>The browser ended the session, with <c>close</c> or by dropping the socket.</summary>
    public void EndFromBrowser(BrowserSessionEndReason reason)
    {
        if (!TryMarkEnded())
            return;

        OnClosed();
        Dispatcher.UIThread.Post(() => Ended?.Invoke(this, reason));
    }

    /// <summary>
    /// The number of the last element command received. Commands and <c>open</c> arrive on the
    /// socket's reader, so it orders a repeated <c>open</c> among the commands.
    /// </summary>
    protected long LastCommand => lastCommand;

    /// <summary>Takes the element's state from an <c>open</c> that followed command <paramref name="command"/>.</summary>
    protected void ApplyOpen(OpenMessage open, long command = 0)
    {
        Title = string.IsNullOrWhiteSpace(open.Title) ? null : open.Title.Trim();
        StartSeconds = Math.Max(0, open.StartTime);
        StartPaused = open.Paused;
        StartRate = open.Rate;
        StartCommand = command;
    }

    /// <summary>Raises <see cref="SourceReset"/> or <see cref="SourceReplaced"/> on the UI thread.</summary>
    protected void PostSourceChange(bool replaced) => Dispatcher.UIThread.Post(() =>
    {
        if (!IsEnded)
            (replaced ? SourceReplaced : SourceReset)?.Invoke(this, EventArgs.Empty);
    });

    /// <summary>Runs once, on the thread that ended the session.</summary>
    protected virtual void OnClosed()
    {
    }

    private bool TryMarkEnded()
    {
        if (Interlocked.Exchange(ref ended, 1) != 0)
            return false;

        controller.Deactivate(this);
        return true;
    }
}

/// <summary>A plain HTTP file the application fetches itself while the page drives it with element commands.</summary>
internal sealed class BrowserFileSession(
    BrowserLinkConnection connection,
    BrowserLinkController controller,
    OpenMessage open,
    StreamLink file)
    : BrowserLinkSession(connection, controller, open)
{
    public override StreamLink? File { get; } = file;

    public override AppendMediaSourceProvider? TakeSource() => null;
}
