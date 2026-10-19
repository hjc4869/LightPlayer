using System;
using System.Collections.Generic;

namespace LightStudio.FfmpegShim.Media;

public enum MediaStreamKind
{
    Video,
    Audio,
    Subtitle,
    Other,
}

/// <summary>How libavcodec decodes a subtitle codec.</summary>
[Flags]
public enum SubtitleCodecKind
{
    None = 0,
    Text = 1,
    Bitmap = 2,
}

/// <summary>Everything the pipeline and the UI need to know about one elementary stream.</summary>
public sealed record MediaStreamInfo(
    int Index,
    MediaStreamKind Kind,
    string CodecName,
    string Language,
    string Title,
    bool IsDefault)
{
    /// <summary>Stream duration, or <see cref="TimeSpan.Zero"/> when the container does not state one.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// File this stream really lives in, when it is not the media itself. Set by sources that merge
    /// sidecar files into one stream table; null for a container's own streams.
    /// </summary>
    public string SourcePath { get; init; }

    // Video
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Sample (pixel) aspect ratio; 1.0 for square pixels.</summary>
    public double PixelAspectRatio { get; init; } = 1.0;

    /// <summary>Degrees the picture has to be turned clockwise for display: 0, 90, 180 or 270.</summary>
    public int Rotation { get; init; }

    /// <summary>Whether the picture is mirrored left to right before <see cref="Rotation"/>.</summary>
    public bool MirrorHorizontally { get; init; }

    /// <summary>Picture size as it is meant to be seen, with the rotation and pixel aspect applied.</summary>
    public (double Width, double Height) DisplaySize => Rotation is 90 or 270
        ? (Height, Width * PixelAspectRatio)
        : (Width * PixelAspectRatio, Height);

    /// <summary>Container/stream nominal frame rate. Advisory only: presentation is PTS driven.</summary>
    public double FrameRate { get; init; }

    public string PixelFormat { get; init; }

    // Audio
    public int SampleRate { get; init; }

    public int ChannelCount { get; init; }

    public string SampleFormat { get; init; }

    /// <summary>How a subtitle stream's codec is decoded, when whoever described the stream knew; null otherwise.</summary>
    public SubtitleCodecKind? SubtitleKind { get; init; }

    public override string ToString() => Kind switch
    {
        MediaStreamKind.Video => $"#{Index} video {CodecName} {Width}x{Height} {FrameRate:0.###} fps",
        MediaStreamKind.Audio => $"#{Index} audio {CodecName} {SampleRate} Hz {ChannelCount} ch{(Language is null ? "" : $" [{Language}]")}",
        _ => $"#{Index} {Kind.ToString().ToLowerInvariant()} {CodecName}",
    };
}

/// <summary>One navigable section of the media.</summary>
/// <remarks>The times are in the same zero-based domain as everything else the pipeline reports,
/// so they can be handed to a seek unchanged.</remarks>
public sealed record MediaChapter(
    int Index,
    TimeSpan Start,
    TimeSpan End,
    string Title)
{
    /// <summary>Label for a chapter list; numbered when the container names no title.</summary>
    public string DisplayName => string.IsNullOrEmpty(Title) ? $"Chapter {Index + 1}" : Title;

    public override string ToString() => $"{Start:hh\\:mm\\:ss}  {DisplayName}";
}

/// <summary>Container-level description produced when a source is opened.</summary>
public sealed record MediaInfo(
    string Source,
    string FormatName,
    TimeSpan Duration,
    long Bitrate,
    bool CanSeek,
    IReadOnlyList<MediaStreamInfo> Streams,
    IReadOnlyDictionary<string, string> Metadata)
{
    public MediaStreamInfo DefaultVideoStream { get; init; }

    public MediaStreamInfo DefaultAudioStream { get; init; }

    /// <summary>Chapters the container states, in order, or empty when it states none.</summary>
    public IReadOnlyList<MediaChapter> Chapters { get; init; } = Array.Empty<MediaChapter>();

    public bool HasVideo => DefaultVideoStream is not null;

    public bool HasAudio => DefaultAudioStream is not null;
}
