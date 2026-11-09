using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;
using LightStudio.NativeBindings.Types;

namespace LightStudio.FfmpegShim.Demuxing;

/// <summary>
/// Reads the log curve a QuickTime video sample description names in its <c>logs</c> box, which
/// libavformat skips. Apple writes it for Apple Log footage, whose color tags leave the transfer
/// unspecified.
/// </summary>
internal static class QuickTimeLogTransfer
{
    private const long MaximumHeaderBoxSize = 1024 * 1024;
    private const int MaximumIdentifierLength = 256;

    // A visual sample entry's fields after the 8-byte box header, before its child boxes.
    private const int VisualSampleEntryFieldsSize = 78;

    /// <summary>Whether libavformat opened the input with its QuickTime and ISO BMFF demuxer.</summary>
    public static unsafe bool IsQuickTime(AVFormatContext* format) =>
        format != null && format->iformat != null && format->iformat->name != null &&
        Array.IndexOf(new string((sbyte*)format->iformat->name).Split(','), "mov") >= 0;

    /// <summary>
    /// Maps the indices of the video streams that declare a log curve to its identifier, reading the
    /// movie header from the start of <paramref name="stream"/> and restoring its position.
    /// </summary>
    public static unsafe Dictionary<int, string> Read(Stream stream, AVFormatContext* format)
    {
        var result = new Dictionary<int, string>();
        if (!stream.CanSeek || !IsQuickTime(format))
            return result;

        Dictionary<int, string> tracks;
        try
        {
            tracks = ReadTracks(stream);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Warning("FFmpeg.Demux", "Could not read the log transfer functions of the QuickTime movie.", exception);
            return result;
        }

        for (int index = 0; index < format->nb_streams && tracks.Count > 0; index++)
        {
            AVStream* candidate = format->streams[index];
            if (candidate->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO &&
                tracks.TryGetValue(candidate->id, out string identifier))
                result[index] = identifier;
        }

        return result;
    }

    private static Dictionary<int, string> ReadTracks(Stream stream)
    {
        var tracks = new Dictionary<int, string>();
        long position = stream.Position;
        try
        {
            long length = stream.Length;
            stream.Position = 0;
            while (ExifBlock.TryReadBoxHeader(stream, length, out uint type, out _, out long end))
            {
                if (type == Code("moov"u8))
                {
                    ReadMovie(stream, end, tracks);
                    break;
                }

                stream.Position = end;
            }
        }
        finally
        {
            stream.Position = position;
        }

        return tracks;
    }

    private static void ReadMovie(Stream stream, long end, Dictionary<int, string> tracks)
    {
        while (ExifBlock.TryReadBoxHeader(stream, end, out uint type, out _, out long trackEnd))
        {
            if (type == Code("trak"u8))
            {
                int? id = null;
                string identifier = null;
                ReadTrack(stream, trackEnd, ref id, ref identifier);
                if (id is { } track && identifier is not null)
                    tracks[track] = identifier;
            }

            stream.Position = trackEnd;
        }
    }

    private static void ReadTrack(Stream stream, long end, ref int? id, ref string identifier)
    {
        while (ExifBlock.TryReadBoxHeader(stream, end, out uint type, out long start, out long boxEnd))
        {
            if (type == Code("tkhd"u8))
                id = ReadTrackId(ReadPayload(stream, start, boxEnd));
            else if (type == Code("mdia"u8) || type == Code("minf"u8) || type == Code("stbl"u8))
                ReadTrack(stream, boxEnd, ref id, ref identifier);
            else if (type == Code("stsd"u8))
                identifier = ReadIdentifier(ReadPayload(stream, start, boxEnd));

            stream.Position = boxEnd;
        }
    }

    private static byte[] ReadPayload(Stream stream, long start, long end)
    {
        if (end - start is <= 0 or > MaximumHeaderBoxSize)
            return [];

        byte[] payload = new byte[end - start];
        int read = stream.ReadAtLeast(payload, payload.Length, throwOnEndOfStream: false);
        return read == payload.Length ? payload : payload[..read];
    }

    private static int? ReadTrackId(ReadOnlySpan<byte> header)
    {
        int offset = header.Length > 0 && header[0] == 1 ? 20 : 12;
        if (header.Length < offset + 4 || header[0] > 1)
            return null;
        uint id = BinaryPrimitives.ReadUInt32BigEndian(header[offset..]);
        return id is > 0 and <= int.MaxValue ? (int)id : null;
    }

    /// <summary>Reads the first sample description's <c>logs</c> box, whose payload is the bare identifier.</summary>
    private static string ReadIdentifier(ReadOnlySpan<byte> descriptions)
    {
        if (descriptions.Length < 8)
            return null;

        int offset = 8;
        if (!ExifBlock.TryReadBox(descriptions, ref offset, out _, out ReadOnlySpan<byte> entry) ||
            entry.Length < VisualSampleEntryFieldsSize)
            return null;

        ReadOnlySpan<byte> children = entry[VisualSampleEntryFieldsSize..];
        offset = 0;
        while (ExifBlock.TryReadBox(children, ref offset, out uint type, out ReadOnlySpan<byte> payload))
        {
            if (type != Code("logs"u8))
                continue;

            int terminator = payload.IndexOf((byte)0);
            if (terminator >= 0)
                payload = payload[..terminator];
            if (payload.IsEmpty || payload.Length > MaximumIdentifierLength)
                return null;
            foreach (byte character in payload)
                if (character is < 0x21 or > 0x7E)
                    return null;
            return Encoding.ASCII.GetString(payload);
        }

        return null;
    }

    private static uint Code(ReadOnlySpan<byte> name) => BinaryPrimitives.ReadUInt32BigEndian(name);
}
