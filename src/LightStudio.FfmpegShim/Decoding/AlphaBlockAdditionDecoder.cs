using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Interop;
using LightStudio.FfmpegShim.Media;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>
/// Decodes the alpha picture a Matroska block addition carries beside each VP8 or VP9 picture, for
/// a build whose decoder leaves the addition in the packet rather than composing it itself.
/// </summary>
internal sealed unsafe class AlphaBlockAdditionDecoder : IDisposable
{
    /// <summary>The BlockAddID Matroska assigns to an alpha addition.</summary>
    private const ulong AlphaAdditionId = 1;

    private readonly Queue<(long Timestamp, IntPtr Frame)> decoded = new();
    private AVCodecContext* codec;
    private AVFrame* frame;
    private AVPacket* packet;

    public AlphaBlockAdditionDecoder(CodecParameters source, AVCodec* decoder, VideoDecoderOptions options)
    {
        AVCodecParameters* parameters = source.ToNative();
        try
        {
            codec = avcodec_alloc_context3(decoder);
            if (codec == null)
                throw new FfmpegException("avcodec_alloc_context3 returned null", 0);

            FfmpegError.ThrowIfError(avcodec_parameters_to_context(codec, parameters), "avcodec_parameters_to_context");
            codec->pkt_timebase = source.NativeTimeBase();
            codec->thread_count = options.ThreadCount > 0
                ? options.ThreadCount
                : Math.Min(Environment.ProcessorCount, 16);
            codec->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
            FfmpegError.ThrowIfError(avcodec_open2(codec, decoder, null), "avcodec_open2");

            frame = av_frame_alloc();
            packet = av_packet_alloc();
            if (frame == null || packet == null)
                throw new FfmpegException("Could not allocate alpha block addition decode state.", 0);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            CodecParameters.FreeNative(&parameters);
        }
    }

    /// <summary>Decodes the alpha addition of <paramref name="source"/>; null drains the decoder.</summary>
    public void Send(AVPacket* source)
    {
        if (codec == null)
            return;

        if (source == null)
        {
            SendPacket(null);
            Drain();
            return;
        }

        nuint size;
        byte* addition = av_packet_get_side_data(source, AVPacketSideDataType.AV_PKT_DATA_MATROSKA_BLOCKADDITIONAL, &size);
        if (addition == null || size <= 8 ||
            BinaryPrimitives.ReadUInt64BigEndian(new ReadOnlySpan<byte>(addition, 8)) != AlphaAdditionId)
        {
            return;
        }

        av_packet_unref(packet);
        FfmpegError.ThrowIfError(av_new_packet(packet, (int)(size - 8)), "av_new_packet");
        new ReadOnlySpan<byte>(addition + 8, (int)(size - 8)).CopyTo(new Span<byte>(packet->data, packet->size));
        packet->pts = source->pts;
        packet->dts = source->dts;
        packet->flags = source->flags;
        SendPacket(packet);
        av_packet_unref(packet);
        Drain();
    }

    /// <summary>
    /// The decoded alpha picture of the frame at <paramref name="timestamp"/>, owned by the caller,
    /// or null when none is ready.
    /// </summary>
    public AVFrame* Take(long timestamp)
    {
        Drain();
        while (decoded.Count > 0)
        {
            (long candidate, IntPtr handle) = decoded.Peek();
            if (timestamp == AV_NOPTS_VALUE || candidate == AV_NOPTS_VALUE || candidate == timestamp)
            {
                decoded.Dequeue();
                return (AVFrame*)handle;
            }

            if (candidate > timestamp)
                return null;

            decoded.Dequeue();
            Free(handle);
        }

        return null;
    }

    public void Flush()
    {
        if (codec != null)
            avcodec_flush_buffers(codec);
        while (decoded.Count > 0)
            Free(decoded.Dequeue().Frame);
    }

    private void SendPacket(AVPacket* value)
    {
        int result = avcodec_send_packet(codec, value);
        if (FfmpegError.IsTryAgain(result))
        {
            Drain();
            result = avcodec_send_packet(codec, value);
        }

        if (result < 0 && !FfmpegError.IsEndOfFile(result))
            FfmpegError.ThrowIfError(result, "avcodec_send_packet (alpha block addition)");
    }

    private void Drain()
    {
        if (codec == null || frame == null)
            return;

        while (true)
        {
            int result = avcodec_receive_frame(codec, frame);
            if (FfmpegError.IsTryAgain(result) || FfmpegError.IsEndOfFile(result))
                return;
            FfmpegError.ThrowIfError(result, "avcodec_receive_frame (alpha block addition)");

            AVFrame* kept = av_frame_alloc();
            if (kept == null)
            {
                av_frame_unref(frame);
                throw new FfmpegException("av_frame_alloc returned null", 0);
            }

            av_frame_move_ref(kept, frame);
            long timestamp = kept->best_effort_timestamp != AV_NOPTS_VALUE ? kept->best_effort_timestamp : kept->pts;
            decoded.Enqueue((timestamp, (IntPtr)kept));
        }
    }

    private static void Free(IntPtr handle)
    {
        AVFrame* value = (AVFrame*)handle;
        av_frame_free(&value);
    }

    public void Dispose()
    {
        Flush();
        if (packet != null)
        {
            AVPacket* handle = packet;
            av_packet_free(&handle);
            packet = null;
        }
        if (frame != null)
        {
            AVFrame* handle = frame;
            av_frame_free(&handle);
            frame = null;
        }
        if (codec != null)
        {
            AVCodecContext* handle = codec;
            avcodec_free_context(&handle);
            codec = null;
        }
    }
}
