using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.NativeBindings;
using LightStudio.NativeBindings.Types;

namespace LightStudio.LightPlayer.WebWorkers.Sources;

/// <summary>
/// A packet source the page reads from a source of its own and relays here in batches. Moving the source
/// starts a new generation; batches of an older one are dropped as they arrive. Buffering figures belong to
/// the page's source, so the page reports them itself.
/// </summary>
[Include("libavformat/avformat.h")]
[Include("libavutil/hwcontext.h")]
internal sealed partial class RelayedPacketSource : IBufferedPacketSource, IReconfigurablePacketSource
{
    private readonly Queue<PacketRelayRecord> queue = new();
    private Dictionary<int, WireRelayStream> streams = [];
    private long queuedBytes;

    public RelayedPacketSource(WireRelaySource source)
    {
        Apply(source);
        AudioStreamIndex = source.AudioStream;
    }

    public MediaInfo Info { get; private set; } = null!;

    public int VideoStreamIndex { get; private set; }

    public int AudioStreamIndex { get; private set; }

    public bool CanSeek { get; private set; }

    public double ReconfigurationPositionSeconds { get; private set; } = double.NaN;

    public double BufferedSeconds => MediaTime.NoTimestamp;

    public double HistoryStartSeconds => MediaTime.NoTimestamp;

    public double EstimatedBandwidthBitsPerSecond => 0;

    /// <summary>Generation of the batches this source accepts; it advances with every seek.</summary>
    public int Generation { get; private set; }

    /// <summary>Bytes of relayed batches taken in total, used or dropped, which reopen the page's window.</summary>
    public long Consumed { get; private set; }

    public bool IsEmpty => queue.Count == 0;

    public void Accept(int generation, byte[] batch)
    {
        if (generation != Generation)
        {
            Consumed += batch.Length;
            return;
        }

        foreach (PacketRelayRecord record in PacketRelayWriter.Read(batch))
        {
            queue.Enqueue(record);
            queuedBytes += record.Length;
        }
    }

    public CodecParameters GetCodecParameters(int streamIndex) =>
        streams.TryGetValue(streamIndex, out WireRelayStream? stream)
            ? stream.Parameters
            : throw new ArgumentOutOfRangeException(nameof(streamIndex), "No such stream in this media.");

    public void SelectAudioStream(int streamIndex) => AudioStreamIndex = streamIndex;

    public PacketReadStatus Read(MediaPacket packet)
    {
        if (!queue.TryDequeue(out PacketRelayRecord record))
            return PacketReadStatus.WouldBlock;

        queuedBytes -= record.Length;
        Consumed += record.Length;
        switch (record.Kind)
        {
            case PacketRelayRecordKind.Packet:
                packet.FillFromWire(record.Stream, record.Pts, record.Dts, record.Duration, record.IsKeyFrame, record.Payload.Span,
                    record.SideData.Span);
                return PacketReadStatus.Ok;

            case PacketRelayRecordKind.End:
                return PacketReadStatus.EndOfFile;

            case PacketRelayRecordKind.Fault:
                throw new IOException(Encoding.UTF8.GetString(record.Payload.Span));

            default:
                Apply(JsonSerializer.Deserialize(record.Payload.Span, PacketRelayJsonContext.Default.WireRelaySource)
                    ?? throw new InvalidDataException("The page relayed an empty reconfiguration."));
                ReconfigurationPositionSeconds = record.Position;
                return PacketReadStatus.Reconfigured;
        }
    }

    public bool Seek(double positionSeconds)
    {
        if (!CanSeek)
            return false;

        Generation++;
        queue.Clear();
        Consumed += queuedBytes;
        queuedBytes = 0;
        return true;
    }

    public double ToSeconds(int streamIndex, long timestamp) =>
        timestamp != AV_NOPTS_VALUE && streams.TryGetValue(streamIndex, out WireRelayStream? stream)
            ? (timestamp * stream.Parameters.TimeBase) + stream.Origin
            : MediaTime.NoTimestamp;

    public void UpdatePlaybackPosition(double positionSeconds)
    {
    }

    public void Dispose()
    {
        queue.Clear();
        queuedBytes = 0;
    }

    private void Apply(WireRelaySource source)
    {
        // The page has no libavcodec to tell its subtitle formats apart.
        Info = FfmpegSubtitleReader.Classify(source.Info);
        VideoStreamIndex = source.VideoStream;
        CanSeek = source.CanSeek;
        var described = new Dictionary<int, WireRelayStream>(source.Streams.Count);
        foreach (WireRelayStream stream in source.Streams)
            described[stream.Index] = stream;
        streams = described;
    }
}
