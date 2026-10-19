using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Media;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Demuxing;

/// <summary>
/// Keeps a bounded history of packets from another source so recent backward seeks stay in memory.
/// </summary>
public sealed partial class PacketHistorySource : IBufferedPacketSource
{
    private const double HistoryDurationSeconds = 30;
    private const long MaximumHistoryBytes = 200L * 1024 * 1024;

    private readonly IPacketSource source;
    private readonly MediaPacketPool packetPool = new();
    private readonly Queue<RetainedPacket> history = new();
    private readonly Queue<RetainedPacket> replay = new();
    private readonly object sync = new();
    private long retainedBytes;
    private double historyStartSeconds = MediaTime.NoTimestamp;
    private double playbackPositionSeconds = MediaTime.NoTimestamp;
    private double bufferedSeconds = MediaTime.NoTimestamp;
    private int selectedAudioTrackId;
    private bool disposed;

    public PacketHistorySource(IPacketSource source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        selectedAudioTrackId = source.AudioStreamIndex;
    }

    public MediaInfo Info => source.Info;

    public int VideoStreamIndex => source.VideoStreamIndex;

    public int AudioStreamIndex => selectedAudioTrackId;

    public bool CanSeek => source.CanSeek || MediaTime.IsValid(HistoryStartSeconds);

    public double BufferedSeconds => Volatile.Read(ref bufferedSeconds);

    public double HistoryStartSeconds => Volatile.Read(ref historyStartSeconds);

    public double EstimatedBandwidthBitsPerSecond => 0;

    public CodecParameters GetCodecParameters(int streamIndex) => source.GetCodecParameters(streamIndex);

    public void SelectAudioStream(int streamIndex)
    {
        source.SelectAudioStream(streamIndex);
        selectedAudioTrackId = streamIndex;
    }

    public PacketReadStatus Read(MediaPacket packet)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(packet);

        lock (sync)
        {
            if (replay.TryDequeue(out RetainedPacket retained))
            {
                packet.ReferenceFrom(retained.Packet);
                history.Enqueue(retained);
                return PacketReadStatus.Ok;
            }
        }

        PacketReadStatus status = source.Read(packet);
        if (status != PacketReadStatus.Ok)
            return status;

        MediaPacket stored = packetPool.Rent();
        try
        {
            stored.ReferenceFrom(packet);
        }
        catch
        {
            stored.Recycle();
            throw;
        }

        double time = PacketTime(stored);
        lock (sync)
        {
            history.Enqueue(new RetainedPacket(stored, time));
            retainedBytes += stored.SizeInBytes;
            TrimHistoryLocked();
            UpdateHistoryStartLocked();
        }

        if (MediaTime.IsValid(time))
            Volatile.Write(ref bufferedSeconds, time);

        return status;
    }

    public bool Seek(double positionSeconds)
    {
        if (disposed)
            return false;

        double target = Math.Max(0, positionSeconds);
        UpdatePlaybackPosition(target);
        lock (sync)
        {
            if (TrySeekWithinHistoryLocked(target))
                return true;
        }

        if (!source.Seek(target))
            return false;

        lock (sync)
        {
            ClearRetainedLocked();
            UpdateHistoryStartLocked();
        }

        Volatile.Write(ref bufferedSeconds, MediaTime.NoTimestamp);
        return true;
    }

    public double ToSeconds(int streamIndex, long timestamp) => source.ToSeconds(streamIndex, timestamp);

    public void UpdatePlaybackPosition(double positionSeconds)
    {
        if (!MediaTime.IsValid(positionSeconds))
            return;

        lock (sync)
        {
            playbackPositionSeconds = positionSeconds;
            TrimHistoryLocked();
            UpdateHistoryStartLocked();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        lock (sync)
            ClearRetainedLocked();
        packetPool.Clear();
        source.Dispose();
    }

    private bool TrySeekWithinHistoryLocked(double target)
    {
        var buffered = new List<RetainedPacket>(history.Count + replay.Count);
        buffered.AddRange(history);
        buffered.AddRange(replay);

        int resume = FindResumeIndex(buffered, target);
        if (resume < 0)
            return false;

        history.Clear();
        replay.Clear();
        for (var index = 0; index < buffered.Count; index++)
            (index < resume ? history : replay).Enqueue(buffered[index]);

        UpdateHistoryStartLocked();
        return true;
    }

    private void TrimHistoryLocked()
    {
        while (retainedBytes > MaximumHistoryBytes && history.TryDequeue(out RetainedPacket retained))
            Discard(retained);

        if (history.Count == 0 || !MediaTime.IsValid(playbackPositionSeconds))
            return;

        double cutoff = playbackPositionSeconds - HistoryDurationSeconds;
        int resume = FindResumeIndex(history.ToList(), cutoff);
        for (var index = 0; index < resume; index++)
            Discard(history.Dequeue());
    }

    private int FindResumeIndex(List<RetainedPacket> buffered, double target)
    {
        bool hasVideo = VideoStreamIndex >= 0;
        int anchorTrack = hasVideo ? VideoStreamIndex : selectedAudioTrackId;
        var awaiting = new List<int>(SelectedTrackIds());
        var anchor = -1;

        for (var index = 0; index < buffered.Count; index++)
        {
            RetainedPacket retained = buffered[index];
            if (!MediaTime.IsValid(retained.Time))
                continue;

            if (retained.Time > target)
            {
                awaiting.Remove(retained.Packet.StreamIndex);
            }
            else if (retained.Packet.StreamIndex == anchorTrack &&
                     (retained.Packet.IsKeyFrame || !hasVideo))
            {
                anchor = index;
            }
        }

        return anchor >= 0 && awaiting.Count == 0 ? anchor : -1;
    }

    private int[] SelectedTrackIds()
    {
        var tracks = new List<int>(2);
        if (VideoStreamIndex >= 0)
            tracks.Add(VideoStreamIndex);
        if (selectedAudioTrackId >= 0 && !tracks.Contains(selectedAudioTrackId))
            tracks.Add(selectedAudioTrackId);
        return tracks.ToArray();
    }

    private double PacketTime(MediaPacket packet)
    {
        long timestamp = packet.Pts != AV_NOPTS_VALUE ? packet.Pts : packet.Dts;
        return timestamp == AV_NOPTS_VALUE
            ? MediaTime.NoTimestamp
            : source.ToSeconds(packet.StreamIndex, timestamp);
    }

    private void UpdateHistoryStartLocked()
    {
        double start = FindHistoryStart(history);
        if (!MediaTime.IsValid(start))
            start = FindHistoryStart(replay);
        Volatile.Write(ref historyStartSeconds, start);
    }

    private double FindHistoryStart(Queue<RetainedPacket> packets)
    {
        bool hasVideo = VideoStreamIndex >= 0;
        int anchorTrack = hasVideo ? VideoStreamIndex : selectedAudioTrackId;
        foreach (RetainedPacket retained in packets)
        {
            if (retained.Packet.StreamIndex == anchorTrack &&
                (!hasVideo || retained.Packet.IsKeyFrame) &&
                MediaTime.IsValid(retained.Time))
            {
                return retained.Time;
            }
        }

        return MediaTime.NoTimestamp;
    }

    private void ClearRetainedLocked()
    {
        while (history.TryDequeue(out RetainedPacket retained))
            Discard(retained);
        while (replay.TryDequeue(out RetainedPacket retained))
            Discard(retained);
    }

    private void Discard(RetainedPacket retained)
    {
        retainedBytes -= retained.Packet.SizeInBytes;
        retained.Packet.Recycle();
    }

    private readonly record struct RetainedPacket(MediaPacket Packet, double Time);
}