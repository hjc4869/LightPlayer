#nullable enable
using System;
using System.Collections.Generic;

namespace LightStudio.MediaLibraryCore.IO;

/// <summary>How an indexed video source is opened.</summary>
public enum VideoMediaKind
{
    File,
    Dvd,
    BluRay,
}

/// <summary>Identity and codec of one audio stream found during a video-library scan.</summary>
public sealed class AudioStreamProbeInfo
{
    public int StreamIndex { get; init; }

    public string? CodecName { get; init; }
}

/// <summary>
/// What the indexer needs to know about a candidate video source: the geometry and codecs it reports,
/// plus an already-encoded preview image when the platform could produce one.
/// </summary>
/// <remarks>
/// A null result from <see cref="IMultiMediaPlatformIO.ProbeVideoAsync"/> means "no video stream";
/// a non-null result with <see cref="IsStillImage"/> set means "one picture, not footage";
/// a non-null result with a null <see cref="Preview"/> means "a video whose preview could not be
/// produced", which the UI renders as a placeholder.
/// </remarks>
public sealed class VideoProbeInfo
{
    /// <summary>Whether the source is a regular container, DVD, or Blu-ray disc.</summary>
    public VideoMediaKind MediaKind { get; init; }

    /// <summary>One-based DVD title selected for playback; zero for other media.</summary>
    public int DiscTitle { get; init; }

    public TimeSpan Duration { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public double FrameRate { get; init; }

    /// <summary>Container or selected-title bitrate in bits per second, when reported.</summary>
    public long BitRate { get; init; }

    public string? VideoCodec { get; init; }

    public int VideoStreamIndex { get; init; }

    /// <summary>
    /// Whether the video stream is a single picture rather than moving footage. Together with
    /// <see cref="AudioStreams"/> this is what separates a photograph (a lone still) from a video
    /// and from audio that merely carries cover art, whatever the file happens to be called.
    /// </summary>
    public bool IsStillImage { get; init; }

    /// <summary>Container title tag, when present.</summary>
    public string? Title { get; init; }

    public IReadOnlyList<AudioStreamProbeInfo> AudioStreams { get; init; } = Array.Empty<AudioStreamProbeInfo>();

    /// <summary>Encoded preview image bytes (JPEG or PNG), or null when none could be produced.</summary>
    public byte[]? Preview { get; init; }

    public int PreviewWidth { get; init; }

    public int PreviewHeight { get; init; }

    /// <summary>Timestamp the preview was taken from.</summary>
    public TimeSpan PreviewPosition { get; init; }
}
