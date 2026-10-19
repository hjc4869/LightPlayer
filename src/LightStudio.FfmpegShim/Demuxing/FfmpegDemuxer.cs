using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Interop;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.NativeBindings.Types;
using LightStudio.Logging;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Demuxing;

/// <summary>
/// libavformat-backed packet source. Owns the <c>AVFormatContext</c> and is only ever touched by
/// the demux thread (plus <see cref="Dispose"/> after that thread has joined).
/// </summary>
public sealed unsafe partial class FfmpegDemuxer : IPacketSource, IAdaptiveVideoPacketSource
{
    private static long nextId;

    private AVFormatContext* format;
    private FfmpegStreamIO io;
    private FfmpegNestedIO nestedIo;
    private FfmpegBlurayIO blurayIo;
    private Stream source;
    private IDisposable sourceOwner;
    private bool ownsSource;
    private double startTime;
    private double timelineOffset;
    private double firstPacketTimelinePosition = double.NaN;
    private readonly long id;
    private bool disposed;

    private FfmpegDemuxer(AVFormatContext* format, FfmpegStreamIO io, Stream source, bool ownsSource)
    {
        id = Interlocked.Increment(ref nextId);
        this.format = format;
        this.io = io;
        this.source = source;
        this.ownsSource = ownsSource;
    }

    public MediaInfo Info { get; private set; } = null!;

    public int VideoStreamIndex { get; private set; } = -1;

    public int AudioStreamIndex { get; private set; } = -1;

    public IReadOnlyList<VideoRendition> VideoRenditions { get; private set; } = Array.Empty<VideoRendition>();

    public bool CanSeek { get; private set; }

    /// <summary>
    /// Places the first audio or video packet delivered by <see cref="Read"/> at
    /// <paramref name="positionSeconds"/>. Call this before <see cref="Read"/> when a live manifest
    /// has its own stable timeline.
    /// </summary>
    public void AlignFirstPacketTo(double positionSeconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(positionSeconds))
            throw new ArgumentOutOfRangeException(nameof(positionSeconds));

        firstPacketTimelinePosition = positionSeconds;
    }

    /// <summary>
    /// Opens an HLS manifest whose root and child resources are supplied by managed code.
    /// Ownership of <paramref name="root"/> is transferred to the returned demuxer.
    /// </summary>
    public static FfmpegDemuxer OpenHls(
        MediaResource root,
        IMediaResourceResolver resolver,
        int liveStartIndex = -3)
    {
        var options = new Dictionary<string, string>
        {
            ["http_persistent"] = "0",
            ["http_multiple"] = "0",
            ["allowed_extensions"] = "ALL",
            ["extension_picky"] = "0",
            ["live_start_index"] = liveStartIndex.ToString(CultureInfo.InvariantCulture),
        };
        return OpenAdaptive(root, resolver, "hls", "HLS", options);
    }

    /// <summary>
    /// Opens a DASH manifest whose resources are supplied by managed code when supported by FFmpeg.
    /// Ownership of <paramref name="root"/> is transferred to the returned demuxer.
    /// </summary>
    public static FfmpegDemuxer OpenDash(MediaResource root, IMediaResourceResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(resolver);

        var options = new Dictionary<string, string>
        {
            ["allowed_extensions"] = "ALL",
        };
        return OpenAdaptive(root, resolver, "dash", "DASH", options);
    }

    private static FfmpegDemuxer OpenAdaptive(
        MediaResource root,
        IMediaResourceResolver resolver,
        string inputFormatName,
        string displayName,
        IReadOnlyDictionary<string, string> inputOptions = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(resolver);

        AVInputFormat* inputFormat = av_find_input_format(inputFormatName);
        if (inputFormat == null)
        {
            root.Dispose();
            throw new NotSupportedException($"The active FFmpeg build does not include the {displayName} demuxer.");
        }

        var io = new FfmpegStreamIO(root.Stream);
        AVFormatContext* context = avformat_alloc_context();
        if (context == null)
        {
            io.Dispose();
            root.Dispose();
            throw new FfmpegException("avformat_alloc_context returned null", 0);
        }

        var nestedIo = new FfmpegNestedIO(resolver);
        context->pb = io.Context;
        nestedIo.Attach(context);

        AVDictionary* options = null;
        try
        {
            if (inputOptions is not null)
            {
                foreach (KeyValuePair<string, string> option in inputOptions)
                {
                    FfmpegError.ThrowIfError(
                        av_dict_set(&options, option.Key, option.Value, 0),
                        $"av_dict_set({option.Key})");
                }
            }

            int ret = avformat_open_input(&context, root.ResolvedUrl, inputFormat, &options);
            if (ret < 0)
            {
                nestedIo.Dispose();
                io.Dispose();
                root.Dispose();
                AppLog.Error("FFmpeg.Demux", $"Could not open {displayName} manifest '{root.ResolvedUrl}': {FfmpegError.Describe(ret)}.");
                throw new FfmpegException($"Cannot open {displayName} manifest '{root.ResolvedUrl}': {FfmpegError.Describe(ret)}", ret);
            }

            var demuxer = new FfmpegDemuxer(context, io, root.Stream, ownsSource: false)
            {
                nestedIo = nestedIo,
                sourceOwner = root,
            };
            context = null;
            try
            {
                demuxer.Initialize(root.ResolvedUrl);
                return demuxer;
            }
            catch (Exception exception)
            {
                AppLog.Error("FFmpeg.Demux", $"#{demuxer.id} failed to inspect {displayName} manifest '{root.ResolvedUrl}'.", exception);
                demuxer.Dispose();
                throw;
            }
        }
        catch
        {
            if (context != null)
            {
                avformat_close_input(&context);
                nestedIo.Dispose();
                io.Dispose();
                root.Dispose();
            }
            throw;
        }
        finally
        {
            av_dict_free(&options);
        }
    }

    /// <summary>Opens a local file path, reading it through the managed stream IO path.</summary>
    public static FfmpegDemuxer Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return Open(stream, path, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an input that libavformat itself resolves. This is used for sources such as optical
    /// discs whose navigation layer needs a path rather than one flat managed stream.
    /// </summary>
    public static FfmpegDemuxer OpenUrl(
        string url,
        string inputFormatName = null,
        IReadOnlyDictionary<string, string> options = null) =>
        OpenUrl(url, inputFormatName, options, null);

    public static FfmpegDemuxer OpenUrl(
        string url,
        string inputFormatName,
        IReadOnlyDictionary<string, string> options,
        IReadOnlyList<MediaChapter> chapterOverride)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);

        AVInputFormat* inputFormat = null;
        if (!string.IsNullOrEmpty(inputFormatName))
        {
            inputFormat = av_find_input_format(inputFormatName);
            if (inputFormat == null)
                throw new NotSupportedException($"The FFmpeg '{inputFormatName}' input format is not available.");
        }

        AVFormatContext* context = null;
        AVDictionary* nativeOptions = null;
        try
        {
            if (options is not null)
            {
                foreach (var option in options)
                    FfmpegError.ThrowIfError(
                        av_dict_set(&nativeOptions, option.Key, option.Value, 0),
                        $"av_dict_set({option.Key})");
            }

            int ret = avformat_open_input(&context, url, inputFormat, &nativeOptions);
            if (ret < 0)
            {
                AppLog.Error("FFmpeg.Demux", $"Could not open '{url}': {FfmpegError.Describe(ret)}.");
                throw new FfmpegException($"Cannot open '{url}': {FfmpegError.Describe(ret)}", ret);
            }

            var demuxer = new FfmpegDemuxer(context, null, null, ownsSource: false);
            context = null;
            try
            {
                // Bluray IO seek workaround
                if (url.StartsWith("bluray:", StringComparison.OrdinalIgnoreCase))
                    demuxer.blurayIo = new FfmpegBlurayIO(demuxer.format);
                demuxer.Initialize(url);
                if (chapterOverride is not null)
                    demuxer.Info = demuxer.Info with { Chapters = chapterOverride };
                return demuxer;
            }
            catch (Exception exception)
            {
                AppLog.Error("FFmpeg.Demux", $"#{demuxer.id} failed to inspect '{url}'.", exception);
                demuxer.Dispose();
                throw;
            }
        }
        finally
        {
            av_dict_free(&nativeOptions);
            if (context != null)
                avformat_close_input(&context);
        }
    }

    /// <summary>
    /// Opens any readable stream. <paramref name="name"/> is only used for diagnostics and for
    /// <see cref="MediaInfo.Source"/>; libavformat probes the content itself. A non-seekable stream
    /// still plays, but <see cref="CanSeek"/> is false.
    /// </summary>
    public static FfmpegDemuxer Open(Stream stream, string name = null, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        name ??= "stream";

        var io = new FfmpegStreamIO(stream);
        AVFormatContext* context = avformat_alloc_context();
        if (context == null)
        {
            io.Dispose();
            throw new FfmpegException("avformat_alloc_context returned null", 0);
        }

        context->pb = io.Context;

        int ret = avformat_open_input(&context, string.Empty, null, null);
        if (ret < 0)
        {
            // avformat_open_input frees and nulls the context on failure, but never our AVIOContext.
            io.Dispose();
            AppLog.Error("FFmpeg.Demux", $"Could not open '{name}': {FfmpegError.Describe(ret)}.");
            throw new FfmpegException($"Cannot open '{name}': {FfmpegError.Describe(ret)}", ret);
        }

        var demuxer = new FfmpegDemuxer(context, io, stream, !leaveOpen);
        try
        {
            demuxer.Initialize(name);
            return demuxer;
        }
        catch (Exception exception)
        {
            AppLog.Error("FFmpeg.Demux", $"#{demuxer.id} failed to inspect '{name}'.", exception);
            demuxer.ownsSource = false;
            demuxer.Dispose();
            throw;
        }
    }

    private void Initialize(string url)
    {
        FfmpegError.ThrowIfError(avformat_find_stream_info(format, null), "avformat_find_stream_info");

        // Containers may start at a non-zero timestamp; normalizing here means every downstream
        // component can treat 0 as "beginning of the media".
        startTime = format->start_time != AV_NOPTS_VALUE
            ? format->start_time / (double)AV_TIME_BASE
            : 0;

        VideoStreamIndex = av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        AudioStreamIndex = av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, VideoStreamIndex, null, 0);
        if (VideoStreamIndex < 0)
            VideoStreamIndex = -1;
        if (AudioStreamIndex < 0)
            AudioStreamIndex = -1;

        bool hasSubtitle = false;
        for (int i = 0; i < format->nb_streams && !hasSubtitle; i++)
            hasSubtitle = format->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE;

        if (VideoStreamIndex < 0 && AudioStreamIndex < 0 && !hasSubtitle)
            throw new FfmpegException($"'{url}' contains no playable media stream.", 0);

        // Attached cover art is a single-frame "video" stream; treating it as the video track would
        // freeze a still image over the real content.
        if (VideoStreamIndex >= 0 &&
            (format->streams[VideoStreamIndex]->disposition & AV_DISPOSITION_ATTACHED_PIC) != 0)
        {
            VideoStreamIndex = -1;
        }

        var streams = new List<MediaStreamInfo>((int)format->nb_streams);
        MediaStreamInfo video = null;
        MediaStreamInfo audio = null;
        for (int i = 0; i < format->nb_streams; i++)
        {
            var info = Describe(i);
            streams.Add(info);
            if (i == VideoStreamIndex)
                video = info;
            if (i == AudioStreamIndex)
                audio = info;
        }

        TimeSpan duration = format->duration != AV_NOPTS_VALUE && format->duration > 0
            ? TimeSpan.FromSeconds(format->duration / (double)AV_TIME_BASE)
            : TimeSpan.Zero;

        CanSeek = (format->ctx_flags & AVFMTCTX_UNSEEKABLE) == 0 &&
            (format->pb == null || format->pb->seekable != 0);

        Info = new MediaInfo(
            url,
            format->iformat != null ? new string((sbyte*)format->iformat->long_name) : "unknown",
            duration,
            format->bit_rate,
            CanSeek,
            streams,
            ReadMetadata(format->metadata))
        {
            DefaultVideoStream = video,
            DefaultAudioStream = audio,
            Chapters = ReadChapters(),
        };

        if (nestedIo is not null)
        {
            VideoRenditions = ReadVideoRenditions();
            if (VideoStreamIndex >= 0)
                SelectVideoStream(VideoStreamIndex);
            if (AudioStreamIndex >= 0)
                SelectAudioStream(AudioStreamIndex);
        }

        AppLog.Information(
            "FFmpeg.Demux",
            $"#{id} opened '{url}': format={Info.FormatName}; duration={Info.Duration}; bitRate={format->bit_rate}; " +
            $"streams={Info.Streams.Count}; videoStream={VideoStreamIndex}; audioStream={AudioStreamIndex}; " +
            $"seekable={CanSeek}; startSeconds={startTime:F3}.");
    }

    private IReadOnlyList<VideoRendition> ReadVideoRenditions()
    {
        var renditions = new List<VideoRendition>();
        var seen = new HashSet<int>();
        bool isDash = format->iformat != null && string.Equals(
            Utils.NullTerminatedUTF8StringToString((sbyte*)format->iformat->name),
            "dash",
            StringComparison.Ordinal);
        for (int programIndex = 0; programIndex < format->nb_programs; programIndex++)
        {
            AVProgram* program = format->programs[programIndex];
            var programMetadata = ReadMetadata(program->metadata);
            long programBandwidth = ReadVariantBitrate(programMetadata);

            for (int position = 0; position < program->nb_stream_indexes; position++)
            {
                int streamIndex = (int)program->stream_index[position];
                if (streamIndex < 0 || streamIndex >= format->nb_streams || !seen.Add(streamIndex))
                    continue;

                AVStream* stream = format->streams[streamIndex];
                if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
                    continue;

                var metadata = ReadMetadata(stream->metadata);
                long bandwidth = ReadVariantBitrate(metadata);
                if (bandwidth <= 0)
                    bandwidth = programBandwidth;
                metadata.TryGetValue("comment", out string name);
                if (string.IsNullOrWhiteSpace(name))
                    metadata.TryGetValue("title", out name);

                renditions.Add(new VideoRendition(
                    programIndex,
                    streamIndex,
                    isDash ? -1 : FindAssociatedAudioStream(program, position),
                    bandwidth,
                    stream->codecpar->width,
                    stream->codecpar->height,
                    name,
                    streamIndex == VideoStreamIndex ||
                        (stream->disposition & AV_DISPOSITION_DEFAULT) != 0));
            }
        }

        if (renditions.Count == 0 && VideoStreamIndex >= 0)
        {
            AVCodecParameters* parameters = format->streams[VideoStreamIndex]->codecpar;
            renditions.Add(new VideoRendition(
                -1,
                VideoStreamIndex,
                AudioStreamIndex,
                format->bit_rate,
                parameters->width,
                parameters->height,
                string.Empty,
                true));
        }

        return renditions;
    }

    private static long ReadVariantBitrate(IReadOnlyDictionary<string, string> metadata) =>
        metadata.TryGetValue("variant_bitrate", out string bitrateText) &&
        long.TryParse(bitrateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bitrate)
            ? bitrate
            : 0;

    private int FindAssociatedAudioStream(AVProgram* program, int videoPosition)
    {
        for (int position = videoPosition + 1; position < program->nb_stream_indexes; position++)
        {
            int streamIndex = (int)program->stream_index[position];
            if (streamIndex < 0 || streamIndex >= format->nb_streams)
                continue;

            AVMediaType type = format->streams[streamIndex]->codecpar->codec_type;
            if (type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                break;
            if (type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                return streamIndex;
        }

        return -1;
    }

    private List<MediaChapter> ReadChapters()
    {
        var chapters = new List<MediaChapter>((int)format->nb_chapters);
        for (int i = 0; i < format->nb_chapters; i++)
        {
            AVChapter* chapter = format->chapters[i];
            double unit = Utils.RationalToDouble(chapter->time_base);

            // Chapter marks are container timestamps, so the same start offset removed from packet
            // timestamps has to come off here for the two to line up.
            double start = chapter->start * unit - startTime;
            double end = chapter->end * unit - startTime;

            ReadMetadata(chapter->metadata).TryGetValue("title", out string title);
            chapters.Add(new MediaChapter(
                i,
                TimeSpan.FromSeconds(Math.Max(0, start)),
                TimeSpan.FromSeconds(Math.Max(0, end)),
                title));
        }

        return chapters;
    }

    private MediaStreamInfo Describe(int index)
    {
        AVStream* stream = format->streams[index];
        AVCodecParameters* par = stream->codecpar;
        var metadata = ReadMetadata(stream->metadata);
        metadata.TryGetValue("language", out string language);
        metadata.TryGetValue("title", out string title);

        MediaStreamKind kind = par->codec_type switch
        {
            AVMediaType.AVMEDIA_TYPE_VIDEO => MediaStreamKind.Video,
            AVMediaType.AVMEDIA_TYPE_AUDIO => MediaStreamKind.Audio,
            AVMediaType.AVMEDIA_TYPE_SUBTITLE => MediaStreamKind.Subtitle,
            _ => MediaStreamKind.Other,
        };

        AVRational sampleAspectRatio = av_guess_sample_aspect_ratio(format, stream, null);
        double par2 = sampleAspectRatio.num > 0 && sampleAspectRatio.den > 0
            ? Utils.RationalToDouble(sampleAspectRatio)
            : 1.0;

        AVRational guessed = av_guess_frame_rate(format, stream, null);
        double frameRate = guessed.den > 0 ? Utils.RationalToDouble(guessed) : 0;

        string pixelFormat = kind == MediaStreamKind.Video
            ? av_get_pix_fmt_name((AVPixelFormat)par->format)
            : null;
        string sampleFormat = kind == MediaStreamKind.Audio
            ? av_get_sample_fmt_name((AVSampleFormat)par->format)
            : null;

        TimeSpan streamDuration = stream->duration != AV_NOPTS_VALUE && stream->duration > 0
            ? TimeSpan.FromSeconds(stream->duration * Utils.RationalToDouble(stream->time_base))
            : TimeSpan.Zero;

        VideoDisplayMatrix.Read(par, out int rotation, out bool mirrorHorizontally);
        string codecName = avcodec_get_name(par->codec_id);

        return new MediaStreamInfo(
            index,
            kind,
            codecName,
            language,
            title,
            (stream->disposition & AV_DISPOSITION_DEFAULT) != 0)
        {
            Duration = streamDuration,
            Width = par->width,
            Height = par->height,
            PixelAspectRatio = par2,
            Rotation = rotation,
            MirrorHorizontally = mirrorHorizontally,
            FrameRate = frameRate,
            PixelFormat = pixelFormat,
            SampleRate = par->sample_rate,
            ChannelCount = par->ch_layout.nb_channels,
            SampleFormat = sampleFormat,
            SubtitleKind = kind == MediaStreamKind.Subtitle ? FfmpegSubtitleReader.Classify(codecName) : null,
        };
    }

    private static Dictionary<string, string> ReadMetadata(AVDictionary* dictionary)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AVDictionaryEntry* entry = null;
        while ((entry = av_dict_get(dictionary, string.Empty, entry, AV_DICT_IGNORE_SUFFIX)) != null)
        {
            string key = Utils.NullTerminatedUTF8StringToString((sbyte*)entry->key);
            result[key] = Utils.NullTerminatedUTF8StringToString((sbyte*)entry->value);
        }

        return result;
    }

    /// <summary>
    /// Chooses which audio stream <see cref="Read"/> delivers packets for; -1 turns audio off.
    /// Only the demux thread may call this.
    /// </summary>
    public void SelectAudioStream(int streamIndex)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (streamIndex >= 0)
        {
            if (streamIndex >= format->nb_streams)
                throw new ArgumentOutOfRangeException(nameof(streamIndex));
            if (format->streams[streamIndex]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                throw new ArgumentException($"Stream {streamIndex} is not an audio stream.", nameof(streamIndex));
        }

        // Dropping the unselected tracks inside libavformat saves reading them out only to throw
        // them away, which matters for the multi-track sources this exists for.
        for (int i = 0; i < format->nb_streams; i++)
        {
            AVStream* stream = format->streams[i];
            if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                continue;
            stream->discard = i == streamIndex ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
        }

        AudioStreamIndex = streamIndex < 0 ? -1 : streamIndex;
    }

    public void SelectVideoStream(int streamIndex)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (streamIndex < 0 || streamIndex >= format->nb_streams)
            throw new ArgumentOutOfRangeException(nameof(streamIndex));
        if (format->streams[streamIndex]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
            throw new ArgumentException($"Stream {streamIndex} is not a video stream.", nameof(streamIndex));

        for (int i = 0; i < format->nb_streams; i++)
        {
            AVStream* stream = format->streams[i];
            if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                stream->discard = i == streamIndex ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
        }

        foreach (VideoRendition rendition in VideoRenditions)
        {
            if (rendition.StreamIndex == streamIndex && rendition.AssociatedAudioStreamIndex >= 0)
            {
                SelectAudioStream(rendition.AssociatedAudioStreamIndex);
                break;
            }
        }

        VideoStreamIndex = streamIndex;
        if (Info is not null)
        {
            MediaStreamInfo selected = null;
            foreach (MediaStreamInfo stream in Info.Streams)
            {
                if (stream.Index == streamIndex)
                {
                    selected = stream;
                    break;
                }
            }
            Info = Info with { DefaultVideoStream = selected };
        }
    }

    /// <summary>
    /// Narrows the demuxer down to a single stream. Everything else is discarded inside
    /// libavformat, which for a container with an index turns a full read into a walk over the
    /// blocks of that one stream. Only the demux thread may call this.
    /// </summary>
    public void SelectOnlyStream(int streamIndex)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (streamIndex < 0 || streamIndex >= format->nb_streams)
            throw new ArgumentOutOfRangeException(nameof(streamIndex));

        for (int i = 0; i < format->nb_streams; i++)
            format->streams[i]->discard = i == streamIndex ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;

        if (format->streams[streamIndex]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            AudioStreamIndex = streamIndex;
        else
            AudioStreamIndex = -1;

        if (format->streams[streamIndex]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
            VideoStreamIndex = -1;
    }

    /// <summary>
    /// Narrows the demuxer down to <paramref name="streamIndices"/>. Everything else is discarded
    /// inside libavformat, which is what keeps the unwanted sound tracks of a multi-track container
    /// off the wire and off the disk. Only the demux thread may call this.
    /// </summary>
    public void SelectStreams(HashSet<int> streamIndices)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(streamIndices);

        for (int i = 0; i < format->nb_streams; i++)
        {
            format->streams[i]->discard = streamIndices.Contains(i)
                ? AVDiscard.AVDISCARD_DEFAULT
                : AVDiscard.AVDISCARD_ALL;
        }

            AppLog.Debug("FFmpeg.Demux", $"#{id} selected streams=[{string.Join(",", streamIndices)}].");
    }

    /// <summary>Stream handle for the decoders, which need the codec parameters and time base.</summary>
    internal AVStream* GetStream(int index)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (index < 0 || index >= format->nb_streams)
            throw new ArgumentOutOfRangeException(nameof(index));
        return format->streams[index];
    }

    public CodecParameters GetCodecParameters(int streamIndex)
    {
        AVStream* stream = GetStream(streamIndex);
        AVRational sampleAspectRatio = av_guess_sample_aspect_ratio(format, stream, null);
        return CodecParameters.FromNative(
            stream->codecpar,
            stream->time_base,
            av_guess_frame_rate(format, stream, null)) with
        {
            SampleAspectRatioNum = sampleAspectRatio.num,
            SampleAspectRatioDen = sampleAspectRatio.den,
        };
    }

    /// <summary>
    /// Managed view of the video stream's codec setup, for decoders that cannot see the native
    /// codec parameters. Null when the container carries no picture.
    /// </summary>
    public VideoCodecDescription GetVideoCodecDescription()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (VideoStreamIndex < 0)
            return null;

        return VideoCodecDescription.FromCodecParameters(GetCodecParameters(VideoStreamIndex), VideoStreamIndex);
    }

    internal AVFormatContext* FormatContext => format;

    public PacketReadStatus Read(MediaPacket packet)
    {
        if (disposed)
            return PacketReadStatus.Fatal;

        int ret = av_read_frame(format, packet.Handle);
        if (ret >= 0)
        {
            AlignTimeline(packet.Handle);
            packet.CaptureFromRead();
            return PacketReadStatus.Ok;
        }

        if (FfmpegError.IsEndOfFile(ret) || (format->pb != null && format->pb->eof_reached != 0))
            return PacketReadStatus.EndOfFile;

        // A damaged packet in the middle of a file is common; the caller retries and lets the
        // decoder resynchronize at the next keyframe instead of ending playback.
        if (FfmpegError.IsTryAgain(ret))
        {
            return PacketReadStatus.Recoverable;
        }

        AppLog.Error("FFmpeg.Demux", $"#{id} packet read failed: {FfmpegError.Describe(ret)}.");
        return PacketReadStatus.Fatal;
    }

    private void AlignTimeline(AVPacket* packet)
    {
        if (!double.IsFinite(firstPacketTimelinePosition))
            return;

        int streamIndex = packet->stream_index;
        if (streamIndex != VideoStreamIndex && streamIndex != AudioStreamIndex)
            return;

        long timestamp = packet->pts != AV_NOPTS_VALUE ? packet->pts : packet->dts;
        if (timestamp == AV_NOPTS_VALUE)
            return;

        timelineOffset = firstPacketTimelinePosition -
            (timestamp * Utils.RationalToDouble(format->streams[streamIndex]->time_base) - startTime);
        firstPacketTimelinePosition = double.NaN;
    }

    public bool Seek(double positionSeconds)
    {
        if (disposed)
            return false;

        long target = (long)((positionSeconds - timelineOffset + startTime) * AV_TIME_BASE);

        // Seeking with stream_index -1 keeps the target in AV_TIME_BASE units and lets libavformat
        // pick the reference stream, which is what keeps audio and video aligned after the seek.
        int ret = avformat_seek_file(format, -1, long.MinValue, target, long.MaxValue, AVSEEK_FLAG_BACKWARD);
        if (ret < 0)
        {
            // Some containers only support seeking on a concrete stream.
            int reference = VideoStreamIndex >= 0 ? VideoStreamIndex : AudioStreamIndex;
            if (reference < 0)
                return false;

            AVStream* stream = format->streams[reference];
            long streamTarget = av_rescale_q(
                target,
                new AVRational { num = 1, den = AV_TIME_BASE },
                stream->time_base);
            ret = av_seek_frame(format, reference, streamTarget, AVSEEK_FLAG_BACKWARD);
        }

        if (ret < 0)
        {
            AppLog.Warning(
                "FFmpeg.Demux",
                $"#{id} seek to {positionSeconds:F3} seconds failed: {FfmpegError.Describe(ret)}.");
        }

        return ret >= 0;
    }

    public double ToSeconds(int streamIndex, long timestamp)
    {
        if (disposed || timestamp == AV_NOPTS_VALUE || streamIndex < 0 || streamIndex >= format->nb_streams)
            return MediaTime.NoTimestamp;

        return timestamp * Utils.RationalToDouble(format->streams[streamIndex]->time_base) - startTime + timelineOffset;
    }

    /// <summary>Normalized start offset of the container, already removed from reported timestamps.</summary>
    public double StartTime => startTime;

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        blurayIo?.Dispose();
        blurayIo = null;

        if (format != null)
        {
            AVFormatContext* context = format;
            avformat_close_input(&context);
            format = null;
        }

        nestedIo?.Dispose();
        nestedIo = null;

        // Must outlive the format context: avformat_close_input never frees caller supplied IO.
        io?.Dispose();
        io = null;

        sourceOwner?.Dispose();
        sourceOwner = null;
        if (ownsSource)
            source?.Dispose();
        source = null;
    }
}
