using System;
using System.Collections.Generic;
using System.IO;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Interop;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

/// <summary>Identity and codec of one audio stream found during a library scan.</summary>
public sealed partial class VideoProbeAudioStreamInfo
{
    public int Index { get; init; }

    public string CodecName { get; init; }
}

/// <summary>Container summary plus an optional preview frame, produced by <see cref="VideoProbe"/>.</summary>
public sealed class VideoProbeResult
{
    public TimeSpan Duration { get; init; }

    /// <summary>Picture width as it is meant to be seen, i.e. after any rotation.</summary>
    public int Width { get; init; }

    /// <inheritdoc cref="Width"/>
    public int Height { get; init; }

    /// <summary>Degrees the coded picture is turned clockwise to reach <see cref="Width"/>x<see cref="Height"/>.</summary>
    public int Rotation { get; init; }

    /// <summary>Whether the coded picture is mirrored left to right before <see cref="Rotation"/>.</summary>
    public bool MirrorHorizontally { get; init; }

    public double FrameRate { get; init; }

    public string VideoCodec { get; init; }

    public int VideoStreamIndex { get; init; }

    /// <summary>
    /// Whether the video stream is a single picture rather than moving footage, so the file is a
    /// photograph even when its container is one videos normally come in.
    /// </summary>
    public bool IsStillImage { get; init; }

    public string Title { get; init; }

    public IReadOnlyList<VideoProbeAudioStreamInfo> AudioStreams { get; init; } =
        Array.Empty<VideoProbeAudioStreamInfo>();

    /// <summary>Preview pixels as tightly packed BGRA32, or null when no frame could be decoded.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] PreviewBgra { get; init; }

    /// <summary>
    /// The preview of an HDR or wide-gamut source, tagged with its turn and pixel aspect ratio, when the probe was asked
    /// to retain it instead of filling <see cref="PreviewBgra"/>. The receiver owns the reference.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public VideoFrame PreviewFrame { get; init; }

    public int PreviewWidth { get; init; }

    public int PreviewHeight { get; init; }

    public TimeSpan PreviewPosition { get; init; }
}

/// <summary>
/// Software-only inspection of a media container: reports the video geometry and optionally decodes
/// a single preview frame. Kept separate from the playback pipeline because indexing wants one
/// short-lived open per file rather than a running decoder.
/// </summary>
public static unsafe partial class VideoProbe
{
    private const int IoBufferSize = 64 * 1024;

    /// <summary><c>SWS_BILINEAR</c>.</summary>
    private const int SwsBilinear = 2;

    /// <summary>
    /// Picks where the preview frame is taken from. The opening frames of a video are routinely a
    /// black fade or a title card, so the sample is pulled from a little way in.
    /// </summary>
    public static TimeSpan ChoosePreviewPosition(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return TimeSpan.FromSeconds(5);
        if (duration < TimeSpan.FromSeconds(4))
            return duration / 2;

        double seconds = Math.Clamp(duration.TotalSeconds * 0.1, 5, 60);
        return TimeSpan.FromSeconds(Math.Min(seconds, duration.TotalSeconds - 1));
    }

    /// <summary>
    /// Reads only the stream table reported by libavformat. This is used by the library scanner for
    /// audio sidecars, where decoding a video preview would be both impossible and unnecessary.
    /// </summary>
    public static IReadOnlyList<VideoProbeAudioStreamInfo> ProbeAudioStreams(Stream stream, bool leaveOpen = true)
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
                format = null;
                return Array.Empty<VideoProbeAudioStreamInfo>();
            }

            return avformat_find_stream_info(format, null) < 0
                ? Array.Empty<VideoProbeAudioStreamInfo>()
                : DescribeAudioStreams(format);
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

    /// <summary>
    /// Inspects an already-open demuxer and optionally decodes a preview from its selected input.
    /// The demuxer is left positioned after the preview frame and remains owned by the caller.
    /// </summary>
    public static VideoProbeResult Probe(
        FfmpegDemuxer demuxer,
        bool withPreview = true,
        int maxPreviewDimension = 640,
        bool retainPreviewFrame = false)
    {
        ArgumentNullException.ThrowIfNull(demuxer);

        try
        {
            return ProbeOpened(
                demuxer.FormatContext,
                withPreview,
                maxPreviewDimension,
                hasExif: false,
                exifRotation: 0,
                exifMirror: false,
                retainPreviewFrame);
        }
        catch (FfmpegException)
        {
            return null;
        }
    }

    /// <summary>
    /// Inspects <paramref name="stream"/> and returns null when it holds no real video stream
    /// (attached cover art does not count). A stream holding a single picture is reported with
    /// <see cref="VideoProbeResult.IsStillImage"/> set rather than rejected. Never throws for
    /// undecodable content: the preview is simply left null so callers can fall back to a placeholder.
    /// </summary>
    /// <param name="maxPreviewDimension">Longest edge of the preview, in pixels.</param>
    /// <param name="retainPreviewFrame">
    /// Whether a preview with HDR or primaries other than BT.709 is returned as <see cref="VideoProbeResult.PreviewFrame"/>
    /// rather than mapped into sRGB.
    /// </param>
    public static VideoProbeResult Probe(
        Stream stream,
        bool withPreview = true,
        int maxPreviewDimension = 640,
        bool leaveOpen = true,
        bool retainPreviewFrame = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Read before libavformat takes the stream over, which leaves the position wherever demuxing
        // ended up. Only a still needs it, but by then the stream is no longer ours to rewind.
        int exifRotation = 0;
        bool exifMirror = false;
        bool hasExif = withPreview && ExifOrientation.TryRead(stream, out exifRotation, out exifMirror);

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

            return ProbeOpened(
                format,
                withPreview,
                maxPreviewDimension,
                hasExif,
                exifRotation,
                exifMirror,
                retainPreviewFrame);
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

    private static VideoProbeResult ProbeOpened(
        AVFormatContext* format,
        bool withPreview,
        int maxPreviewDimension,
        bool hasExif,
        int exifRotation,
        bool exifMirror,
        bool retainPreviewFrame)
    {
        int videoIndex = av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        if (videoIndex < 0)
            return null;
        if ((format->streams[videoIndex]->disposition & AV_DISPOSITION_ATTACHED_PIC) != 0)
            return null;

        AVStream* video = format->streams[videoIndex];
        AVCodecParameters* par = video->codecpar;

        TimeSpan duration = format->duration != AV_NOPTS_VALUE && format->duration > 0
            ? TimeSpan.FromSeconds(format->duration / (double)AV_TIME_BASE)
            : TimeSpan.Zero;

        AVRational guessed = av_guess_frame_rate(format, video, null);
        TimeSpan previewPosition = ChoosePreviewPosition(duration);
        VideoDisplayMatrix.Read(par, out int rotation, out bool mirrorHorizontally);

        // A container that records a turn is believed over the EXIF tag; a photograph usually
        // records none, and its camera's tag is then the only thing that stands it upright.
        bool isStillImage = IsStillImage(format, video, videoIndex);
        if (isStillImage && hasExif && rotation == 0 && !mirrorHorizontally)
        {
            rotation = exifRotation;
            mirrorHorizontally = exifMirror;
        }

        bool swapAxes = rotation is 90 or 270;

        var result = new VideoProbeResult
        {
            Duration = duration,
            Width = swapAxes ? par->height : par->width,
            Height = swapAxes ? par->width : par->height,
            Rotation = rotation,
            MirrorHorizontally = mirrorHorizontally,
            FrameRate = guessed.den > 0 ? Utils.RationalToDouble(guessed) : 0,
            VideoCodec = avcodec_get_name(par->codec_id),
            VideoStreamIndex = videoIndex,
            IsStillImage = isStillImage,
            Title = ReadTitle(format->metadata),
            AudioStreams = DescribeAudioStreams(format),
            PreviewPosition = previewPosition,
        };

        if (!withPreview)
            return result;

        return DecodePreview(format, video, videoIndex, result, previewPosition, maxPreviewDimension, retainPreviewFrame) ?? result;
    }

    private static VideoProbeResult DecodePreview(
        AVFormatContext* format,
        AVStream* video,
        int videoIndex,
        VideoProbeResult probe,
        TimeSpan position,
        int maxPreviewDimension,
        bool retainPreviewFrame)
    {
        AVCodec* codec = avcodec_find_decoder(video->codecpar->codec_id);
        if (codec == null)
            return null;

        AVCodecContext* context = avcodec_alloc_context3(codec);
        if (context == null)
            return null;

        AVPacket* packet = null;
        AVFrame* frame = null;
        SwsContext* scaler = null;
        DolbyVisionEnhancementDecoder enhancement = null;
        try
        {
            if (avcodec_parameters_to_context(context, video->codecpar) < 0)
                return null;

            context->thread_count = 1;
            context->pkt_timebase = video->time_base;
            if (avcodec_open2(context, codec, null) < 0)
                return null;
            CodecParameters description = CodecParameters.FromNative(video->codecpar, video->time_base, video->avg_frame_rate);
            if (description.DolbyVisionProfile == 7 && description.DolbyVisionConfiguration.Length > 5 &&
                description.DolbyVisionConfiguration[5] != 0)
                enhancement = new DolbyVisionEnhancementDecoder(description, new VideoDecoderOptions { ThreadCount = 1 });

            // Everything but this stream is dead weight for a single frame.
            for (int i = 0; i < format->nb_streams; i++)
                format->streams[i]->discard = i == videoIndex ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;

            long targetPts = SeekTo(format, video, videoIndex, position);
            avcodec_flush_buffers(context);

            packet = av_packet_alloc();
            frame = av_frame_alloc();
            if (packet == null || frame == null)
                return null;

            if (!TryDecodeFrame(format, context, videoIndex, packet, frame, enhancement) || !IsIntact(frame))
            {
                // Either the seek landed past the last decodable packet, or it landed on a picture
                // the container flags as a key frame but the codec cannot start from (open GOP,
                // non-IDR recovery points). The latter still yields a frame, but one rebuilt purely
                // by error concealment: flat blocks of color. Replaying from the head of the file
                // costs more but always reconstructs the frame properly.
                if (position <= TimeSpan.Zero)
                    return null;

                avformat_seek_file(format, videoIndex, long.MinValue, long.MinValue, long.MaxValue, AVSEEK_FLAG_BACKWARD);
                avcodec_flush_buffers(context);
                enhancement?.Flush();
                if (!TryDecodeForward(format, context, videoIndex, packet, frame, targetPts, enhancement))
                    return null;
            }

            if (frame->width <= 0 || frame->height <= 0 || frame->format < 0)
                return null;

            ComputePreviewSize(
                frame->width,
                frame->height,
                video->codecpar->sample_aspect_ratio,
                maxPreviewDimension,
                out int targetWidth,
                out int targetHeight);

            byte[] pixels = null;
            VideoFrame previewFrame = null;
            if (StillImage.NeedsSoftwareColor(frame, video->codecpar))
            {
                VideoFrame retained = StillImage.RetainSoftwareFrame(frame, video->codecpar);
                try
                {
                    VideoFrame layer = enhancement?.Take(frame->pts);
                    if (retained.DolbyVision.EnhancementLayerResidual)
                        retained.AttachEnhancementLayer(layer);
                    else
                        layer?.Release();
                    if (retainPreviewFrame)
                    {
                        AVRational aspect = video->codecpar->sample_aspect_ratio;
                        retained.PixelAspectRatio = aspect.num > 0 && aspect.den > 0 ? Utils.RationalToDouble(aspect) : 1;
                        retained.Rotation = probe.Rotation;
                        retained.MirrorHorizontally = probe.MirrorHorizontally;
                        previewFrame = retained.AddRef();
                    }
                    else
                    {
                        using var mapped = StillImage.RenderSoftwarePixels(retained, targetWidth, targetHeight);
                        pixels = mapped.GetSpan().ToArray();
                    }
                }
                finally { retained.Release(); }
            }
            else
            {
                scaler = sws_getContext(
                    frame->width,
                    frame->height,
                    (AVPixelFormat)frame->format,
                    targetWidth,
                    targetHeight,
                    AVPixelFormat.AV_PIX_FMT_BGRA,
                    SwsBilinear,
                    null,
                    null,
                    null);
                if (scaler == null)
                    return null;

                int stride = targetWidth * 4;
                pixels = new byte[checked(stride * targetHeight)];
                fixed (byte* destination = pixels)
                {
                    byte** sourceData = stackalloc byte*[4] { frame->data[0], frame->data[1], frame->data[2], frame->data[3] };
                    int* sourceStride = stackalloc int[4] { frame->linesize[0], frame->linesize[1], frame->linesize[2], frame->linesize[3] };
                    byte** destinationData = stackalloc byte*[4] { destination, null, null, null };
                    int* destinationStride = stackalloc int[4] { stride, 0, 0, 0 };
                    if (sws_scale(scaler, sourceData, sourceStride, 0, frame->height, destinationData, destinationStride) <= 0)
                        return null;
                }
            }

            if (previewFrame is not null && probe.Rotation is 90 or 270)
                (targetWidth, targetHeight) = (targetHeight, targetWidth);

            return new VideoProbeResult
            {
                Duration = probe.Duration,
                Width = probe.Width,
                Height = probe.Height,
                Rotation = probe.Rotation,
                MirrorHorizontally = probe.MirrorHorizontally,
                FrameRate = probe.FrameRate,
                VideoCodec = probe.VideoCodec,
                VideoStreamIndex = probe.VideoStreamIndex,
                IsStillImage = probe.IsStillImage,
                Title = probe.Title,
                AudioStreams = probe.AudioStreams,
                PreviewBgra = pixels is null ? null
                    : Orient(pixels, ref targetWidth, ref targetHeight, probe.Rotation, probe.MirrorHorizontally),
                PreviewFrame = previewFrame,
                PreviewWidth = targetWidth,
                PreviewHeight = targetHeight,
                PreviewPosition = position,
            };
        }
        finally
        {
            enhancement?.Dispose();
            if (scaler != null)
                sws_freeContext(scaler);
            if (frame != null)
                av_frame_free(&frame);
            if (packet != null)
                av_packet_free(&packet);
            avcodec_free_context(&context);
        }
    }

    /// <summary>
    /// Whether the stream holds one picture and nothing more. The frame count settles it wherever a
    /// container records one (MP4, HEIF/AVIF, GIF); the image demuxers record none, but each of them
    /// only ever produces a single picture per file, so their name answers instead; and where
    /// neither does -- a cover picture muxed beside audio in a Matroska or Ogg file -- the packets are
    /// counted.
    /// </summary>
    private static bool IsStillImage(AVFormatContext* format, AVStream* video, int videoIndex)
    {
        if ((video->disposition & AV_DISPOSITION_STILL_IMAGE) != 0)
            return true;
        if (video->nb_frames > 0)
            return video->nb_frames == 1;
        if (IsSinglePictureDemuxer(format))
            return true;

        // Only picture codecs are worth reading packets for. Everything that codes motion is a video
        // whatever the count says, and checking would cost the indexer a read per file for nothing.
        AVCodecDescriptor* descriptor = avcodec_descriptor_get(video->codecpar->codec_id);
        if (descriptor == null || (descriptor->props & AV_CODEC_PROP_INTRA_ONLY) == 0)
            return false;

        return HasSinglePicture(format, videoIndex);
    }

    /// <summary>image2 and the per-format pipe demuxers behind it: png_pipe, webp_pipe, jpegxl_pipe, ...</summary>
    private static bool IsSinglePictureDemuxer(AVFormatContext* format)
    {
        if (format->iformat == null || format->iformat->name == null)
            return false;

        string demuxer = new string((sbyte*)format->iformat->name);
        return demuxer is "image2" or "image2pipe" || demuxer.EndsWith("_pipe", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads past the first video packet to see whether a second follows, then rewinds so the file
    /// is left as it was found. Answers no for an input that cannot be rewound: putting the packets
    /// back matters more than the answer, and nothing that streams is a photograph.
    /// </summary>
    private static bool HasSinglePicture(AVFormatContext* format, int videoIndex)
    {
        if (format->pb == null || format->pb->seekable == 0)
            return false;

        AVPacket* packet = av_packet_alloc();
        if (packet == null)
            return false;

        var discards = new AVDiscard[format->nb_streams];
        for (int i = 0; i < format->nb_streams; i++)
        {
            discards[i] = format->streams[i]->discard;
            format->streams[i]->discard = i == videoIndex ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
        }

        try
        {
            int pictures = 0;
            while (pictures < 2 && av_read_frame(format, packet) >= 0)
            {
                if (packet->stream_index == videoIndex)
                    pictures++;
                av_packet_unref(packet);
            }

            return pictures == 1;
        }
        finally
        {
            av_packet_free(&packet);
            for (int i = 0; i < format->nb_streams; i++)
                format->streams[i]->discard = discards[i];
            avformat_seek_file(format, -1, long.MinValue, 0, long.MaxValue, AVSEEK_FLAG_BACKWARD);
        }
    }

    private static VideoProbeAudioStreamInfo[] DescribeAudioStreams(AVFormatContext* format)
    {
        var streams = new List<VideoProbeAudioStreamInfo>();
        for (int index = 0; index < format->nb_streams; index++)
        {
            AVStream* stream = format->streams[index];
            AVCodecParameters* parameters = stream->codecpar;
            if (parameters->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                continue;
            }

            streams.Add(new VideoProbeAudioStreamInfo
            {
                Index = index,
                CodecName = avcodec_get_name(parameters->codec_id),
            });
        }

        return streams.ToArray();
    }

    /// <summary>Seeks to <paramref name="position"/> and returns its pts, or AV_NOPTS_VALUE when no seek happened.</summary>
    private static long SeekTo(AVFormatContext* format, AVStream* video, int videoIndex, TimeSpan position)
    {
        if (position <= TimeSpan.Zero)
            return AV_NOPTS_VALUE;
        if (format->pb != null && format->pb->seekable == 0)
            return AV_NOPTS_VALUE;

        double unit = Utils.RationalToDouble(video->time_base);
        if (unit <= 0)
            return AV_NOPTS_VALUE;

        long target = (long)(position.TotalSeconds / unit);
        if (video->start_time != AV_NOPTS_VALUE)
            target += video->start_time;

        // BACKWARD lands on the key frame at or before the target, which is exactly what a preview
        // wants: no wasted decoding of the frames in between.
        avformat_seek_file(format, videoIndex, long.MinValue, target, target, AVSEEK_FLAG_BACKWARD);
        return target;
    }

    /// <summary>True when the decoder reconstructed the frame without falling back to concealment.</summary>
    private static bool IsIntact(AVFrame* frame)
        => frame->decode_error_flags == 0 && (frame->flags & AV_FRAME_FLAG_CORRUPT) == 0;

    private static bool TryDecodeFrame(
        AVFormatContext* format,
        AVCodecContext* context,
        int videoIndex,
        AVPacket* packet,
        AVFrame* frame,
        DolbyVisionEnhancementDecoder enhancement)
    {
        // Bounded so a stream of undecodable packets cannot stall the indexer.
        const int MaxPackets = 512;

        for (int i = 0; i < MaxPackets; i++)
        {
            int read = av_read_frame(format, packet);
            if (read < 0)
                break;

            if (packet->stream_index != videoIndex)
            {
                av_packet_unref(packet);
                continue;
            }

            int sent = avcodec_send_packet(context, packet);
            if (sent >= 0)
                enhancement?.Send(packet);
            av_packet_unref(packet);
            if (sent < 0 && !FfmpegError.IsTryAgain(sent))
                continue;

            if (avcodec_receive_frame(context, frame) == 0)
                return true;
        }

        avcodec_send_packet(context, null);
        enhancement?.Send(null);
        return avcodec_receive_frame(context, frame) == 0;
    }

    /// <summary>
    /// Decodes straight through from the current position up to <paramref name="targetPts"/>, leaving
    /// the frame it stopped on in <paramref name="frame"/>. Used when seeking produced no usable
    /// picture; the budget keeps a long file from turning one preview into a full decode.
    /// </summary>
    private static bool TryDecodeForward(
        AVFormatContext* format,
        AVCodecContext* context,
        int videoIndex,
        AVPacket* packet,
        AVFrame* frame,
        long targetPts,
        DolbyVisionEnhancementDecoder enhancement)
    {
        const int MaxPackets = 4096;

        AVFrame* latest = av_frame_alloc();
        if (latest == null)
            return false;

        bool decoded = false;
        try
        {
            for (int i = 0; i < MaxPackets; i++)
            {
                if (av_read_frame(format, packet) < 0)
                    break;

                if (packet->stream_index != videoIndex)
                {
                    av_packet_unref(packet);
                    continue;
                }

                int sent = avcodec_send_packet(context, packet);
                if (sent >= 0)
                    enhancement?.Send(packet);
                av_packet_unref(packet);
                if (sent < 0 && !FfmpegError.IsTryAgain(sent))
                    continue;

                while (avcodec_receive_frame(context, frame) == 0)
                {
                    decoded = true;
                    if (targetPts == AV_NOPTS_VALUE ||
                        (frame->pts != AV_NOPTS_VALUE && frame->pts >= targetPts))
                    {
                        return true;
                    }

                    // receive_frame unreferences its destination before every attempt, including
                    // the one that reports EAGAIN/EOF. Keep the most recent picture elsewhere so
                    // running out before the target does not turn a successful decode into an
                    // empty frame.
                    if (latest->width > 0)
                        enhancement?.Take(latest->pts)?.Release();
                    av_frame_unref(latest);
                    av_frame_move_ref(latest, frame);
                }
            }

            if (decoded)
            {
                av_frame_unref(frame);
                av_frame_move_ref(frame, latest);
            }

            // Running out of packets or budget still returns the last decoded frame, which is a
            // better preview than none.
            return decoded;
        }
        finally
        {
            av_frame_free(&latest);
        }
    }

    private static void ComputePreviewSize(
        int width,
        int height,
        AVRational sampleAspectRatio,
        int maxDimension,
        out int targetWidth,
        out int targetHeight)
    {
        double displayWidth = width;
        if (sampleAspectRatio.num > 0 && sampleAspectRatio.den > 0)
            displayWidth = width * Utils.RationalToDouble(sampleAspectRatio);

        double scale = Math.Min(1.0, maxDimension / Math.Max(displayWidth, height));
        targetWidth = Math.Max(2, (int)Math.Round(displayWidth * scale));
        targetHeight = Math.Max(2, (int)Math.Round(height * scale));

        // sws is happiest with even dimensions for chroma-subsampled sources.
        targetWidth -= targetWidth % 2;
        targetHeight -= targetHeight % 2;
    }

    private static string ReadTitle(AVDictionary* metadata)
    {
        AVDictionaryEntry* entry = av_dict_get(metadata, "title", null, 0);
        return entry != null ? Utils.NullTerminatedUTF8StringToString((sbyte*)entry->value) : null;
    }

    /// <summary>
    /// Applies the container's display matrix to a packed BGRA preview. Copying a picture this small
    /// is cheaper than teaching swscale to do it, and it happens once per file.
    /// </summary>
    private static byte[] Orient(byte[] pixels, ref int width, ref int height, int rotation, bool mirrorHorizontally)
    {
        if (rotation == 0 && !mirrorHorizontally)
            return pixels;

        bool swapAxes = rotation is 90 or 270;
        int outputWidth = swapAxes ? height : width;
        int outputHeight = swapAxes ? width : height;
        var rotated = new byte[pixels.Length];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Mirror first, then turn, which is the order the matrix decomposes into.
                int mirrored = mirrorHorizontally ? width - 1 - x : x;
                (int targetX, int targetY) = rotation switch
                {
                    90 => (height - 1 - y, mirrored),
                    180 => (width - 1 - mirrored, height - 1 - y),
                    270 => (y, width - 1 - mirrored),
                    _ => (mirrored, y),
                };

                Buffer.BlockCopy(pixels, ((y * width) + x) * 4, rotated, ((targetY * outputWidth) + targetX) * 4, 4);
            }
        }

        width = outputWidth;
        height = outputHeight;
        return rotated;
    }
}
