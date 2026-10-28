using System.IO;
using System.Text;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;

namespace LightMediaRenderer.Avalonia.Pipeline;

/// <summary>
/// One selectable subtitle: a text or bitmap stream of the media itself, a stream of another
/// container added alongside it, or a plain script file.
/// </summary>
public sealed class SubtitleTrack
{
    internal SubtitleTrack(MediaStreamInfo stream, string? source, string? mediaPath, string defaultTrackName)
    {
        StreamIndex = stream.Index;
        Source = source;
        CodecName = stream.CodecName;
        Language = stream.Language;
        Title = stream.Title;
        IsDefault = stream.IsDefault;
        IsBitmap = FfmpegSubtitleReader.IsBitmapBased(stream);
        DisplayName = Describe(mediaPath, defaultTrackName);
    }

    /// <summary>A script file, which is the whole track and so has no stream of its own.</summary>
    internal SubtitleTrack(string path, string? mediaPath, string defaultTrackName)
    {
        StreamIndex = -1;
        Source = path;
        CodecName = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        DisplayName = Describe(mediaPath, defaultTrackName);
    }

    /// <summary>Index of the stream inside the file it lives in, or -1 for a script file.</summary>
    public int StreamIndex { get; }

    /// <summary>Path of the file holding the track, or <c>null</c> for the media's own streams.</summary>
    public string? Source { get; }

    public bool IsExternal => Source is not null;

    /// <summary>Whether the track is a complete text script rather than a demuxed stream.</summary>
    public bool IsScriptFile => StreamIndex < 0;

    /// <summary>Whether the track is made of positioned bitmap compositions.</summary>
    public bool IsBitmap { get; }

    /// <summary>Whether the track is text without authored styles, such as SubRip or WebVTT.</summary>
    public bool IsPlainText => !IsBitmap && CodecName is not ("ass" or "ssa");

    public string CodecName { get; }

    public string? Language { get; }

    public string? Title { get; }

    public bool IsDefault { get; }

    /// <summary>Label for a subtitle picker.</summary>
    public string DisplayName { get; }

    public override string ToString() => DisplayName;

    private string Describe(string? mediaPath, string defaultTrackName)
    {
        var text = new StringBuilder();
        if (IsExternal)
        {
            text.Append(SidecarName.Describe(Source, mediaPath, defaultTrackName));
            // Only needed to tell apart several tracks living in the same sidecar container.
            if (!IsScriptFile && StreamIndex > 0)
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

        // A script's format is all that is left to tell "Default (ASS)" from "Default (SRT)".
        if (IsScriptFile)
            text.Append(" (").Append(CodecName.ToUpperInvariant()).Append(')');
        else
            text.Append(" · ").Append(CodecName);

        return text.ToString();
    }
}
