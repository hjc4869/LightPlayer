using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading.Channels;
using System.Threading.Tasks;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Desktop.Services.BrowserLink;

/// <summary>
/// Media the page pushes the way it feeds a Media Source, played through an
/// <see cref="AppendMediaSourceProvider"/>. Source messages and appends are applied in arrival order
/// on a worker of their own, so parsing holds up neither the socket nor the UI thread.
/// </summary>
internal sealed class BrowserSourceSession : BrowserLinkSession
{
    private const string LogCategory = "BrowserLink";

    /// <summary>
    /// Longer than any media; page players use such lengths for an unbounded one (2^63 microseconds,
    /// for example), which the player's time arithmetic cannot represent.
    /// </summary>
    private const double UnboundedSeconds = 1e9;

    private readonly Channel<BrowserLinkMessage> work = Channel.CreateUnbounded<BrowserLinkMessage>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object sourceGate = new();
    private readonly HashSet<uint> declared = [];
    private readonly HashSet<uint> appending = [];
    private AppendMediaSourceProvider source;
    private AppendMediaSourceProvider? untaken;
    private bool resetPending;

    public BrowserSourceSession(
        BrowserLinkConnection connection,
        BrowserLinkController controller,
        OpenMessage open,
        long budgetBytes)
        : base(connection, controller, open)
    {
        source = new AppendMediaSourceProvider(budgetBytes) { Title = DisplayTitle(open) };
        source.BufferedChanged += OnBufferedChanged;
        untaken = source;
        _ = Task.Run(ProcessAsync);
    }

    public override StreamLink? File => null;

    public override AppendMediaSourceProvider? TakeSource()
    {
        lock (sourceGate)
        {
            if (IsEnded)
                return null;
            AppendMediaSourceProvider? taken = untaken;
            untaken = null;
            return taken;
        }
    }

    public override void HandleSource(SourceMessage message) => work.Writer.TryWrite(message);

    public override void HandleAppend(AppendFrame frame)
    {
        // A buffer has one append in flight, which bounds what waits for the worker.
        lock (appending)
        {
            if (!appending.Add(frame.Buffer))
                throw new BrowserLinkProtocolException($"Buffer {frame.Buffer} has an append in flight.");
        }

        work.Writer.TryWrite(new AppendMessage(frame));
    }

    public override void Reopen(OpenMessage open) => work.Writer.TryWrite(new Restarting(open, LastCommand));

    protected override void OnClosed()
    {
        work.Writer.TryComplete();
        AppendMediaSourceProvider? orphan;
        lock (sourceGate)
        {
            orphan = untaken;
            untaken = null;
        }

        orphan?.Dispose();
    }

    private void OnBufferedChanged(object? sender, BufferedReply change)
    {
        // Sent from the worker, so it cannot overtake the reply to an append or removal.
        if (sender is AppendMediaSourceProvider provider)
            work.Writer.TryWrite(new BufferedChange(provider, change.Buffer));
    }

    private async Task ProcessAsync()
    {
        await foreach (BrowserLinkMessage message in work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (IsEnded)
                break;

            try
            {
                Apply(message);
            }
            catch (BrowserLinkProtocolException exception)
            {
                AppLog.Warning(LogCategory, $"#{Connection.Id} protocol error: {exception.Message}");
                Connection.Close(BrowserLinkProtocol.ProtocolError, exception.Message);
                break;
            }
            catch (ObjectDisposedException) when (IsEnded)
            {
                break;
            }
            catch (Exception exception)
            {
                AppLog.Error(LogCategory, $"#{Connection.Id} could not apply '{message.GetType().Name}'.", exception);
                Terminate(BrowserSessionEndReason.Error, (int)WebSocketCloseStatus.NormalClosure, exception.Message);
                break;
            }
        }
    }

    private void Apply(BrowserLinkMessage message)
    {
        AppendMediaSourceProvider target = source;
        switch (message)
        {
            case AppendMessage { Frame: var frame }:
                Require(frame.Buffer);
                AppendReply reply = target.Append(frame.Buffer, frame.Sequence, frame.Bytes.Span);
                lock (appending)
                    appending.Remove(frame.Buffer);
                switch (reply)
                {
                    case AppendedReply appended:
                        Connection.Send(BrowserLinkMessageWriter.Appended(appended));
                        if (appended.Tracks is not null)
                            CheckReset();
                        break;
                    case AppendFailedReply failed:
                        Connection.Send(BrowserLinkMessageWriter.AppendFailed(failed));
                        if (failed.Reason == AppendFailureReason.Encrypted)
                            Terminate(BrowserSessionEndReason.Encrypted, (int)WebSocketCloseStatus.NormalClosure);
                        break;
                }
                break;
            case AddSourceBufferMessage add:
                SourceBufferAddResult added = target.AddSourceBuffer(add.Buffer, add.MimeType);
                switch (added)
                {
                    case SourceBufferAddResult.Added:
                        declared.Add(add.Buffer);
                        break;
                    case SourceBufferAddResult.DuplicateId:
                        throw new BrowserLinkProtocolException($"Buffer {add.Buffer} is already declared.");
                    default:
                        AppLog.Information(LogCategory, $"#{Connection.Id} refused buffer '{add.MimeType}': {added}.");
                        Terminate(
                            BrowserSessionEndReason.Unsupported,
                            (int)WebSocketCloseStatus.NormalClosure,
                            added == SourceBufferAddResult.TooLate
                                ? "Buffers cannot be added once playback is set up."
                                : $"'{add.MimeType}' is not supported.");
                        break;
                }
                break;
            case RemoveSourceBufferMessage remove:
                Require(remove.Buffer);
                target.RemoveSourceBuffer(remove.Buffer);
                declared.Remove(remove.Buffer);
                CheckReset();
                break;
            case ChangeTypeMessage change:
                Require(change.Buffer);
                if (!target.ChangeType(change.Buffer, change.MimeType))
                    Terminate(
                        BrowserSessionEndReason.Unsupported,
                        (int)WebSocketCloseStatus.NormalClosure,
                        $"'{change.MimeType}' is not supported.");
                break;
            case ConfigureMessage configure:
                Require(configure.Buffer);
                target.Configure(
                    configure.Buffer,
                    configure.TimestampOffset.IsPresent ? configure.TimestampOffset.Value : null,
                    configure.AppendWindowStart.IsPresent ? configure.AppendWindowStart.Value ?? double.NegativeInfinity : null,
                    configure.AppendWindowEnd.IsPresent ? configure.AppendWindowEnd.Value ?? double.PositiveInfinity : null);
                break;
            case RemoveMessage remove:
                Require(remove.Buffer);
                Connection.Send(BrowserLinkMessageWriter.Removed(
                    target.Remove(remove.Buffer, remove.Request, remove.Start, remove.End ?? double.PositiveInfinity)));
                break;
            case AbortMessage abort:
                Require(abort.Buffer);
                target.Abort(abort.Buffer);
                break;
            case DurationMessage duration:
                target.SetDuration(Bounded(duration.Value ?? double.PositiveInfinity));
                break;
            case EndOfStreamMessage endOfStream:
                target.EndOfStream(endOfStream.Error switch
                {
                    null => EndOfStreamError.None,
                    "network" => EndOfStreamError.Network,
                    "decode" => EndOfStreamError.Decode,
                    _ => throw new BrowserLinkProtocolException($"Unknown end of stream error '{endOfStream.Error}'."),
                });
                break;
            case TimelineMessage timeline:
                target.SetTimeline(timeline.Live, Bounded(timeline.SeekableStart), Bounded(timeline.SeekableEnd));
                break;
            case BufferedChange change:
                if (ReferenceEquals(change.Provider, target) && declared.Contains(change.Buffer))
                    Connection.Send(BrowserLinkMessageWriter.Buffered(target.GetBuffered(change.Buffer)));
                break;
            case Restarting restart:
                Restart(restart.Open, restart.Command);
                break;
        }
    }

    /// <summary>A repeated <c>open</c>, received after command <paramref name="command"/>: every buffer goes and the source starts again.</summary>
    private void Restart(OpenMessage open, long command)
    {
        ApplyOpen(open, command);
        AppLog.Information(
            LogCategory,
            $"#{Connection.Id} the page attached a new source: start={StartSeconds:0.###}; paused={StartPaused}.");
        source.Title = DisplayTitle(open);
        source.Reset();
        declared.Clear();
        resetPending = true;
        PostSourceChange(replaced: false);
    }

    /// <summary>Once every buffer after a restart has its initialization segment, moves to a new provider if the track kinds changed.</summary>
    private void CheckReset()
    {
        if (!resetPending || !source.IsReady)
            return;

        resetPending = false;
        if (!source.RequiresReopen)
            return;

        AppendMediaSourceProvider previous = source;
        AppendMediaSourceProvider next = previous.Transfer();
        previous.BufferedChanged -= OnBufferedChanged;
        next.BufferedChanged += OnBufferedChanged;
        AppendMediaSourceProvider? orphan;
        lock (sourceGate)
        {
            source = next;
            orphan = ReferenceEquals(untaken, previous) ? previous : null;
            untaken = next;
        }

        orphan?.Dispose();
        AppLog.Information(LogCategory, $"#{Connection.Id} the new source has other kinds of tracks; playback opens again.");
        PostSourceChange(replaced: true);
    }

    private static double Bounded(double seconds) => seconds > UnboundedSeconds ? double.PositiveInfinity : seconds;

    private string DisplayTitle(OpenMessage open) =>
        Title ?? (Uri.TryCreate(open.PageUrl, UriKind.Absolute, out Uri? page) ? page.Host : string.Empty);

    private void Require(uint buffer)
    {
        if (!declared.Contains(buffer))
            throw new BrowserLinkProtocolException($"Buffer {buffer} is not declared.");
    }

    /// <summary>Eviction changed a buffer's ranges or full flag.</summary>
    private sealed record BufferedChange(AppendMediaSourceProvider Provider, uint Buffer) : BrowserLinkMessage;

    /// <summary>A repeated <c>open</c> and the number of the last command received before it.</summary>
    private sealed record Restarting(OpenMessage Open, long Command) : BrowserLinkMessage;
}
