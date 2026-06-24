using System;

namespace LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;

public sealed class ExternalLrcInfo
{
    public static ExternalLrcInfo[] EmptyArray { get; } = Array.Empty<ExternalLrcInfo>();

    public object Opaque { get; set; }

    public string Title { get; set; }

    public string Artist { get; set; }

    public string Album { get; set; }

    public string Source { get; set; }
}
