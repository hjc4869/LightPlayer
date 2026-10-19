using System.Collections.Generic;

namespace LightStudio.FfmpegShim.Demuxing;

/// <summary>One video representation exposed by an adaptive container.</summary>
public sealed record VideoRendition(
    int ProgramIndex,
    int StreamIndex,
    int AssociatedAudioStreamIndex,
    long Bandwidth,
    int Width,
    int Height,
    string Name,
    bool IsDefault);

/// <summary>A packet source whose active video representation can change while it is open.</summary>
public interface IAdaptiveVideoPacketSource
{
    IReadOnlyList<VideoRendition> VideoRenditions { get; }

    /// <summary>
    /// Changes the delivered video stream and its associated audio stream, when it has one. Only
    /// the demux thread may call this.
    /// </summary>
    void SelectVideoStream(int streamIndex);
}