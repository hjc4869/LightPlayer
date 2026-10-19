using System;
using System.IO;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Video.Pipeline;

/// <summary>
/// Implemented by a platform I/O whose server can demux on this device's behalf.
/// </summary>
public interface IMediaSourceProviderFactory
{
    bool SupportsTranscoding => false;

    /// <summary>Returns null to leave <paramref name="path"/> to the local reader.</summary>
    IMediaSourceProvider? TryCreateSourceProvider(
        string path,
        VideoMediaKind mediaKind = VideoMediaKind.File,
        RemoteTranscodeIntent? transcode = null);

    /// <summary>
    /// Like <see cref="TryCreateSourceProvider"/>, for a registered decode session whose engine opens sources
    /// without blocking. Null leaves <paramref name="path"/> to the platform's files.
    /// </summary>
    IMediaSessionInput? TryCreateSessionInput(
        string path,
        VideoMediaKind mediaKind = VideoMediaKind.File,
        RemoteTranscodeIntent? transcode = null) => null;
}

/// <summary>Chooses how a media identifier is read.</summary>
/// <remarks>
/// The choice follows whichever platform I/O is installed rather than a hook of its own, so
/// switching between a server and this device's own library cannot leave the wrong reader behind.
/// </remarks>
public static class MediaSourceProviders
{
    public static IMediaSourceProvider Create(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return (IMultiMediaPlatformIO.Instance as IMediaSourceProviderFactory)?.TryCreateSourceProvider(path)
            ?? CreateLocal(path);
    }

    public static LocalMediaSourceProvider CreateLocal(string path, IMultiMediaPlatformIO? platform = null)
    {
        platform ??= IMultiMediaPlatformIO.Instance;
        return LocalMediaSourceProvider.ForPath(path,
            source => platform.OpenMediaFileAsync(source).GetAwaiter().GetResult());
    }

    public static Stream OpenFile(string path) =>
        IMultiMediaPlatformIO.Instance.OpenMediaFileAsync(path).GetAwaiter().GetResult();

    public static IMediaSourceProvider Create(MediaReference media, IClientMediaResolver resolver)
    {
        ArgumentException.ThrowIfNullOrEmpty(media.Path);
        ArgumentNullException.ThrowIfNull(resolver);
        return resolver.CreateVideoSource(media);
    }

    public static IMediaDecodeSession CreateDecodeSession(MediaPlayerOptions options, MediaReference media,
        IClientMediaResolver resolver, VideoMediaKind mediaKind = VideoMediaKind.File, int discTitle = 0,
        RemoteTranscodeIntent? transcode = null)
    {
        if (!MediaDecodeSessionFactory.IsRegistered)
            return new MediaDecodeSession(options, () => resolver.CreateVideoSource(media, mediaKind, discTitle, transcode));

        // The caller has connected the source already.
        IMultiMediaPlatformIO platform = resolver.ResolvePlatform(media);
        if ((platform as IMediaSourceProviderFactory)?.TryCreateSessionInput(media.Path, mediaKind, transcode) is { } input)
            return MediaDecodeSessionFactory.Create(new(media.Path, options) { Input = input });
        if (mediaKind != VideoMediaKind.File)
            throw new NotSupportedException($"{mediaKind} playback is not supported by the registered media session.");
        return MediaDecodeSessionFactory.Create(new(media.Path, options,
            cancellationToken => platform.OpenMediaFileAsync(media.Path, cancellationToken))
        {
            OpenFile = platform.OpenMediaFileAsync,
        });
    }
}
