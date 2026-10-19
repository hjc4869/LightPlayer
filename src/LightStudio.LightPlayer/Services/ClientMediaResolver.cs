using LightStudio.LightPlayer.Video.Pipeline;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Services;

public interface IClientMediaResolver
{
    Task<IMultiMediaPlatformIO> ResolvePlatformAsync(
        MediaReference media,
        CancellationToken cancellationToken = default);

    IMultiMediaPlatformIO ResolvePlatform(MediaReference media);

    IMediaSourceProvider CreateVideoSource(
        MediaReference media,
        VideoMediaKind mediaKind = VideoMediaKind.File,
        int discTitle = 0,
        RemoteTranscodeIntent? transcode = null);

    ISubtitleFileBrowser? ResolveSubtitleBrowser(LibrarySourceId sourceId);

    LibrarySourceCapabilities GetCapabilities(LibrarySourceId sourceId);
}

public sealed class ClientMediaResolver(ILibrarySourceRegistry registry) : IClientMediaResolver
{
    public async Task<IMultiMediaPlatformIO> ResolvePlatformAsync(
        MediaReference media,
        CancellationToken cancellationToken = default)
    {
        var source = ResolveSource(media.SourceId);
        var services = await source.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return services.MultimediaPlatform;
    }

    public IMultiMediaPlatformIO ResolvePlatform(MediaReference media)
    {
        var source = ResolveSource(media.SourceId);
        return source.Services?.MultimediaPlatform
            ?? throw new InvalidOperationException($"Library source '{source.DisplayName}' is not connected.");
    }

    public IMediaSourceProvider CreateVideoSource(
        MediaReference media,
        VideoMediaKind mediaKind = VideoMediaKind.File,
        int discTitle = 0,
        RemoteTranscodeIntent? transcode = null)
    {
        var platform = ResolvePlatform(media);
        if ((platform as IMediaSourceProviderFactory)?.TryCreateSourceProvider(media.Path, mediaKind, transcode) is { } remote)
        {
            return remote;
        }

        if (mediaKind == VideoMediaKind.File)
        {
            return MediaSourceProviders.CreateLocal(media.Path, platform);
        }

        if (platform is not IVideoDiscPlatformIO discPlatform || !discPlatform.SupportsVideoDisc(mediaKind))
        {
            throw new NotSupportedException($"{mediaKind} playback is not available for this library source.");
        }

        return new DiscMediaSourceProvider(media.Path, mediaKind, discTitle, platform);
    }

    public ISubtitleFileBrowser? ResolveSubtitleBrowser(LibrarySourceId sourceId) =>
        ResolveSource(sourceId).Services?.SubtitleFiles;

    public LibrarySourceCapabilities GetCapabilities(LibrarySourceId sourceId) =>
        ResolveSource(sourceId).Capabilities;

    private ILibrarySource ResolveSource(LibrarySourceId sourceId) =>
        registry.TryGetSource(sourceId, out var source)
            ? source
            : throw new KeyNotFoundException($"Library source '{sourceId}' is no longer configured.");
}

internal sealed class CapturedPlatformMediaResolver(IMultiMediaPlatformIO platform) : IClientMediaResolver
{
    public Task<IMultiMediaPlatformIO> ResolvePlatformAsync(
        MediaReference media,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(platform);
    }

    public IMultiMediaPlatformIO ResolvePlatform(MediaReference media) => platform;

    public IMediaSourceProvider CreateVideoSource(
        MediaReference media,
        VideoMediaKind mediaKind = VideoMediaKind.File,
        int discTitle = 0,
        RemoteTranscodeIntent? transcode = null)
    {
        if ((platform as IMediaSourceProviderFactory)?.TryCreateSourceProvider(media.Path, mediaKind, transcode) is { } remote)
        {
            return remote;
        }

        if (mediaKind == VideoMediaKind.File)
        {
            return MediaSourceProviders.CreateLocal(media.Path, platform);
        }

        if (platform is not IVideoDiscPlatformIO discPlatform || !discPlatform.SupportsVideoDisc(mediaKind))
        {
            throw new NotSupportedException($"{mediaKind} playback is not available for this library source.");
        }

        return new DiscMediaSourceProvider(media.Path, mediaKind, discTitle, platform);
    }

    public ISubtitleFileBrowser? ResolveSubtitleBrowser(LibrarySourceId sourceId) => null;

    public LibrarySourceCapabilities GetCapabilities(LibrarySourceId sourceId) => new(true);
}