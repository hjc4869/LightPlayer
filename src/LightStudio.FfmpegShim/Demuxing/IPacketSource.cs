using System;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.FfmpegShim.Demuxing;

public enum PacketReadStatus
{
    /// <summary>A packet was produced.</summary>
    Ok,

    /// <summary>The source is exhausted; decoders should be drained.</summary>
    EndOfFile,

    /// <summary>A recoverable read error; the caller may retry.</summary>
    Recoverable,

    /// <summary>
    /// Nothing is available yet but the source is still live. Only a source that waits on something
    /// slower than a disk returns this; the caller should come back after attending to anything else
    /// it has to do.
    /// </summary>
    WouldBlock,

    /// <summary>An unrecoverable read error.</summary>
    Fatal,

    /// <summary>Output parameters or encoder continuity changed; rebuild decoders before reading packets.</summary>
    Reconfigured,
}

public interface IReconfigurablePacketSource : IPacketSource
{
    double ReconfigurationPositionSeconds { get; }
}

/// <summary>
/// Produces compressed packets and answers timing questions about the streams it carries. The
/// pipeline knows nothing about FFmpeg beyond this interface, so an alternative container reader
/// (or a network/streaming source) can be substituted without touching decoding or rendering.
/// </summary>
public interface IPacketSource : IDisposable
{
    MediaInfo Info { get; }

    /// <summary>Index of the stream the video decoder consumes, or -1.</summary>
    int VideoStreamIndex { get; }

    /// <summary>Index of the stream the audio decoder consumes, or -1.</summary>
    int AudioStreamIndex { get; }

    bool CanSeek { get; }

    /// <summary>
    /// Codec setup of one stream, as plain data. This is what lets a decoder be built over a source
    /// that has no container behind it.
    /// </summary>
    CodecParameters GetCodecParameters(int streamIndex);

    /// <summary>
    /// Chooses which audio stream <see cref="Read"/> delivers packets for; -1 turns audio off. Only
    /// the demux thread may call this. A source that cannot act on the change immediately applies it
    /// at the next <see cref="Seek"/>.
    /// </summary>
    void SelectAudioStream(int streamIndex);

    /// <summary>
    /// Fills <paramref name="packet"/> with the next packet in file order. Implementations must
    /// not block indefinitely so the demux thread stays responsive to seek and stop requests.
    /// </summary>
    PacketReadStatus Read(MediaPacket packet);

    /// <summary>
    /// Repositions the source to the nearest keyframe at or before <paramref name="positionSeconds"/>.
    /// </summary>
    bool Seek(double positionSeconds);

    /// <summary>Converts a stream timestamp to seconds on the normalized playback timeline.</summary>
    double ToSeconds(int streamIndex, long timestamp);
}
