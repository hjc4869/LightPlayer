using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace LightStudio.LightPlayer.Tools.AudioProbe;

internal static class AudioProbeQueuePlayer
{
    private static readonly TimeSpan CommandPollDelay = TimeSpan.FromMilliseconds(15);

    public static async Task<QueuePlaybackSummary> PlayAsync(
        PlaybackQueueController queue,
        QueuePlaybackOptions options,
        TextReader input,
        TextWriter log,
        CancellationToken cancellationToken = default)
    {
        PrintControls(log);
        using var commandReader = CreateCommandReader(input, log, cancellationToken);
        Subscribe(queue, log, out var unsubscribe);

        Task<QueuePlaybackSummary>? playbackTask = null;
        try
        {
            playbackTask = queue.PlayAsync(options, cancellationToken);
            while (!playbackTask.IsCompleted)
            {
                while (commandReader.TryRead(out var command))
                {
                    DispatchCommand(queue, command, log);
                }

                await Task.Delay(CommandPollDelay, cancellationToken);
            }

            return await playbackTask;
        }
        finally
        {
            unsubscribe();
            if (playbackTask is not null && !playbackTask.IsCompleted)
            {
                queue.Stop();
            }
        }
    }

    public static QueuePlaybackOptions CreateOptions(TimeSpan? durationPerTrack, float volume)
    {
        return PlaybackQueueController.CreateOptions(durationPerTrack, volume);
    }

    public static bool TryParseMode(string value, out PlaybackMode mode)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "seq":
            case "sequential":
                mode = PlaybackMode.Sequential;
                return true;
            case "list":
            case "list-loop":
            case "loop":
            case "all":
                mode = PlaybackMode.ListLoop;
                return true;
            case "single":
            case "single-loop":
            case "single-track-loop":
            case "one":
                mode = PlaybackMode.SingleTrackLoop;
                return true;
            case "random":
            case "shuffle":
                mode = PlaybackMode.Random;
                return true;
            default:
                mode = PlaybackMode.Sequential;
                return false;
        }
    }

    private static void DispatchCommand(PlaybackQueueController queue, QueuePlaybackCommand command, TextWriter log)
    {
        switch (command.Type)
        {
            case QueuePlaybackCommandType.TogglePause:
                if (!queue.TogglePause())
                {
                    log.WriteLine("No active playback to pause or resume.");
                }
                break;
            case QueuePlaybackCommandType.Next:
                if (!queue.Next())
                {
                    log.WriteLine("No next track.");
                }
                break;
            case QueuePlaybackCommandType.Previous:
                if (!queue.Previous())
                {
                    log.WriteLine("No previous track.");
                }
                break;
            case QueuePlaybackCommandType.Quit:
                queue.Stop();
                break;
            case QueuePlaybackCommandType.SetIndex:
                if (!queue.PlayAt(command.Index))
                {
                    log.WriteLine($"Track number out of range: {command.Index + 1}");
                }
                break;
            case QueuePlaybackCommandType.SetMode:
                queue.Mode = command.Mode;
                break;
            case QueuePlaybackCommandType.CycleMode:
                queue.CycleMode();
                break;
            case QueuePlaybackCommandType.SetVolume:
                queue.SetVolume(command.Volume);
                break;
            case QueuePlaybackCommandType.Seek:
                if (!queue.Seek(command.Position))
                {
                    log.WriteLine("No active track to seek.");
                }
                break;
            case QueuePlaybackCommandType.List:
                PrintQueue(queue, log);
                break;
            case QueuePlaybackCommandType.Status:
                PrintTrackStatus(queue, log);
                break;
            case QueuePlaybackCommandType.Help:
                PrintControls(log);
                break;
        }
    }

    private static void Subscribe(PlaybackQueueController queue, TextWriter log, out Action unsubscribe)
    {
        EventHandler<PlaybackQueueStatusChangedEventArgs> statusChanged = (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Message))
            {
                PrintQueueStatus(args.Message, args.Status, log);
            }
        };
        EventHandler<PlaybackQueueStateChangedEventArgs> stateChanged = (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Message))
            {
                log.WriteLine(args.Message);
            }
        };
        EventHandler<PlaybackQueueTrackStartedEventArgs> trackStarted = (_, args) =>
        {
            log.WriteLine($"Track started: {args.Index + 1}/{args.Count}: {args.Item.FilePath}");
            log.WriteLine($"Title: {args.Title}");
            log.WriteLine($"Duration: {args.Duration}");
            log.WriteLine($"Mode: {args.Mode}; volume: {args.Volume.ToString(CultureInfo.InvariantCulture)}");
        };
        EventHandler<PlaybackQueueTrackEndedEventArgs> trackEnded = (_, args) =>
        {
            if (args.Reason != PlaybackQueueTrackEndReason.Ended)
            {
                return;
            }

            if (args.DecodedBytes == 0)
            {
                log.WriteLine("Track ended without decoded audio.");
                return;
            }

            log.WriteLine($"Track ended: decoded {args.DecodedDuration}, played {args.PlayedDuration}");
        };
        EventHandler<PlaybackQueueErrorEventArgs> errorOccurred = (_, args) =>
        {
            log.WriteLine($"Track failed: {args.FilePath}");
            log.WriteLine(args.Message);
        };
        EventHandler<string> backendError = (_, message) => log.WriteLine($"Backend error: {message}");
        EventHandler<string> underrun = (_, message) => log.WriteLine($"Underrun: {message}");

        queue.StatusChanged += statusChanged;
        queue.PlaybackStateChanged += stateChanged;
        queue.TrackStarted += trackStarted;
        queue.TrackEnded += trackEnded;
        queue.ErrorOccurred += errorOccurred;
        queue.BackendError += backendError;
        queue.Underrun += underrun;

        unsubscribe = () =>
        {
            queue.StatusChanged -= statusChanged;
            queue.PlaybackStateChanged -= stateChanged;
            queue.TrackStarted -= trackStarted;
            queue.TrackEnded -= trackEnded;
            queue.ErrorOccurred -= errorOccurred;
            queue.BackendError -= backendError;
            queue.Underrun -= underrun;
        };
    }

    private static void PrintControls(TextWriter log)
    {
        log.WriteLine("Commands: p pause/resume, n next, b previous, goto <number>, mode [seq|loop|single|random], vol <0-1>, seek <time>, list, status, q quit");
    }

    private static void PrintQueueStatus(string prefix, PlaybackQueueStatus status, TextWriter log)
    {
        log.WriteLine(status.CurrentIndex < 0
            ? $"{prefix}: empty"
            : $"{prefix}: {status.CurrentIndex + 1}/{status.Count}, mode {status.Mode}, current {status.CurrentItem?.DisplayName}, state {status.State}");
    }

    private static void PrintQueue(PlaybackQueueController queue, TextWriter log)
    {
        for (var i = 0; i < queue.Items.Count; i++)
        {
            var marker = i == queue.CurrentIndex ? ">" : " ";
            log.WriteLine($"{marker} {i + 1}. {queue.Items[i].DisplayName}");
        }
    }

    private static void PrintTrackStatus(PlaybackQueueController queue, TextWriter log)
    {
        var status = queue.Status;
        log.WriteLine($"Status: {status.CurrentIndex + 1}/{status.Count}, {status.Mode}, {status.State}, source {status.SourcePosition}, played {status.PlayedDuration}, buffered {status.BufferedDuration}");
    }

    private static IQueueCommandReader CreateCommandReader(TextReader input, TextWriter log, CancellationToken cancellationToken)
    {
        return ReferenceEquals(input, Console.In) && !Console.IsInputRedirected
            ? new InteractiveConsoleCommandReader(log)
            : RedirectedCommandReader.Start(input, log, cancellationToken);
    }

    private static async Task ReadRedirectedCommandsAsync(
        TextReader input,
        TextWriter log,
        ChannelWriter<QueuePlaybackCommand> commands,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (!TryParseCommand(line, out var command, out var error))
                {
                    log.WriteLine(error);
                    continue;
                }

                if (command is null)
                {
                    continue;
                }

                await commands.WriteAsync(command, cancellationToken);
                if (command.Type == QueuePlaybackCommandType.Quit)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            commands.TryComplete();
        }
    }

    private static bool TryParseCommand(string line, out QueuePlaybackCommand? command, out string error)
    {
        command = null;
        error = string.Empty;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return true;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "p":
            case "pause":
            case "resume":
            case "play":
                command = new QueuePlaybackCommand(QueuePlaybackCommandType.TogglePause);
                return true;
            case "n":
            case "next":
                command = new QueuePlaybackCommand(QueuePlaybackCommandType.Next);
                return true;
            case "b":
            case "prev":
            case "previous":
                command = new QueuePlaybackCommand(QueuePlaybackCommandType.Previous);
                return true;
            case "q":
            case "quit":
            case "stop":
                command = new QueuePlaybackCommand(QueuePlaybackCommandType.Quit);
                return true;
            case "goto":
            case "track":
                return TryParseTrackNumber(parts, out command, out error);
            case "mode":
                return TryParseModeCommand(parts, out command, out error);
            case "seq":
            case "sequential":
            case "loop":
            case "single":
            case "random":
            case "shuffle":
                return TryParseModeCommand(new[] { "mode", parts[0] }, out command, out error);
            case "vol":
            case "volume":
                return TryParseVolume(parts, out command, out error);
            case "seek":
                return TryParseSeek(parts, out command, out error);
            case "list":
            case "ls":
                command = new QueuePlaybackCommand(QueuePlaybackCommandType.List);
                return true;
            case "status":
            case "s":
                command = new QueuePlaybackCommand(QueuePlaybackCommandType.Status);
                return true;
            case "h":
            case "help":
            case "?":
                command = new QueuePlaybackCommand(QueuePlaybackCommandType.Help);
                return true;
            default:
                error = $"Unknown command: {parts[0]}. Type help for controls.";
                return false;
        }
    }

    private static bool TryParseTrackNumber(string[] parts, out QueuePlaybackCommand? command, out string error)
    {
        command = null;
        error = string.Empty;
        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var trackNumber) || trackNumber <= 0)
        {
            error = "Usage: goto <track-number>";
            return false;
        }

        command = new QueuePlaybackCommand(QueuePlaybackCommandType.SetIndex, Index: trackNumber - 1);
        return true;
    }

    private static bool TryParseModeCommand(string[] parts, out QueuePlaybackCommand? command, out string error)
    {
        command = null;
        error = string.Empty;
        if (parts.Length == 1)
        {
            command = new QueuePlaybackCommand(QueuePlaybackCommandType.CycleMode);
            return true;
        }

        if (parts.Length != 2 || !TryParseMode(parts[1], out var mode))
        {
            error = "Usage: mode [seq|loop|single|random]";
            return false;
        }

        command = new QueuePlaybackCommand(QueuePlaybackCommandType.SetMode, Mode: mode);
        return true;
    }

    private static bool TryParseVolume(string[] parts, out QueuePlaybackCommand? command, out string error)
    {
        command = null;
        error = string.Empty;
        if (parts.Length != 2 || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var volume) || volume < 0 || volume > 1)
        {
            error = "Usage: vol <0-1>";
            return false;
        }

        command = new QueuePlaybackCommand(QueuePlaybackCommandType.SetVolume, Volume: volume);
        return true;
    }

    private static bool TryParseSeek(string[] parts, out QueuePlaybackCommand? command, out string error)
    {
        command = null;
        error = string.Empty;
        if (parts.Length != 2 || !TryParsePosition(parts[1], out var position))
        {
            error = "Usage: seek <seconds|hh:mm:ss>";
            return false;
        }

        command = new QueuePlaybackCommand(QueuePlaybackCommandType.Seek, Position: position);
        return true;
    }

    private static bool TryParsePosition(string value, out TimeSpan position)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out position) && position >= TimeSpan.Zero)
        {
            return true;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            position = TimeSpan.FromSeconds(seconds);
            return true;
        }

        position = TimeSpan.Zero;
        return false;
    }

    private interface IQueueCommandReader : IDisposable
    {
        bool TryRead(out QueuePlaybackCommand command);
    }

    private sealed class InteractiveConsoleCommandReader : IQueueCommandReader
    {
        private readonly TextWriter log;
        private readonly StringBuilder line = new();

        public InteractiveConsoleCommandReader(TextWriter log)
        {
            this.log = log;
        }

        public bool TryRead(out QueuePlaybackCommand command)
        {
            command = default!;

            while (HasKeyAvailable())
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    log.WriteLine();
                    var value = line.ToString();
                    line.Clear();

                    if (!TryParseCommand(value, out var parsedCommand, out var error))
                    {
                        log.WriteLine(error);
                        continue;
                    }

                    if (parsedCommand is null)
                    {
                        continue;
                    }

                    command = parsedCommand;
                    return true;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (line.Length > 0)
                    {
                        line.Length--;
                        log.Write("\b \b");
                        log.Flush();
                    }

                    continue;
                }

                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    log.WriteLine("^C");
                    command = new QueuePlaybackCommand(QueuePlaybackCommandType.Quit);
                    return true;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    line.Append(key.KeyChar);
                    log.Write(key.KeyChar);
                    log.Flush();
                }
            }

            return false;
        }

        public void Dispose()
        {
        }

        private static bool HasKeyAvailable()
        {
            try
            {
                return Console.KeyAvailable;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    private sealed class RedirectedCommandReader : IQueueCommandReader
    {
        private readonly Channel<QueuePlaybackCommand> commands;
        private readonly CancellationTokenSource cancellation;
        private readonly Task inputTask;

        private RedirectedCommandReader(
            Channel<QueuePlaybackCommand> commands,
            CancellationTokenSource cancellation,
            Task inputTask)
        {
            this.commands = commands;
            this.cancellation = cancellation;
            this.inputTask = inputTask;
        }

        public static RedirectedCommandReader Start(TextReader input, TextWriter log, CancellationToken cancellationToken)
        {
            var channel = Channel.CreateUnbounded<QueuePlaybackCommand>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });
            var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var inputTask = ReadRedirectedCommandsAsync(input, log, channel.Writer, inputCancellation.Token);
            return new RedirectedCommandReader(channel, inputCancellation, inputTask);
        }

        public bool TryRead(out QueuePlaybackCommand command)
        {
            return commands.Reader.TryRead(out command!);
        }

        public void Dispose()
        {
            cancellation.Cancel();
            commands.Writer.TryComplete();
            try
            {
                inputTask.Wait(TimeSpan.FromMilliseconds(250));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
            {
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }
}

internal enum QueuePlaybackCommandType
{
    TogglePause,
    Next,
    Previous,
    Quit,
    SetIndex,
    SetMode,
    CycleMode,
    SetVolume,
    Seek,
    List,
    Status,
    Help
}

internal sealed record QueuePlaybackCommand(
    QueuePlaybackCommandType Type,
    int Index = -1,
    PlaybackMode Mode = PlaybackMode.Sequential,
    float Volume = 1.0f,
    TimeSpan Position = default);
