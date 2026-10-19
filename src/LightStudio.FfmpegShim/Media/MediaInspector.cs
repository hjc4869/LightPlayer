using System;
using System.Collections.Generic;
using System.IO;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Interop;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

/// <summary>One elementary stream as libavformat describes it, without decoding anything.</summary>
public sealed partial class MediaStreamInspection
{
    public int Index { get; init; }

    public string Kind { get; init; } = "unknown";

    public string CodecName { get; init; }

    public string CodecLongName { get; init; }

    public string CodecTag { get; init; }

    public string Profile { get; init; }

    public int Level { get; init; }

    public long BitRate { get; init; }

    public TimeSpan Duration { get; init; }

    public long FrameCount { get; init; }

    public string Disposition { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string PixelFormat { get; init; }

    public double FrameRate { get; init; }

    public double AverageFrameRate { get; init; }

    public string SampleAspectRatio { get; init; }

    public string DisplayAspectRatio { get; init; }

    /// <summary>Degrees the picture has to be turned clockwise for display: 0, 90, 180 or 270.</summary>
    public int Rotation { get; init; }

    /// <summary>Whether the picture is mirrored left to right before <see cref="Rotation"/>.</summary>
    public bool MirrorHorizontally { get; init; }

    public int BitDepth { get; init; }

    public string ColorRange { get; init; }

    public string ColorSpace { get; init; }

    public string ColorPrimaries { get; init; }

    public string ColorTransfer { get; init; }

    public int SampleRate { get; init; }

    public int ChannelCount { get; init; }

    public string ChannelLayout { get; init; }

    public string SampleFormat { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> Tags { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();
}

public sealed class MediaChapterInspection
{
    public TimeSpan Start { get; init; }

    public TimeSpan End { get; init; }

    public string Title { get; init; }
}

/// <summary>Container summary produced by <see cref="MediaInspector"/>.</summary>
public sealed class MediaInspection
{
    public string FormatName { get; init; }

    public string FormatLongName { get; init; }

    public TimeSpan Duration { get; init; }

    public TimeSpan StartTime { get; init; }

    public long BitRate { get; init; }

    public IReadOnlyList<MediaStreamInspection> Streams { get; init; } =
        Array.Empty<MediaStreamInspection>();

    public IReadOnlyList<MediaChapterInspection> Chapters { get; init; } =
        Array.Empty<MediaChapterInspection>();

    public IReadOnlyList<KeyValuePair<string, string>> Tags { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();
}

/// <summary>
/// Reads the full stream table of a container the way <c>ffprobe</c> prints it. Nothing is decoded:
/// this is the same one-shot open <see cref="VideoProbe"/> performs, but it keeps every stream
/// rather than only what the indexer needs.
/// </summary>
public static unsafe partial class MediaInspector
{
    private const int IoBufferSize = 64 * 1024;

    /// <summary>
    /// Inspects <paramref name="stream"/>, returning null when libavformat cannot open it.
    /// </summary>
    public static MediaInspection Inspect(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var io = new FfmpegStreamIO(stream, IoBufferSize);
        AVFormatContext* format = avformat_alloc_context();
        if (format == null)
        {
            io.Dispose();
            if (!leaveOpen)
                stream.Dispose();
            throw new FfmpegException("avformat_alloc_context returned null", 0);
        }

        format->pb = io.Context;
        try
        {
            if (avformat_open_input(&format, string.Empty, null, null) < 0)
            {
                // avformat_open_input frees and nulls the context on failure.
                format = null;
                return null;
            }

            if (avformat_find_stream_info(format, null) < 0)
                return null;

            return Describe(format);
        }
        catch (FfmpegException)
        {
            return null;
        }
        finally
        {
            if (format != null)
                avformat_close_input(&format);
            io.Dispose();
            if (!leaveOpen)
                stream.Dispose();
        }
    }

    /// <summary>Inspects the native format context owned by an already-open demuxer.</summary>
    public static MediaInspection Inspect(FfmpegDemuxer demuxer)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        AVFormatContext* format = demuxer.FormatContext;
        ObjectDisposedException.ThrowIf(format == null, demuxer);
        return Describe(format);
    }

    private static MediaInspection Describe(AVFormatContext* format) => new()
    {
        FormatName = format->iformat != null ? ReadString(format->iformat->name) : null,
        FormatLongName = format->iformat != null ? ReadString(format->iformat->long_name) : null,
        Duration = FromAvTimeBase(format->duration),
        StartTime = FromAvTimeBase(format->start_time),
        BitRate = format->bit_rate > 0 ? format->bit_rate : 0,
        Streams = DescribeStreams(format),
        Chapters = DescribeChapters(format),
        Tags = ReadTags(format->metadata),
    };

    private static MediaStreamInspection[] DescribeStreams(AVFormatContext* format)
    {
        var streams = new MediaStreamInspection[format->nb_streams];
        for (int index = 0; index < format->nb_streams; index++)
        {
            AVStream* stream = format->streams[index];
            AVCodecParameters* parameters = stream->codecpar;
            bool isVideo = parameters->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO;
            bool isAudio = parameters->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO;
            AVRational guessed = av_guess_frame_rate(format, stream, null);
            VideoDisplayMatrix.Read(isVideo ? parameters : null, out int rotation, out bool mirrorHorizontally);

            streams[index] = new MediaStreamInspection
            {
                Index = index,
                Kind = av_get_media_type_string(parameters->codec_type) ?? "unknown",
                CodecName = avcodec_get_name(parameters->codec_id),
                CodecLongName = ReadCodecLongName(parameters->codec_id),
                CodecTag = DescribeTag(parameters->codec_tag),
                Profile = avcodec_profile_name(parameters->codec_id, parameters->profile),
                Level = parameters->level,
                BitRate = parameters->bit_rate > 0 ? parameters->bit_rate : 0,
                Duration = FromTimeBase(stream->duration, stream->time_base),
                FrameCount = stream->nb_frames > 0 ? stream->nb_frames : 0,
                Disposition = DescribeDisposition(stream->disposition),
                Width = isVideo ? parameters->width : 0,
                Height = isVideo ? parameters->height : 0,
                PixelFormat = isVideo ? av_get_pix_fmt_name((AVPixelFormat)parameters->format) : null,
                FrameRate = isVideo && guessed.den > 0 ? Utils.RationalToDouble(guessed) : 0,
                AverageFrameRate = isVideo && stream->avg_frame_rate.den > 0
                    ? Utils.RationalToDouble(stream->avg_frame_rate)
                    : 0,
                SampleAspectRatio = isVideo ? DescribeRatio(parameters->sample_aspect_ratio) : null,
                DisplayAspectRatio = isVideo ? DescribeDisplayAspectRatio(parameters) : null,
                Rotation = rotation,
                MirrorHorizontally = mirrorHorizontally,
                BitDepth = isVideo ? BitDepthOf((AVPixelFormat)parameters->format) : 0,
                ColorRange = isVideo ? NonUnspecified(av_color_range_name(parameters->color_range)) : null,
                ColorSpace = isVideo ? NonUnspecified(av_color_space_name(parameters->color_space)) : null,
                ColorPrimaries = isVideo ? NonUnspecified(av_color_primaries_name(parameters->color_primaries)) : null,
                ColorTransfer = isVideo ? NonUnspecified(av_color_transfer_name(parameters->color_trc)) : null,
                SampleRate = isAudio ? parameters->sample_rate : 0,
                ChannelCount = isAudio ? parameters->ch_layout.nb_channels : 0,
                ChannelLayout = isAudio ? DescribeChannelLayout(&parameters->ch_layout) : null,
                SampleFormat = isAudio ? av_get_sample_fmt_name((AVSampleFormat)parameters->format) : null,
                Tags = ReadTags(stream->metadata),
            };
        }

        return streams;
    }

    private static MediaChapterInspection[] DescribeChapters(AVFormatContext* format)
    {
        var chapters = new MediaChapterInspection[format->nb_chapters];
        for (int index = 0; index < format->nb_chapters; index++)
        {
            AVChapter* chapter = format->chapters[index];
            AVDictionaryEntry* title = av_dict_get(chapter->metadata, "title", null, 0);
            chapters[index] = new MediaChapterInspection
            {
                Start = FromTimeBase(chapter->start, chapter->time_base),
                End = FromTimeBase(chapter->end, chapter->time_base),
                Title = title != null ? Utils.NullTerminatedUTF8StringToString((sbyte*)title->value) : null,
            };
        }

        return chapters;
    }

    private static KeyValuePair<string, string>[] ReadTags(AVDictionary* metadata)
    {
        if (metadata == null)
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }

        var tags = new List<KeyValuePair<string, string>>();
        AVDictionaryEntry* entry = null;
        while ((entry = av_dict_get(metadata, string.Empty, entry, AV_DICT_IGNORE_SUFFIX)) != null)
        {
            var key = Utils.NullTerminatedUTF8StringToString((sbyte*)entry->key);
            var value = Utils.NullTerminatedUTF8StringToString((sbyte*)entry->value);
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                tags.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        return tags.ToArray();
    }

    private static string ReadCodecLongName(AVCodecID codecId)
    {
        AVCodecDescriptor* descriptor = avcodec_descriptor_get(codecId);
        return descriptor != null ? ReadString(descriptor->long_name) : null;
    }

    private static int BitDepthOf(AVPixelFormat pixelFormat)
    {
        AVPixFmtDescriptor* descriptor = av_pix_fmt_desc_get(pixelFormat);
        return descriptor != null && descriptor->nb_components > 0 ? descriptor->comp[0].depth : 0;
    }

    private static string DescribeChannelLayout(AVChannelLayout* layout)
    {
        const int BufferSize = 128;
        byte* buffer = stackalloc byte[BufferSize];
        int written = av_channel_layout_describe(layout, buffer, BufferSize);
        return written > 0 ? Utils.NullTerminatedUTF8StringToString((sbyte*)buffer) : null;
    }

    /// <summary>Renders a container codec tag as the four characters it usually spells.</summary>
    private static string DescribeTag(uint tag)
    {
        if (tag == 0)
        {
            return null;
        }

        Span<char> characters = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            var value = (char)((tag >> (8 * i)) & 0xFF);
            if (value is < ' ' or > '~')
            {
                return null;
            }

            characters[i] = value;
        }

        return new string(characters).TrimEnd();
    }

    private static string DescribeRatio(AVRational ratio) =>
        ratio.num > 0 && ratio.den > 0 && ratio.num != ratio.den ? $"{ratio.num}:{ratio.den}" : null;

    private static string DescribeDisplayAspectRatio(AVCodecParameters* parameters)
    {
        if (parameters->width <= 0 || parameters->height <= 0)
        {
            return null;
        }

        AVRational sample = parameters->sample_aspect_ratio;
        long num = parameters->width * (long)(sample.num > 0 ? sample.num : 1);
        long den = parameters->height * (long)(sample.den > 0 ? sample.den : 1);
        int reducedNum;
        int reducedDen;
        av_reduce(&reducedNum, &reducedDen, num, den, 1024 * 1024);
        return reducedDen > 0 ? $"{reducedNum}:{reducedDen}" : null;
    }

    private static string DescribeDisposition(int disposition)
    {
        if (disposition == 0)
        {
            return null;
        }

        var flags = new List<string>();
        Append(flags, disposition, AV_DISPOSITION_DEFAULT, "default");
        Append(flags, disposition, AV_DISPOSITION_DUB, "dub");
        Append(flags, disposition, AV_DISPOSITION_ORIGINAL, "original");
        Append(flags, disposition, AV_DISPOSITION_COMMENT, "comment");
        Append(flags, disposition, AV_DISPOSITION_LYRICS, "lyrics");
        Append(flags, disposition, AV_DISPOSITION_KARAOKE, "karaoke");
        Append(flags, disposition, AV_DISPOSITION_FORCED, "forced");
        Append(flags, disposition, AV_DISPOSITION_HEARING_IMPAIRED, "hearing impaired");
        Append(flags, disposition, AV_DISPOSITION_VISUAL_IMPAIRED, "visual impaired");
        Append(flags, disposition, AV_DISPOSITION_CLEAN_EFFECTS, "clean effects");
        Append(flags, disposition, AV_DISPOSITION_ATTACHED_PIC, "attached picture");
        Append(flags, disposition, AV_DISPOSITION_CAPTIONS, "captions");
        Append(flags, disposition, AV_DISPOSITION_DESCRIPTIONS, "descriptions");
        Append(flags, disposition, AV_DISPOSITION_METADATA, "metadata");
        Append(flags, disposition, AV_DISPOSITION_DEPENDENT, "dependent");
        Append(flags, disposition, AV_DISPOSITION_STILL_IMAGE, "still image");
        return flags.Count == 0 ? null : string.Join(", ", flags);

        static void Append(List<string> flags, int disposition, int flag, string name)
        {
            if ((disposition & flag) != 0)
            {
                flags.Add(name);
            }
        }
    }

    private static string NonUnspecified(string name) =>
        name is null or "unknown" or "unspecified" ? null : name;

    private static string ReadString(byte* value) =>
        value == null ? null : Utils.NullTerminatedUTF8StringToString((sbyte*)value);

    private static TimeSpan FromAvTimeBase(long value) =>
        value == AV_NOPTS_VALUE || value <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(value / (double)AV_TIME_BASE);

    private static TimeSpan FromTimeBase(long value, AVRational timeBase)
    {
        if (value == AV_NOPTS_VALUE || value <= 0 || timeBase.den <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(value * Utils.RationalToDouble(timeBase));
    }
}
