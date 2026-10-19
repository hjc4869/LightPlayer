#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.FfmpegShim.Engine;

// A packet source the page reads itself, such as a server's packet stream, relayed to its demuxer in a
// media worker. Packets travel in binary batches tagged with a generation: the worker starts a new one
// whenever it moves the source, and drops batches of older ones as they arrive.

/// <summary>Streams of a relayed source and how each one's timestamps map onto the playback timeline.</summary>
public sealed record WireRelaySource(
    MediaInfo Info,
    int VideoStream,
    int AudioStream,
    bool CanSeek,
    IReadOnlyList<WireRelayStream> Streams)
{
    /// <summary>Describes <paramref name="source"/> as it currently is.</summary>
    public static WireRelaySource Describe(IPacketSource source)
    {
        var streams = new List<WireRelayStream>(source.Info.Streams.Count);
        foreach (MediaStreamInfo stream in source.Info.Streams)
            streams.Add(new WireRelayStream(stream.Index, source.GetCodecParameters(stream.Index), source.ToSeconds(stream.Index, 0)));
        return new WireRelaySource(source.Info, source.VideoStreamIndex, source.AudioStreamIndex, source.CanSeek, streams);
    }
}

/// <param name="Origin">Playback time of timestamp zero, in seconds.</param>
public sealed record WireRelayStream(int Index, CodecParameters Parameters, double Origin);

/// <param name="Generation">The relay generation after the seek; it changes when the seek moved the source.</param>
public sealed record WireSeekResult(bool Succeeded, int Generation);

/// <summary>Bytes of relayed batches a worker demuxer has taken in total, used or dropped.</summary>
public sealed record WireRelayStatus(int Demuxer, long Consumed);

public static class PacketRelayRecordKind
{
    public const byte Packet = 1;
    public const byte End = 2;
    public const byte Fault = 3;
    public const byte Reconfigure = 4;
}

/// <summary>
/// One record of a relayed batch. <see cref="Payload"/> holds a packet's bitstream, a fault's message or a
/// reconfiguration's description, and refers into the batch.
/// </summary>
public readonly record struct PacketRelayRecord(
    byte Kind,
    int Length,
    int Stream,
    long Pts,
    long Dts,
    long Duration,
    bool IsKeyFrame,
    ReadOnlyMemory<byte> Payload,
    ReadOnlyMemory<byte> SideData,
    double Position);

/// <summary>Writes the records of one relayed batch, in the order the source's reads returned them.</summary>
public sealed class PacketRelayWriter : IPacketWriter
{
    private const int PacketHeaderSize = 38;
    private byte[] buffer = new byte[64 * 1024];
    private int length;

    public int Length => length;

    public Span<byte> Written => buffer.AsSpan(0, length);

    public void Reset() => length = 0;

    public void Write(
        int streamIndex,
        long pts,
        long dts,
        long duration,
        bool isKeyFrame,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> hdr10PlusMetadata)
    {
        Span<byte> record = Reserve(PacketHeaderSize + payload.Length + hdr10PlusMetadata.Length);
        record[0] = PacketRelayRecordKind.Packet;
        record[1] = isKeyFrame ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(record[2..], streamIndex);
        BinaryPrimitives.WriteInt64LittleEndian(record[6..], pts);
        BinaryPrimitives.WriteInt64LittleEndian(record[14..], dts);
        BinaryPrimitives.WriteInt64LittleEndian(record[22..], duration);
        BinaryPrimitives.WriteInt32LittleEndian(record[30..], payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(record[34..], hdr10PlusMetadata.Length);
        payload.CopyTo(record[PacketHeaderSize..]);
        hdr10PlusMetadata.CopyTo(record[(PacketHeaderSize + payload.Length)..]);
    }

    public void WriteEnd() => Reserve(1)[0] = PacketRelayRecordKind.End;

    public void WriteFault(string message)
    {
        byte[] text = Encoding.UTF8.GetBytes(message);
        Span<byte> record = Reserve(5 + text.Length);
        record[0] = PacketRelayRecordKind.Fault;
        BinaryPrimitives.WriteInt32LittleEndian(record[1..], text.Length);
        text.CopyTo(record[5..]);
    }

    /// <summary>The source changed its streams; packets after this record belong to the new description.</summary>
    public void WriteReconfigure(double position, WireRelaySource source)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(source, PacketRelayJsonContext.Default.WireRelaySource);
        Span<byte> record = Reserve(13 + json.Length);
        record[0] = PacketRelayRecordKind.Reconfigure;
        BinaryPrimitives.WriteDoubleLittleEndian(record[1..], position);
        BinaryPrimitives.WriteInt32LittleEndian(record[9..], json.Length);
        json.CopyTo(record[13..]);
    }

    /// <summary>Reads every record of <paramref name="batch"/>.</summary>
    public static List<PacketRelayRecord> Read(ReadOnlyMemory<byte> batch)
    {
        var records = new List<PacketRelayRecord>();
        ReadOnlySpan<byte> data = batch.Span;
        int offset = 0;
        while (offset < data.Length)
        {
            int start = offset;
            byte kind = data[offset];
            switch (kind)
            {
                case PacketRelayRecordKind.Packet:
                {
                    Require(data, offset, PacketHeaderSize);
                    ReadOnlySpan<byte> header = data.Slice(offset, PacketHeaderSize);
                    int payload = BinaryPrimitives.ReadInt32LittleEndian(header[30..]);
                    int sideData = BinaryPrimitives.ReadInt32LittleEndian(header[34..]);
                    if (payload < 0 || sideData < 0)
                        throw new InvalidDataException("A relayed packet has a negative length.");
                    offset += PacketHeaderSize;
                    Require(data, offset, payload + sideData);
                    records.Add(new PacketRelayRecord(kind, PacketHeaderSize + payload + sideData,
                        BinaryPrimitives.ReadInt32LittleEndian(header[2..]),
                        BinaryPrimitives.ReadInt64LittleEndian(header[6..]),
                        BinaryPrimitives.ReadInt64LittleEndian(header[14..]),
                        BinaryPrimitives.ReadInt64LittleEndian(header[22..]),
                        header[1] != 0,
                        batch.Slice(offset, payload),
                        batch.Slice(offset + payload, sideData),
                        double.NaN));
                    offset += payload + sideData;
                    break;
                }

                case PacketRelayRecordKind.End:
                    offset++;
                    records.Add(new PacketRelayRecord(kind, 1, -1, 0, 0, 0, false, default, default, double.NaN));
                    break;

                case PacketRelayRecordKind.Fault:
                {
                    Require(data, offset, 5);
                    int text = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 1)..]);
                    offset += 5;
                    Require(data, offset, text);
                    records.Add(new PacketRelayRecord(kind, offset + text - start, -1, 0, 0, 0, false, batch.Slice(offset, text), default,
                        double.NaN));
                    offset += text;
                    break;
                }

                case PacketRelayRecordKind.Reconfigure:
                {
                    Require(data, offset, 13);
                    double position = BinaryPrimitives.ReadDoubleLittleEndian(data[(offset + 1)..]);
                    int json = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 9)..]);
                    offset += 13;
                    Require(data, offset, json);
                    records.Add(new PacketRelayRecord(kind, offset + json - start, -1, 0, 0, 0, false, batch.Slice(offset, json), default,
                        position));
                    offset += json;
                    break;
                }

                default:
                    throw new InvalidDataException($"Unknown relayed record {kind}.");
            }
        }

        return records;
    }

    private static void Require(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (count < 0 || offset + count > data.Length)
            throw new InvalidDataException("A relayed batch ends inside a record.");
    }

    private Span<byte> Reserve(int count)
    {
        if (length + count > buffer.Length)
            Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + count));
        Span<byte> record = buffer.AsSpan(length, count);
        length += count;
        return record;
    }
}

/// <summary>
/// Read-only properties are left out: the page has no FFmpeg, and some of them, such as a stream's bit depth,
/// ask FFmpeg for their value.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(WireRelaySource))]
[JsonSerializable(typeof(CodecParameters))]
public sealed partial class PacketRelayJsonContext : JsonSerializerContext;
