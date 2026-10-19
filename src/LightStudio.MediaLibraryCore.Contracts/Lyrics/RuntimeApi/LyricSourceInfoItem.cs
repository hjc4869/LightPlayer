namespace LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

/// <summary>A text item, optionally linked to an external page, supplied by a lyric source.</summary>
public sealed class LyricSourceInfoItem
{
    public string Text { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public bool IsLink => !string.IsNullOrWhiteSpace(Url);
}