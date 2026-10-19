using System;
using System.Collections.Generic;
using System.Linq;
using LightStudio.LightPlayer.Remote;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// What this device would like a server to re-encode for it, decided before anything is being
/// watched.
/// </summary>
/// <remarks>
/// Thresholds are evaluated on this device against a fresh server stream description.
/// Codec lists are stored as one comma separated string so the whole of this stays a
/// value that can be compared and written out as it stands.
/// </remarks>
public sealed record RemoteTranscodeSettings
{
    public static RemoteTranscodeSettings Default { get; } = new();

    /// <summary>Whether video may be re-encoded when a threshold below is crossed.</summary>
    public bool Video { get; init; }

    public bool Audio { get; init; }

    /// <summary>
    /// Codecs this device can decode, comma separated. Video in anything else is re-encoded whatever
    /// the thresholds say. Empty accepts everything.
    /// </summary>
    public string VideoCodecs { get; init; } = "h264,hevc,av1,vp9";

    /// <summary>Codec to ask for, or empty to take whatever the server offers first.</summary>
    public string VideoCodec { get; init; } = string.Empty;

    /// <summary>Whole-file bits per second above which video is re-encoded. 0 never triggers.</summary>
    public long FileBitrateThreshold { get; init; } = 12_000_000;

    public long VideoBitrate { get; init; } = 6_000_000;

    /// <summary>Picture height above which video is re-encoded. 0 never triggers.</summary>
    public int VideoHeightThreshold { get; init; } = 1080;

    /// <summary>Height to come down to. 0 leaves the size alone and only lowers the bitrate.</summary>
    public int VideoHeight { get; init; } = 1080;

    public string AudioCodec { get; init; } = string.Empty;

    /// <summary>Audio codecs that should always be re-encoded, comma separated.</summary>
    public string AudioCodecsToTranscode { get; init; } =
        "flac,truehd,mlp,alac,wavpack,ape,tta,pcm_*";

    public long AudioBitrate { get; init; } = 160_000;

    public string[] VideoCodecList => Split(VideoCodecs);

    public string[] AudioCodecList => Split(AudioCodecsToTranscode);

    private static string[] Split(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Re-encoding turned on or off for the video being watched, without disturbing the settings the
/// next one will start from.
/// </summary>
/// <param name="Video">Null follows the settings; true and false override them outright.</param>
public sealed record RemoteTranscodeOverride(
    bool? Video = null,
    bool? Audio = null,
    long VideoBitrate = 0,
    long AudioBitrate = 0,
    int VideoHeight = 0)
{
    public static RemoteTranscodeOverride None { get; } = new();

    public bool IsEmpty =>
        Video is null && Audio is null && VideoBitrate == 0 && AudioBitrate == 0 && VideoHeight == 0;
}

public sealed record RemoteTranscodeIntent(RemoteTranscodeSettings Settings, RemoteTranscodeOverride Override);

/// <summary>Builds explicit track requests from captured playback preferences.</summary>
public static class RemoteTranscoding
{
    public static bool Offers(RemoteTranscodeCapabilities? capabilities) =>
        capabilities is { VideoCodecs.Length: > 0 } or { AudioCodecs.Length: > 0 };

    /// <summary>
    /// Applies captured preferences to current source facts. Automatic output candidates are
    /// ordered explicitly; the server resolves their availability for this request. A kind the
    /// server offers no output codec for stays passthrough.
    /// </summary>
    public static RemoteTranscodeRequest? BuildRequest(
        RemoteStreamManifest video,
        RemoteTranscodeCapabilities? offered,
        RemoteTranscodeSettings current,
        RemoteTranscodeOverride wanted)
    {
        ArgumentNullException.ThrowIfNull(video);
        current ??= RemoteTranscodeSettings.Default;
        wanted ??= RemoteTranscodeOverride.None;
        bool videoEnabled = (wanted.Video ?? current.Video) && offered is { VideoCodecs.Length: > 0 };
        bool audioEnabled = (wanted.Audio ?? current.Audio) && offered is { AudioCodecs.Length: > 0 };
        if (!videoEnabled && !audioEnabled)
        {
            return null;
        }

        var tracks = new List<RemoteTrackTranscodeRequest>();
        foreach (RemoteStreamTrack stream in video.Tracks)
        {
            if (videoEnabled && stream.Id == video.DefaultVideoTrackId && stream.Kind == "video")
            {
                bool requested = wanted.Video == true ||
                    !Accepts(current.VideoCodecList, stream.CodecName) ||
                    (current.FileBitrateThreshold > 0 && video.Bitrate > current.FileBitrateThreshold) ||
                    (current.VideoHeightThreshold > 0 && stream.Height > current.VideoHeightThreshold);
                if (requested)
                {
                    tracks.Add(CreateTrackRequest(
                        stream,
                        (offered?.VideoCodecs ?? []).Where(codec => Accepts(current.VideoCodecList, codec)).ToArray(),
                        current.VideoCodec,
                        wanted.VideoBitrate > 0 ? wanted.VideoBitrate : current.VideoBitrate,
                        wanted.VideoHeight > 0 ? wanted.VideoHeight : current.VideoHeight));
                }
            }

            if (audioEnabled && stream.Kind == "audio")
            {
                bool requested = wanted.Audio == true || Matches(current.AudioCodecList, stream.CodecName);
                if (requested)
                {
                    tracks.Add(CreateTrackRequest(
                        stream,
                        offered?.AudioCodecs ?? [],
                        current.AudioCodec,
                        wanted.AudioBitrate > 0 ? wanted.AudioBitrate : current.AudioBitrate));
                }
            }
        }

        return tracks.Count == 0 ? null : new RemoteTranscodeRequest(tracks.ToArray());
    }

    private static bool Accepts(string[] codecs, string codecName) =>
        codecs.Length == 0 || Matches(codecs, codecName);

    private static bool Matches(string[] codecs, string codecName)
    {
        string value = codecName ?? string.Empty;
        return codecs.Any(codec =>
            codec.EndsWith('*')
                ? value.StartsWith(codec[..^1], StringComparison.OrdinalIgnoreCase)
                : string.Equals(codec, value, StringComparison.OrdinalIgnoreCase));
    }

    private static RemoteTrackTranscodeRequest CreateTrackRequest(
        RemoteStreamTrack stream,
        string[] offered,
        string wanted,
        long bitRate,
        int maxHeight = 0)
    {
        if (string.IsNullOrWhiteSpace(wanted) && offered.Length == 0)
        {
            throw new NotSupportedException($"This server offers no acceptable {stream.Kind} output codec.");
        }

        return new RemoteTrackTranscodeRequest(
            stream.SourcePath,
            stream.StreamIndex,
            wanted.Trim(),
            bitRate,
            maxHeight,
            string.IsNullOrWhiteSpace(wanted) ? offered : null);
    }
}
