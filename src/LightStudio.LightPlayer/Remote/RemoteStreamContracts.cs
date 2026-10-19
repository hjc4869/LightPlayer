using System;
using System.Collections.Generic;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;

namespace LightStudio.LightPlayer.Remote;

/// <summary>
/// One playable elementary stream of a video, wherever it physically lives.
/// </summary>
/// <param name="Id">
/// Identity of the track, and the stream index the client's packets carry. The media's own streams
/// keep their container index; sidecar streams are numbered after them. The numbering is derived
/// from the current video and readable indexed sidecars within the source revision.
/// </param>
/// <param name="SourcePath">
/// Sidecar file the track lives in, or null when it belongs to the video itself.
/// </param>
/// <param name="StartTimeSeconds">
/// Origin of the container the track comes from. Packet timestamps arrive exactly as its demuxer
/// produced them, so this has to come off before they mean anything on the playback timeline.
/// </param>
/// <param name="DeliveryMode">
/// How the bytes are produced: <c>passthrough</c> for the container's own, <c>transcode</c> for a
/// track this server re-encoded.
/// </param>
/// <param name="VariantKey">
/// Opaque fingerprint of the source stamp and full output parameters. Independent encoded runs
/// still require separate decoder continuity even when their keys match.
/// </param>
public sealed record RemoteStreamTrack(
    int Id,
    string Kind,
    string CodecName,
    string Language,
    string Title,
    bool IsDefault,
    string? SourcePath,
    long DurationMilliseconds,
    double StartTimeSeconds,
    string DeliveryMode,
    string VariantKey,
    CodecParameters Parameters,
    int Width = 0,
    int Height = 0,
    double FrameRate = 0,
    int SampleRate = 0,
    int ChannelCount = 0,
    int StreamIndex = -1);

public sealed record RemoteStreamChapter(int Index, long StartMilliseconds, long EndMilliseconds, string Title);

/// <summary>
/// Source or output description. Each packet run announces its actual output before packets.
/// </summary>
/// <param name="Path">Library path the manifest describes.</param>
/// <param name="DefaultVideoTrackId">Track the server would play, or -1 for none.</param>
public sealed record RemoteStreamManifest(
    string Path,
    string Source,
    string FormatName,
    long DurationMilliseconds,
    bool CanSeek,
    int DefaultVideoTrackId,
    int DefaultAudioTrackId,
    RemoteStreamTrack[] Tracks,
    RemoteStreamChapter[] Chapters,
    Dictionary<string, string> Metadata)
{
    /// <summary>Container bitrate in bits per second, or zero when unknown.</summary>
    public long Bitrate { get; init; }

    public string SourceRevision { get; init; } = string.Empty;

    public RemoteTranscodeCapabilities? Transcoding { get; init; }
}

/// <summary>Opens a packet socket on one video.</summary>
/// <param name="Token">
/// The bearer token, which cannot travel in a header: a browser sets none on a WebSocket handshake,
/// and putting it in the URL would leave it in every log the connection passes through.
/// </param>
/// <param name="Transcode">
/// Exact tracks the client decided to re-encode and their output targets. Null asks for nothing.
/// </param>
public sealed record RemoteStreamHello(
    string Path,
    string? Token = null,
    RemoteTranscodeRequest? Transcode = null,
    string? SourceRevision = null);

/// <summary>One packet run carried by a streaming HTTP response.</summary>
public sealed record RemotePacketStreamRequest(
    string Path,
    double StartSeconds,
    int[] TrackIds,
    RemoteTranscodeRequest? Transcode = null,
    string? SourceRevision = null);

/// <summary>One explicit client decision to re-encode an indexed elementary stream.</summary>
/// <param name="SourcePath">Null for the main video; otherwise the indexed audio sidecar path.</param>
/// <param name="StreamIndex">The stream's index inside its source container.</param>
/// <param name="CodecName">Exact output codec, or empty to resolve the ordered candidates.</param>
/// <param name="BitRate">Output bits per second, or zero to use the encoder default.</param>
/// <param name="MaxHeight">Maximum video height, or zero to retain the source height.</param>
public sealed record RemoteTrackTranscodeRequest(
    string? SourcePath,
    int StreamIndex,
    string CodecName,
    long BitRate = 0,
    int MaxHeight = 0,
    string[]? CodecCandidates = null);

/// <summary>Exact transcode work requested by the client. An absent track remains passthrough.</summary>
public sealed record RemoteTranscodeRequest(RemoteTrackTranscodeRequest[] Tracks);

/// <summary>Configured encoder candidates. Availability is resolved for each playback request.</summary>
/// <param name="Hardware">
/// Whether the configured candidates prefer hardware. Actual availability is request-local.
/// </param>
public sealed record RemoteTranscodeCapabilities(
    string[] VideoCodecs,
    string[] AudioCodecs,
    bool Hardware = false,
    string DeviceType = "");

public sealed record RemoteStreamSubtitleRequest(string Path, int TrackId);

public sealed record RemoteStreamSubtitle(string Script);

public static class RemoteStreamDefaults
{
    public const string PassthroughDelivery = "passthrough";

    /// <summary>Delivery mode of a track the server decoded and encoded again.</summary>
    public const string TranscodeDelivery = "transcode";

    /// <summary>Capability the server advertises once it can demux on a client's behalf.</summary>
    public const string PacketStreamCapability = "packet-stream/2";

    public const string PacketStreamContentType = "application/vnd.lightplayer.packets";

    /// <summary>Capability the server advertises once it can re-encode what it sends.</summary>
    public const string TranscodeCapability = "transcode/2";

    /// <summary>Query parameter naming the server a same-origin relay should connect the socket to.</summary>
    public const string ForwardTargetQuery = "forwardTo";

    public const string SocketPath = "stream/socket";
    public const string DescribePath = "stream/describe";
    public const string OpenPath = "stream/open";
    public const string PacketsPath = "stream/packets";
    public const string SubtitlePath = "stream/subtitle";
}
