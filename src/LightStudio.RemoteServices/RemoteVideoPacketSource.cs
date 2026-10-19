using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.RemoteServices;

/// <summary>
/// Packet source fed by a server that demuxes on this client's behalf, over HTTP or a WebSocket.
/// </summary>
/// <remarks>
/// <para>
/// Only the tracks being played cross the network. A container holding several sound tracks, such as
/// a film with three dubs, would otherwise have to be fetched whole for the client to demux it,
/// and everything but one of those tracks would be thrown away after being paid for.
/// </para>
/// <para>
/// The socket carries packets down and flow control up. Nothing arrives that this end has not
/// already agreed to hold: credit is granted in bytes and released again as the demux thread takes
/// packets off the queue. Leaving it to the transport to push back is not an option in a browser,
/// where an unread socket goes on filling memory that script can neither see nor bound.
/// </para>
/// <para>
/// HTTP seeking replaces the streaming response. Firefox uses a WebSocket with explicit byte
/// credit instead, because its fetch implementation can buffer an unread response without bound.
/// </para>
/// </remarks>
public sealed class RemoteVideoPacketSource : IRelayablePacketSource, IAsyncDisposable
{
    private const int QueueCapacity = 128;
    private const double HistoryDurationSeconds = 30;
    private const long MaximumHistoryBytes = 200L * 1024 * 1024;

    /// <summary>How long the producer waits before retrying a full packet queue.</summary>
    private static readonly TimeSpan QueueRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>Replacement connections allowed without a new packet arriving before giving up.</summary>
    private const int MaximumReconnectAttempts = 5;

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(250);
    private const double ReconnectBufferReserveSeconds = 3;
    private const int BandwidthSampleBytes = 512 * 1024;
    private const double BandwidthBurstGapSeconds = 0.5;
    private const double BandwidthSmoothingFactor = 0.2;

    /// <summary>Packets taken this close together came from the same read of the connection.</summary>
    private const double BandwidthSameReadSeconds = 0.001;

    private readonly RemoteApiClient client;
    private RemoteStreamManifest manifest;
    private readonly RemoteTranscodeRequest? transcode;
    private Dictionary<int, RemoteStreamTrack> tracksById;
    private readonly Queue<WirePacket> queue = new(QueueCapacity);
    private readonly Queue<WirePacket> history = new();
    private readonly Queue<WirePacket> replay = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly object socketSync = new();
    private readonly object readerSync = new();
    private readonly object disposalSync = new();

    /// <summary>Held while the queue is emptied and refilled, so arrivals cannot land out of order.</summary>
    private readonly object queueSync = new();

    /// <summary>Last decode timestamp taken for each track. Touched only by the connection task.</summary>
    private readonly Dictionary<int, long> lastReceivedDts = [];

    private WebSocket? socket;
    private readonly bool usesWebSocket;
    private Task sendChain = Task.CompletedTask;
    private Task? receiveTask;
    private Task? disposal;
    private CancellationTokenSource? readerLifetime;

    /// <summary>Completed when a read may return something new. Guarded by <see cref="queueSync"/>.</summary>
    private TaskCompletionSource? arrival;
    private volatile Exception? readerFault;
    private volatile bool runEnded;
    private volatile bool started;
    private volatile bool receivedSinceConnect;
    private uint requestedRun;
    private uint currentRun;

    /// <summary>The run that continues a dropped socket rather than moving. Zero when none does.</summary>
    private uint resumeRunId;
    private int releasedCredit;
    private int reconnectCount;
    private double position;
    private long historyBytes;
    private double historyStartSeconds = MediaTime.NoTimestamp;
    private double playbackPositionSeconds = MediaTime.NoTimestamp;

    /// <summary>Read off the connection task by whoever is drawing the transport.</summary>
    private double bufferedSeconds = MediaTime.NoTimestamp;

    // Written only by the connection task. A long gap starts a new burst so flow-control pauses
    // are not mistaken for slow network transfer.
    private double downloadWindowStarted;
    private double lastPacketReceived;
    private long downloadWindowBytes;
    private double estimatedBandwidthBitsPerSecond;

    private int selectedAudioTrackId;
    private bool disposed;
    private bool reconfigurationPending;
    private double reconfigurationPositionSeconds;
    private double requestedStartSeconds;

    /// <summary>
    /// Uses explicit socket flow control instead of HTTP backpressure. The web head enables this
    /// only for Firefox, whose fetch implementation buffers an unread streaming response.
    /// </summary>
    public static bool PreferWebSocket { get; set; }

    private RemoteVideoPacketSource(
        RemoteApiClient client,
        WebSocket? socket,
        RemoteStreamManifest manifest,
        RemoteTranscodeRequest? transcode,
        bool usesWebSocket)
    {
        this.client = client;
        this.socket = socket;
        this.manifest = manifest;
        this.transcode = PinTranscode(transcode, manifest);
        this.usesWebSocket = usesWebSocket;
        tracksById = manifest.Tracks.ToDictionary(track => track.Id);
        selectedAudioTrackId = manifest.DefaultAudioTrackId;
        VideoStreamIndex = manifest.DefaultVideoTrackId;
        Info = BuildInfo(manifest);
    }

    /// <summary>
    /// Opens a socket on <paramref name="path"/> and waits for the manifest that names its tracks.
    /// </summary>
    /// <param name="transcode">
    /// Exact indexed tracks this device decided to re-encode before opening the socket.
    /// </param>
    public static async Task<RemoteVideoPacketSource> CreateAsync(
        RemoteApiClient client,
        string path,
        RemoteTranscodeRequest? transcode = null,
        CancellationToken cancellationToken = default,
        string? sourceRevision = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!PreferWebSocket)
        {
            var hello = new RemoteStreamHello(path, Transcode: transcode, SourceRevision: sourceRevision);
            RemoteStreamManifest manifest = await client
                .PostAsync<RemoteStreamHello, RemoteStreamManifest>(
                    RemoteStreamDefaults.OpenPath,
                    hello,
                    cancellationToken)
                .ConfigureAwait(false);
            return new RemoteVideoPacketSource(client, null, manifest, transcode, usesWebSocket: false);
        }

        WebSocket socket = await client.OpenPacketSocketAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket
                .SendAsync(HelloMessage(client, path, transcode, sourceRevision), WebSocketMessageType.Binary, true, cancellationToken)
                .ConfigureAwait(false);

            var reader = new PacketSocketReader(socket);
            RemoteStreamManifest manifest = await ReadManifestAsync(reader, cancellationToken).ConfigureAwait(false);
            var source = new RemoteVideoPacketSource(client, socket, manifest, transcode, usesWebSocket: true);
            source.StartReceiving(reader);
            return source;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static byte[] HelloMessage(
        RemoteApiClient client, string path, RemoteTranscodeRequest? transcode, string? sourceRevision) =>
        PacketStreamProtocol.EncodeHello(
            JsonSerializer.SerializeToUtf8Bytes(
                new RemoteStreamHello(path, client.Token, transcode, sourceRevision),
                JsonTypeInfo<RemoteStreamHello>()));

    public MediaInfo Info { get; private set; }

    public double ReconfigurationPositionSeconds => Volatile.Read(ref reconfigurationPositionSeconds);

    public int VideoStreamIndex { get; }

    public int AudioStreamIndex => selectedAudioTrackId;

    public bool CanSeek => manifest.CanSeek;

    /// <summary>Why the stream stopped, when it stopped badly. The read status alone cannot say.</summary>
    public Exception? LastError => readerFault;

    /// <summary>
    /// How many times the packet connection had to be replaced. Playback hides a reconnection, so
    /// this is the only sign that the transport is being lost and rebuilt underneath it.
    /// </summary>
    public int ReconnectCount => Volatile.Read(ref reconnectCount);

    public double BufferedSeconds => Volatile.Read(ref bufferedSeconds);

    public double HistoryStartSeconds => Volatile.Read(ref historyStartSeconds);

    public double EstimatedBandwidthBitsPerSecond => Volatile.Read(ref estimatedBandwidthBitsPerSecond);

    public void UpdatePlaybackPosition(double positionSeconds)
    {
        if (!MediaTime.IsValid(positionSeconds))
        {
            return;
        }

        lock (queueSync)
        {
            playbackPositionSeconds = positionSeconds;
            RecalculateHistoryBytesLocked();
            TrimHistoryLocked();
            UpdateHistoryStartLocked();
        }
    }

    public string TransportDescription =>
        usesWebSocket ? "server packet stream (WebSocket)" : "server packet stream (HTTP fetch)";

    public CodecParameters GetCodecParameters(int streamIndex) =>
        tracksById.TryGetValue(streamIndex, out var track)
            ? track.Parameters
            : throw new ArgumentOutOfRangeException(nameof(streamIndex), "No such track in this media.");

    /// <summary>
    /// Records the track to ask for. The server decides what a run carries when it is asked for, so
    /// the change lands with the seek the player always performs after a switch.
    /// </summary>
    public void SelectAudioStream(int streamIndex)
    {
        selectedAudioTrackId = streamIndex;
    }

    public PacketReadStatus Read(MediaPacket packet) => Read((IPacketWriter)packet);

    public PacketReadStatus Read(IPacketWriter writer)
    {
        lock (queueSync)
        {
            return ReadCore(writer);
        }
    }

    public Task WaitForPacketsAsync()
    {
        lock (queueSync)
        {
            if (disposed || queue.Count > 0 || replay.Count > 0 || runEnded || readerFault is not null || reconfigurationPending)
            {
                return Task.CompletedTask;
            }

            arrival ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return arrival.Task;
        }
    }

    private PacketReadStatus ReadCore(IPacketWriter writer)
    {
        if (disposed)
        {
            return PacketReadStatus.Fatal;
        }

        if (!started)
        {
            // Nothing is asked for until the pipeline actually wants packets, so the decoders can be
            // built without a run going on behind them.
            StartRun(position);
        }

        lock (queueSync)
        {
            if (reconfigurationPending)
            {
                reconfigurationPending = false;
                return PacketReadStatus.Reconfigured;
            }
        }

        // Thrown rather than reported as a status, because the status cannot carry why: the demux
        // thread turns this into the message the user is shown.
        if (readerFault is { } fault)
        {
            throw new IOException(fault.Message, fault);
        }

        if (!TryTakePacket(out WirePacket wire, out bool releaseCredit))
        {
            if (readerFault is { } pending)
            {
                throw new IOException(pending.Message, pending);
            }

            return runEnded ? PacketReadStatus.EndOfFile : PacketReadStatus.WouldBlock;
        }

        try
        {
            writer.Write(
                wire.TrackId,
                wire.Pts,
                wire.Dts,
                wire.Duration,
                wire.IsKeyFrame,
                wire.Buffer.AsSpan(0, wire.Length),
                wire.Buffer.AsSpan(wire.Length, wire.Hdr10PlusMetadataLength));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(wire.Buffer);
            throw;
        }

        Remember(wire);
        if (releaseCredit)
        {
            ReleaseCredit(PacketStreamProtocol.PacketCost(wire.Length));
        }

        double time = ToSeconds(wire.TrackId, wire.Dts);
        if (MediaTime.IsValid(time))
        {
            position = time;
        }

        return PacketReadStatus.Ok;
    }

    private bool TryTakePacket(out WirePacket wire, out bool releaseCredit)
    {
        lock (queueSync)
        {
            if (replay.TryDequeue(out wire))
            {
                releaseCredit = false;
                return true;
            }

            releaseCredit = queue.TryDequeue(out wire);
            return releaseCredit;
        }
    }

    private void Remember(WirePacket wire)
    {
        lock (queueSync)
        {
            RememberLocked(wire);
        }
    }

    private void RememberLocked(WirePacket wire)
    {
        double time = PacketTime(wire);
        if (!MediaTime.IsValid(time) ||
            !MediaTime.IsValid(playbackPositionSeconds) ||
            time <= playbackPositionSeconds)
        {
            historyBytes += wire.Buffer.LongLength;
        }

        history.Enqueue(wire);
        TrimHistoryLocked();
        UpdateHistoryStartLocked();
    }

    private void TrimHistoryLocked()
    {
        while (historyBytes > MaximumHistoryBytes && history.TryDequeue(out WirePacket wire))
        {
            DiscardHistory(wire);
        }

        if (history.Count == 0)
        {
            return;
        }

        if (!MediaTime.IsValid(playbackPositionSeconds))
        {
            return;
        }

        double cutoff = playbackPositionSeconds - HistoryDurationSeconds;
        double oldest = MediaTime.NoTimestamp;
        foreach (WirePacket wire in history)
        {
            oldest = PacketTime(wire);
            if (MediaTime.IsValid(oldest))
            {
                break;
            }
        }

        if (!MediaTime.IsValid(oldest) || oldest >= cutoff)
        {
            return;
        }

        int resume = FindResumeIndex(history.ToList(), cutoff);
        for (var i = 0; i < resume; i++)
        {
            DiscardHistory(history.Dequeue());
        }
    }

    private void UpdateHistoryStartLocked()
    {
        double start = FindHistoryStart(history);
        if (!MediaTime.IsValid(start))
        {
            start = FindHistoryStart(replay);
        }

        Volatile.Write(ref historyStartSeconds, start);
    }

    private double FindHistoryStart(Queue<WirePacket> packets)
    {
        bool hasVideo = VideoStreamIndex >= 0;
        int anchorTrack = hasVideo ? VideoStreamIndex : selectedAudioTrackId;
        foreach (WirePacket wire in packets)
        {
            if (wire.TrackId != anchorTrack || (hasVideo && !wire.IsKeyFrame))
            {
                continue;
            }

            double time = PacketTime(wire);
            if (MediaTime.IsValid(time))
            {
                return time;
            }
        }

        return MediaTime.NoTimestamp;
    }

    private void DiscardHistory(WirePacket wire)
    {
        double time = PacketTime(wire);
        if (!MediaTime.IsValid(time) ||
            !MediaTime.IsValid(playbackPositionSeconds) ||
            time <= playbackPositionSeconds)
        {
            historyBytes -= wire.Buffer.LongLength;
        }

        ArrayPool<byte>.Shared.Return(wire.Buffer);
    }

    public bool Seek(double positionSeconds)
    {
        if (disposed || !CanSeek)
        {
            return false;
        }

        double target = Math.Max(0, positionSeconds);
        UpdatePlaybackPosition(target);
        bool local = target < position
            ? TrySeekWithinHistory(target)
            : target > position && TrySeekWithinQueue(target);
        position = target;
        if (!local)
        {
            StartRun(position);
        }

        return true;
    }

    /// <summary>Rewinds into recently consumed packets without replacing the active run.</summary>
    private bool TrySeekWithinHistory(double target)
    {
        if (!started || readerFault is not null)
        {
            return false;
        }

        lock (queueSync)
        {
            var buffered = new List<WirePacket>(history.Count + replay.Count);
            buffered.AddRange(history);
            buffered.AddRange(replay);

            int resume = FindResumeIndex(buffered, target);
            if (resume < 0)
            {
                return false;
            }

            history.Clear();
            replay.Clear();
            for (var i = 0; i < buffered.Count; i++)
            {
                (i < resume ? history : replay).Enqueue(buffered[i]);
            }

            RecalculateHistoryBytesLocked();
            UpdateHistoryStartLocked();

            return true;
        }
    }

    /// <summary>
    /// Serves a forward seek out of what has already arrived, when the queue reaches past the
    /// target. A skip of a few seconds is most of what seeking is used for, and asking the server to
    /// begin again costs a round trip and a fresh seek in the container for packets this end is
    /// already holding.
    /// </summary>
    /// <returns>Whether the queue was cut, leaving the run in flight untouched.</returns>
    private bool TrySeekWithinQueue(double target)
    {
        if (!started || readerFault is not null)
        {
            return false;
        }

        int reclaimed;
        bool moved;
        lock (queueSync)
        {
            if (replay.Count > 0)
            {
                var replayed = replay.ToList();
                int replayResume = FindResumeIndex(replayed, target);
                replay.Clear();
                for (var i = 0; i < replayed.Count; i++)
                {
                    if (replayResume >= 0 && i >= replayResume)
                    {
                        replay.Enqueue(replayed[i]);
                    }
                    else
                    {
                        RememberLocked(replayed[i]);
                    }
                }

                if (replayResume >= 0)
                {
                    return true;
                }
            }

            var buffered = new List<WirePacket>(queue.Count);
            while (queue.TryDequeue(out WirePacket wire))
            {
                buffered.Add(wire);
            }

            int resume = FindResumeIndex(buffered, target);
            moved = resume >= 0;
            reclaimed = 0;

            for (var i = 0; i < buffered.Count; i++)
            {
                if (!moved || i >= resume)
                {
                    queue.Enqueue(buffered[i]);
                    continue;
                }

                reclaimed += PacketStreamProtocol.PacketCost(buffered[i].Length);
                RememberLocked(buffered[i]);
            }
        }

        if (reclaimed > 0)
        {
            // The server counted these against the window when it sent them, so dropping them here
            // still has to be paid back or the window walks down to nothing.
            ReleaseCredit(reclaimed);
        }

        return moved;
    }

    /// <summary>
    /// Where the queue may be cut for a jump to <paramref name="target"/>, or -1 when it may not.
    /// </summary>
    /// <remarks>
    /// The cut lands on the last picture a decoder can start cold from at or before the target, and
    /// only when every selected track carries packets past the target: a jump into a stretch one of
    /// them has yet to reach would leave it silent until the run caught up.
    /// </remarks>
    private int FindResumeIndex(List<WirePacket> buffered, double target)
    {
        bool hasVideo = VideoStreamIndex >= 0;
        int anchorTrack = hasVideo ? VideoStreamIndex : selectedAudioTrackId;
        var awaiting = new List<int>(SelectedTrackIds());
        var anchor = -1;

        for (var i = 0; i < buffered.Count; i++)
        {
            WirePacket wire = buffered[i];
            double time = PacketTime(wire);
            if (!MediaTime.IsValid(time))
            {
                continue;
            }

            if (time > target)
            {
                awaiting.Remove(wire.TrackId);
            }
            else if (wire.TrackId == anchorTrack && (wire.IsKeyFrame || !hasVideo))
            {
                anchor = i;
            }
        }

        return anchor >= 0 && awaiting.Count == 0 ? anchor : -1;
    }

    /// <summary>When a packet is meant to be seen or heard, falling back to when it is decoded.</summary>
    private double PacketTime(WirePacket wire) =>
        ToSeconds(wire.TrackId, wire.Pts != PacketStreamProtocol.NoTimestamp ? wire.Pts : wire.Dts);

    public double ToSeconds(int streamIndex, long timestamp)
    {
        if (timestamp == PacketStreamProtocol.NoTimestamp || !tracksById.TryGetValue(streamIndex, out var track))
        {
            return MediaTime.NoTimestamp;
        }

        return (timestamp * track.Parameters.TimeBase) - track.StartTimeSeconds;
    }

    /// <summary>Reads a subtitle track out as an ASS script, which the server extracts for us.</summary>
    /// <remarks>
    /// Over the API rather than this socket: extracting a subtitle track walks a container to the
    /// end, so the server opens one of its own rather than moving the containers feeding playback.
    /// </remarks>
    public string ReadSubtitleScript(int trackId, CancellationToken cancellationToken)
    {
        return ReadSubtitleScriptAsync(trackId, cancellationToken).GetAwaiter().GetResult();
    }

    public async Task<string> ReadSubtitleScriptAsync(int trackId, CancellationToken cancellationToken)
    {
        var result = await client.PostAsync<RemoteStreamSubtitleRequest, RemoteStreamSubtitle>(
            RemoteStreamDefaults.SubtitlePath,
            new RemoteStreamSubtitleRequest(manifest.Path, trackId),
            cancellationToken).ConfigureAwait(false);
        return result.Script;
    }

    /// <summary>
    /// Collects the encoded packets of a bitmap subtitle track from a packet stream of that track alone, for
    /// this end to decode. Encoded subtitles are far smaller than the bitmaps they decode to.
    /// </summary>
    public async Task<BitmapSubtitlePackets> ReadSubtitlePacketsAsync(int trackId, CancellationToken cancellationToken)
    {
        RemoteStreamManifest opened = manifest;
        var request = new RemotePacketStreamRequest(opened.Path, 0, [trackId], SourceRevision: opened.SourceRevision);
        await using Stream body = await client.OpenPacketStreamAsync(request, cancellationToken).ConfigureAwait(false);
        using HttpPacketStreamReader reader = await HttpPacketStreamReader
            .CreateAsync(body, cancellationToken)
            .ConfigureAwait(false);
        RemoteStreamManifest? run = null;
        var packets = new List<SubtitlePacket>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (reader.LastType)
            {
                case PacketStreamRecordType.Manifest:
                    run = JsonSerializer.Deserialize(reader.LastPayload, JsonTypeInfo<RemoteStreamManifest>())
                        ?? throw new InvalidDataException("The server sent an empty run manifest.");
                    break;

                case PacketStreamRecordType.Packet when reader.LastTrackId == trackId:
                    if (run is null)
                        throw new InvalidDataException("The server sent packets without an output manifest.");
                    var (pts, dts, duration) = PacketStreamProtocol.ReadPacketHeader(reader.LastPayload);
                    packets.Add(new SubtitlePacket(pts, dts, duration, reader.LastPayload[PacketStreamProtocol.PacketHeaderSize..].ToArray()));
                    break;

                case PacketStreamRecordType.EndOfStream:
                    RemoteStreamManifest described = run
                        ?? throw new InvalidDataException("The server ended the subtitle stream without a manifest.");
                    RemoteStreamTrack track = described.Tracks.FirstOrDefault(candidate => candidate.Id == trackId)
                        ?? throw new InvalidDataException("The server omitted the requested subtitle track.");
                    RemoteStreamTrack? video = described.Tracks.FirstOrDefault(candidate => candidate.Id == described.DefaultVideoTrackId);
                    return new BitmapSubtitlePackets(
                        track.Parameters,
                        track.StartTimeSeconds,
                        described.DurationMilliseconds / 1000.0,
                        video?.Width ?? 0,
                        video?.Height ?? 0,
                        packets);

                case PacketStreamRecordType.Error:
                    var (code, text) = PacketStreamProtocol.DecodeError(reader.LastPayload);
                    throw new IOException($"The server stopped streaming subtitles ({code}): {text}");
            }
        }

        throw new IOException("The server ended the subtitle stream unexpectedly.");
    }

    /// <summary>Retires whatever is running and asks for a new run at <paramref name="startSeconds"/>.</summary>
    /// <param name="keepQueued">
    /// Keeps what has already arrived, for a run that continues the last one rather than moving.
    /// </param>
    private uint StartRun(double startSeconds, bool keepQueued = false)
    {
        int[] tracks = SelectedTrackIds();
        Volatile.Write(ref requestedStartSeconds, startSeconds);

        // Raising the requested run first is what makes the receive loop drop everything still
        // arriving from the run being replaced, so the drain below cannot be refilled behind it.
        uint runId = Interlocked.Increment(ref requestedRun);
        if (!keepQueued)
        {
            DrainQueue();
        }

        readerFault = null;
        runEnded = false;
        Interlocked.Exchange(ref releasedCredit, 0);
        started = true;

        if (!usesWebSocket)
        {
            lastReceivedDts.Clear();
            Volatile.Write(ref bufferedSeconds, MediaTime.NoTimestamp);
            StartHttpReader(runId, startSeconds, tracks);
            return runId;
        }

        Post(PacketStreamProtocol.EncodePlay(
            runId,
            startSeconds,
            tracks,
            PacketStreamProtocol.DefaultCreditBytes));
        return runId;
    }

    private void StartHttpReader(uint runId, double startSeconds, int[] tracks)
    {
        var nextLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? previousLifetime;
        Task? previousTask;
        lock (readerSync)
        {
            previousLifetime = readerLifetime;
            previousTask = receiveTask;
            readerLifetime = nextLifetime;
            receiveTask = RunAfterAsync(start.Task, async () =>
            {
                try { await PumpHttpAsync(runId, startSeconds, tracks, nextLifetime.Token).ConfigureAwait(false); }
                finally
                {
                    try
                    {
                        if (previousTask is not null) await previousTask.ConfigureAwait(false);
                    }
                    finally { previousLifetime?.Dispose(); }
                }
            });
        }

        previousLifetime?.Cancel();
        start.SetResult();
    }

    private static async Task RunAfterAsync(Task start, Func<Task> operation)
    {
        await start.ConfigureAwait(false);
        await operation().ConfigureAwait(false);
    }

    private async Task PumpHttpAsync(
        uint runId,
        double startSeconds,
        int[] tracks,
        CancellationToken cancellationToken)
    {
        var attempts = 0;
        var resuming = false;
        var deferReconfiguration = false;
        var endedTracks = new HashSet<int>();
        while (!cancellationToken.IsCancellationRequested && !disposed)
        {
            var received = false;
            try
            {
                var request = new RemotePacketStreamRequest(manifest.Path, startSeconds, tracks, transcode, manifest.SourceRevision);
                await using Stream body = await client
                    .OpenPacketStreamAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                using HttpPacketStreamReader reader = await HttpPacketStreamReader
                    .CreateAsync(body, cancellationToken)
                    .ConfigureAwait(false);
                var pendingSideData = new PendingPacketSideData();
                bool hasManifest = false;

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (runId != Volatile.Read(ref requestedRun))
                    {
                        return;
                    }

                    switch (reader.LastType)
                    {
                        case PacketStreamRecordType.Manifest:
                            if (deferReconfiguration)
                            {
                                if (!await WaitForPlaybackAsync(runId, startSeconds, cancellationToken).ConfigureAwait(false))
                                {
                                    return;
                                }

                                startSeconds = Math.Max(startSeconds, PlaybackAnchor());
                                deferReconfiguration = false;
                            }

                            AcceptRunManifest(reader.LastPayload, runId, startSeconds, tracks);
                            hasManifest = true;
                            break;

                        case PacketStreamRecordType.PacketSideData
                            when reader.LastFlags == (byte)PacketStreamSideDataType.Hdr10Plus:
                            pendingSideData.Capture(reader.LastTrackId, reader.LastPayload);
                            break;

                        case PacketStreamRecordType.Packet:
                        {
                            RequireRunManifest(hasManifest);
                            var (pts, dts, duration) = PacketStreamProtocol.ReadPacketHeader(reader.LastPayload);
                            if (resuming && WasAlreadyReceived(reader.LastTrackId, pts, dts))
                            {
                                pendingSideData.Clear();
                                break;
                            }

                            received = true;
                            endedTracks.Remove(reader.LastTrackId);
                            WirePacket wire = Decode(
                                reader.LastTrackId,
                                reader.LastFlags,
                                pts,
                                dts,
                                duration,
                                reader.LastPayload,
                                pendingSideData);
                            int packetCost = PacketStreamProtocol.PacketCost(wire.Length);
                            bool added = await EnqueueAsync(
                                wire,
                                runId,
                                () =>
                                {
                                    RecordDownload(packetCost);
                                    if (dts != PacketStreamProtocol.NoTimestamp)
                                    {
                                        lastReceivedDts[reader.LastTrackId] = dts;
                                        Volatile.Write(ref bufferedSeconds, EarliestDelivered());
                                    }
                                },
                                cancellationToken).ConfigureAwait(false);
                            if (!added)
                            {
                                ArrayPool<byte>.Shared.Return(wire.Buffer);
                                return;
                            }

                            break;
                        }

                        case PacketStreamRecordType.EndOfTrack:
                            endedTracks.Add(reader.LastTrackId);
                            break;

                        case PacketStreamRecordType.EndOfStream:
                            RequireRunManifest(hasManifest);
                            EndRun();
                            return;

                        case PacketStreamRecordType.Error:
                            var (code, text) = PacketStreamProtocol.DecodeError(reader.LastPayload);
                            throw new PacketStreamRunFault($"The server stopped streaming ({code}): {text}");

                        default:
                            break;
                    }
                }

                throw new IOException("The server ended the packet response unexpectedly.");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (PacketStreamRunFault fault)
            {
                if (runId == Volatile.Read(ref requestedRun))
                {
                    Fail(fault);
                }

                return;
            }
            catch (Exception exception)
            {
                if (runId != Volatile.Read(ref requestedRun) || cancellationToken.IsCancellationRequested || disposed)
                {
                    return;
                }

                if (tracks.All(endedTracks.Contains))
                {
                    EndRun();
                    return;
                }

                attempts = received ? 1 : attempts + 1;
                if (attempts > MaximumReconnectAttempts)
                {
                    Fail(new IOException("The HTTP packet stream kept failing.", exception));
                    return;
                }

                AppLog.Warning("Remote.PacketStream",
                    $"HTTP run={runId} interrupted; retry={attempts}; bufferedSeconds={BufferedSeconds:F3}; " +
                    $"playbackSeconds={PlaybackAnchor():F3}; endedTracks=[{string.Join(",", endedTracks)}].",
                    exception);

                try
                {
                    await Task.Delay(ReconnectDelay * attempts, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                bool transcoded = HasTranscodedTracks(tracks);
                startSeconds = transcoded ? TranscodeResumeAnchor(tracks, endedTracks) : ResumeAnchor();
                resuming = !transcoded;
                deferReconfiguration = transcoded;
                Interlocked.Increment(ref reconnectCount);
            }
        }
    }

    private double TranscodeResumeAnchor(int[] selected, HashSet<int> endedTracks)
    {
        lock (queueSync)
        {
            double played = PlaybackAnchor();
            if (!MediaTime.IsValid(playbackPositionSeconds))
            {
                return played;
            }

            double earliest = double.MaxValue;
            foreach (int trackId in selected)
            {
                if (endedTracks.Contains(trackId))
                {
                    continue;
                }

                if (!lastReceivedDts.TryGetValue(trackId, out long dts))
                {
                    return played;
                }

                double time = ToSeconds(trackId, dts);
                if (!MediaTime.IsValid(time))
                {
                    return played;
                }

                earliest = Math.Min(earliest, time);
            }

            return earliest == double.MaxValue
                ? played
                : Math.Max(played, earliest - ReconnectBufferReserveSeconds);
        }
    }

    private async Task<bool> WaitForPlaybackAsync(
        uint runId,
        double startSeconds,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !disposed && runId == Volatile.Read(ref requestedRun))
        {
            if (PlaybackAnchor() >= startSeconds || !MediaTime.IsValid(Volatile.Read(ref playbackPositionSeconds)))
            {
                return true;
            }

            await Task.Delay(QueueRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private void StartReceiving(PacketSocketReader reader)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiveTask = RunAfterAsync(start.Task, () => RunConnectionAsync(reader, lifetime.Token));
        start.SetResult();
    }

    /// <summary>
    /// Keeps a socket under the player for as long as it is watching, replacing one that fails.
    /// </summary>
    /// <remarks>
    /// The server keeps no state between sockets, so a replacement identifies the media and resumes
    /// from the current playback position.
    /// </remarks>
    private async Task RunConnectionAsync(PacketSocketReader reader, CancellationToken cancellationToken)
    {
        var attempts = 0;
        Exception? lastFailure = null;
        while (!cancellationToken.IsCancellationRequested && !disposed)
        {
            try
            {
                await ReceiveLoopAsync(reader, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (PacketStreamRunFault fault)
            {
                // The server refused the run itself; another socket would only be refused again.
                Fail(fault);
                return;
            }
            catch (Exception exception)
            {
                // The transport went. Everything needed to rebuild it is still here.
                lastFailure = exception;
            }

            if (cancellationToken.IsCancellationRequested || disposed || runEnded)
            {
                return;
            }

            attempts = receivedSinceConnect ? 1 : attempts + 1;
            if (attempts > MaximumReconnectAttempts)
            {
                Fail(new IOException("The packet socket kept failing.", lastFailure));
                return;
            }

            try
            {
                await Task.Delay(ReconnectDelay * attempts, cancellationToken).ConfigureAwait(false);
                reader = await ReopenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // Out of attempts is the only way this gives up; the next turn tries again.
                lastFailure = exception;
            }
        }
    }

    /// <summary>Replaces the socket and asks for the current position again.</summary>
    private async Task<PacketSocketReader> ReopenAsync(CancellationToken cancellationToken)
    {
        WebSocket fresh = await client.OpenPacketSocketAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await fresh
                .SendAsync(
                    HelloMessage(client, manifest.Path, transcode, manifest.SourceRevision),
                    WebSocketMessageType.Binary,
                    true,
                    cancellationToken)
                .ConfigureAwait(false);

            var reader = new PacketSocketReader(fresh);
            RemoteStreamManifest reopened = await ReadManifestAsync(reader, cancellationToken).ConfigureAwait(false);
            ValidateSource(reopened);

            ReplaceSocket(fresh);
            receivedSinceConnect = false;
            Interlocked.Increment(ref reconnectCount);

            if (HasTranscodedTracks(SelectedTrackIds()))
            {
                resumeRunId = 0;
                StartRun(PlaybackAnchor());
            }
            else
            {
                resumeRunId = StartRun(ResumeAnchor(), keepQueued: true);
            }
            return reader;
        }
        catch
        {
            fresh.Dispose();
            throw;
        }
    }

    private void ReplaceSocket(WebSocket fresh)
    {
        WebSocket? previous;
        lock (socketSync)
        {
            previous = socket;
            socket = fresh;

            // Anything still queued was addressed to the socket that just died.
            sendChain = Task.CompletedTask;
        }

        previous?.Dispose();
    }

    private async Task ReceiveLoopAsync(PacketSocketReader reader, CancellationToken cancellationToken)
    {
        var pendingSideData = new PendingPacketSideData();
        bool hasManifest = false;
        while (await reader.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            ReadOnlyMemory<byte> message = reader.Message;
            var (type, flags, trackId) = PacketStreamProtocol.ReadRecordHeader(message.Span);
            ReadOnlyMemory<byte> payload = message[PacketStreamProtocol.RecordHeaderSize..];

            switch (type)
            {
                case PacketStreamRecordType.RunStarted:
                    pendingSideData.Clear();
                    hasManifest = false;
                    currentRun = PacketStreamProtocol.ReadRunId(payload.Span);
                    if (currentRun != resumeRunId)
                    {
                        // A run that moves, so nothing already received says anything about what
                        // this one should deliver.
                        lastReceivedDts.Clear();
                        Volatile.Write(ref bufferedSeconds, MediaTime.NoTimestamp);
                    }

                    break;

                case PacketStreamRecordType.Manifest when IsCurrentRun():
                    AcceptRunManifest(payload.Span, currentRun, Volatile.Read(ref requestedStartSeconds), SelectedTrackIds());
                    hasManifest = true;
                    break;

                case PacketStreamRecordType.PacketSideData
                    when flags == (byte)PacketStreamSideDataType.Hdr10Plus && IsCurrentRun():
                    pendingSideData.Capture(trackId, payload.Span);
                    break;

                case PacketStreamRecordType.Packet when IsCurrentRun():
                {
                    RequireRunManifest(hasManifest);
                    receivedSinceConnect = true;
                    var (pts, dts, duration) = PacketStreamProtocol.ReadPacketHeader(payload.Span);
                    int packetCost = PacketStreamProtocol.PacketCost(
                        payload.Length - PacketStreamProtocol.PacketHeaderSize);
                    if (IsAlreadyReceived(trackId, pts, dts))
                    {
                        // Dropped here, but the server spent window on it all the same, so it has
                        // to be given back or a resume walks the window down to nothing.
                        pendingSideData.Clear();
                        ReleaseCredit(packetCost);
                        break;
                    }

                    WirePacket wire = Decode(
                        trackId,
                        flags,
                        pts,
                        dts,
                        duration,
                        payload.Span,
                        pendingSideData);
                    uint runId = currentRun;
                    bool added = await EnqueueAsync(
                        wire,
                        runId,
                        () =>
                        {
                            RecordDownload(packetCost);
                            if (dts != PacketStreamProtocol.NoTimestamp)
                            {
                                lastReceivedDts[trackId] = dts;
                                Volatile.Write(ref bufferedSeconds, EarliestDelivered());
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                    if (!added)
                    {
                        ArrayPool<byte>.Shared.Return(wire.Buffer);
                    }

                    break;
                }

                case PacketStreamRecordType.EndOfStream when IsCurrentRun():
                    RequireRunManifest(hasManifest);
                    EndRun();
                    break;

                case PacketStreamRecordType.Error when IsCurrentRun():
                    var (code, text) = PacketStreamProtocol.DecodeError(payload.Span);
                    throw new PacketStreamRunFault($"The server stopped streaming ({code}): {text}");

                default:
                    // A run being replaced, including its error, or anything this version does not know about.
                    break;
            }
        }

        throw new IOException("The server closed the packet socket.");
    }

    private static RemoteTranscodeRequest? PinTranscode(RemoteTranscodeRequest? requested, RemoteStreamManifest opened)
    {
        if (requested is null)
            return null;
        return new RemoteTranscodeRequest(requested.Tracks.Select(request =>
        {
            RemoteStreamTrack track = opened.Tracks.FirstOrDefault(candidate =>
                candidate.SourcePath == request.SourcePath && candidate.StreamIndex == request.StreamIndex)
                ?? throw new IOException("The server omitted a requested transcode track.");
            if (track.DeliveryMode != RemoteStreamDefaults.TranscodeDelivery ||
                (!string.IsNullOrWhiteSpace(request.CodecName) &&
                    !string.Equals(request.CodecName, track.CodecName, StringComparison.OrdinalIgnoreCase)) ||
                (string.IsNullOrWhiteSpace(request.CodecName) &&
                    !(request.CodecCandidates ?? []).Contains(track.CodecName, StringComparer.OrdinalIgnoreCase)))
                throw new IOException("The server did not honor the requested output codec.");
            return request with { CodecName = track.CodecName, CodecCandidates = null };
        }).ToArray());
    }

    private void ValidateSource(RemoteStreamManifest next)
    {
        if (string.IsNullOrEmpty(next.SourceRevision) || next.SourceRevision != manifest.SourceRevision ||
            next.Path != manifest.Path || next.DefaultVideoTrackId != manifest.DefaultVideoTrackId ||
            next.Tracks.Length != manifest.Tracks.Length || manifest.Tracks.Any(previous =>
                !next.Tracks.Any(track => track.Id == previous.Id && track.Kind == previous.Kind &&
                    track.SourcePath == previous.SourcePath && track.StreamIndex == previous.StreamIndex)))
            throw new PacketStreamRunFault("The media changed. Reopen it to negotiate the current source.");
        _ = PinTranscode(transcode, next);
    }

    private void AcceptRunManifest(ReadOnlySpan<byte> json, uint runId, double startSeconds, int[] selected)
    {
        RemoteStreamManifest next = JsonSerializer.Deserialize(json, JsonTypeInfo<RemoteStreamManifest>())
            ?? throw new PacketStreamRunFault("The server sent an empty run manifest.");
        lock (queueSync)
        {
            if (runId != Volatile.Read(ref requestedRun))
                return;
            ValidateSource(next);
            var nextTracks = next.Tracks.ToDictionary(track => track.Id);
            bool reset = HasTranscodedTracks(selected) || selected.Any(id =>
                !nextTracks.TryGetValue(id, out var track) ||
                track.StartTimeSeconds != tracksById[id].StartTimeSeconds ||
                RemoteStreamIdentity.ParametersKey(track.Parameters) != RemoteStreamIdentity.ParametersKey(tracksById[id].Parameters));
            if (reset)
            {
                DrainQueue();
                lastReceivedDts.Clear();
                Volatile.Write(ref bufferedSeconds, MediaTime.NoTimestamp);
                Volatile.Write(ref reconfigurationPositionSeconds, startSeconds);
                reconfigurationPending = true;
                SignalArrivalLocked();
            }

            manifest = next;
            tracksById = nextTracks;
            Info = BuildInfo(next);
        }
    }

    private bool HasTranscodedTracks(int[] selected) => selected.Any(id =>
        tracksById.TryGetValue(id, out var track) && track.DeliveryMode == RemoteStreamDefaults.TranscodeDelivery);

    private double PlaybackAnchor()
    {
        double played = Volatile.Read(ref playbackPositionSeconds);
        return MediaTime.IsValid(played) ? Math.Max(0, played) : Math.Max(0, position);
    }

    private static void RequireRunManifest(bool received)
    {
        if (!received)
            throw new PacketStreamRunFault("The server sent packets without an output manifest.");
    }

    private async Task<bool> EnqueueAsync(
        WirePacket wire,
        uint runId,
        Action accepted,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            lock (queueSync)
            {
                if (runId != Volatile.Read(ref requestedRun) || disposed)
                {
                    return false;
                }

                if (queue.Count < QueueCapacity)
                {
                    queue.Enqueue(wire);
                    accepted();
                    SignalArrivalLocked();
                    return true;
                }
            }

            try
            {
                await Task.Delay(QueueRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        return false;
    }

    private void RecordDownload(int bytes)
    {
        double now = MediaTime.Now;
        if (downloadWindowStarted <= 0 || now - lastPacketReceived > BandwidthBurstGapSeconds)
        {
            downloadWindowStarted = now;
            downloadWindowBytes = 0;
        }
        else if (downloadWindowBytes >= BandwidthSampleBytes &&
            now - lastPacketReceived > BandwidthSameReadSeconds &&
            lastPacketReceived - downloadWindowStarted > 0.001)
        {
            // A sample closes where a read ends, since the packets parsed from one read all arrived by then
            // rather than when each is taken; the next sample starts from that point.
            double sample = downloadWindowBytes * 8.0 / (lastPacketReceived - downloadWindowStarted);
            double current = Volatile.Read(ref estimatedBandwidthBitsPerSecond);
            Volatile.Write(
                ref estimatedBandwidthBitsPerSecond,
                current > 0 ? current + ((sample - current) * BandwidthSmoothingFactor) : sample);

            downloadWindowStarted = lastPacketReceived;
            downloadWindowBytes = 0;
        }

        downloadWindowBytes += bytes;
        lastPacketReceived = now;
    }

    /// <summary>
    /// Whether a resuming run is re-sending a packet this end already has. Decode timestamps only
    /// ever climb within a track. A missing DTS can use a PTS at or before the last received DTS.
    /// </summary>
    private bool IsAlreadyReceived(int trackId, long pts, long dts) =>
        currentRun == resumeRunId &&
        WasAlreadyReceived(trackId, pts, dts);

    private bool WasAlreadyReceived(int trackId, long pts, long dts)
    {
        long timestamp = dts != PacketStreamProtocol.NoTimestamp ? dts : pts;
        return timestamp != PacketStreamProtocol.NoTimestamp &&
            lastReceivedDts.TryGetValue(trackId, out long last) &&
            timestamp <= last;
    }

    /// <summary>
    /// Where a replacement socket should carry on from: the earliest point any track still needs,
    /// so no track is left with a gap when the server rounds the request back to a keyframe.
    /// </summary>
    private double ResumeAnchor()
    {
        double earliest = EarliestDelivered();
        return MediaTime.IsValid(earliest) ? Math.Max(0, earliest) : position;
    }

    /// <summary>
    /// How far every selected track has been delivered, which is as far as playback could reach
    /// without waiting on the network. Invalid until a run has sent something for each of them.
    /// </summary>
    private double EarliestDelivered()
    {
        var earliest = double.MaxValue;
        foreach ((int trackId, long dts) in lastReceivedDts)
        {
            double time = ToSeconds(trackId, dts);
            if (MediaTime.IsValid(time) && time < earliest)
            {
                earliest = time;
            }
        }

        return earliest == double.MaxValue ? MediaTime.NoTimestamp : earliest;
    }

    /// <summary>A run the server itself refused, as opposed to a socket that merely went.</summary>
    private sealed class PacketStreamRunFault(string message) : IOException(message);

    private void EndRun()
    {
        runEnded = true;
        lock (queueSync)
        {
            SignalArrivalLocked();
        }
    }

    /// <summary>Stops the run with <paramref name="fault"/>, which the next read throws.</summary>
    private void Fail(Exception fault)
    {
        readerFault = fault;
        lock (queueSync)
        {
            SignalArrivalLocked();
        }
    }

    private void SignalArrivalLocked()
    {
        arrival?.TrySetResult();
        arrival = null;
    }

    /// <summary>
    /// Whether the run the server is sending is the one this end last asked for. Records of any
    /// other belong to a position the player has already left.
    /// </summary>
    private bool IsCurrentRun() => currentRun == Volatile.Read(ref requestedRun);

    /// <summary>
    /// Tells the server it may send <paramref name="bytes"/> more. Batched, because a message per
    /// packet would cost more than the flow control it buys.
    /// </summary>
    private void ReleaseCredit(int bytes)
    {
        if (!usesWebSocket)
        {
            return;
        }

        int pending = Interlocked.Add(ref releasedCredit, bytes);
        if (pending < PacketStreamProtocol.DefaultCreditBytes / PacketStreamProtocol.CreditReleaseDivisor)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref releasedCredit, 0, pending) == pending)
        {
            Post(PacketStreamProtocol.EncodeCredit(pending));
        }
    }

    /// <summary>
    /// Queues a message behind whatever is already going out. Sends are chained rather than awaited
    /// because the demux thread posts them and a socket allows only one send at a time.
    /// </summary>
    /// <remarks>
    /// The send itself is not cancelable: canceling one aborts the socket. A failed send is left
    /// to the receive loop, which is already watching for a socket that has gone.
    /// </remarks>
    private void Post(byte[] message)
    {
        lock (socketSync)
        {
            WebSocket current = socket
                ?? throw new InvalidOperationException("The packet source is not using a WebSocket.");
            sendChain = sendChain.ContinueWith(
                async _ =>
                {
                    try
                    {
                        await current
                            .SendAsync(message, WebSocketMessageType.Binary, true, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private static async Task<RemoteStreamManifest> ReadManifestAsync(
        PacketSocketReader reader,
        CancellationToken cancellationToken)
    {
        while (await reader.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            ReadOnlySpan<byte> message = reader.Message.Span;
            var (type, _, _) = PacketStreamProtocol.ReadRecordHeader(message);
            ReadOnlySpan<byte> payload = message[PacketStreamProtocol.RecordHeaderSize..];

            switch (type)
            {
                case PacketStreamRecordType.Manifest:
                    return JsonSerializer.Deserialize(payload, JsonTypeInfo<RemoteStreamManifest>())
                        ?? throw new IOException("The server sent an empty manifest.");

                case PacketStreamRecordType.Error:
                    var (code, text) = PacketStreamProtocol.DecodeError(payload);
                    throw new PacketStreamRunFault($"The server would not open that media ({code}): {text}");

                default:
                    break;
            }
        }

        throw new IOException("The server closed the packet socket before naming the media.");
    }

    private static WirePacket Decode(
        int trackId,
        byte flags,
        long pts,
        long dts,
        long duration,
        ReadOnlySpan<byte> payload,
        PendingPacketSideData pendingSideData)
    {
        ReadOnlySpan<byte> bitstream = payload[PacketStreamProtocol.PacketHeaderSize..];
        ReadOnlySpan<byte> hdr10PlusMetadata = pendingSideData.Take(trackId, pts, dts);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, bitstream.Length + hdr10PlusMetadata.Length));
        bitstream.CopyTo(buffer);
        hdr10PlusMetadata.CopyTo(buffer.AsSpan(bitstream.Length));
        return new WirePacket(
            trackId,
            pts,
            dts,
            duration,
            (flags & PacketStreamProtocol.KeyFrameFlag) != 0,
            buffer,
                bitstream.Length,
                hdr10PlusMetadata.Length);
    }

    private int[] SelectedTrackIds()
    {
        var tracks = new List<int>(2);
        if (VideoStreamIndex >= 0)
        {
            tracks.Add(VideoStreamIndex);
        }

        int audio = selectedAudioTrackId;
        if (audio >= 0 && !tracks.Contains(audio))
        {
            tracks.Add(audio);
        }

        return tracks.ToArray();
    }

    private void DrainQueue()
    {
        lock (queueSync)
        {
            while (queue.TryDequeue(out WirePacket wire))
            {
                ArrayPool<byte>.Shared.Return(wire.Buffer);
            }

            while (history.TryDequeue(out WirePacket wire))
            {
                ArrayPool<byte>.Shared.Return(wire.Buffer);
            }

            while (replay.TryDequeue(out WirePacket wire))
            {
                ArrayPool<byte>.Shared.Return(wire.Buffer);
            }

            historyBytes = 0;
            Volatile.Write(ref historyStartSeconds, MediaTime.NoTimestamp);
        }
    }

    private void RecalculateHistoryBytesLocked()
    {
        historyBytes = 0;
        foreach (WirePacket wire in history)
        {
            double time = PacketTime(wire);
            if (!MediaTime.IsValid(time) ||
                !MediaTime.IsValid(playbackPositionSeconds) ||
                time <= playbackPositionSeconds)
            {
                historyBytes += wire.Buffer.LongLength;
            }
        }
    }

    private static JsonTypeInfo<T> JsonTypeInfo<T>() =>
        (JsonTypeInfo<T>?)RemoteApiJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new InvalidOperationException($"No generated JSON metadata exists for {typeof(T)}.");

    private static MediaInfo BuildInfo(RemoteStreamManifest manifest)
    {
        var streams = new List<MediaStreamInfo>(manifest.Tracks.Length);
        MediaStreamInfo? video = null;
        MediaStreamInfo? audio = null;

        foreach (RemoteStreamTrack track in manifest.Tracks)
        {
            var stream = new MediaStreamInfo(
                track.Id,
                Enum.TryParse(track.Kind, ignoreCase: true, out MediaStreamKind kind) ? kind : MediaStreamKind.Other,
                track.CodecName,
                track.Language,
                track.Title,
                track.IsDefault)
            {
                Duration = TimeSpan.FromMilliseconds(track.DurationMilliseconds),
                SourcePath = track.SourcePath,
                Width = track.Width,
                Height = track.Height,
                PixelAspectRatio = track.Parameters.PixelAspectRatio,
                Rotation = track.Parameters.Rotation,
                MirrorHorizontally = track.Parameters.MirrorHorizontally,
                FrameRate = track.FrameRate,
                PixelFormat = track.Parameters.PixelFormat,
                SampleRate = track.SampleRate,
                ChannelCount = track.ChannelCount,
                SampleFormat = track.Parameters.SampleFormat,
            };

            streams.Add(stream);
            if (track.Id == manifest.DefaultVideoTrackId)
            {
                video = stream;
            }

            if (track.Id == manifest.DefaultAudioTrackId)
            {
                audio = stream;
            }
        }

        return new MediaInfo(
            manifest.Source,
            manifest.FormatName,
            TimeSpan.FromMilliseconds(manifest.DurationMilliseconds),
            manifest.Bitrate,
            manifest.CanSeek,
            streams,
            manifest.Metadata)
        {
            DefaultVideoStream = video,
            DefaultAudioStream = audio,
            Chapters = manifest.Chapters
                .Select(chapter => new MediaChapter(
                    chapter.Index,
                    TimeSpan.FromMilliseconds(chapter.StartMilliseconds),
                    TimeSpan.FromMilliseconds(chapter.EndMilliseconds),
                    chapter.Title))
                .ToArray(),
        };
    }

    public void Dispose()
    {
        _ = ObserveDisposalAsync();
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalSync)
            return new ValueTask(disposal ??= DisposeCoreAsync());
    }

    private async Task ObserveDisposalAsync()
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            AppLog.Warning("Remote.Video", "Could not close the packet source.", exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        disposed = true;
        lifetime.Cancel();
        DrainQueue();
        lock (queueSync)
        {
            SignalArrivalLocked();
        }

        WebSocket? current;
        Task sending;
        lock (socketSync)
        {
            current = socket;
            sending = sendChain;
        }

        try
        {
            if (receiveTask is not null)
                await receiveTask.ConfigureAwait(false);
            await sending.ConfigureAwait(false);
            if (current?.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource();
                Task close = current.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done.", timeout.Token);
                Task deadline = Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
                try
                {
                    if (await Task.WhenAny(close, deadline).ConfigureAwait(false) != close)
                        current.Abort();
                    await close.ConfigureAwait(false);
                }
                finally { timeout.Cancel(); }
            }
        }
        finally
        {
            current?.Dispose();
            readerLifetime?.Dispose();
            lifetime.Dispose();
        }
    }

    private sealed class PendingPacketSideData
    {
        private byte[] buffer = [];
        private int trackId;
        private long pts;
        private long dts;
        private int length;

        public void Capture(int packetTrackId, ReadOnlySpan<byte> payload)
        {
            var timestamps = PacketStreamProtocol.ReadPacketSideDataHeader(payload);
            int metadataLength = payload.Length - PacketStreamProtocol.PacketSideDataHeaderSize;
            if (metadataLength <= 0 || metadataLength > PacketStreamProtocol.MaximumPacketSideDataSize)
            {
                throw new InvalidDataException("The packet HDR10+ side data has an invalid length.");
            }

            if (buffer.Length < metadataLength)
            {
                buffer = new byte[Math.Max(metadataLength, buffer.Length * 2)];
            }

            payload[PacketStreamProtocol.PacketSideDataHeaderSize..].CopyTo(buffer);
            trackId = packetTrackId;
            pts = timestamps.Pts;
            dts = timestamps.Dts;
            length = metadataLength;
        }

        public ReadOnlySpan<byte> Take(int packetTrackId, long packetPts, long packetDts)
        {
            if (!Matches(packetTrackId, packetPts, packetDts))
            {
                Clear();
                return [];
            }

            ReadOnlySpan<byte> result = buffer.AsSpan(0, length);
            Clear();
            return result;
        }

        public void Clear() => length = 0;

        private bool Matches(int packetTrackId, long packetPts, long packetDts) =>
            length > 0 && trackId == packetTrackId && pts == packetPts && dts == packetDts;
    }

    private readonly record struct WirePacket(
        int TrackId,
        long Pts,
        long Dts,
        long Duration,
        bool IsKeyFrame,
        byte[] Buffer,
        int Length,
        int Hdr10PlusMetadataLength);
}
