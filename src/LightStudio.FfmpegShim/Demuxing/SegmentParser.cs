#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LightStudio.FfmpegShim.Interop;
using LightStudio.FfmpegShim.Media;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Demuxing;

/// <summary>Byte stream format of a Media Source buffer.</summary>
public enum SegmentContainer
{
    /// <summary>Fragmented MP4: an <c>ftyp</c>/<c>moov</c> initialization and <c>moof</c>/<c>mdat</c> media units.</summary>
    Mp4,

    /// <summary>WebM: an EBML header, Info and Tracks initialization, and Cluster media units.</summary>
    WebM,
}

/// <summary>One track an initialization segment declares.</summary>
/// <param name="StreamIndex">Index of the stream inside the segment, which packets carry.</param>
/// <param name="TrackId">The container's own track number.</param>
/// <param name="Parameters">Codec setup with the stream's time base.</param>
public sealed record SegmentTrack(int StreamIndex, int TrackId, CodecParameters Parameters);

/// <summary>
/// Demuxes Media Source segments one media unit at a time. Every call opens libavformat over the
/// buffer's initialization segment followed by one unit, in memory and without seeking, so units can
/// arrive in any order and switch between initialization segments without state carried over.
/// </summary>
/// <remarks>
/// The input format comes from the container, streams are not probed, and timestamps are left as the
/// demuxer reports them with its default edit-list handling: nothing is subtracted. Each open marks
/// the first packet of every audio stream for priming removal where the track declares priming; that
/// mark is stripped, so priming is trimmed by whoever owns the timeline, while trailing discard marks
/// stay.
/// </remarks>
public static unsafe class SegmentParser
{
    private const int IoBufferSize = 0x10000;

    // A Cluster with Timestamp 0 and nothing else: the Matroska demuxer reads its header only up to the
    // first Cluster, so an initialization segment alone does not open.
    private static readonly byte[] EmptyCluster = [0x1F, 0x43, 0xB6, 0x75, 0x83, 0xE7, 0x81, 0x00];

    /// <summary>Reads the tracks and codec setup an initialization segment declares.</summary>
    /// <exception cref="InvalidDataException">The segment cannot be opened.</exception>
    public static IReadOnlyList<SegmentTrack> ReadTracks(SegmentContainer container, ReadOnlySpan<byte> initialization)
    {
        ReadOnlySpan<byte> unit = container == SegmentContainer.WebM ? EmptyCluster : default;
        AVFormatContext* format = null;
        AVIOContext* io = null;
        var memory = new MemoryInput();
        fixed (byte* first = initialization)
        fixed (byte* second = unit)
        {
            memory.First = first;
            memory.FirstLength = initialization.Length;
            memory.Second = second;
            memory.SecondLength = unit.Length;
            try
            {
                Open(container, &memory, ref format, ref io);
                var tracks = new List<SegmentTrack>((int)format->nb_streams);
                for (int index = 0; index < format->nb_streams; index++)
                {
                    AVStream* stream = format->streams[index];
                    CodecParameters parameters = CodecParameters.FromNative(
                        stream->codecpar,
                        stream->time_base,
                        stream->avg_frame_rate);
                    tracks.Add(new SegmentTrack(index, stream->id, parameters));
                }

                return tracks;
            }
            finally
            {
                Close(ref format, ref io);
            }
        }
    }

    /// <summary>
    /// Demuxes one media unit, appending its packets in container order to <paramref name="packets"/>.
    /// Timestamps are in the time base <see cref="ReadTracks"/> reports for the same initialization
    /// segment. Returns the number of packets added.
    /// </summary>
    /// <exception cref="InvalidDataException">The unit cannot be demuxed.</exception>
    public static int ReadPackets(
        SegmentContainer container,
        ReadOnlySpan<byte> initialization,
        ReadOnlySpan<byte> unit,
        MediaPacketPool pool,
        List<MediaPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(packets);

        int added = 0;
        AVFormatContext* format = null;
        AVIOContext* io = null;
        var memory = new MemoryInput();
        fixed (byte* first = initialization)
        fixed (byte* second = unit)
        {
            memory.First = first;
            memory.FirstLength = initialization.Length;
            memory.Second = second;
            memory.SecondLength = unit.Length;
            try
            {
                Open(container, &memory, ref format, ref io);
                int streamCount = (int)format->nb_streams;
                Span<bool> primingChecked = streamCount <= 64 ? stackalloc bool[streamCount] : new bool[streamCount];
                while (true)
                {
                    MediaPacket packet = pool.Rent();
                    int ret = av_read_frame(format, packet.Handle);
                    if (ret < 0)
                    {
                        packet.Recycle();
                        if (FfmpegError.IsEndOfFile(ret))
                            break;
                        throw new InvalidDataException($"The media segment could not be demuxed: {FfmpegError.Describe(ret)}.");
                    }

                    packet.CaptureFromRead();
                    int stream = packet.StreamIndex;
                    if (stream >= 0 && stream < streamCount && !primingChecked[stream])
                    {
                        primingChecked[stream] = true;
                        if (format->streams[stream]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                            ClearLeadingSkip(packet.Handle);
                    }

                    packets.Add(packet);
                    added++;
                }

                return added;
            }
            catch
            {
                for (int index = packets.Count - added; index < packets.Count; index++)
                    packets[index].Recycle();
                packets.RemoveRange(packets.Count - added, added);
                throw;
            }
            finally
            {
                Close(ref format, ref io);
            }
        }
    }

    /// <summary>Clears the leading half of <c>AV_PKT_DATA_SKIP_SAMPLES</c>, keeping a trailing discard.</summary>
    private static void ClearLeadingSkip(AVPacket* packet)
    {
        nuint size = 0;
        byte* side = av_packet_get_side_data(packet, AVPacketSideDataType.AV_PKT_DATA_SKIP_SAMPLES, &size);
        if (side == null || size < 4)
            return;

        // Little-endian uint32 sample count, then the trailing discard count.
        side[0] = 0;
        side[1] = 0;
        side[2] = 0;
        side[3] = 0;
    }

    private static void Open(SegmentContainer container, MemoryInput* memory, ref AVFormatContext* format, ref AVIOContext* io)
    {
        AVInputFormat* inputFormat = av_find_input_format(container == SegmentContainer.Mp4 ? "mov" : "matroska");
        if (inputFormat == null)
            throw new NotSupportedException($"The active FFmpeg build has no demuxer for {container}.");

        byte* buffer = (byte*)av_malloc(IoBufferSize);
        if (buffer == null)
            throw new OutOfMemoryException("Failed to allocate the AVIO buffer.");

        // No seek callback: the input is a forward-only stream, which is what keeps libavformat from
        // looking for an index at the end of the data.
        delegate* unmanaged[Cdecl]<void*, byte*, int, int> read = &Read;
        io = avio_alloc_context(buffer, IoBufferSize, 0, memory, read, null, null);
        if (io == null)
        {
            av_free(buffer);
            throw new OutOfMemoryException("Failed to allocate the AVIO context.");
        }

        format = avformat_alloc_context();
        if (format == null)
            throw new OutOfMemoryException("avformat_alloc_context returned null.");

        format->pb = io;
        format->flags |= AVFMT_FLAG_CUSTOM_IO;
        AVFormatContext* context = format;

        // avformat_open_input frees the context on failure and leaves it null.
        int ret = avformat_open_input(&context, string.Empty, inputFormat, null);
        format = context;
        if (ret < 0)
            throw new InvalidDataException($"The segment could not be opened: {FfmpegError.Describe(ret)}.");
    }

    private static void Close(ref AVFormatContext* format, ref AVIOContext* io)
    {
        if (format != null)
        {
            AVFormatContext* context = format;
            avformat_close_input(&context);
            format = null;
        }

        if (io != null)
        {
            // libavformat may have replaced the buffer, so the current one is freed.
            av_freep(&io->buffer);
            av_free(io);
            io = null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(void* opaque, byte* buffer, int size)
    {
        var memory = (MemoryInput*)opaque;
        int done = 0;
        while (done < size)
        {
            long position = memory->Position;
            byte* source;
            long remaining;
            if (position < memory->FirstLength)
            {
                source = memory->First + position;
                remaining = memory->FirstLength - position;
            }
            else
            {
                long offset = position - memory->FirstLength;
                if (offset >= memory->SecondLength)
                    break;
                source = memory->Second + offset;
                remaining = memory->SecondLength - offset;
            }

            int take = (int)Math.Min(remaining, size - done);
            Buffer.MemoryCopy(source, buffer + done, size - done, take);
            done += take;
            memory->Position += take;
        }

        return done > 0 ? done : AVERROR_EOF;
    }

    private struct MemoryInput
    {
        public byte* First;
        public long FirstLength;
        public byte* Second;
        public long SecondLength;
        public long Position;
    }
}
