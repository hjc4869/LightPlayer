using LightStudio.FfmpegShim.Core;

namespace LightStudio.FfmpegShim.Demuxing;

/// <summary>
/// A packet source that holds packets ahead of the reader and can say how far ahead it has got.
/// </summary>
/// <remarks>
/// Only a source fed by something slower than a disk implements this. A local container is read on
/// demand, so there is no frontier to report and nothing a viewer would recognize as buffering.
/// </remarks>
public interface IBufferedPacketSource : IPacketSource
{
    /// <summary>
    /// Point on the playback timeline every selected track has been delivered up to, or
    /// <see cref="MediaTime.NoTimestamp"/> when nothing has arrived for the run yet.
    /// </summary>
    double BufferedSeconds { get; }

    /// <summary>
    /// Earliest point on the playback timeline retained for a local backward seek, or
    /// <see cref="MediaTime.NoTimestamp"/> when no seekable history is available.
    /// </summary>
    double HistoryStartSeconds { get; }

    /// <summary>
    /// Reports the point currently being presented so decoded-ahead packets are not counted as
    /// backward-seek history.
    /// </summary>
    void UpdatePlaybackPosition(double positionSeconds);

    /// <summary>
    /// Rough recent rate at which packet bytes can be delivered, in bits per second, or 0 until a
    /// useful sample exists. Implementations should exclude pauses caused by a full local buffer.
    /// </summary>
    double EstimatedBandwidthBitsPerSecond { get; }
}
