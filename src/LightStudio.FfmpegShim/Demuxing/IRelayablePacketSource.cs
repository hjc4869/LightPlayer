using System;
using System.Threading.Tasks;

namespace LightStudio.FfmpegShim.Demuxing;

/// <summary>Takes one compressed packet as plain data.</summary>
public interface IPacketWriter
{
    void Write(
        int streamIndex,
        long pts,
        long dts,
        long duration,
        bool isKeyFrame,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> hdr10PlusMetadata);
}

/// <summary>
/// A packet source that can hand its packets over as plain data, so a reader without FFmpeg can forward
/// them to an engine in another memory domain. Every stream's timestamps map linearly onto the playback
/// timeline: <c>ToSeconds(stream, t)</c> is <c>t</c> in the stream's time base plus <c>ToSeconds(stream, 0)</c>.
/// </summary>
public interface IRelayablePacketSource : IBufferedPacketSource, IReconfigurablePacketSource
{
    /// <summary>Reads like <see cref="IPacketSource.Read"/>, giving a packet to <paramref name="writer"/>.</summary>
    PacketReadStatus Read(IPacketWriter writer);

    /// <summary>Completes once a read may return something other than <see cref="PacketReadStatus.WouldBlock"/>.</summary>
    Task WaitForPacketsAsync();
}
