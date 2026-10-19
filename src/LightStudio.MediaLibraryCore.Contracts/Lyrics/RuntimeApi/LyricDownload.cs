namespace LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

public enum LyricType
{
    Plain,
    Synced
}

public sealed record LyricDownload(string Content, LyricType Type);