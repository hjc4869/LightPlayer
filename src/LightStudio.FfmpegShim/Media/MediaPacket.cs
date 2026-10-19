using System;
using System.Collections.Concurrent;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Interop;
using LightStudio.NativeBindings.Types;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

/// <summary>
/// A compressed packet on its way from the demuxer to a decoder. Wraps a reusable
/// <c>AVPacket</c>; the payload itself is reference counted by FFmpeg, so moving a packet through
/// the queue never copies the bitstream.
/// </summary>
public sealed unsafe partial class MediaPacket : IMediaQueueItem, IPacketWriter
{
    private int size;

    public MediaPacket(MediaPacketPool owner)
    {
        Owner = owner;
        Handle = av_packet_alloc();
    }

    public MediaPacketPool Owner { get; }

    public AVPacket* Handle { get; private set; }

    public int Serial { get; set; }

    public int StreamIndex { get; private set; } = -1;

    public int SizeInBytes => size;

    /// <summary>Bitstream payload. Valid until the packet is recycled.</summary>
    public IntPtr Data => Handle == null ? IntPtr.Zero : (IntPtr)Handle->data;

    /// <summary>Presentation timestamp in stream time-base units, or <c>AV_NOPTS_VALUE</c>.</summary>
    public long Pts => Handle == null ? AV_NOPTS_VALUE : Handle->pts;

    /// <summary>Decode timestamp in stream time-base units, or <c>AV_NOPTS_VALUE</c>.</summary>
    public long Dts => Handle == null ? AV_NOPTS_VALUE : Handle->dts;

    public bool IsKeyFrame => Handle != null && (Handle->flags & AV_PKT_FLAG_KEY) != 0;

    /// <summary>Dynamic HDR metadata FFmpeg's demuxer attached to this compressed frame.</summary>
    public DynamicHdrMetadataKind DynamicHdrMetadata
    {
        get
        {
            if (Handle == null)
                return DynamicHdrMetadataKind.None;

            nuint size = 0;
            DynamicHdrMetadataKind found = av_packet_get_side_data(
                Handle,
                AVPacketSideDataType.AV_PKT_DATA_DYNAMIC_HDR10_PLUS,
                &size) != null && size > 0
                    ? DynamicHdrMetadataKind.Hdr10Plus
                    : DynamicHdrMetadataKind.None;

            size = 0;
            if (av_packet_get_side_data(
                    Handle,
                    AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF,
                    &size) != null && size > 0)
            {
                found |= DynamicHdrMetadataKind.DolbyVision;
            }

            return found;
        }
    }

    /// <summary>
    /// Copies FFmpeg's HDR10+ packet side data into its stable SMPTE ST 2094-40 T.35 payload.
    /// </summary>
    public byte[] CopyHdr10PlusMetadata()
    {
        if (Handle == null)
            return null;

        nuint sideDataSize = 0;
        byte* sideData = av_packet_get_side_data(
            Handle,
            AVPacketSideDataType.AV_PKT_DATA_DYNAMIC_HDR10_PLUS,
            &sideDataSize);
        if (sideData == null || sideDataSize < (ulong)sizeof(AVDynamicHDRPlus))
            return null;

        byte* payload = null;
        nuint payloadSize = 0;
        FfmpegError.ThrowIfError(
            av_dynamic_hdr_plus_to_t35((AVDynamicHDRPlus*)sideData, &payload, &payloadSize),
            "av_dynamic_hdr_plus_to_t35");
        try
        {
            if (payload == null || payloadSize == 0)
                return null;
            if (payloadSize > int.MaxValue)
                throw new InvalidOperationException("The HDR10+ metadata payload is too large.");

            // FFmpeg serializes this byte as version 1 regardless of the source structure. Keep
            // version 0 metadata as version 0 so its percentile fields retain their ST 2094-40
            // meaning when the client parses the payload again.
            payload[0] = ((AVDynamicHDRPlus*)sideData)->application_version;
            return new ReadOnlySpan<byte>(payload, (int)payloadSize).ToArray();
        }
        finally
        {
            av_free(payload);
        }
    }

    /// <summary>Packet duration in stream time-base units, 0 when the container states none.</summary>
    public long Duration => Handle == null ? 0 : Handle->duration;

    /// <summary>
    /// Drain marker. Passed to <c>avcodec_send_packet</c> as a null packet so the decoder emits
    /// the frames it is still holding before reporting end of file.
    /// </summary>
    public bool IsEndOfStream { get; internal set; }

    public void CaptureFromRead()
    {
        StreamIndex = Handle->stream_index;
        size = Handle->size;
    }

    /// <summary>Retains the same reference-counted FFmpeg packet payload.</summary>
    internal void ReferenceFrom(MediaPacket source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Handle == null)
            throw new ObjectDisposedException(nameof(source));

        Reset();
        FfmpegError.ThrowIfError(av_packet_ref(Handle, source.Handle), "av_packet_ref");
        StreamIndex = source.StreamIndex;
        size = source.size;
    }

    /// <summary>
    /// Takes over a packet an encoder produced. An encoder knows nothing of containers, so the
    /// stream it belongs to has to be named here.
    /// </summary>
    public void CaptureFromEncoder(int streamIndex)
    {
        Handle->stream_index = streamIndex;
        StreamIndex = streamIndex;
        size = Handle->size;
    }

    /// <summary>
    /// Fills this packet from a payload that did not come out of a container. <c>av_new_packet</c>
    /// gives the same reference counted, padded buffer a demuxer would have produced, so everything
    /// downstream cannot tell the difference.
    /// </summary>
    public void FillFromWire(
        int streamIndex,
        long pts,
        long dts,
        long duration,
        bool isKeyFrame,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> hdr10PlusMetadata = default)
    {
        FfmpegError.ThrowIfError(av_new_packet(Handle, payload.Length), "av_new_packet");
        payload.CopyTo(new Span<byte>(Handle->data, payload.Length));

        Handle->stream_index = streamIndex;
        Handle->pts = pts;
        Handle->dts = dts;
        Handle->duration = duration;
        Handle->flags = isKeyFrame ? AV_PKT_FLAG_KEY : 0;
        if (!hdr10PlusMetadata.IsEmpty)
        {
            byte* sideData = av_packet_new_side_data(
                Handle,
                AVPacketSideDataType.AV_PKT_DATA_DYNAMIC_HDR10_PLUS,
                (nuint)sizeof(AVDynamicHDRPlus));
            if (sideData == null)
            {
                av_packet_unref(Handle);
                throw new OutOfMemoryException("Could not allocate HDR10+ packet side data.");
            }

            fixed (byte* metadata = hdr10PlusMetadata)
            {
                int error = av_dynamic_hdr_plus_from_t35(
                    (AVDynamicHDRPlus*)sideData,
                    metadata,
                    (nuint)hdr10PlusMetadata.Length);
                if (error < 0)
                {
                    av_packet_unref(Handle);
                    FfmpegError.ThrowIfError(error, "av_dynamic_hdr_plus_from_t35");
                }
            }
        }

        StreamIndex = streamIndex;
        size = payload.Length;
    }

    void IPacketWriter.Write(
        int streamIndex,
        long pts,
        long dts,
        long duration,
        bool isKeyFrame,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> hdr10PlusMetadata) =>
        FillFromWire(streamIndex, pts, dts, duration, isKeyFrame, payload, hdr10PlusMetadata);

    public void MarkEndOfStream(int streamIndex)
    {
        IsEndOfStream = true;
        StreamIndex = streamIndex;
        size = 0;
    }

    public void Reset()
    {
        if (Handle != null)
            av_packet_unref(Handle);
        IsEndOfStream = false;
        StreamIndex = -1;
        size = 0;
    }

    public void Free()
    {
        if (Handle == null)
            return;
        AVPacket* handle = Handle;
        av_packet_free(&handle);
        Handle = null;
    }

    /// <summary>Returns the packet to its pool. Safe to call from any thread.</summary>
    public void Recycle() => Owner.Return(this);
}

/// <summary>Recycles <see cref="MediaPacket"/> instances so steady-state demuxing allocates nothing.</summary>
public sealed class MediaPacketPool
{
    private readonly ConcurrentBag<MediaPacket> idle = new();
    private readonly int maxIdle;

    public MediaPacketPool(int maxIdle = 256) => this.maxIdle = maxIdle;

    public MediaPacket Rent() => idle.TryTake(out var packet) ? packet : new MediaPacket(this);

    public void Return(MediaPacket packet)
    {
        packet.Reset();
        if (idle.Count >= maxIdle)
        {
            packet.Free();
            return;
        }

        idle.Add(packet);
    }

    public void Clear()
    {
        while (idle.TryTake(out var packet))
            packet.Free();
    }
}
