using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Turns an FFmpeg container inspection into the transport-friendly <see cref="MediaProbeReport"/>
/// the properties view renders. Shared by every platform whose files FFmpeg can open directly.
/// </summary>
public static class MediaProbeFactory
{
    /// <summary>Inspects <paramref name="stream"/>, returning null when it cannot be opened.</summary>
    public static MediaProbeReport? Inspect(Stream stream, bool leaveOpen = false)
    {
        MediaInspection? inspection;
        try
        {
            inspection = MediaInspector.Inspect(stream, leaveOpen);
        }
        catch (Exception)
        {
            return null;
        }

        return inspection is null ? null : Map(inspection);
    }

    /// <summary>Maps an already-open disc title into the properties transport model.</summary>
    public static MediaProbeReport Inspect(FfmpegDemuxer demuxer) =>
        Map(MediaInspector.Inspect(demuxer));

    /// <summary>Maps an inspection made elsewhere, such as in a browser media worker.</summary>
    public static MediaProbeReport Map(MediaInspection inspection) => new()
    {
        FormatName = inspection.FormatName,
        FormatLongName = inspection.FormatLongName,
        DurationMilliseconds = (long)inspection.Duration.TotalMilliseconds,
        StartTimeMilliseconds = (long)inspection.StartTime.TotalMilliseconds,
        BitRate = inspection.BitRate,
        Streams = inspection.Streams.Select(Map).ToArray(),
        Chapters = inspection.Chapters.Select(Map).ToArray(),
        Tags = ToDictionary(inspection.Tags),
    };

    private static MediaStreamProbeReport Map(MediaStreamInspection stream) => new()
    {
        Index = stream.Index,
        Kind = stream.Kind,
        CodecName = stream.CodecName,
        CodecLongName = stream.CodecLongName,
        CodecTag = stream.CodecTag,
        Profile = stream.Profile,
        Level = stream.Level,
        BitRate = stream.BitRate,
        DurationMilliseconds = (long)stream.Duration.TotalMilliseconds,
        FrameCount = stream.FrameCount,
        Disposition = stream.Disposition,
        Width = stream.Width,
        Height = stream.Height,
        PixelFormat = stream.PixelFormat,
        FrameRate = stream.FrameRate,
        AverageFrameRate = stream.AverageFrameRate,
        SampleAspectRatio = stream.SampleAspectRatio,
        DisplayAspectRatio = stream.DisplayAspectRatio,
        Rotation = stream.Rotation,
        MirrorHorizontally = stream.MirrorHorizontally,
        BitDepth = stream.BitDepth,
        ColorRange = stream.ColorRange,
        ColorSpace = stream.ColorSpace,
        ColorPrimaries = stream.ColorPrimaries,
        ColorTransfer = stream.ColorTransfer,
        SampleRate = stream.SampleRate,
        ChannelCount = stream.ChannelCount,
        ChannelLayout = stream.ChannelLayout,
        SampleFormat = stream.SampleFormat,
        Tags = ToDictionary(stream.Tags),
    };

    private static MediaChapterProbeReport Map(MediaChapterInspection chapter) => new()
    {
        StartMilliseconds = (long)chapter.Start.TotalMilliseconds,
        EndMilliseconds = (long)chapter.End.TotalMilliseconds,
        Title = chapter.Title,
    };

    private static Dictionary<string, string> ToDictionary(IReadOnlyList<KeyValuePair<string, string>> tags)
    {
        var map = new Dictionary<string, string>(tags.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            map.TryAdd(tag.Key, tag.Value);
        }

        return map;
    }
}
