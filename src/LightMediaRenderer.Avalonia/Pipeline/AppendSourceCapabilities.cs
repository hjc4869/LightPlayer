using System;
using System.Collections.Generic;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Demuxing;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>A decodable video codec family with the attributes <c>ready</c> lists for it.</summary>
public sealed record AppendVideoCodec(string Codec, int MaxWidth, int MaxHeight, IReadOnlyList<int> BitDepths, bool Hardware);

/// <summary>A decodable audio codec family.</summary>
public sealed record AppendAudioCodec(string Codec);

/// <summary>
/// The byte stream formats and codec families an <see cref="AppendMediaSourceProvider"/> accepts,
/// which is what the browser link reports in <c>ready</c> and what declaring a buffer is checked against.
/// </summary>
public static class AppendSourceCapabilities
{
    private static readonly (string Family, string Decoder, int[] BitDepths)[] VideoFamilies =
    [
        ("avc1", "h264", [8, 10]),
        ("hvc1", "hevc", [8, 10, 12]),
        ("av01", "av1", [8, 10, 12]),
        ("vp09", "vp9", [8, 10, 12]),
        ("vp08", "vp8", [8]),
    ];

    private static readonly (string Family, string Decoder)[] AudioFamilies =
    [
        ("mp4a", "aac"),
        ("opus", "opus"),
        ("flac", "flac"),
        ("vorbis", "vorbis"),
        ("ac-3", "ac3"),
        ("ec-3", "eac3"),
    ];

    private static readonly HashSet<string> WebMFamilies = new(StringComparer.Ordinal) { "vp08", "vp09", "av01", "opus", "vorbis" };

    // Software decoding has no fixed size limit; this is the largest picture offered to page players.
    private const int MaximumWidth = 8192;
    private const int MaximumHeight = 4320;

    private static readonly Lazy<IReadOnlyList<AppendVideoCodec>> videoCodecs = new(DetectVideoCodecs);
    private static readonly Lazy<IReadOnlyList<AppendAudioCodec>> audioCodecs = new(DetectAudioCodecs);

    /// <summary>Byte stream formats: <c>mp4</c> and <c>webm</c>.</summary>
    public static IReadOnlyList<string> Containers { get; } = ["mp4", "webm"];

    /// <summary>Video codec families the FFmpeg build decodes. Probing hardware opens each device type once.</summary>
    public static IReadOnlyList<AppendVideoCodec> VideoCodecs => videoCodecs.Value;

    /// <summary>Audio codec families the FFmpeg build decodes.</summary>
    public static IReadOnlyList<AppendAudioCodec> AudioCodecs => audioCodecs.Value;

    /// <summary>
    /// Whether a buffer of this MIME type is accepted: an MP4 or WebM type whose <c>codecs</c> parameter
    /// names only decodable families allowed in that container. A type without codecs is accepted.
    /// </summary>
    public static bool IsTypeSupported(string mimeType) => TryParse(mimeType, out _);

    internal static bool TryParse(string mimeType, out SegmentContainer container)
    {
        container = SegmentContainer.Mp4;
        if (string.IsNullOrWhiteSpace(mimeType))
            return false;

        string[] parts = mimeType.Split(';');
        string type = parts[0].Trim().ToLowerInvariant();
        bool audioOnly;
        switch (type)
        {
            case "video/mp4":
            case "audio/mp4":
                container = SegmentContainer.Mp4;
                audioOnly = type[0] == 'a';
                break;
            case "video/webm":
            case "audio/webm":
                container = SegmentContainer.WebM;
                audioOnly = type[0] == 'a';
                break;
            default:
                return false;
        }

        for (int index = 1; index < parts.Length; index++)
        {
            string parameter = parts[index].Trim();
            int equals = parameter.IndexOf('=');
            if (equals < 0 || !parameter[..equals].Trim().Equals("codecs", StringComparison.OrdinalIgnoreCase))
                continue;

            string value = parameter[(equals + 1)..].Trim().Trim('"', '\'');
            foreach (string codec in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string? family = FamilyOfCodecString(codec);
                if (family is null || !IsDecodable(family) || (audioOnly && IsVideoFamily(family)))
                    return false;
                if (container == SegmentContainer.WebM && !WebMFamilies.Contains(family))
                    return false;
            }
        }

        return true;
    }

    /// <summary>Codec family of an FFmpeg codec name, or null when none of the accepted families.</summary>
    internal static string? FamilyOfDecoder(string codecName)
    {
        foreach ((string family, string decoder, _) in VideoFamilies)
        {
            if (decoder == codecName)
                return family;
        }

        foreach ((string family, string decoder) in AudioFamilies)
        {
            if (decoder == codecName)
                return family;
        }

        return null;
    }

    /// <summary>Whether a codec family decodes here and the container may carry it.</summary>
    internal static bool Accepts(SegmentContainer container, string family) =>
        IsDecodable(family) && (container == SegmentContainer.Mp4 || WebMFamilies.Contains(family));

    private static bool IsVideoFamily(string family) => Array.Exists(VideoFamilies, entry => entry.Family == family);

    private static bool IsDecodable(string family)
    {
        foreach (AppendVideoCodec codec in VideoCodecs)
        {
            if (codec.Codec == family)
                return true;
        }

        foreach (AppendAudioCodec codec in AudioCodecs)
        {
            if (codec.Codec == family)
                return true;
        }

        return false;
    }

    /// <summary>Maps an RFC 6381 codec string, such as <c>avc1.64001f</c> or <c>mp4a.40.2</c>, to its family.</summary>
    private static string? FamilyOfCodecString(string codec)
    {
        string lower = codec.ToLowerInvariant();
        string head = lower.Split('.')[0];
        switch (head)
        {
            case "avc1" or "avc3":
                return "avc1";
            // dvh1 and dvhe are Dolby Vision on HEVC: the HEVC layer with its metadata.
            case "hvc1" or "hev1" or "dvh1" or "dvhe":
                return "hvc1";
            case "av01":
                return "av01";
            case "vp09" or "vp9":
                return "vp09";
            case "vp08" or "vp8":
                return "vp08";
            case "opus":
                return "opus";
            case "flac":
                return "flac";
            case "vorbis":
                return "vorbis";
            case "ac-3":
                return "ac-3";
            case "ec-3":
                return "ec-3";
            case "mp4a":
                string[] fields = lower.Split('.');
                if (fields.Length < 2)
                    return "mp4a";

                // Object types: 40 MPEG-4 audio (AAC), 66-68 MPEG-2 AAC, a5 AC-3, a6 E-AC-3. MPEG-1
                // and MPEG-2 layer audio (69, 6b) are not accepted.
                return fields[1] switch
                {
                    "40" or "66" or "67" or "68" => "mp4a",
                    "a5" => "ac-3",
                    "a6" => "ec-3",
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static IReadOnlyList<AppendVideoCodec> DetectVideoCodecs()
    {
        var codecs = new List<AppendVideoCodec>();
        foreach ((string family, string decoder, int[] depths) in VideoFamilies)
        {
            if (DecoderCapabilities.HasDecoder(decoder))
                codecs.Add(new AppendVideoCodec(family, MaximumWidth, MaximumHeight, depths, DecoderCapabilities.HasHardwareDecoder(decoder)));
        }

        return codecs;
    }

    private static IReadOnlyList<AppendAudioCodec> DetectAudioCodecs()
    {
        var codecs = new List<AppendAudioCodec>();
        foreach ((string family, string decoder) in AudioFamilies)
        {
            if (DecoderCapabilities.HasDecoder(decoder))
                codecs.Add(new AppendAudioCodec(family));
        }

        return codecs;
    }
}
