using LightStudio.LightPlayer.Video.Pipeline;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.RemoteServices;

/// <summary>
/// Media I/O backed by a Light Player server: files are streamed over HTTP range requests and
/// metadata is read from the server's own decoder.
/// </summary>
public sealed class RemoteMultimediaPlatformIO : IMultiMediaPlatformIO, IMediaSourceProviderFactory
{
    private readonly RemoteApiClient client;
    private readonly RemoteMediaBlockCache blockCache;
    private readonly RemoteImageCache? imageCache;

    private RemoteMultimediaPlatformIO(
        RemoteApiClient client,
        IMediaBlockStore blockStore,
        RemoteImageCache? imageCache,
        RemoteTranscodeCapabilities? transcodeCapabilities,
        bool supportsMediaBrowse)
    {
        this.client = client;
        this.imageCache = imageCache;
        TranscodeCapabilities = transcodeCapabilities;
        SupportsMediaBrowse = supportsMediaBrowse;
        blockCache = new RemoteMediaBlockCache(blockStore);
        LocalDataPath = Path.Combine(PlatformServices.AppDataPath, "LightStudio.LightPlayer");
    }

    public int OptimalIoThreadCount => 4;

    public string LocalDataPath { get; }

    public RemoteTranscodeCapabilities? TranscodeCapabilities { get; }

    /// <summary>Whether this server offered to re-encode when it was connected.</summary>
    public bool SupportsTranscoding => RemoteTranscoding.Offers(TranscodeCapabilities);

    /// <summary>Whether this server lists the videos in a folder, and not only its subtitles.</summary>
    public bool SupportsMediaBrowse { get; }

    /// <summary>Locally persisted blocks of the remote media this head streams.</summary>
    public IStorageCache MediaBlockCache => blockCache;

    /// <summary>
    /// Locally persisted album art and video previews, or <see langword="null"/> on heads whose HTTP
    /// stack already caches responses (the browser).
    /// </summary>
    public IStorageCache? ImageCache => imageCache;

    public static async Task<RemoteMultimediaPlatformIO> CreateAsync(
        RemoteApiClient client,
        IMediaBlockStore blockStore,
        RemoteImageCache? imageCache = null,
        CancellationToken cancellationToken = default)
    {
        var server = await client
            .GetAsync<RemoteServerInfo>(RemoteApiDefaults.ServerInfoPath, cancellationToken)
            .ConfigureAwait(false);
        var transcodeCapabilities =
            server.Capabilities?.Contains(RemoteStreamDefaults.TranscodeCapability) == true
                ? server.Transcoding
                : null;

        return new RemoteMultimediaPlatformIO(
            client,
            blockStore,
            imageCache,
            transcodeCapabilities,
            server.Capabilities?.Contains(RemoteApiDefaults.MediaBrowseCapability) == true);
    }

    public void InitializePlatform() => IMultiMediaPlatformIO.Instance = this;

    /// <summary>
    /// Hands video over to the server's own demuxer, so a container's unwanted sound tracks are
    /// never fetched. Music is left alone: a track is one stream, and the block cache behind the
    /// file path is worth more to it than saving nothing.
    /// </summary>
    public IMediaSourceProvider? TryCreateSourceProvider(
        string path,
        VideoMediaKind mediaKind = VideoMediaKind.File,
        RemoteTranscodeIntent? transcode = null) =>
        mediaKind != VideoMediaKind.File || VideoContainerFormats.Contains(Path.GetExtension(path))
            ? new RemoteVideoSourceProvider(client, path, this, transcode)
            : null;

    public IMediaSessionInput? TryCreateSessionInput(
        string path,
        VideoMediaKind mediaKind = VideoMediaKind.File,
        RemoteTranscodeIntent? transcode = null) =>
        mediaKind != VideoMediaKind.File || VideoContainerFormats.Contains(Path.GetExtension(path))
            ? new RemoteVideoSessionInput(client, path, this, transcode)
            : null;

    public async Task<Stream> OpenMediaFileAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RemoteMediaStream
                .CreateAsync(client, blockCache, path, cancellationToken,
                    downloadWholeFile: MediaContainerFormats.IsImage(path))
                .ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            // The endpoint cannot serve byte ranges: fall back to buffering the whole file.
            var bytes = await client
                .GetBytesAsync($"media/file?path={Encode(path)}", cancellationToken)
                .ConfigureAwait(false);
            return new MemoryStream(bytes, writable: false);
        }
    }

    /// <summary>Containers opened through the server's video packet stream.</summary>
    private static readonly System.Collections.Generic.IReadOnlySet<string> VideoContainerFormats =
        MediaContainerFormats.Video;

    public Task<bool> FolderExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task<MediaFileProperties?> GetMediaFilePropertiesAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        client.PostAsync<RemotePathRequest, MediaFileProperties?>(
            "media/properties",
            new RemotePathRequest(path),
            cancellationToken);

    /// <summary>
    /// Remote queue expansion never resolves client-side sibling paths: CUE and lyric handling run
    /// on the server, where the server's platform I/O implementation owns the filesystem context.
    /// </summary>
    public Task<string?> ResolveFileRelativeToAsync(
        string sourceFilePath,
        string referencedFileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public Task<IFile[]> ListFilesFullNameInPathAsync(string path) =>
        throw new NotSupportedException("A remote session does not enumerate the server's filesystem.");

    public Task<string[]> ListFoldersFullNameInPathAsync(string path) =>
        throw new NotSupportedException("A remote session does not enumerate the server's filesystem.");

    public async Task<MediaInfo> GetMediaInfoAsync(string mediaFilePath) =>
        (await client.PostAsync<RemotePathRequest, RemoteMediaInfo>(
            "media/info",
            new RemotePathRequest(mediaFilePath)).ConfigureAwait(false)).ToModel();

    public async Task<byte[]?> GetAlbumCoverAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        var path = $"media/artwork?path={Encode(mediaFilePath)}";
        var bytes = imageCache is null
            ? await client.GetBytesAsync(path, cancellationToken).ConfigureAwait(false)
            : await imageCache
                .GetAlbumArtAsync(mediaFilePath, token => client.GetBytesAsync(path, token))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        return bytes.Length == 0 ? null : bytes;
    }

    public async Task<ArtworkBuffer?> GetAlbumCoverBufferAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        if (imageCache is not null)
        {
            return await GetAlbumCoverAsync(mediaFilePath, cancellationToken).ConfigureAwait(false) is { } cached
                ? ArtworkBuffer.Copy(cached)
                : null;
        }

        return await client
            .GetArtworkBufferAsync($"media/artwork?path={Encode(mediaFilePath)}", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Indexing happens on the server, so a remote head never probes: it consumes the video list the
    /// server already built.
    /// </summary>
    public Task<VideoProbeInfo?> ProbeVideoAsync(
        string mediaFilePath,
        bool withPreview = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<VideoProbeInfo?>(null);
    }

    public Task<IReadOnlyList<AudioStreamProbeInfo>> ProbeAudioStreamsAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AudioStreamProbeInfo>>([]);
    }

    /// <summary>
    /// The file lives on the server, so the container inspection is done there and sent back whole.
    /// </summary>
    public Task<MediaProbeReport?> InspectMediaAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default) =>
        client.PostAsync<RemotePathRequest, MediaProbeReport?>(
            "media/inspect",
            new RemotePathRequest(mediaFilePath),
            cancellationToken);

    public T GetConfigurationValue<T>(string key) => default!;

    public void SetConfigurationValue<T>(string key, T value)
    {
    }

    private static string Encode(string value) => Uri.EscapeDataString(value);
}
