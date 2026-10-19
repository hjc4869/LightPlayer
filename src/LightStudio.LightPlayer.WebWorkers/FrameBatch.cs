using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;
using LightStudio.NativeBindings.Types;

namespace LightStudio.LightPlayer.WebWorkers;

/// <summary>
/// The output of a decoder that runs in this worker. Frames are handed over only while the page has
/// room for them, and the planes stay in this worker's memory until the page releases them.
/// </summary>
internal sealed class FrameExport<T>(int decoder, FrameBatch batch, Func<int> serial) : IFrameProcessor<T>
    where T : class, IMediaQueueItem
{
    private long sent;

    public int Decoder => decoder;

    /// <summary>Frames handed over so far, including any the worker script sent for this decoder.</summary>
    public long Sent
    {
        get => sent;
        set => sent = value;
    }

    /// <summary>The total number of frames the page has room for, counting all it has taken.</summary>
    public long Allowance { get; set; }

    /// <summary>The run of the latest seek of the demuxer that feeds this decoder.</summary>
    public int Serial => serial();

    public bool TryWrite(T item)
    {
        if (!item.IsEndOfStream && sent >= Allowance)
            return false;
        if (!item.IsEndOfStream)
            sent++;
        batch.Add(decoder, item);
        return true;
    }

    public bool Enqueue(T item, System.Threading.CancellationToken cancellationToken, bool keepOnAbort = false) =>
        throw new NotSupportedException("The worker hands frames over without waiting.");
}

/// <summary>Frames the page holds by identity, released when the page is done with them.</summary>
internal static class FrameRegistry
{
    private static readonly ConcurrentDictionary<int, (VideoFrame? Video, AudioFrame? Audio, GCHandle Pin)> frames = [];
    private static int nextId;
    internal static int Count => frames.Count;

    public static int Add(VideoFrame frame) => Add((frame, null, default));

    public static int Add(AudioFrame frame, out IntPtr address)
    {
        GCHandle pin = GCHandle.Alloc(frame.Data, GCHandleType.Pinned);
        try
        {
            address = pin.AddrOfPinnedObject();
            return Add((null, frame, pin));
        }
        catch
        {
            pin.Free();
            throw;
        }
    }

    public static void Release(int id)
    {
        if (!frames.TryRemove(id, out var frame))
            return;
        if (frame.Video is { } video)
            video.Release();
        else if (frame.Audio is { } audio)
        {
            frame.Pin.Free();
            audio.Recycle();
        }
    }

    private static int Add((VideoFrame? Video, AudioFrame? Audio, GCHandle Pin) frame)
    {
        int id;
        do
            id = Interlocked.Increment(ref nextId) & int.MaxValue;
        while (id == 0 || !frames.TryAdd(id, frame));
        return id;
    }
}

/// <summary>
/// Collects what one pump produced for the page: frames, status snapshots and notifications, written
/// as one JSON document in the shape the page's frame import reads.
/// </summary>
internal sealed class FrameBatch
{
    private readonly ArrayBufferWriter<byte> buffer = new(4096);
    private readonly Utf8JsonWriter writer;
    private readonly List<string> notifications = [];
    private int frames;

    public FrameBatch()
    {
        writer = new Utf8JsonWriter(buffer);
        Begin();
    }

    public bool IsEmpty => frames == 0 && notifications.Count == 0;

    public void Add<T>(int decoder, T item) where T : class, IMediaQueueItem
    {
        frames++;
        writer.WriteStartObject();
        writer.WriteNumber("output", decoder);
        writer.WriteNumber("serial", item.Serial);
        if (item.IsEndOfStream)
        {
            writer.WriteNumber("kind", 4);
            writer.WriteEndObject();
            ReleaseMarker(item);
            return;
        }

        switch (item)
        {
            case VideoFrame video:
                writer.WriteNumber("kind", 1);
                writer.WriteNumber("id", FrameRegistry.Add(video));
                WriteVideo(writer, video);
                if (video.EnhancementLayer is { } layer)
                {
                    writer.WriteStartObject("enhancement");
                    writer.WriteNumber("id", FrameRegistry.Add(layer.AddRef()));
                    writer.WriteNumber("kind", 1);
                    writer.WriteNumber("serial", item.Serial);
                    WriteVideo(writer, layer);
                    writer.WriteEndObject();
                }

                break;

            case AudioFrame audio:
                writer.WriteNumber("kind", 2);
                writer.WriteNumber("id", FrameRegistry.Add(audio, out IntPtr address));
                WriteDouble(writer, "pts", audio.Pts);
                WriteDouble(writer, "duration", audio.Duration);
                writer.WriteNumber("address", (ulong)(nuint)address);
                writer.WriteNumber("length", audio.Length);
                break;
        }

        writer.WriteEndObject();
    }

    public void Notify(WireNotification notification) =>
        notifications.Add(JsonSerializer.Serialize(notification, EngineWireContext.Default.WireNotification));

    /// <summary>Closes the document and starts the next one.</summary>
    public string Finish(IEnumerable<WireDemuxerStatus> demuxers, IEnumerable<WireDecoderStatus> decoders,
        IEnumerable<WireRelayStatus> relays, bool busy, int retryAfter)
    {
        writer.WriteEndArray();
        writer.WriteStartArray("demuxers");
        foreach (WireDemuxerStatus status in demuxers)
            JsonSerializer.Serialize(writer, status, EngineWireContext.Default.WireDemuxerStatus);
        writer.WriteEndArray();
        writer.WriteStartArray("decoders");
        foreach (WireDecoderStatus status in decoders)
            JsonSerializer.Serialize(writer, status, EngineWireContext.Default.WireDecoderStatus);
        writer.WriteEndArray();
        writer.WriteStartArray("relays");
        foreach (WireRelayStatus status in relays)
            JsonSerializer.Serialize(writer, status, EngineWireContext.Default.WireRelayStatus);
        writer.WriteEndArray();
        writer.WriteStartArray("notifications");
        foreach (string notification in notifications)
            writer.WriteRawValue(notification);
        writer.WriteEndArray();
        writer.WriteBoolean("busy", busy);
        writer.WriteNumber("retryAfter", retryAfter);
        writer.WriteEndObject();
        writer.Flush();
        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        Begin();
        return json;
    }

    private void Begin()
    {
        buffer.ResetWrittenCount();
        writer.Reset(buffer);
        notifications.Clear();
        frames = 0;
        writer.WriteStartObject();
        writer.WriteStartArray("frames");
    }

    /// <summary>Describes a video frame's planes in this worker's memory and its color, as the page imports them.</summary>
    internal static void WriteVideo(Utf8JsonWriter writer, VideoFrame frame)
    {
        WriteDouble(writer, "pts", frame.Pts);
        WriteDouble(writer, "duration", frame.Duration);
        writer.WriteNumber("width", frame.Width);
        writer.WriteNumber("height", frame.Height);
        writer.WriteBoolean("hasAlpha", frame.HasAlpha);
        writer.WriteString("format", PixelFormatName(frame.Format));
        writer.WriteStartArray("planes");
        for (int plane = 0; plane < frame.PlaneCount; plane++)
        {
            writer.WriteStartObject();
            writer.WriteNumber("offset", (ulong)(nuint)frame.GetPlane(plane));
            writer.WriteNumber("stride", frame.GetStride(plane));
            writer.WriteNumber("rowBytes", frame.GetRowBytes(plane));
            writer.WriteNumber("height", frame.GetPlaneHeight(plane));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("key", frame.IsKeyFrame);
        writer.WriteNumber("matrix", MatrixCode(frame.ColorSpace));
        writer.WriteNumber("primaries", PrimariesCode(frame.ColorPrimaries));
        writer.WriteNumber("transfer", TransferCode(frame.ColorTransfer));
        writer.WriteNumber("range", (int)(frame.ColorRange == VideoColorRange.Full
            ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG));
        writer.WriteNumber("chroma", (int)frame.ChromaLocation + 1);
        writer.WriteNumber("fieldOrder", FieldOrderCode(frame.FieldOrder));
        writer.WriteNumber("rotation", frame.Rotation);
        writer.WriteBoolean("mirror", frame.MirrorHorizontally);
        WriteDouble(writer, "pixelAspect", frame.PixelAspectRatio);
        WriteDouble(writer, "peak", frame.HdrPeakLuminance);
        WriteDouble(writer, "minimumLuminance", frame.HdrMasteringLuminance?.MinimumNits ?? 0);
        WriteDouble(writer, "maximumLuminance", frame.HdrMasteringLuminance?.MaximumNits ?? 0);
        if (frame.Hdr10Plus.IsPresent)
            writer.WriteBase64String("hdr10PlusMetadata", VideoFrameMetadataCodec.Write(frame.Hdr10Plus));
        if (frame.DolbyVision.IsPresent)
            writer.WriteBase64String("dolbyVisionMetadata", VideoFrameMetadataCodec.Write(frame.DolbyVision));
    }

    // JSON has no non-finite numbers; the page reads named literals such as "NaN" instead.
    private static void WriteDouble(Utf8JsonWriter writer, string name, double value)
    {
        if (double.IsFinite(value))
            writer.WriteNumber(name, value);
        else
            writer.WriteString(name, value.ToString(CultureInfo.InvariantCulture));
    }

    private static void ReleaseMarker<T>(T item)
    {
        if (item is VideoFrame video)
            video.Release();
        else if (item is AudioFrame audio)
            audio.Recycle();
    }

    private static string PixelFormatName(VideoPixelFormat format) => format switch
    {
        VideoPixelFormat.Yuv420p => "yuv420p",
        VideoPixelFormat.Yuv422p => "yuv422p",
        VideoPixelFormat.Yuv444p => "yuv444p",
        VideoPixelFormat.Yuv420p10 => "yuv420p10le",
        VideoPixelFormat.Yuv422p10 => "yuv422p10le",
        VideoPixelFormat.Yuv444p10 => "yuv444p10le",
        VideoPixelFormat.Yuv420p12 => "yuv420p12le",
        VideoPixelFormat.Yuv422p12 => "yuv422p12le",
        VideoPixelFormat.Yuv444p12 => "yuv444p12le",
        VideoPixelFormat.Yuv420p16 => "yuv420p16le",
        VideoPixelFormat.Yuv422p16 => "yuv422p16le",
        VideoPixelFormat.Yuv444p16 => "yuv444p16le",
        VideoPixelFormat.Nv12 => "nv12",
        VideoPixelFormat.Nv21 => "nv21",
        VideoPixelFormat.P010 => "p010le",
        VideoPixelFormat.P012 => "p012le",
        VideoPixelFormat.P016 => "p016le",
        VideoPixelFormat.Bgra32 => "bgra",
        VideoPixelFormat.Rgba64 => "rgba64le",
        _ => throw new NotSupportedException($"The page cannot import {format} frames."),
    };

    private static int MatrixCode(VideoColorSpace space) => (int)(space switch
    {
        VideoColorSpace.Bt709 => AVColorSpace.AVCOL_SPC_BT709,
        VideoColorSpace.Bt2020 => AVColorSpace.AVCOL_SPC_BT2020_NCL,
        _ => AVColorSpace.AVCOL_SPC_SMPTE170M,
    });

    private static int PrimariesCode(VideoColorPrimaries primaries) => (int)(primaries switch
    {
        VideoColorPrimaries.Bt2020 => AVColorPrimaries.AVCOL_PRI_BT2020,
        VideoColorPrimaries.Smpte170M => AVColorPrimaries.AVCOL_PRI_SMPTE170M,
        VideoColorPrimaries.Bt470Bg => AVColorPrimaries.AVCOL_PRI_BT470BG,
        VideoColorPrimaries.Bt470M => AVColorPrimaries.AVCOL_PRI_BT470M,
        VideoColorPrimaries.DisplayP3 => AVColorPrimaries.AVCOL_PRI_SMPTE432,
        VideoColorPrimaries.DciP3 => AVColorPrimaries.AVCOL_PRI_SMPTE431,
        _ => AVColorPrimaries.AVCOL_PRI_BT709,
    });

    private static int TransferCode(VideoColorTransfer transfer) => (int)(transfer switch
    {
        VideoColorTransfer.Pq => AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084,
        VideoColorTransfer.Hlg => AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67,
        VideoColorTransfer.Linear => AVColorTransferCharacteristic.AVCOL_TRC_LINEAR,
        VideoColorTransfer.Srgb => AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1,
        _ => AVColorTransferCharacteristic.AVCOL_TRC_BT709,
    });

    private static int FieldOrderCode(VideoFieldOrder order) => (int)(order switch
    {
        VideoFieldOrder.Progressive => AVFieldOrder.AV_FIELD_PROGRESSIVE,
        VideoFieldOrder.TopFieldFirst => AVFieldOrder.AV_FIELD_TT,
        VideoFieldOrder.BottomFieldFirst => AVFieldOrder.AV_FIELD_BB,
        _ => AVFieldOrder.AV_FIELD_UNKNOWN,
    });
}
