using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Desktop.Services.BrowserLink;

/// <summary>Constants of the browser link protocol, version 1.</summary>
internal static class BrowserLinkProtocol
{
    public const string Path = "/browser-link/v1";
    public const string SubProtocol = "lightplayer.browser-link.v1";
    public const int Version = 1;

    public const int ProtocolError = 4400;
    public const int Unauthorized = 4401;
    public const int PairingRejected = 4403;
    public const int Replaced = 4409;

    public static string ReasonName(BrowserSessionEndReason reason) => reason switch
    {
        BrowserSessionEndReason.Stopped => "stopped",
        BrowserSessionEndReason.Replaced => "replaced",
        BrowserSessionEndReason.Unauthorized => "unauthorized",
        BrowserSessionEndReason.Encrypted => "encrypted",
        BrowserSessionEndReason.Unsupported => "unsupported",
        _ => "error",
    };
}

/// <summary>A message the peer sent that the protocol does not allow; closes the socket with 4400.</summary>
internal sealed class BrowserLinkProtocolException(string message) : Exception(message);

internal abstract record BrowserLinkMessage;

internal sealed record PairMessage(string Code, string Browser, string ExtensionVersion) : BrowserLinkMessage;

internal sealed record HelloMessage(string Token, string ExtensionVersion) : BrowserLinkMessage;

internal sealed record OpenMessage(
    string Kind,
    string Title,
    string PageUrl,
    double StartTime,
    bool Paused,
    double Rate,
    double Volume,
    bool Muted,
    bool? Live,
    IReadOnlyList<string> Urls,
    string? Referer,
    string? Origin,
    string? UserAgent) : BrowserLinkMessage;

internal sealed record CommandMessage(BrowserElementCommand Command) : BrowserLinkMessage;

internal sealed record PongMessage : BrowserLinkMessage;

internal sealed record CloseMessage(string Reason) : BrowserLinkMessage;

/// <summary>A member that may be absent (left as it is), null (unbounded) or a value.</summary>
internal readonly record struct OptionalBound(bool IsPresent, double? Value);

/// <summary>Messages that feed the append-driven source of a <c>source</c> session.</summary>
internal abstract record SourceMessage : BrowserLinkMessage;

internal sealed record AddSourceBufferMessage(uint Buffer, string MimeType) : SourceMessage;

internal sealed record RemoveSourceBufferMessage(uint Buffer) : SourceMessage;

internal sealed record ChangeTypeMessage(uint Buffer, string MimeType) : SourceMessage;

internal sealed record ConfigureMessage(
    uint Buffer,
    OptionalBound TimestampOffset,
    OptionalBound AppendWindowStart,
    OptionalBound AppendWindowEnd) : SourceMessage;

internal sealed record RemoveMessage(uint Buffer, long Request, double Start, double? End) : SourceMessage;

internal sealed record AbortMessage(uint Buffer) : SourceMessage;

internal sealed record DurationMessage(double? Value) : SourceMessage;

/// <param name="Error"><c>network</c>, <c>decode</c>, or null for a normal end.</param>
internal sealed record EndOfStreamMessage(string? Error) : SourceMessage;

internal sealed record TimelineMessage(bool Live, double SeekableStart, double SeekableEnd) : SourceMessage;

/// <summary>One <c>appendBuffer</c> call, carried by a binary frame.</summary>
internal readonly record struct AppendFrame(uint Buffer, uint Sequence, ReadOnlyMemory<byte> Bytes)
{
    public const int HeaderSize = 8;

    public static AppendFrame Parse(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length < HeaderSize)
            throw new BrowserLinkProtocolException("A binary frame is shorter than its header.");

        ReadOnlySpan<byte> header = frame.Span;
        return new AppendFrame(
            BinaryPrimitives.ReadUInt32LittleEndian(header),
            BinaryPrimitives.ReadUInt32LittleEndian(header[4..]),
            frame[HeaderSize..]);
    }
}

/// <summary>Reads the JSON text frames of the protocol into typed messages.</summary>
internal static class BrowserLinkMessageReader
{
    public static BrowserLinkMessage Read(ReadOnlyMemory<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new BrowserLinkProtocolException($"A text frame is not JSON: {exception.Message}");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new BrowserLinkProtocolException("A text frame is not a JSON object.");

            string type = RequiredString(root, "type");
            return type switch
            {
                "pair" => new PairMessage(
                    RequiredString(root, "code"),
                    OptionalString(root, "browser") ?? string.Empty,
                    OptionalString(root, "extensionVersion") ?? string.Empty),
                "hello" => new HelloMessage(
                    RequiredString(root, "token"),
                    OptionalString(root, "extensionVersion") ?? string.Empty),
                "open" => ReadOpen(root),
                "play" => Command(root, BrowserElementCommandKind.Play),
                "pause" => Command(root, BrowserElementCommandKind.Pause),
                "seek" => Command(root, BrowserElementCommandKind.Seek, RequiredNumber(root, "time")),
                "rate" => Command(root, BrowserElementCommandKind.Rate, RequiredNumber(root, "value")),
                "volume" => Command(
                    root,
                    BrowserElementCommandKind.Volume,
                    RequiredNumber(root, "value"),
                    OptionalBoolean(root, "muted") ?? false),
                "pong" => new PongMessage(),
                "close" => new CloseMessage(OptionalString(root, "reason") ?? string.Empty),
                "addSourceBuffer" => new AddSourceBufferMessage(
                    RequiredBuffer(root),
                    RequiredString(root, "mimeType")),
                "removeSourceBuffer" => new RemoveSourceBufferMessage(RequiredBuffer(root)),
                "changeType" => new ChangeTypeMessage(RequiredBuffer(root), RequiredString(root, "mimeType")),
                "configure" => new ConfigureMessage(
                    RequiredBuffer(root),
                    Bound(root, "timestampOffset"),
                    Bound(root, "appendWindowStart"),
                    Bound(root, "appendWindowEnd")),
                "remove" => new RemoveMessage(
                    RequiredBuffer(root),
                    (long)RequiredNumber(root, "request"),
                    RequiredNumber(root, "start"),
                    NullableNumber(root, "end")),
                "abort" => new AbortMessage(RequiredBuffer(root)),
                "duration" => new DurationMessage(NullableNumber(root, "value")),
                "endOfStream" => new EndOfStreamMessage(OptionalString(root, "error")),
                "timeline" => new TimelineMessage(
                    OptionalBoolean(root, "live") ?? false,
                    RequiredNumber(root, "seekableStart"),
                    RequiredNumber(root, "seekableEnd")),
                _ => throw new BrowserLinkProtocolException($"Unknown message type '{type}'."),
            };
        }
    }

    private static OpenMessage ReadOpen(JsonElement root)
    {
        var urls = new List<string>();
        if (root.TryGetProperty("urls", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement url in list.EnumerateArray())
            {
                if (url.ValueKind != JsonValueKind.String)
                    throw new BrowserLinkProtocolException("'urls' holds something other than strings.");
                urls.Add(url.GetString()!);
            }
        }

        string? referer = null, origin = null, userAgent = null;
        if (root.TryGetProperty("headers", out JsonElement headers) && headers.ValueKind == JsonValueKind.Object)
        {
            referer = OptionalString(headers, "referer");
            origin = OptionalString(headers, "origin");
            userAgent = OptionalString(headers, "userAgent");
        }

        return new OpenMessage(
            RequiredString(root, "kind"),
            OptionalString(root, "title") ?? string.Empty,
            OptionalString(root, "pageUrl") ?? string.Empty,
            OptionalNumber(root, "startTime") ?? 0,
            OptionalBoolean(root, "paused") ?? false,
            OptionalNumber(root, "rate") ?? 1,
            OptionalNumber(root, "volume") ?? 1,
            OptionalBoolean(root, "muted") ?? false,
            OptionalBoolean(root, "live"),
            urls,
            referer,
            origin,
            userAgent);
    }

    private static CommandMessage Command(
        JsonElement root,
        BrowserElementCommandKind kind,
        double value = 0,
        bool muted = false)
    {
        double number = RequiredNumber(root, "command");
        if (number < 1 || number != Math.Floor(number))
            throw new BrowserLinkProtocolException("'command' must be a positive whole number.");
        return new CommandMessage(new BrowserElementCommand((long)number, kind, value, muted));
    }

    private static uint RequiredBuffer(JsonElement root)
    {
        double number = RequiredNumber(root, "buffer");
        if (number < 0 || number > uint.MaxValue || number != Math.Floor(number))
            throw new BrowserLinkProtocolException("'buffer' must be a 32-bit unsigned whole number.");
        return (uint)number;
    }

    private static string RequiredString(JsonElement root, string name) =>
        OptionalString(root, name) ?? throw new BrowserLinkProtocolException($"'{name}' is missing.");

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double RequiredNumber(JsonElement root, string name) =>
        OptionalNumber(root, name) ?? throw new BrowserLinkProtocolException($"'{name}' is missing.");

    private static double? OptionalNumber(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double number) && double.IsFinite(number)
            ? number
            : null;

    private static double? NullableNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return OptionalNumber(root, name) ?? throw new BrowserLinkProtocolException($"'{name}' is not a number.");
    }

    private static OptionalBound Bound(JsonElement root, string name) =>
        root.TryGetProperty(name, out _) ? new OptionalBound(true, NullableNumber(root, name)) : default;

    private static bool? OptionalBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}

/// <summary>Writes the JSON text frames the application sends.</summary>
internal static class BrowserLinkMessageWriter
{
    public static byte[] Paired(string token, string appVersion) => Write(writer =>
    {
        writer.WriteString("type", "paired");
        writer.WriteString("token", token);
        writer.WriteString("appVersion", appVersion);
    });

    public static byte[] Ready(string appVersion, BrowserLinkCapabilities capabilities) => Write(writer =>
    {
        writer.WriteString("type", "ready");
        writer.WriteString("appVersion", appVersion);
        writer.WriteNumber("protocol", BrowserLinkProtocol.Version);
        writer.WriteNumber("budgetBytes", capabilities.BudgetBytes);
        writer.WriteStartArray("containers");
        foreach (string container in capabilities.Containers)
            writer.WriteStringValue(container);
        writer.WriteEndArray();
        writer.WriteStartArray("video");
        foreach (BrowserLinkVideoCodec codec in capabilities.Video)
        {
            writer.WriteStartObject();
            writer.WriteString("codec", codec.Codec);
            writer.WriteNumber("maxWidth", codec.MaxWidth);
            writer.WriteNumber("maxHeight", codec.MaxHeight);
            writer.WriteStartArray("bitDepths");
            foreach (int depth in codec.BitDepths)
                writer.WriteNumberValue(depth);
            writer.WriteEndArray();
            writer.WriteBoolean("hardware", codec.Hardware);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("audio");
        foreach (string codec in capabilities.Audio)
        {
            writer.WriteStartObject();
            writer.WriteString("codec", codec);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteBoolean("hdr", capabilities.Hdr);
    });

    public static byte[] State(BrowserElementState state) => Write(writer =>
    {
        writer.WriteString("type", "state");
        writer.WriteNumber("ack", state.Ack);
        writer.WriteNumber("time", Finite(state.Time));
        writer.WriteBoolean("paused", state.Paused);
        writer.WriteNumber("rate", Finite(state.Rate));
        writer.WriteNumber("volume", Finite(state.Volume));
        writer.WriteBoolean("muted", state.Muted);
        writer.WriteNumber("readyState", state.ReadyState);
        writer.WriteBoolean("seeking", state.Seeking);
        writer.WriteBoolean("ended", state.Ended);
        if (state.Duration is { } duration && double.IsFinite(duration))
            writer.WriteNumber("duration", duration);
        else
            writer.WriteNull("duration");
        writer.WriteNumber("videoWidth", state.VideoWidth);
        writer.WriteNumber("videoHeight", state.VideoHeight);
        writer.WriteNumber("totalFrames", state.TotalFrames);
        writer.WriteNumber("droppedFrames", state.DroppedFrames);
        writer.WriteNumber("viewWidth", Finite(state.ViewWidth));
        writer.WriteNumber("viewHeight", Finite(state.ViewHeight));
        writer.WriteNumber("devicePixelRatio", Finite(state.DevicePixelRatio));
    });

    public static byte[] Appended(AppendedReply reply) => Write(writer =>
    {
        writer.WriteString("type", "appended");
        writer.WriteNumber("buffer", reply.Buffer);
        writer.WriteNumber("sequence", reply.Sequence);
        WriteRanges(writer, reply.Buffered);
        writer.WriteBoolean("full", reply.Full);
        if (reply.Tracks is { } tracks)
        {
            writer.WriteStartArray("tracks");
            foreach (AppendedTrack track in tracks)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", track.Id);
                writer.WriteString("kind", track.Kind);
                writer.WriteString("codec", track.Codec);
                if (track.Kind == "video")
                {
                    writer.WriteNumber("width", track.Width);
                    writer.WriteNumber("height", track.Height);
                }
                else
                {
                    writer.WriteNumber("sampleRate", track.SampleRate);
                    writer.WriteNumber("channels", track.Channels);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
    });

    public static byte[] AppendFailed(AppendFailedReply reply) => Write(writer =>
    {
        writer.WriteString("type", "appendFailed");
        writer.WriteNumber("buffer", reply.Buffer);
        writer.WriteNumber("sequence", reply.Sequence);
        writer.WriteString("reason", reply.Reason switch
        {
            AppendFailureReason.Unsupported => "unsupported",
            AppendFailureReason.Encrypted => "encrypted",
            _ => "decode",
        });
    });

    public static byte[] Removed(RemovedReply reply) => Write(writer =>
    {
        writer.WriteString("type", "removed");
        writer.WriteNumber("buffer", reply.Buffer);
        writer.WriteNumber("request", reply.Request);
        WriteRanges(writer, reply.Buffered);
        writer.WriteBoolean("full", reply.Full);
    });

    public static byte[] Buffered(BufferedReply reply) => Write(writer =>
    {
        writer.WriteString("type", "buffered");
        writer.WriteNumber("buffer", reply.Buffer);
        WriteRanges(writer, reply.Buffered);
        writer.WriteBoolean("full", reply.Full);
    });

    public static byte[] Ping() => Write(writer => writer.WriteString("type", "ping"));

    public static byte[] Close(BrowserSessionEndReason reason, string? message) => Write(writer =>
    {
        writer.WriteString("type", "close");
        writer.WriteString("reason", BrowserLinkProtocol.ReasonName(reason));
        if (!string.IsNullOrEmpty(message))
            writer.WriteString("message", message);
    });

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    private static void WriteRanges(Utf8JsonWriter writer, IReadOnlyList<MediaTimeRange> ranges)
    {
        writer.WriteStartArray("buffered");
        foreach (MediaTimeRange range in ranges)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Finite(range.Start));
            writer.WriteNumberValue(Finite(range.End));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
