using System;
using System.Collections.Generic;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Interop;
using LightStudio.FfmpegShim.Media;
using LightStudio.NativeBindings.Types;
using LightStudio.Logging;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>Extracts and decodes the HEVC enhancement layer embedded in Profile 7 packets.</summary>
internal sealed unsafe partial class DolbyVisionEnhancementDecoder : IDisposable
{
    private readonly record struct DecodedFrame(long Timestamp, VideoFrame Frame);

    private readonly VideoFramePool pool;
    private readonly Queue<DecodedFrame> decoded = new();
    private readonly int inputNalLengthSize;
    private int outputNalLengthSize;
    private AVCodecContext* codec;
    private AVFrame* frame;
    private AVPacket* packet;
    private AVBSFContext* splitter;
    private bool waitingForKeyFrame = true;
    private bool firstFrameReported;

    public DolbyVisionEnhancementDecoder(CodecParameters source, VideoDecoderOptions options)
    {
        byte[] extradata = source.DolbyVisionEnhancementConfiguration;
        int divisor = source.DolbyVisionProfile == 7 && source.Width > 1920 ? 2 : 1;
        CodecParameters enhancement = source with
        {
            Extradata = extradata,
            Width = source.Width / divisor,
            Height = source.Height / divisor,
            DolbyVisionConfiguration = Array.Empty<byte>(),
            DolbyVisionEnhancementConfiguration = Array.Empty<byte>(),
        };

        inputNalLengthSize = NalLengthSize(source.Extradata);
        outputNalLengthSize = NalLengthSize(extradata);
        pool = new VideoFramePool(
            Math.Max(4, options.FramePoolSize),
            Math.Max(32L * 1024 * 1024, options.FramePoolByteCapacity / 4));

        AVCodecParameters* ownedParameters = null;
        try
        {
            splitter = CreateSplitter(source);
            AVCodecParameters* parameters = splitter != null ? splitter->par_out : null;
            if (parameters != null && parameters->extradata != null && parameters->extradata_size > 0)
            {
                outputNalLengthSize = NalLengthSize(
                    new ReadOnlySpan<byte>(parameters->extradata, parameters->extradata_size));
            }
            if (parameters == null)
                parameters = ownedParameters = enhancement.ToNative();

            AVCodec* decoder = avcodec_find_decoder(parameters->codec_id);
            if (decoder == null)
                throw new FfmpegException("No HEVC decoder is available for the Dolby Vision enhancement layer.", 0);

            codec = avcodec_alloc_context3(decoder);
            if (codec == null)
                throw new FfmpegException("avcodec_alloc_context3 returned null", 0);

            FfmpegError.ThrowIfError(
                avcodec_parameters_to_context(codec, parameters),
                "avcodec_parameters_to_context");
            codec->pkt_timebase = source.NativeTimeBase();
            codec->thread_count = options.ThreadCount > 0
                ? options.ThreadCount
                : Math.Min(Environment.ProcessorCount, 16);
            codec->thread_type = FF_THREAD_SLICE;
            FfmpegError.ThrowIfError(avcodec_open2(codec, decoder, null), "avcodec_open2");

            frame = av_frame_alloc();
            packet = av_packet_alloc();
            if (frame == null || packet == null)
                throw new FfmpegException("Could not allocate Dolby Vision enhancement-layer decode state.", 0);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            CodecParameters.FreeNative(&ownedParameters);
        }
    }

    private static AVBSFContext* CreateSplitter(CodecParameters source)
    {
        AVBitStreamFilter* filter = av_bsf_get_by_name("dovi_split");
        if (filter == null)
            return null;

        AVBSFContext* context = null;
        FfmpegError.ThrowIfError(av_bsf_alloc(filter, &context), "av_bsf_alloc (dovi_split)");
        AVCodecParameters* parameters = source.ToNative();
        try
        {
            FfmpegError.ThrowIfError(
                avcodec_parameters_copy(context->par_in, parameters),
                "avcodec_parameters_copy (dovi_split)");
            context->time_base_in = source.NativeTimeBase();
            FfmpegError.ThrowIfError(
                av_opt_set(context->priv_data, "mode", "el", 0),
                "av_opt_set (dovi_split mode)");
            FfmpegError.ThrowIfError(av_bsf_init(context), "av_bsf_init (dovi_split)");
            return context;
        }
        catch
        {
            av_bsf_free(&context);
            throw;
        }
        finally
        {
            CodecParameters.FreeNative(&parameters);
        }
    }

    public void Send(AVPacket* source)
    {
        if (codec == null)
            return;

        if (source == null)
        {
            if (splitter != null)
            {
                FfmpegError.ThrowIfError(av_bsf_send_packet(splitter, null), "av_bsf_send_packet (dovi_split)");
                DrainSplitter();
            }
            SendPacket(null);
            Drain();
            return;
        }

        if (splitter != null)
        {
            AVPacket* input = av_packet_clone(source);
            if (input == null)
                throw new FfmpegException("av_packet_clone returned null", 0);
            try
            {
                int result = av_bsf_send_packet(splitter, input);
                if (FfmpegError.IsTryAgain(result))
                {
                    DrainSplitter();
                    result = av_bsf_send_packet(splitter, input);
                }
                FfmpegError.ThrowIfError(result, "av_bsf_send_packet (dovi_split)");
                DrainSplitter();
                Drain();
                return;
            }
            finally
            {
                av_packet_free(&input);
            }
        }

        if (waitingForKeyFrame)
        {
            if ((source->flags & AV_PKT_FLAG_KEY) == 0)
                return;
            waitingForKeyFrame = false;
        }

        av_packet_unref(packet);
        if (!TryExtract(source, packet, inputNalLengthSize, outputNalLengthSize))
            return;

        SendPacket(packet);
        av_packet_unref(packet);
        Drain();
    }

    public VideoFrame Take(long timestamp)
    {
        Drain();
        while (decoded.Count > 0)
        {
            DecodedFrame candidate = decoded.Peek();
            if (timestamp == AV_NOPTS_VALUE ||
                candidate.Timestamp == AV_NOPTS_VALUE ||
                candidate.Timestamp == timestamp)
            {
                decoded.Dequeue();
                return candidate.Frame;
            }

            if (candidate.Timestamp > timestamp)
                return null;

            decoded.Dequeue();
            candidate.Frame.Release();
        }

        return null;
    }

    public void Flush()
    {
        if (codec != null)
            avcodec_flush_buffers(codec);
        if (splitter != null)
            av_bsf_flush(splitter);
        waitingForKeyFrame = true;
        while (decoded.Count > 0)
            decoded.Dequeue().Frame.Release();
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
            FfmpegError.ThrowIfError(result, "avcodec_send_packet (Dolby Vision enhancement layer)");
    }

    private void DrainSplitter()
    {
        while (true)
        {
            av_packet_unref(packet);
            int result = av_bsf_receive_packet(splitter, packet);
            if (FfmpegError.IsTryAgain(result) || FfmpegError.IsEndOfFile(result))
                return;
            FfmpegError.ThrowIfError(result, "av_bsf_receive_packet (dovi_split)");
            if (waitingForKeyFrame)
            {
                if (!ContainsRandomAccessPicture(packet, outputNalLengthSize))
                    continue;
                waitingForKeyFrame = false;
            }
            SendPacket(packet);
        }
    }

    private static bool ContainsRandomAccessPicture(AVPacket* value, int nalLengthSize)
    {
        ReadOnlySpan<byte> data = new(value->data, value->size);
        if (nalLengthSize > 0)
        {
            int offset = 0;
            while (offset + nalLengthSize <= data.Length)
            {
                int length = ReadLength(data.Slice(offset, nalLengthSize));
                offset += nalLengthSize;
                if (length <= 0 || length > data.Length - offset)
                    return false;
                if (((data[offset] >> 1) & 0x3f) is >= 16 and <= 23)
                    return true;
                offset += length;
            }
            return false;
        }

        int search = 0;
        while (FindStartCode(data, search, out int prefix, out int payload))
        {
            if (payload < data.Length && ((data[payload] >> 1) & 0x3f) is >= 16 and <= 23)
                return true;
            search = Math.Max(payload, prefix + 1);
        }
        return false;
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
            FfmpegError.ThrowIfError(result, "avcodec_receive_frame (Dolby Vision enhancement layer)");

            try
            {
                VideoPixelFormat? format = VideoFrameMetadata.MapLayout((AVPixelFormat)frame->format);
                if (format is null)
                    throw new FfmpegException("The Dolby Vision enhancement-layer pixel format is not planar.", 0);

                VideoFrame target = pool.Rent(frame->width, frame->height, format.Value);
                for (int plane = 0; plane < target.PlaneCount; plane++)
                {
                    av_image_copy_plane(
                        (byte*)target.GetPlane(plane),
                        target.GetStride(plane),
                        frame->data[(uint)plane],
                        frame->linesize[(uint)plane],
                        target.GetRowBytes(plane),
                        target.GetPlaneHeight(plane));
                }

                target.ChromaLocation = VideoFrameMetadata.MapChromaLocation(frame->chroma_location);
                long timestamp = frame->best_effort_timestamp != AV_NOPTS_VALUE
                    ? frame->best_effort_timestamp
                    : frame->pts;
                decoded.Enqueue(new DecodedFrame(timestamp, target));
                if (!firstFrameReported)
                {
                    firstFrameReported = true;
                    AppLog.Information(
                        "FFmpeg.Decode",
                        $"Decoded first Dolby Vision enhancement-layer frame: " +
                        $"size={target.Width}x{target.Height}; format={target.Format}; timestamp={timestamp}.");
                }
            }
            finally
            {
                av_frame_unref(frame);
            }
        }
    }

    internal static bool TryExtract(
        AVPacket* source,
        AVPacket* destination,
        int inputNalLengthSize,
        int outputNalLengthSize)
    {
        if (source == null || destination == null || source->data == null || source->size <= 0)
            return false;

        ReadOnlySpan<byte> input = new(source->data, source->size);
        var payloads = new List<ReadOnlyMemory<byte>>();
        byte[] copy = input.ToArray();
        ReadOnlySpan<byte> stable = copy;
        if (inputNalLengthSize > 0)
        {
            int offset = 0;
            while (offset + inputNalLengthSize <= stable.Length)
            {
                int length = ReadLength(stable.Slice(offset, inputNalLengthSize));
                offset += inputNalLengthSize;
                if (length <= 0 || length > stable.Length - offset)
                    return false;
                AddEnhancementPayload(copy, offset, length, payloads);
                offset += length;
            }

            if (offset != stable.Length)
                return false;
        }
        else
        {
            int search = 0;
            while (FindStartCode(stable, search, out _, out int payloadStart))
            {
                int nextSearch = payloadStart;
                bool hasNext = FindStartCode(stable, nextSearch, out int nextPrefix, out _);
                int payloadEnd = hasNext ? nextPrefix : stable.Length;
                while (payloadEnd > payloadStart && stable[payloadEnd - 1] == 0)
                    payloadEnd--;
                AddEnhancementPayload(copy, payloadStart, payloadEnd - payloadStart, payloads);
                if (!hasNext)
                    break;
                search = nextPrefix;
            }
        }

        if (payloads.Count == 0)
            return false;

        int prefixSize = outputNalLengthSize > 0 ? outputNalLengthSize : 4;
        int outputSize = 0;
        foreach (ReadOnlyMemory<byte> payload in payloads)
            outputSize = checked(outputSize + prefixSize + payload.Length);

        FfmpegError.ThrowIfError(av_new_packet(destination, outputSize), "av_new_packet");
        FfmpegError.ThrowIfError(av_packet_copy_props(destination, source), "av_packet_copy_props");
        Span<byte> output = new(destination->data, outputSize);
        int write = 0;
        foreach (ReadOnlyMemory<byte> payload in payloads)
        {
            if (outputNalLengthSize == 0)
            {
                output.Slice(write, 4).Clear();
                output[write + 3] = 1;
            }
            else
            {
                WriteLength(output.Slice(write, outputNalLengthSize), payload.Length);
            }
            write += prefixSize;
            payload.Span.CopyTo(output.Slice(write));
            write += payload.Length;
        }

        return true;
    }

    private static void AddEnhancementPayload(
        byte[] packet,
        int offset,
        int length,
        List<ReadOnlyMemory<byte>> payloads)
    {
        if (length <= 2 || ((packet[offset] >> 1) & 0x3f) != 63)
            return;
        payloads.Add(packet.AsMemory(offset + 2, length - 2));
    }

    private static int NalLengthSize(ReadOnlySpan<byte> extradata) =>
        extradata.Length >= 23 && extradata[0] == 1 ? (extradata[21] & 3) + 1 : 0;

    private static int ReadLength(ReadOnlySpan<byte> value)
    {
        int result = 0;
        foreach (byte item in value)
            result = (result << 8) | item;
        return result;
    }

    private static void WriteLength(Span<byte> destination, int value)
    {
        for (int index = destination.Length - 1; index >= 0; index--)
        {
            destination[index] = (byte)value;
            value >>= 8;
        }
        if (value != 0)
            throw new InvalidOperationException("A Dolby Vision enhancement NAL is too large for its prefix.");
    }

    private static bool FindStartCode(
        ReadOnlySpan<byte> data,
        int start,
        out int prefixStart,
        out int payloadStart)
    {
        for (int index = Math.Max(start, 0); index + 3 <= data.Length; index++)
        {
            if (data[index] != 0 || data[index + 1] != 0)
                continue;
            if (data[index + 2] == 1)
            {
                prefixStart = index;
                payloadStart = index + 3;
                return true;
            }
            if (index + 4 <= data.Length && data[index + 2] == 0 && data[index + 3] == 1)
            {
                prefixStart = index;
                payloadStart = index + 4;
                return true;
            }
        }

        prefixStart = 0;
        payloadStart = 0;
        return false;
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
        if (splitter != null)
        {
            AVBSFContext* handle = splitter;
            av_bsf_free(&handle);
            splitter = null;
        }
        pool.Clear();
    }
}