using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim.Engine;

namespace LightMediaRenderer.Avalonia.Pipeline;

public sealed record MediaDecodeRequest(string Path, MediaPlayerOptions Options,
    Func<CancellationToken, Task<Stream>>? OpenSource = null)
{
    /// <summary>Opens other files of the same source, such as sidecar sound tracks and subtitle scripts.</summary>
    public Func<string, CancellationToken, Task<Stream>>? OpenFile { get; init; }

    /// <summary>What the session reads instead of <see cref="Path"/> and the openers, such as a server's packet stream.</summary>
    public IMediaSessionInput? Input { get; init; }
}

public static class MediaDecodeSessionFactory
{
    private static Func<MediaDecodeRequest, IMediaDecodeSession>? factory;

    public static bool IsRegistered => factory is not null;
    public static IReadOnlyList<string>? HardwareDeviceTypes { get; private set; }

    public static void Register(Func<MediaDecodeRequest, IMediaDecodeSession> create, IReadOnlyList<string>? hardwareDeviceTypes = null)
    {
        ArgumentNullException.ThrowIfNull(create);
        factory = create;
        HardwareDeviceTypes = hardwareDeviceTypes ?? Array.Empty<string>();
    }

    public static IMediaDecodeSession Create(MediaDecodeRequest request) => factory is { } create
        ? create(request)
        : request.Input is { } input
        ? new MediaDecodeSession(request.Options, input, new InProcessMediaEngine(), ownsEngine: true)
        : new MediaDecodeSession(request.Options, () => request.OpenSource is { } open
            ? new LocalMediaSourceProvider(() => open(CancellationToken.None).GetAwaiter().GetResult(), request.Path, request.Options.OpenFile)
            : request.Options.SourceProviderFactory?.Invoke(request.Path)
                ?? LocalMediaSourceProvider.ForPath(request.Path, request.Options.OpenFile));
}
