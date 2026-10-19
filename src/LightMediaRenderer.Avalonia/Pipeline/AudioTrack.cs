using System.Text;
using LightStudio.FfmpegShim.Media;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>
/// One selectable sound track: either a stream of the media itself or a stream of a file added
/// alongside it with <see cref="MediaPlayer.AddExternalAudio"/>.
/// </summary>
public sealed class AudioTrack
{
    public AudioTrack(MediaStreamInfo stream, string? source, string? mediaPath, string defaultTrackName)
    {
        StreamIndex = stream.Index;
        Source = source;
        CodecName = stream.CodecName;
        Language = stream.Language;
        Title = stream.Title;
        SampleRate = stream.SampleRate;
        ChannelCount = stream.ChannelCount;
        IsDefault = stream.IsDefault;
        DisplayName = Describe(mediaPath, defaultTrackName);
    }

    /// <summary>Index of the stream inside the file it lives in.</summary>
    public int StreamIndex { get; }

    /// <summary>Path of the file holding the track, or <c>null</c> for the media's own streams.</summary>
    public string? Source { get; }

    public bool IsExternal => Source is not null;

    public string CodecName { get; }

    public string? Language { get; }

    public string? Title { get; }

    public int SampleRate { get; }

    public int ChannelCount { get; }

    public bool IsDefault { get; }

    /// <summary>Label for a track picker.</summary>
    public string DisplayName { get; }

    public override string ToString() => DisplayName;

    private string Describe(string? mediaPath, string defaultTrackName)
    {
        var text = new StringBuilder();
        if (IsExternal)
        {
            text.Append(SidecarName.Describe(Source, mediaPath, defaultTrackName));
            // Only needed to tell apart several tracks living in the same sidecar container.
            if (StreamIndex > 0)
                text.Append(" #").Append(StreamIndex);
        }
        else
        {
            text.Append("Track ").Append(StreamIndex);
        }

        if (!string.IsNullOrEmpty(Title))
            text.Append(" — ").Append(Title);
        if (!string.IsNullOrEmpty(Language))
            text.Append(" [").Append(Language).Append(']');

        text.Append(" · ").Append(CodecName);
        if (ChannelCount > 0)
            text.Append(' ').Append(ChannelCount).Append(" ch");

        return text.ToString();
    }
}
