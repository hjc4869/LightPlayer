using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Interop;
using LightStudio.FfmpegShim.Media;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Subtitles;

/// <summary>
/// Lifts a text subtitle stream out of a container and hands it back as one ASS script.
/// </summary>
/// <remarks>
/// <para>
/// libavcodec normalizes every text format to ASS, so SubRip, WebVTT and MOV text all come out
/// as events against a generated style and the caller only ever deals with one syntax. The header
/// is the decoder's, which for an embedded ASS track is the script's own <c>[Script Info]</c> and
/// <c>[V4+ Styles]</c> sections verbatim.
/// </para>
/// <para>
/// The whole stream is read in one pass rather than followed along with playback: subtitles are a
/// few hundred kilobytes even for a feature film, and having the complete script up front is what
/// lets a seek show the right line immediately. Everything but the wanted stream is discarded
/// inside libavformat, so the pass costs a walk over that stream's blocks rather than over the
/// file.
/// </para>
/// </remarks>
public static unsafe partial class FfmpegSubtitleReader
{
    private const string GeneratedHeader =
        "[Script Info]\nScriptType: v4.00+\nPlayResX: 384\nPlayResY: 288\n\n" +
        "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, " +
        "Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, " +
        "MarginL, MarginR, MarginV, Encoding\n" +
        "Style: Default,Arial,16,&Hffffff,&Hffffff,&H0,&H0,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,0\n";

    private const string EventsSection =
        "\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

    /// <summary>
    /// Whether <paramref name="codecName"/> is a subtitle format made of text, and so something
    /// <see cref="Read(string, int, CancellationToken)"/> can turn into a script. Bitmap formats
    /// such as PGS or VobSub are not.
    /// </summary>
    public static bool IsTextBased(string codecName) => (Classify(codecName) & SubtitleCodecKind.Text) != 0;

    /// <inheritdoc cref="IsTextBased(string)"/>
    public static bool IsTextBased(MediaStreamInfo stream) =>
        stream is { Kind: MediaStreamKind.Subtitle } && ((stream.SubtitleKind ?? Classify(stream.CodecName)) & SubtitleCodecKind.Text) != 0;

    /// <summary>Whether <paramref name="codecName"/> is a decoded bitmap subtitle format.</summary>
    public static bool IsBitmapBased(string codecName) => (Classify(codecName) & SubtitleCodecKind.Bitmap) != 0;

    /// <inheritdoc cref="IsBitmapBased(string)"/>
    public static bool IsBitmapBased(MediaStreamInfo stream) =>
        stream is { Kind: MediaStreamKind.Subtitle } && ((stream.SubtitleKind ?? Classify(stream.CodecName)) & SubtitleCodecKind.Bitmap) != 0;

    /// <summary>Whether the player can render <paramref name="stream"/> as text or bitmaps.</summary>
    public static bool IsSupported(MediaStreamInfo stream) => IsTextBased(stream) || IsBitmapBased(stream);

    /// <summary>How libavcodec decodes the subtitle codec <paramref name="codecName"/>.</summary>
    public static SubtitleCodecKind Classify(string codecName)
    {
        if (string.IsNullOrEmpty(codecName))
            return SubtitleCodecKind.None;

        AVCodecDescriptor* descriptor = avcodec_descriptor_get_by_name(codecName);
        if (descriptor == null)
            return SubtitleCodecKind.None;

        SubtitleCodecKind kind = SubtitleCodecKind.None;
        if ((descriptor->props & AV_CODEC_PROP_TEXT_SUB) != 0)
            kind |= SubtitleCodecKind.Text;
        if ((descriptor->props & AV_CODEC_PROP_BITMAP_SUB) != 0)
            kind |= SubtitleCodecKind.Bitmap;
        return kind;
    }

    /// <summary>Records how each subtitle stream of <paramref name="info"/> is decoded, for readers without libavcodec.</summary>
    public static MediaInfo Classify(MediaInfo info)
    {
        var streams = new MediaStreamInfo[info.Streams.Count];
        for (int index = 0; index < streams.Length; index++)
        {
            MediaStreamInfo stream = info.Streams[index];
            streams[index] = stream.Kind == MediaStreamKind.Subtitle && stream.SubtitleKind is null
                ? stream with { SubtitleKind = Classify(stream.CodecName) }
                : stream;
        }

        return info with { Streams = streams };
    }

    /// <summary>Decodes a bitmap subtitle stream from the file at <paramref name="path"/>.</summary>
    public static BitmapSubtitleDocument ReadBitmaps(
        string path,
        int streamIndex,
        CancellationToken token = default,
        bool preserveContainerTimestamps = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using FfmpegDemuxer demuxer = FfmpegDemuxer.Open(path);
        return ReadBitmaps(demuxer, streamIndex, token, preserveContainerTimestamps);
    }

    /// <summary>
    /// Decodes stream <paramref name="streamIndex"/> into indexed bitmap compositions. The demuxer
    /// must be newly opened; it is narrowed to that stream and left at its end. Keeping the initial
    /// probe buffer intact matters for raw SUP files whose first display set sits at their start time.
    /// </summary>
    public static BitmapSubtitleDocument ReadBitmaps(
        FfmpegDemuxer demuxer,
        int streamIndex,
        CancellationToken token = default,
        bool preserveContainerTimestamps = false)
    {
        ArgumentNullException.ThrowIfNull(demuxer);

        AVStream* stream = demuxer.GetStream(streamIndex);
        AVCodecParameters* parameters = stream->codecpar;
        if (parameters->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE)
            throw new ArgumentException($"Stream {streamIndex} is not a subtitle stream.", nameof(streamIndex));

        AVCodecDescriptor* descriptor = avcodec_descriptor_get(parameters->codec_id);
        if (descriptor == null || (descriptor->props & AV_CODEC_PROP_BITMAP_SUB) == 0)
            throw new ArgumentException($"Stream {streamIndex} is not a bitmap subtitle stream.", nameof(streamIndex));

        AVCodec* decoder = avcodec_find_decoder(parameters->codec_id);
        if (decoder == null)
            throw new FfmpegException($"No decoder for {avcodec_get_name(parameters->codec_id)}.", 0);

        AVCodecContext* codec = avcodec_alloc_context3(decoder);
        if (codec == null)
            throw new FfmpegException("avcodec_alloc_context3 returned null", 0);

        var pool = new MediaPacketPool(1);
        MediaPacket packet = pool.Rent();
        var document = new BitmapSubtitleBuilder(demuxer.Info.Duration.TotalSeconds);
        int canvasWidth = parameters->width;
        int canvasHeight = parameters->height;
        try
        {
            FfmpegError.ThrowIfError(
                avcodec_parameters_to_context(codec, parameters), "avcodec_parameters_to_context");
            codec->pkt_timebase = stream->time_base;
            FfmpegError.ThrowIfError(avcodec_open2(codec, decoder, null), "avcodec_open2");

            demuxer.SelectOnlyStream(streamIndex);

            while (true)
            {
                token.ThrowIfCancellationRequested();

                packet.Reset();
                PacketReadStatus status = demuxer.Read(packet);
                if (status == PacketReadStatus.EndOfFile)
                    break;
                if (status == PacketReadStatus.Fatal)
                    throw new FfmpegException("The media source failed while reading subtitles.", 0);
                if (status != PacketReadStatus.Ok || packet.StreamIndex != streamIndex)
                    continue;

                double containerStartTime = preserveContainerTimestamps ? 0 : demuxer.StartTime;
                DecodeBitmaps(
                    document,
                    codec,
                    packet,
                    demuxer.ToSeconds(streamIndex, packet.Handle->pts) + demuxer.StartTime - containerStartTime,
                    containerStartTime);
                if (codec->width > 0 && codec->height > 0)
                {
                    canvasWidth = codec->width;
                    canvasHeight = codec->height;
                }
            }

            if (canvasWidth <= 0 || canvasHeight <= 0)
            {
                MediaStreamInfo video = demuxer.Info.DefaultVideoStream;
                canvasWidth = video?.Width ?? 0;
                canvasHeight = video?.Height ?? 0;
            }

            return document.Build(canvasWidth, canvasHeight);
        }
        finally
        {
            packet.Recycle();
            pool.Clear();
            avcodec_free_context(&codec);
        }
    }

    /// <summary>
    /// Decodes bitmap subtitle packets that arrived without their container, such as a server's packet
    /// stream of one subtitle track. Times are placed on the playback timeline.
    /// </summary>
    public static BitmapSubtitleDocument ReadBitmaps(BitmapSubtitlePackets track, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        AVCodecParameters* parameters = track.Parameters.ToNative();
        AVCodecContext* codec = null;
        var pool = new MediaPacketPool(1);
        MediaPacket packet = pool.Rent();
        try
        {
            if (parameters->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                throw new ArgumentException("The packets are not a subtitle stream.", nameof(track));

            AVCodecDescriptor* descriptor = avcodec_descriptor_get(parameters->codec_id);
            if (descriptor == null || (descriptor->props & AV_CODEC_PROP_BITMAP_SUB) == 0)
                throw new ArgumentException("The packets are not a bitmap subtitle stream.", nameof(track));

            AVCodec* decoder = avcodec_find_decoder(parameters->codec_id);
            if (decoder == null)
                throw new FfmpegException($"No decoder for {avcodec_get_name(parameters->codec_id)}.", 0);

            codec = avcodec_alloc_context3(decoder);
            if (codec == null)
                throw new FfmpegException("avcodec_alloc_context3 returned null", 0);

            FfmpegError.ThrowIfError(
                avcodec_parameters_to_context(codec, parameters), "avcodec_parameters_to_context");
            codec->pkt_timebase = track.Parameters.NativeTimeBase();
            FfmpegError.ThrowIfError(avcodec_open2(codec, decoder, null), "avcodec_open2");

            var document = new BitmapSubtitleBuilder(track.DurationSeconds);
            int canvasWidth = parameters->width;
            int canvasHeight = parameters->height;
            double timeBase = track.Parameters.TimeBase;
            foreach (SubtitlePacket item in track.Packets)
            {
                token.ThrowIfCancellationRequested();

                packet.Reset();
                packet.FillFromWire(0, item.Pts, item.Dts, item.Duration, isKeyFrame: true, item.Data);
                DecodeBitmaps(
                    document,
                    codec,
                    packet,
                    item.Pts == AV_NOPTS_VALUE ? MediaTime.NoTimestamp : item.Pts * timeBase - track.StartTimeSeconds,
                    track.StartTimeSeconds);
                if (codec->width > 0 && codec->height > 0)
                {
                    canvasWidth = codec->width;
                    canvasHeight = codec->height;
                }
            }

            if (canvasWidth <= 0 || canvasHeight <= 0)
            {
                canvasWidth = track.CanvasWidth;
                canvasHeight = track.CanvasHeight;
            }

            return document.Build(canvasWidth, canvasHeight);
        }
        finally
        {
            packet.Recycle();
            pool.Clear();
            avcodec_free_context(&codec);
            CodecParameters.FreeNative(&parameters);
        }
    }

    /// <summary>Reads stream <paramref name="streamIndex"/> of the file at <paramref name="path"/>.</summary>
    public static string Read(string path, int streamIndex, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using FfmpegDemuxer demuxer = FfmpegDemuxer.Open(path);
        return Read(demuxer, streamIndex, token);
    }

    /// <summary>
    /// Reads stream <paramref name="streamIndex"/> of <paramref name="demuxer"/>. The demuxer is
    /// narrowed to that stream and left at the end of it, so it must not be one that is also
    /// feeding playback.
    /// </summary>
    public static string Read(FfmpegDemuxer demuxer, int streamIndex, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(demuxer);

        AVStream* stream = demuxer.GetStream(streamIndex);
        AVCodecParameters* parameters = stream->codecpar;
        if (parameters->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE)
            throw new ArgumentException($"Stream {streamIndex} is not a subtitle stream.", nameof(streamIndex));

        AVCodec* decoder = avcodec_find_decoder(parameters->codec_id);
        if (decoder == null)
            throw new FfmpegException($"No decoder for {avcodec_get_name(parameters->codec_id)}.", 0);

        AVCodecContext* codec = avcodec_alloc_context3(decoder);
        if (codec == null)
            throw new FfmpegException("avcodec_alloc_context3 returned null", 0);

        var pool = new MediaPacketPool(1);
        MediaPacket packet = pool.Rent();
        var script = new StringBuilder();
        try
        {
            FfmpegError.ThrowIfError(
                avcodec_parameters_to_context(codec, parameters), "avcodec_parameters_to_context");
            codec->pkt_timebase = stream->time_base;
            FfmpegError.ThrowIfError(avcodec_open2(codec, decoder, null), "avcodec_open2");

            AppendHeader(script, codec);

            demuxer.SelectOnlyStream(streamIndex);
            demuxer.Seek(0);

            while (true)
            {
                token.ThrowIfCancellationRequested();

                packet.Reset();
                PacketReadStatus status = demuxer.Read(packet);
                if (status == PacketReadStatus.EndOfFile)
                    break;
                if (status == PacketReadStatus.Fatal)
                    throw new FfmpegException("The media source failed while reading subtitles.", 0);
                if (status == PacketReadStatus.Recoverable || packet.StreamIndex != streamIndex)
                    continue;

                Decode(script, codec, packet, demuxer, streamIndex);
            }

            return script.ToString();
        }
        finally
        {
            packet.Recycle();
            pool.Clear();
            avcodec_free_context(&codec);
        }
    }

    private static void AppendHeader(StringBuilder script, AVCodecContext* codec)
    {
        string header = codec->subtitle_header != null && codec->subtitle_header_size > 0
            ? Encoding.UTF8.GetString(codec->subtitle_header, codec->subtitle_header_size)
            : string.Empty;

        if (header.Length == 0)
            header = GeneratedHeader;

        script.Append(header.ReplaceLineEndings("\n"));
        if (script[^1] != '\n')
            script.Append('\n');

        // An embedded ASS track carries its own [Events] format line; a format libavcodec converted
        // has no events section at all, because the decoder never saw one.
        if (!header.Contains("[Events]", StringComparison.OrdinalIgnoreCase))
            script.Append(EventsSection);
    }

    private static void Decode(
        StringBuilder script, AVCodecContext* codec, MediaPacket packet, FfmpegDemuxer demuxer, int streamIndex)
    {
        AVSubtitle subtitle;
        int decoded;
        if (avcodec_decode_subtitle2(codec, &subtitle, &decoded, packet.Handle) < 0 || decoded == 0)
            return;

        try
        {
            // avcodec reports the presentation time in AV_TIME_BASE units against the container's
            // own origin, which the demuxer already takes off every other timestamp it hands out.
            double origin = subtitle.pts != AV_NOPTS_VALUE
                ? subtitle.pts / (double)AV_TIME_BASE - demuxer.StartTime
                : demuxer.ToSeconds(streamIndex, packet.Handle->pts);
            if (!MediaTime.IsValid(origin))
                return;

            double start = origin + subtitle.start_display_time / 1000.0;
            double end = origin + subtitle.end_display_time / 1000.0;

            for (uint i = 0; i < subtitle.num_rects; i++)
            {
                AVSubtitleRect* rect = subtitle.rects[i];
                if (rect == null || rect->type != AVSubtitleType.SUBTITLE_ASS || rect->ass == null)
                    continue;

                AppendDialogue(script, Utils.NullTerminatedUTF8StringToString((sbyte*)rect->ass), start, end);
            }
        }
        finally
        {
            avsubtitle_free(&subtitle);
        }
    }

    /// <param name="packetOrigin">The packet's own presentation time, used when the decoder reports none.</param>
    private static void DecodeBitmaps(
        BitmapSubtitleBuilder document,
        AVCodecContext* codec,
        MediaPacket packet,
        double packetOrigin,
        double containerStartTime)
    {
        AVSubtitle subtitle;
        int decoded;
        if (avcodec_decode_subtitle2(codec, &subtitle, &decoded, packet.Handle) < 0 || decoded == 0)
            return;

        try
        {
            double origin = subtitle.pts != AV_NOPTS_VALUE
                ? subtitle.pts / (double)AV_TIME_BASE - containerStartTime
                : packetOrigin;
            if (!MediaTime.IsValid(origin))
                return;

            double start = origin + subtitle.start_display_time / 1000.0;
            double end = subtitle.end_display_time != uint.MaxValue &&
                         subtitle.end_display_time > subtitle.start_display_time
                ? origin + subtitle.end_display_time / 1000.0
                : MediaTime.NoTimestamp;

            var rectangles = new List<BitmapSubtitleRectangle>((int)subtitle.num_rects);
            for (uint i = 0; i < subtitle.num_rects; i++)
            {
                AVSubtitleRect* rectangle = subtitle.rects[i];
                if (rectangle == null || rectangle->type != AVSubtitleType.SUBTITLE_BITMAP)
                    continue;

                rectangles.Add(CopyBitmapRectangle(rectangle));
            }

            document.Add(new BitmapSubtitleState(start, end, rectangles.ToArray()));
        }
        finally
        {
            avsubtitle_free(&subtitle);
        }
    }

    private static BitmapSubtitleRectangle CopyBitmapRectangle(AVSubtitleRect* rectangle)
    {
        if (rectangle->w <= 0 || rectangle->h <= 0 || rectangle->data[0] == null || rectangle->data[1] == null)
            throw new InvalidDataException("FFmpeg returned an incomplete bitmap subtitle rectangle.");

        long pixelCount = checked((long)rectangle->w * rectangle->h);
        if (pixelCount > int.MaxValue || Math.Abs((long)rectangle->linesize[0]) < rectangle->w)
            throw new InvalidDataException("FFmpeg returned invalid bitmap subtitle dimensions.");

        var pixels = new byte[(int)pixelCount];
        for (int row = 0; row < rectangle->h; row++)
        {
            new ReadOnlySpan<byte>(rectangle->data[0] + row * rectangle->linesize[0], rectangle->w)
                .CopyTo(pixels.AsSpan(row * rectangle->w, rectangle->w));
        }

        int colorCount = Math.Clamp(rectangle->nb_colors, 1, 256);
        var palette = new uint[colorCount];
        new ReadOnlySpan<uint>((uint*)rectangle->data[1], colorCount).CopyTo(palette);

        return new BitmapSubtitleRectangle(
            rectangle->x,
            rectangle->y,
            rectangle->w,
            rectangle->h,
            pixels,
            palette);
    }

    private sealed class BitmapSubtitleBuilder(double durationSeconds)
    {
        private readonly List<BitmapSubtitleCue> cues = new();
        private BitmapSubtitleState pending;

        public void Add(BitmapSubtitleState state)
        {
            if (pending is not null)
                Append(pending, state.StartSeconds);
            pending = state;
        }

        public BitmapSubtitleDocument Build(int canvasWidth, int canvasHeight)
        {
            if (pending is not null)
            {
                Append(pending, MediaTime.NoTimestamp);
                pending = null;
            }

            return new BitmapSubtitleDocument(canvasWidth, canvasHeight, cues.ToArray());
        }

        private void Append(BitmapSubtitleState state, double nextStart)
        {
            double end = MediaTime.IsValid(state.EndSeconds) && state.EndSeconds > state.StartSeconds
                ? state.EndSeconds
                : nextStart;

            if (MediaTime.IsValid(nextStart) && nextStart > state.StartSeconds)
                end = MediaTime.IsValid(end) ? Math.Min(end, nextStart) : nextStart;
            if (durationSeconds > state.StartSeconds)
                end = MediaTime.IsValid(end) ? Math.Min(end, durationSeconds) : durationSeconds;
            if (!MediaTime.IsValid(end))
                end = double.MaxValue;

            if (state.Rectangles.Length == 0 || end <= state.StartSeconds)
                return;

            if (cues.Count > 0 &&
                Math.Abs(cues[^1].EndSeconds - state.StartSeconds) < 0.001 &&
                SameComposition(cues[^1].Rectangles, state.Rectangles))
            {
                cues[^1] = cues[^1] with { EndSeconds = end };
            }
            else
            {
                cues.Add(new BitmapSubtitleCue(state.StartSeconds, end, state.Rectangles));
            }
        }
    }

    private static bool SameComposition(
        BitmapSubtitleRectangle[] left,
        BitmapSubtitleRectangle[] right)
    {
        if (left.Length != right.Length)
            return false;

        for (int index = 0; index < left.Length; index++)
        {
            BitmapSubtitleRectangle first = left[index];
            BitmapSubtitleRectangle second = right[index];
            if (first.X != second.X || first.Y != second.Y ||
                first.Width != second.Width || first.Height != second.Height ||
                !first.Pixels.AsSpan().SequenceEqual(second.Pixels) ||
                !first.Palette.AsSpan().SequenceEqual(second.Palette))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record BitmapSubtitleState(
        double StartSeconds,
        double EndSeconds,
        BitmapSubtitleRectangle[] Rectangles);

    /// <summary>
    /// Turns one decoded event into a <c>Dialogue:</c> line. libavcodec hands out the Matroska form
    /// of the event - the fields of a dialogue line with a read order where the timestamps belong -
    /// so the first field is dropped and the times put back in its place.
    /// </summary>
    private static void AppendDialogue(StringBuilder script, string dialogue, double start, double end)
    {
        dialogue = dialogue.TrimEnd('\r', '\n');
        if (dialogue.Length == 0)
            return;

        string layer = "0";
        string body = dialogue;

        int readOrder = dialogue.IndexOf(',');
        int afterLayer = readOrder < 0 ? -1 : dialogue.IndexOf(',', readOrder + 1);
        if (afterLayer > 0)
        {
            ReadOnlySpan<char> field = dialogue.AsSpan(readOrder + 1, afterLayer - readOrder - 1).Trim();
            if (int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                layer = parsed.ToString(CultureInfo.InvariantCulture);
                body = dialogue[(afterLayer + 1)..];
            }
        }

        script.Append("Dialogue: ").Append(layer).Append(',')
            .Append(FormatTime(start)).Append(',')
            .Append(FormatTime(end)).Append(',')
            .Append(body).Append('\n');
    }

    private static string FormatTime(double seconds)
    {
        long centiseconds = seconds > 0 ? (long)Math.Round(seconds * 100) : 0;
        long hours = centiseconds / 360000;
        long minutes = centiseconds / 6000 % 60;
        long whole = centiseconds / 100 % 60;
        long fraction = centiseconds % 100;
        return string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{whole:00}.{fraction:00}");
    }
}
