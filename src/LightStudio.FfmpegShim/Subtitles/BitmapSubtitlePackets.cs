using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.FfmpegShim.Subtitles;

/// <summary>One encoded subtitle packet, with timestamps in its stream's time base.</summary>
public sealed record SubtitlePacket(long Pts, long Dts, long Duration, byte[] Data);

/// <summary>
/// The encoded packets of one bitmap subtitle stream and what decoding them needs, for a reader that has
/// the packets but not the container they came from.
/// </summary>
/// <param name="StartTimeSeconds">Container start time removed from packet times to reach the playback timeline.</param>
/// <param name="DurationSeconds">Media duration that bounds the last composition.</param>
/// <param name="CanvasWidth">Picture width used when the subtitle stream does not state its own canvas.</param>
/// <param name="CanvasHeight">Picture height used when the subtitle stream does not state its own canvas.</param>
/// <param name="Packets">Packets in decode order. One read by <see cref="BitmapSubtitlePacketsCodec.Read"/> can be enumerated once.</param>
public sealed record BitmapSubtitlePackets(
    CodecParameters Parameters,
    double StartTimeSeconds,
    double DurationSeconds,
    int CanvasWidth,
    int CanvasHeight,
    IEnumerable<SubtitlePacket> Packets);

/// <summary>Writes <see cref="BitmapSubtitlePackets"/> as compact bytes and back, for an engine in another memory domain.</summary>
public static class BitmapSubtitlePacketsCodec
{
    private const int HeaderSize = 8 + 8 + 4 + 4 + 4 + 4;
    private const int PacketHeaderSize = 8 + 8 + 8 + 4;

    public static byte[] Write(BitmapSubtitlePackets packets)
    {
        ArgumentNullException.ThrowIfNull(packets);
        byte[] parameters = JsonSerializer.SerializeToUtf8Bytes(packets.Parameters, PacketRelayJsonContext.Default.CodecParameters);
        long size = HeaderSize + parameters.Length;
        foreach (SubtitlePacket packet in packets.Packets)
            size += PacketHeaderSize + packet.Data.Length;
        if (size > int.MaxValue)
            throw new InvalidDataException("The subtitle packets are too large to transfer.");

        var output = new byte[size];
        Span<byte> span = output;
        BinaryPrimitives.WriteDoubleLittleEndian(span, packets.StartTimeSeconds);
        BinaryPrimitives.WriteDoubleLittleEndian(span[8..], packets.DurationSeconds);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], packets.CanvasWidth);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], packets.CanvasHeight);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], parameters.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], packets.Packets.Count());
        parameters.CopyTo(span[HeaderSize..]);
        int offset = HeaderSize + parameters.Length;
        foreach (SubtitlePacket packet in packets.Packets)
        {
            BinaryPrimitives.WriteInt64LittleEndian(span[offset..], packet.Pts);
            BinaryPrimitives.WriteInt64LittleEndian(span[(offset + 8)..], packet.Dts);
            BinaryPrimitives.WriteInt64LittleEndian(span[(offset + 16)..], packet.Duration);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 24)..], packet.Data.Length);
            packet.Data.CopyTo(span[(offset + PacketHeaderSize)..]);
            offset += PacketHeaderSize + packet.Data.Length;
        }

        return output;
    }

    /// <summary>
    /// Reads the header from <paramref name="source"/> and returns packets that are read from it one at a time
    /// as they are enumerated, so the whole set is never held at once. The stream must stay open until then.
    /// </summary>
    public static BitmapSubtitlePackets Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Span<byte> header = stackalloc byte[HeaderSize];
        source.ReadExactly(header);
        int parametersLength = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
        if (parametersLength < 0 || count < 0)
            throw new InvalidDataException("The subtitle packets declare a negative length.");

        var parametersJson = new byte[parametersLength];
        source.ReadExactly(parametersJson);
        CodecParameters parameters = JsonSerializer.Deserialize(parametersJson, PacketRelayJsonContext.Default.CodecParameters)
            ?? throw new InvalidDataException("The subtitle packets carry no codec parameters.");
        return new BitmapSubtitlePackets(
            parameters,
            BinaryPrimitives.ReadDoubleLittleEndian(header),
            BinaryPrimitives.ReadDoubleLittleEndian(header[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[20..]),
            ReadPackets(source, count));
    }

    private static IEnumerable<SubtitlePacket> ReadPackets(Stream source, int count)
    {
        var header = new byte[PacketHeaderSize];
        for (int index = 0; index < count; index++)
        {
            source.ReadExactly(header);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24));
            if (length < 0)
                throw new InvalidDataException("A subtitle packet declares a negative length.");

            var data = new byte[length];
            source.ReadExactly(data);
            yield return new SubtitlePacket(
                BinaryPrimitives.ReadInt64LittleEndian(header),
                BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8)),
                BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16)),
                data);
        }
    }
}
