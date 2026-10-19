#nullable enable
using System.Collections.Generic;

namespace LightStudio.MediaLibraryCore.IO;

/// <summary>
/// Container-level description of a media file, at the level of detail <c>ffprobe</c> prints.
/// </summary>
/// <remarks>
/// Everything travels as plain numbers, names and tag strings so the report survives a trip over a
/// remote session where the two ends may be built against different FFmpeg majors.
/// </remarks>
public sealed class MediaProbeReport
{
    /// <summary>Comma-separated demuxer names, e.g. <c>mov,mp4,m4a,3gp,3g2,mj2</c>.</summary>
    public string? FormatName { get; init; }

    public string? FormatLongName { get; init; }

    public long DurationMilliseconds { get; init; }

    public long StartTimeMilliseconds { get; init; }

    /// <summary>Container bit rate in bits per second, 0 when the container does not state one.</summary>
    public long BitRate { get; init; }

    public MediaStreamProbeReport[] Streams { get; init; } = [];

    public MediaChapterProbeReport[] Chapters { get; init; } = [];

    /// <summary>Container-level metadata exactly as the demuxer reported it.</summary>
    public Dictionary<string, string> Tags { get; init; } = [];
}

/// <summary>One elementary stream of a <see cref="MediaProbeReport"/>.</summary>
public sealed class MediaStreamProbeReport
{
    public int Index { get; init; }

    /// <summary>FFmpeg media type name: <c>video</c>, <c>audio</c>, <c>subtitle</c>, and so on.</summary>
    public string Kind { get; init; } = "unknown";

    public string? CodecName { get; init; }

    public string? CodecLongName { get; init; }

    /// <summary>Four-character container tag, when the container uses one.</summary>
    public string? CodecTag { get; init; }

    public string? Profile { get; init; }

    public int Level { get; init; }

    public long BitRate { get; init; }

    public long DurationMilliseconds { get; init; }

    public long FrameCount { get; init; }

    /// <summary>Comma-separated disposition flags, e.g. <c>default, forced</c>.</summary>
    public string? Disposition { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string? PixelFormat { get; init; }

    public double FrameRate { get; init; }

    public double AverageFrameRate { get; init; }

    /// <summary>Pixel aspect ratio as <c>num:den</c>, null when square or unknown.</summary>
    public string? SampleAspectRatio { get; init; }

    public string? DisplayAspectRatio { get; init; }

    /// <summary>Degrees the picture has to be turned clockwise for display: 0, 90, 180 or 270.</summary>
    public int Rotation { get; init; }

    /// <summary>Whether the picture is mirrored left to right before <see cref="Rotation"/>.</summary>
    public bool MirrorHorizontally { get; init; }

    public int BitDepth { get; init; }

    public string? ColorRange { get; init; }

    public string? ColorSpace { get; init; }

    public string? ColorPrimaries { get; init; }

    public string? ColorTransfer { get; init; }

    public int SampleRate { get; init; }

    public int ChannelCount { get; init; }

    public string? ChannelLayout { get; init; }

    public string? SampleFormat { get; init; }

    /// <summary>Stream metadata, which is where language and stream titles live.</summary>
    public Dictionary<string, string> Tags { get; init; } = [];
}

public sealed class MediaChapterProbeReport
{
    public long StartMilliseconds { get; init; }

    public long EndMilliseconds { get; init; }

    public string? Title { get; init; }
}
