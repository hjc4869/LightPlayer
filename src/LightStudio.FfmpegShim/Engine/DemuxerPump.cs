#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.FfmpegShim.Engine;

/// <summary>
/// Reads packets from one source and routes them to the linked decoders. Every member except
/// <see cref="GetStatus"/> and <see cref="ReportPlaybackPosition"/> must be called by the pump's owner.
/// </summary>
public sealed class DemuxerPump : IDisposable
{
    private readonly IPacketSource source;
    private readonly PumpMode mode;
    private readonly MediaPacketPool pool;
    private readonly Action<DemuxerNotification> notify;
    private readonly Dictionary<int, PacketLink> links = new();

    // Packets no queue would take yet, kept rather than dropped: a hole in a run reaches the decoder
    // as a missing picture. End-of-stream markers wait here too.
    private readonly List<(PacketLink Link, MediaPacket Packet)> held = new();
    private double playbackPosition = double.NaN;
    private bool endOfFile;
    private bool reconfiguring;
    private bool faulted;
    private int recoverableErrors;

    public DemuxerPump(IPacketSource source, PumpMode mode, MediaPacketPool pool, Action<DemuxerNotification> notify)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.mode = mode;
        this.pool = pool;
        this.notify = notify;
    }

    public IPacketSource Source => source;

    /// <summary>Interrupts a blocked hand-off so the owner can run a command. Blocking mode only.</summary>
    public CancellationToken Interrupt { get; set; }

    public bool IsFaulted => faulted;

    /// <summary>The last read found nothing yet from a source that is still live.</summary>
    public bool IsWaitingForSource { get; private set; }

    /// <summary>Routes <paramref name="streamIndex"/> to <paramref name="link"/>, or stops reading it.</summary>
    public void Link(int streamIndex, PacketLink? link)
    {
        if (links.TryGetValue(streamIndex, out PacketLink? previous) && !ReferenceEquals(previous, link))
            DropHeld(previous);

        bool audio = IsAudioStream(streamIndex);
        if (link is null)
        {
            lock (links)
                links.Remove(streamIndex);
            if (audio && source.AudioStreamIndex == streamIndex)
                source.SelectAudioStream(-1);
            return;
        }

        lock (links)
            links[streamIndex] = link;
        if (audio && source.AudioStreamIndex != streamIndex)
            source.SelectAudioStream(streamIndex);
    }

    /// <summary>Switches an adaptive source to another video representation.</summary>
    public void SelectRendition(int videoStreamIndex, int audioStreamIndex)
    {
        if (source is not IAdaptiveVideoPacketSource adaptive)
            throw new NotSupportedException("The source has no alternative video representations.");

        adaptive.SelectVideoStream(videoStreamIndex);
        if (source.AudioStreamIndex != audioStreamIndex)
            source.SelectAudioStream(audioStreamIndex);
    }

    /// <summary>Moves the source and every linked queue to the run in <paramref name="request"/>.</summary>
    public bool Seek(SeekRequest request)
    {
        // The target is in place before any packet of the new run can reach a decoder.
        foreach (PacketLink link in links.Values)
            link.SetTarget(request.Serial, request.Position);

        bool cut = !request.SourceAlreadyPositioned && request.PreferBuffered && TryCutToBuffered(request);
        if (!cut)
        {
            if (!request.DiscardBeforePosition && !request.SourceAlreadyPositioned)
            {
                foreach (PacketLink link in links.Values)
                    link.SetTarget(request.Serial, double.NaN);
            }

            if (!request.SourceAlreadyPositioned && !source.Seek(request.Position))
                return false;

            foreach (PacketLink link in links.Values)
                link.Queue.Flush(request.Serial);
            DropHeld(null);
        }

        endOfFile = false;
        reconfiguring = false;
        return true;
    }

    /// <summary>Records the presented position for a source that keeps history.</summary>
    public void ReportPlaybackPosition(double seconds) => Volatile.Write(ref playbackPosition, seconds);

    /// <summary>Wakes a hand-off blocked on any linked queue. Safe from any thread.</summary>
    public void AbortLinks()
    {
        lock (links)
        {
            foreach (PacketLink link in links.Values)
                link.Queue.Abort();
        }
    }

    public PumpResult Step()
    {
        if (faulted)
            return PumpResult.Finished;
        if (reconfiguring)
            return PumpResult.Idle;

        double position = Volatile.Read(ref playbackPosition);
        if (!double.IsNaN(position))
            (source as IBufferedPacketSource)?.UpdatePlaybackPosition(position);

        while (held.Count > 0)
        {
            (PacketLink link, MediaPacket packet) = held[0];
            if (!Hand(link, packet))
                return PumpResult.Idle;
            held.RemoveAt(0);
        }

        if (endOfFile)
            return PumpResult.Finished;

        MediaPacket next = pool.Rent();
        PacketReadStatus status;
        try
        {
            status = source.Read(next);
        }
        catch (Exception exception)
        {
            next.Recycle();
            Fail(exception);
            return PumpResult.Finished;
        }

        IsWaitingForSource = status == PacketReadStatus.WouldBlock;

        switch (status)
        {
            case PacketReadStatus.Ok:
                Route(next);
                return PumpResult.Progressed;

            case PacketReadStatus.EndOfFile:
                next.Recycle();
                foreach (PacketLink link in links.Values)
                {
                    MediaPacket marker = pool.Rent();
                    marker.MarkEndOfStream(StreamOf(link));
                    held.Add((link, marker));
                }

                endOfFile = true;
                return PumpResult.Progressed;

            case PacketReadStatus.Recoverable:
                next.Recycle();
                Interlocked.Increment(ref recoverableErrors);
                return PumpResult.Progressed;

            case PacketReadStatus.WouldBlock:
                next.Recycle();
                return PumpResult.Idle;

            case PacketReadStatus.Reconfigured when source is IReconfigurablePacketSource changed:
                next.Recycle();
                DropHeld(null);
                reconfiguring = true;
                notify(new DemuxerNotification(
                    DemuxerNotificationKind.Reconfigured,
                    Info: source.Info,
                    Position: changed.ReconfigurationPositionSeconds));
                return PumpResult.Idle;

            default:
                next.Recycle();
                Fail(new IOException("The media source failed while reading."));
                return PumpResult.Finished;
        }
    }

    public DemuxerStatus GetStatus()
    {
        int videoCount = 0, audioCount = 0;
        long videoBytes = 0, audioBytes = 0;
        bool audioFull = false;
        double occupancy = 0;
        int videoStream = source.VideoStreamIndex;
        lock (links)
        {
            foreach ((int stream, PacketLink link) in links)
            {
                int count = link.Queue.Count;
                long bytes = link.Queue.ByteCount;
                occupancy = Math.Max(occupancy, link.Queue.Occupancy);
                if (stream == videoStream)
                {
                    videoCount += count;
                    videoBytes += bytes;
                }
                else
                {
                    audioCount += count;
                    audioBytes += bytes;
                    audioFull |= link.Queue.IsFull;
                }
            }
        }

        var buffered = source as IBufferedPacketSource;
        return new DemuxerStatus
        {
            EndOfFile = Volatile.Read(ref endOfFile),
            IsBuffered = buffered is not null,
            BufferedSeconds = buffered?.BufferedSeconds ?? double.NaN,
            HistoryStartSeconds = buffered?.HistoryStartSeconds ?? double.NaN,
            EstimatedBandwidthBitsPerSecond = buffered?.EstimatedBandwidthBitsPerSecond ?? 0,
            VideoPacketCount = videoCount,
            AudioPacketCount = audioCount,
            VideoPacketBytes = videoBytes,
            AudioPacketBytes = audioBytes,
            AudioPacketQueueFull = audioFull,
            PacketOccupancy = occupancy,
            RecoverableErrors = Volatile.Read(ref recoverableErrors),
        };
    }

    public void Dispose()
    {
        DropHeld(null);
        lock (links)
            links.Clear();
        source.Dispose();
    }

    private void Route(MediaPacket packet)
    {
        if (!links.TryGetValue(packet.StreamIndex, out PacketLink? link))
        {
            packet.Recycle();
            return;
        }

        if (!Hand(link, packet))
            held.Add((link, packet));
    }

    /// <summary>Offers a packet to its queue. False leaves it with the caller to offer again.</summary>
    private bool Hand(PacketLink link, MediaPacket packet)
    {
        if (link.IsClosed)
        {
            packet.Recycle();
            return true;
        }

        return mode == PumpMode.Blocking
            ? link.Queue.Enqueue(packet, Interrupt, keepOnAbort: true)
            : link.Queue.TryWrite(packet);
    }

    /// <summary>
    /// Repositions the run inside the packets already fetched, rather than moving the source. Only a
    /// source that reads ahead over a network has anything to save here: a skip inside its buffer would
    /// otherwise throw away everything fetched and wait out a server round trip for material held here.
    /// What is kept begins at a keyframe at or before the target, so the decoders resume from a clean
    /// picture and drop their way to the target as they would after any seek.
    /// </summary>
    private bool TryCutToBuffered(SeekRequest request)
    {
        if (source is not IBufferedPacketSource || !links.TryGetValue(source.VideoStreamIndex, out PacketLink? video))
            return false;

        double target = request.Position;
        MediaQueue<MediaPacket> queue = video.Queue;
        if (!queue.Any(packet => Seconds(packet) <= target) || !queue.Any(packet => Seconds(packet) > target))
            return false;

        links.TryGetValue(source.AudioStreamIndex, out PacketLink? audio);
        int previousVideo = queue.Serial;
        if (queue.CutTo(
                packet => packet.IsKeyFrame && Seconds(packet) <= target,
                packet => Seconds(packet) > target,
                request.Serial))
        {
            // Sound costs so much less per second than picture that its queue nearly always reaches
            // further; where it does not, it is only put on the new run.
            if (audio is not null && !audio.Queue.CutTo(packet => Seconds(packet) <= target, _ => true, request.Serial))
                audio.Queue.Restamp(request.Serial);
            return true;
        }

        // No picture to start cold from: nothing is cut, and the decoders keep their state and run
        // through to the target, which still beats fetching it all again.
        video.MarkContinuation(previousVideo, request.Serial);
        queue.Restamp(request.Serial);
        if (audio is not null)
        {
            audio.MarkContinuation(audio.Queue.Serial, request.Serial);
            audio.Queue.Restamp(request.Serial);
        }

        return true;
    }

    private double Seconds(MediaPacket packet) =>
        packet.IsEndOfStream ? double.NaN : source.ToSeconds(packet.StreamIndex, packet.Pts);

    private bool IsAudioStream(int streamIndex)
    {
        foreach (MediaStreamInfo stream in source.Info.Streams)
        {
            if (stream.Index == streamIndex)
                return stream.Kind == MediaStreamKind.Audio;
        }

        return false;
    }

    private int StreamOf(PacketLink link)
    {
        foreach ((int stream, PacketLink candidate) in links)
        {
            if (ReferenceEquals(candidate, link))
                return stream;
        }

        return -1;
    }

    private void DropHeld(PacketLink? only)
    {
        for (int index = held.Count - 1; index >= 0; index--)
        {
            if (only is null || ReferenceEquals(held[index].Link, only))
            {
                held[index].Packet.Recycle();
                held.RemoveAt(index);
            }
        }
    }

    private void Fail(Exception exception)
    {
        faulted = true;
        DropHeld(null);
        notify(new DemuxerNotification(DemuxerNotificationKind.Faulted, exception.Message, Exception: exception));
    }
}
