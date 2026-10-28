using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Remote;

/// <summary>Kinds of record a server sends down a packet socket.</summary>
public enum PacketStreamRecordType : byte
{
    /// <summary>Everything the client needs to stand its decoders up. Payload is JSON.</summary>
    Manifest = 1,

    /// <summary>
    /// Every record after this one belongs to the run named in its payload. A client that has asked
    /// to play elsewhere discards what arrives until it sees its own request named here.
    /// </summary>
    RunStarted = 2,

    /// <summary>One compressed packet, timestamps in its track's own time base.</summary>
    Packet = 3,

    /// <summary>The named track has no more packets; its decoder can be drained.</summary>
    EndOfTrack = 4,

    /// <summary>Every selected track has ended.</summary>
    EndOfStream = 5,

    /// <summary>The run failed. The socket stays usable: another run may still be asked for.</summary>
    Error = 6,

    /// <summary>Side data for the packet with the same track and timestamps that follows it.</summary>
    PacketSideData = 7,
}

/// <summary>Kinds of packet side data carried independently of the compressed bitstream.</summary>
public enum PacketStreamSideDataType : byte
{
    /// <summary>SMPTE ST 2094-40 metadata encoded as its canonical T.35 payload.</summary>
    Hdr10Plus = 1,
}

/// <summary>Kinds of message a client sends up a packet socket.</summary>
public enum PacketStreamRequestType : byte
{
    /// <summary>Names the media and proves the client may read it. Payload is JSON.</summary>
    Hello = 1,

    /// <summary>Plays a set of tracks from a position, retiring whatever run is in flight.</summary>
    Play = 2,

    /// <summary>Releases bytes the client has consumed, letting the server send that many more.</summary>
    Credit = 3,
}

/// <summary>
/// Framing for the packet socket a server sends when it demuxes on a client's behalf.
/// </summary>
/// <remarks>
/// <para>
/// One record per WebSocket message, so the message boundary is the length and only the four header
/// bytes are spent on framing. An unknown record type is skipped whole, which is the only thing
/// keeping a client of this version usable against a server that has learned to send something new.
/// </para>
/// <para>
/// Flow control is explicit and travels the other way. A client grants the server a number of bytes
/// it is willing to hold, and the server sends no more than that until the client releases what it
/// has consumed. Letting the transport push back instead is not available to us: browsers do not
/// extend receive backpressure to script, so a socket the client stops reading keeps filling with
/// the response somewhere the client cannot see or bound.
/// </para>
/// </remarks>
public static class PacketStreamProtocol
{
    /// <summary>Names the wire format during the WebSocket handshake, so a mismatch fails to connect.</summary>
    public const string SubProtocol = "lightplayer.packets.v2";

    /// <summary>Type, flags, track.</summary>
    public const int RecordHeaderSize = 4;

    /// <summary>Pts, dts, duration.</summary>
    public const int PacketHeaderSize = 24;

    /// <summary>Pts and dts identifying the packet to which a side-data record belongs.</summary>
    public const int PacketSideDataHeaderSize = 16;

    /// <summary>Largest ST 2094-40 payload FFmpeg accepts for one packet.</summary>
    public const int MaximumPacketSideDataSize = 907;

    /// <summary>Largest message either end will accept, which caps what a bad peer can allocate.</summary>
    public const int MaximumMessageSize = 64 * 1024 * 1024;

    /// <summary>
    /// Largest message a server will accept. A client only ever sends control messages, so this is
    /// far below what it may receive.
    /// </summary>
    public const int MaximumRequestSize = 64 * 1024;

    public const byte KeyFrameFlag = 0x01;

    /// <summary>Value a timestamp takes when the container states none. Matches <c>AV_NOPTS_VALUE</c>.</summary>
    public const long NoTimestamp = long.MinValue;

    /// <summary>
    /// Bytes a client lets the server run ahead by. This is the whole of what is in flight and
    /// queued, so it is sized to ride out a jittery link rather than to hold a stretch of playback.
    /// </summary>
    public const int DefaultCreditBytes = 4 * 1024 * 1024;

    /// <summary>Consumed bytes are released once they reach this fraction of the window.</summary>
    public const int CreditReleaseDivisor = 4;

    public static void WriteRecordHeader(Span<byte> destination, PacketStreamRecordType type, byte flags, int trackId)
    {
        destination[0] = (byte)type;
        destination[1] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)trackId);
    }

    public static (PacketStreamRecordType Type, byte Flags, int TrackId) ReadRecordHeader(ReadOnlySpan<byte> message)
    {
        if (message.Length < RecordHeaderSize)
        {
            throw new InvalidDataException("A packet socket message is too short to hold a record header.");
        }

        return ((PacketStreamRecordType)message[0], message[1], BinaryPrimitives.ReadUInt16LittleEndian(message[2..]));
    }

    public static void WritePacketHeader(Span<byte> destination, long pts, long dts, long duration)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, pts);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], dts);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], duration);
    }

    public static (long Pts, long Dts, long Duration) ReadPacketHeader(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < PacketHeaderSize)
        {
            throw new InvalidDataException("A packet record is too short to hold its own header.");
        }

        return (
            BinaryPrimitives.ReadInt64LittleEndian(payload),
            BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(payload[16..]));
    }

    public static void WritePacketSideDataHeader(Span<byte> destination, long pts, long dts)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, pts);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], dts);
    }

    public static (long Pts, long Dts) ReadPacketSideDataHeader(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < PacketSideDataHeaderSize)
        {
            throw new InvalidDataException("A packet side-data record is too short to identify its packet.");
        }

        return (
            BinaryPrimitives.ReadInt64LittleEndian(payload),
            BinaryPrimitives.ReadInt64LittleEndian(payload[8..]));
    }

    /// <summary>What a packet of <paramref name="bitstreamLength"/> costs against the credit window.</summary>
    /// <remarks>
    /// Side-data records are deliberately excluded: v1 clients skip unknown records and therefore
    /// cannot release their cost. Their standards-defined maximum keeps them bounded by the packet
    /// queue instead.
    /// </remarks>
    public static int PacketCost(int bitstreamLength) => RecordHeaderSize + PacketHeaderSize + bitstreamLength;

    public static byte[] EncodeError(string code, string message)
    {
        var codeBytes = Encoding.UTF8.GetBytes(code ?? string.Empty);
        var messageBytes = Encoding.UTF8.GetBytes(message ?? string.Empty);
        var payload = new byte[2 + codeBytes.Length + messageBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)codeBytes.Length);
        codeBytes.CopyTo(payload, 2);
        messageBytes.CopyTo(payload, 2 + codeBytes.Length);
        return payload;
    }

    public static (string Code, string Message) DecodeError(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            return (string.Empty, string.Empty);
        }

        int codeLength = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(payload), payload.Length - 2);
        return (
            Encoding.UTF8.GetString(payload.Slice(2, codeLength)),
            Encoding.UTF8.GetString(payload[(2 + codeLength)..]));
    }

    public static uint ReadRunId(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0;

    public static byte[] EncodeHello(ReadOnlySpan<byte> json)
    {
        var message = new byte[1 + json.Length];
        message[0] = (byte)PacketStreamRequestType.Hello;
        json.CopyTo(message.AsSpan(1));
        return message;
    }

    /// <param name="runId">Names the run, so its records can be told from those of the run it replaces.</param>
    /// <param name="credit">Bytes the client is willing to hold, counted afresh from this run.</param>
    public static byte[] EncodePlay(uint runId, double startSeconds, ReadOnlySpan<int> trackIds, int credit)
    {
        var message = new byte[19 + (trackIds.Length * 2)];
        message[0] = (byte)PacketStreamRequestType.Play;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), runId);
        BinaryPrimitives.WriteDoubleLittleEndian(message.AsSpan(5), startSeconds);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(13), credit);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(17), (ushort)trackIds.Length);
        for (int i = 0; i < trackIds.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(19 + (i * 2)), (ushort)trackIds[i]);
        }

        return message;
    }

    public static (uint RunId, double StartSeconds, int Credit, int[] TrackIds) DecodePlay(ReadOnlySpan<byte> message)
    {
        if (message.Length < 19)
        {
            throw new InvalidDataException("A play message is too short.");
        }

        uint runId = BinaryPrimitives.ReadUInt32LittleEndian(message[1..]);
        double start = BinaryPrimitives.ReadDoubleLittleEndian(message[5..]);
        int credit = BinaryPrimitives.ReadInt32LittleEndian(message[13..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(message[17..]);
        if (message.Length < 19 + (count * 2))
        {
            throw new InvalidDataException("A play message names more tracks than it carries.");
        }

        var tracks = new int[count];
        for (int i = 0; i < count; i++)
        {
            tracks[i] = BinaryPrimitives.ReadUInt16LittleEndian(message[(19 + (i * 2))..]);
        }

        return (runId, start, credit, tracks);
    }

    public static byte[] EncodeCredit(int bytes)
    {
        var message = new byte[5];
        message[0] = (byte)PacketStreamRequestType.Credit;
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(1), bytes);
        return message;
    }

    public static int DecodeCredit(ReadOnlySpan<byte> message)
    {
        if (message.Length < 5)
        {
            throw new InvalidDataException("A credit message is too short.");
        }

        return BinaryPrimitives.ReadInt32LittleEndian(message[1..]);
    }
}

public interface IPacketStreamWriter
{
    Task WriteManifestAsync(ReadOnlyMemory<byte> json);

    Task WritePacketAsync(
        int trackId,
        long pts,
        long dts,
        long duration,
        bool isKeyFrame,
        IntPtr bitstream,
        int length,
        ReadOnlyMemory<byte> hdr10PlusMetadata = default);

    Task WriteEndOfTrackAsync(int trackId);

    Task WriteEndOfStreamAsync();

    Task WriteErrorAsync(string code, string message);
}

/// <summary>Length-prefixed packet records carried by a streaming HTTP response.</summary>
public static class HttpPacketStreamProtocol
{
    public const uint Magic = 0x5350504C;
    public const ushort Version = 2;
    public const int PreambleSize = 8;
    public const int RecordHeaderSize = 8;
    public const int MaximumPayloadSize = PacketStreamProtocol.MaximumMessageSize;

    public static void WritePreamble(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], 0);
    }

    public static void WriteRecordHeader(
        Span<byte> destination,
        PacketStreamRecordType type,
        byte flags,
        int trackId,
        int payloadLength)
    {
        destination[0] = (byte)type;
        destination[1] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)trackId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)payloadLength);
    }
}

public sealed class HttpPacketStreamReader : IDisposable
{
    // Records are read a few bytes at a time, and in a browser every read of a response stream is a
    // round trip through script and the event loop, so the response is read in large pieces.
    private const int ReadBufferSize = 512 * 1024;

    private readonly Stream source;
    private readonly byte[] header = new byte[HttpPacketStreamProtocol.RecordHeaderSize];
    private byte[] payload = new byte[64 * 1024];
    private int payloadLength;
    private bool disposed;

    private HttpPacketStreamReader(Stream source) => this.source = source;

    public static async Task<HttpPacketStreamReader> CreateAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        source = new BufferedStream(source, ReadBufferSize);
        var preamble = new byte[HttpPacketStreamProtocol.PreambleSize];
        await source.ReadExactlyAsync(preamble, cancellationToken).ConfigureAwait(false);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(preamble);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(preamble.AsSpan(4));
        if (magic != HttpPacketStreamProtocol.Magic)
        {
            throw new InvalidDataException("The response is not a Light Media Center packet stream.");
        }

        if (version != HttpPacketStreamProtocol.Version)
        {
            throw new InvalidDataException($"Packet stream version {version} is not supported.");
        }

        return new HttpPacketStreamReader(source);
    }

    public PacketStreamRecordType LastType { get; private set; }

    public byte LastFlags { get; private set; }

    public int LastTrackId { get; private set; }

    public ReadOnlySpan<byte> LastPayload => payload.AsSpan(0, payloadLength);

    public async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int read = await ReadAtMostAsync(header, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return false;
        }

        if (read < header.Length)
        {
            throw new EndOfStreamException("The packet stream ended inside a record header.");
        }

        LastType = (PacketStreamRecordType)header[0];
        LastFlags = header[1];
        LastTrackId = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        if (length > HttpPacketStreamProtocol.MaximumPayloadSize)
        {
            throw new InvalidDataException($"A packet stream record claims {length} bytes.");
        }

        if (payload.Length < length)
        {
            payload = new byte[Math.Max(length, payload.Length * 2)];
        }

        if (length > 0)
        {
            await source.ReadExactlyAsync(payload.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }

        payloadLength = length;
        return true;
    }

    private async Task<int> ReadAtMostAsync(byte[] destination, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = await source
                .ReadAsync(destination.AsMemory(total, destination.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        source.Dispose();
    }
}

public sealed class HttpPacketStreamWriter(Stream destination, CancellationToken cancellationToken)
    : IPacketStreamWriter
{
    private readonly byte[] header = new byte[
        HttpPacketStreamProtocol.RecordHeaderSize + PacketStreamProtocol.PacketHeaderSize];
    private byte[] scratch = new byte[64 * 1024];

    public async Task WritePreambleAsync()
    {
        var preamble = new byte[HttpPacketStreamProtocol.PreambleSize];
        HttpPacketStreamProtocol.WritePreamble(preamble);
        await destination.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
    }

    public async Task WritePacketAsync(
        int trackId,
        long pts,
        long dts,
        long duration,
        bool isKeyFrame,
        IntPtr bitstream,
        int length,
        ReadOnlyMemory<byte> hdr10PlusMetadata = default)
    {
        if (hdr10PlusMetadata.Length > PacketStreamProtocol.MaximumPacketSideDataSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hdr10PlusMetadata),
                "The HDR10+ metadata exceeds the packet side-data limit.");
        }

        if (scratch.Length < length)
        {
            scratch = new byte[Math.Max(length, scratch.Length * 2)];
        }

        System.Runtime.InteropServices.Marshal.Copy(bitstream, scratch, 0, length);
        if (!hdr10PlusMetadata.IsEmpty)
        {
            int sideDataPayloadLength = PacketStreamProtocol.PacketSideDataHeaderSize + hdr10PlusMetadata.Length;
            HttpPacketStreamProtocol.WriteRecordHeader(
                header,
                PacketStreamRecordType.PacketSideData,
                (byte)PacketStreamSideDataType.Hdr10Plus,
                trackId,
                sideDataPayloadLength);
            PacketStreamProtocol.WritePacketSideDataHeader(
                header.AsSpan(HttpPacketStreamProtocol.RecordHeaderSize),
                pts,
                dts);
            await destination
                .WriteAsync(
                    header.AsMemory(
                        0,
                        HttpPacketStreamProtocol.RecordHeaderSize + PacketStreamProtocol.PacketSideDataHeaderSize),
                    cancellationToken)
                .ConfigureAwait(false);
            await destination.WriteAsync(hdr10PlusMetadata, cancellationToken).ConfigureAwait(false);
        }

        int payloadLength = PacketStreamProtocol.PacketHeaderSize + length;
        HttpPacketStreamProtocol.WriteRecordHeader(
            header,
            PacketStreamRecordType.Packet,
            isKeyFrame ? PacketStreamProtocol.KeyFrameFlag : (byte)0,
            trackId,
            payloadLength);
        PacketStreamProtocol.WritePacketHeader(
            header.AsSpan(HttpPacketStreamProtocol.RecordHeaderSize),
            pts,
            dts,
            duration);
        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(scratch.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
    }

    public Task WriteManifestAsync(ReadOnlyMemory<byte> json) =>
        WriteControlAsync(PacketStreamRecordType.Manifest, 0, json);

    public Task WriteEndOfTrackAsync(int trackId) =>
        WriteControlAsync(PacketStreamRecordType.EndOfTrack, trackId, ReadOnlyMemory<byte>.Empty);

    public Task WriteEndOfStreamAsync() =>
        WriteControlAsync(PacketStreamRecordType.EndOfStream, 0, ReadOnlyMemory<byte>.Empty);

    public Task WriteErrorAsync(string code, string message) =>
        WriteControlAsync(PacketStreamRecordType.Error, 0, PacketStreamProtocol.EncodeError(code, message));

    private async Task WriteControlAsync(
        PacketStreamRecordType type,
        int trackId,
        ReadOnlyMemory<byte> payload)
    {
        HttpPacketStreamProtocol.WriteRecordHeader(header, type, 0, trackId, payload.Length);
        await destination
            .WriteAsync(header.AsMemory(0, HttpPacketStreamProtocol.RecordHeaderSize), cancellationToken)
            .ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await destination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Receives whole messages from a packet socket, growing one buffer to fit the largest seen.
/// </summary>
/// <remarks>
/// A WebSocket permits one receive at a time, so one of these belongs to one socket for its life.
/// </remarks>
/// <param name="maximumMessageSize">
/// What the peer is allowed to make this end hold. A server takes only small requests; a client has
/// to take whatever a packet turns out to weigh.
/// </param>
public sealed class PacketSocketReader(WebSocket socket, int maximumMessageSize = PacketStreamProtocol.MaximumMessageSize)
{
    private byte[] buffer = new byte[Math.Min(64 * 1024, maximumMessageSize)];
    private int length;

    /// <summary>The message just received. Replaced by the following call.</summary>
    public ReadOnlyMemory<byte> Message => buffer.AsMemory(0, length);

    /// <summary>Receives the next message. False once the peer has closed.</summary>
    public async Task<bool> ReceiveAsync(CancellationToken cancellationToken)
    {
        int total = 0;
        while (true)
        {
            if (total == buffer.Length)
            {
                if (buffer.Length >= maximumMessageSize)
                {
                    throw new InvalidDataException("A packet socket message exceeded the size limit.");
                }

                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, maximumMessageSize));
            }

            ValueWebSocketReceiveResult result = await socket
                .ReceiveAsync(buffer.AsMemory(total), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                length = 0;
                return false;
            }

            total += result.Count;
            if (result.EndOfMessage)
            {
                length = total;
                return true;
            }
        }
    }
}

/// <summary>
/// Writes records into a packet socket. Sends are serialized here because a WebSocket allows only
/// one in flight and the run producing packets is not the only thing that speaks.
/// </summary>
/// <remarks>
/// No send is cancelable, and that is deliberate: canceling one aborts the whole socket, which
/// would make every retired run -- every seek -- take the connection down with it. A send is bounded
/// by the credit the peer granted, and closing the socket is what releases one that is stuck.
/// </remarks>
/// <param name="connectionLifetime">
/// Canceled when the socket is going away, so a sender queued behind another stops waiting.
/// </param>
public sealed class PacketSocketWriter(WebSocket socket, CancellationToken connectionLifetime)
    : IPacketStreamWriter, IDisposable
{
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private byte[] scratch = new byte[64 * 1024];

    public Task WriteManifestAsync(ReadOnlyMemory<byte> json) =>
        WriteRecordAsync(PacketStreamRecordType.Manifest, 0, 0, json);

    public Task WriteRunStartedAsync(uint runId)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, runId);
        return WriteRecordAsync(PacketStreamRecordType.RunStarted, 0, 0, payload);
    }

    /// <summary>
    /// Writes a packet whose payload is still in FFmpeg's own buffer. The copy is the price of
    /// handing bytes to a socket and is one per packet.
    /// </summary>
    public async Task WritePacketAsync(
        int trackId,
        long pts,
        long dts,
        long duration,
        bool isKeyFrame,
        IntPtr bitstream,
        int length,
        ReadOnlyMemory<byte> hdr10PlusMetadata = default)
    {
        if (hdr10PlusMetadata.Length > PacketStreamProtocol.MaximumPacketSideDataSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hdr10PlusMetadata),
                "The HDR10+ metadata exceeds the packet side-data limit.");
        }

        int packetSize = PacketStreamProtocol.RecordHeaderSize + PacketStreamProtocol.PacketHeaderSize + length;
        int sideDataSize = hdr10PlusMetadata.IsEmpty
            ? 0
            : PacketStreamProtocol.RecordHeaderSize +
                PacketStreamProtocol.PacketSideDataHeaderSize +
                hdr10PlusMetadata.Length;
        await sendGate.WaitAsync(connectionLifetime).ConfigureAwait(false);
        try
        {
            if (scratch.Length < Math.Max(packetSize, sideDataSize))
            {
                scratch = new byte[Math.Max(Math.Max(packetSize, sideDataSize), scratch.Length * 2)];
            }

            if (!hdr10PlusMetadata.IsEmpty)
            {
                PacketStreamProtocol.WriteRecordHeader(
                    scratch,
                    PacketStreamRecordType.PacketSideData,
                    (byte)PacketStreamSideDataType.Hdr10Plus,
                    trackId);
                PacketStreamProtocol.WritePacketSideDataHeader(
                    scratch.AsSpan(PacketStreamProtocol.RecordHeaderSize),
                    pts,
                    dts);
                hdr10PlusMetadata.Span.CopyTo(
                    scratch.AsSpan(
                        PacketStreamProtocol.RecordHeaderSize + PacketStreamProtocol.PacketSideDataHeaderSize));
                await socket
                    .SendAsync(
                        scratch.AsMemory(0, sideDataSize),
                        WebSocketMessageType.Binary,
                        true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            PacketStreamProtocol.WriteRecordHeader(
                scratch,
                PacketStreamRecordType.Packet,
                isKeyFrame ? PacketStreamProtocol.KeyFrameFlag : (byte)0,
                trackId);
            PacketStreamProtocol.WritePacketHeader(
                scratch.AsSpan(PacketStreamProtocol.RecordHeaderSize),
                pts,
                dts,
                duration);
            System.Runtime.InteropServices.Marshal.Copy(
                bitstream,
                scratch,
                PacketStreamProtocol.RecordHeaderSize + PacketStreamProtocol.PacketHeaderSize,
                length);

            await socket
                .SendAsync(scratch.AsMemory(0, packetSize), WebSocketMessageType.Binary, true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    public Task WriteEndOfTrackAsync(int trackId) =>
        WriteRecordAsync(PacketStreamRecordType.EndOfTrack, 0, trackId, ReadOnlyMemory<byte>.Empty);

    public Task WriteEndOfStreamAsync() =>
        WriteRecordAsync(PacketStreamRecordType.EndOfStream, 0, 0, ReadOnlyMemory<byte>.Empty);

    public Task WriteErrorAsync(string code, string message) =>
        WriteRecordAsync(
            PacketStreamRecordType.Error,
            0,
            0,
            PacketStreamProtocol.EncodeError(code, message));

    private async Task WriteRecordAsync(
        PacketStreamRecordType type,
        byte flags,
        int trackId,
        ReadOnlyMemory<byte> payload)
    {
        await sendGate.WaitAsync(connectionLifetime).ConfigureAwait(false);
        try
        {
            int size = PacketStreamProtocol.RecordHeaderSize + payload.Length;
            if (scratch.Length < size)
            {
                scratch = new byte[Math.Max(size, scratch.Length * 2)];
            }

            PacketStreamProtocol.WriteRecordHeader(scratch, type, flags, trackId);
            payload.Span.CopyTo(scratch.AsSpan(PacketStreamProtocol.RecordHeaderSize));
            await socket
                .SendAsync(scratch.AsMemory(0, size), WebSocketMessageType.Binary, true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    public void Dispose() => sendGate.Dispose();
}

/// <summary>
/// The bytes a server may still send before the client has to release more.
/// </summary>
/// <remarks>
/// A run may overshoot by at most the one packet that took the credit below zero, which keeps a
/// packet larger than the whole window from deadlocking the stream.
/// </remarks>
public sealed class PacketCreditGate
{
    private readonly object sync = new();
    private long available;
    private TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Reset(long credit)
    {
        lock (sync)
        {
            available = credit;
            ReleaseWaiter();
        }
    }

    public void Add(long credit)
    {
        lock (sync)
        {
            available += credit;
            ReleaseWaiter();
        }
    }

    public void Take(long bytes)
    {
        lock (sync)
        {
            available -= bytes;
        }
    }

    /// <summary>Returns once anything at all may be sent.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task pending;
            lock (sync)
            {
                if (available > 0)
                {
                    return;
                }

                pending = waiter.Task;
            }

            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ReleaseWaiter()
    {
        if (available <= 0)
        {
            return;
        }

        TaskCompletionSource previous = waiter;
        waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }
}
