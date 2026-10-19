using LightStudio.LightPlayer.Video.Pipeline;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightStudio.LightPlayer.Services;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;
using FfmpegPacketMediaInfo = LightStudio.FfmpegShim.Media.MediaInfo;
using LibraryMediaInfo = LightStudio.MediaLibraryCore.IO.MediaInfo;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.RemoteServices;

internal sealed class WebDavMultimediaPlatformIO : IMultiMediaPlatformIO, IMediaSourceProviderFactory
{
    private static readonly string[] FolderArtworkNames =
    [
        "cover.jpg", "cover.jpeg", "folder.jpg", "folder.jpeg", "front.jpg", "front.jpeg",
        "cover.png", "folder.png", "front.png",
    ];

    private readonly WebDavClient client;
    private readonly RemoteMediaBlockCache audioBlockCache;
    private readonly RemoteImageCache? imageCache;
    private readonly IMultiMediaPlatformIO? probes;

    public WebDavMultimediaPlatformIO(
        WebDavClient client,
        IMediaBlockStore audioBlockStore,
        RemoteImageCache? imageCache,
        string localDataPath,
        Func<Func<string, CancellationToken, Task<Stream>>, IMultiMediaPlatformIO>? probeFactory = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.imageCache = imageCache;
        ArgumentNullException.ThrowIfNull(audioBlockStore);
        audioBlockCache = new RemoteMediaBlockCache(audioBlockStore);
        LocalDataPath = localDataPath;
        probes = probeFactory?.Invoke(OpenMediaFileForScanAsync);
    }

    public int OptimalIoThreadCount => 2;

    public string LocalDataPath { get; }

    public IStorageCache MediaBlockCache => audioBlockCache;

    public IStorageCache? ImageCache => imageCache;

    public void InitializePlatform() => IMultiMediaPlatformIO.Instance = this;

    public IMediaSourceProvider? TryCreateSourceProvider(
        string path,
        VideoMediaKind mediaKind = VideoMediaKind.File,
        RemoteTranscodeIntent? transcode = null) =>
        IsVideoPath(path) ? new WebDavMediaSourceProvider(this, path) : null;

    public Task<Stream> OpenMediaFileAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        IsVideoPath(path)
            ? OpenVolatileMediaFileAsync(path, cancellationToken)
            : OpenWithCacheAsync(path, audioBlockCache, cancellationToken);

    internal Task<Stream> OpenVolatileMediaFileAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        client.OpenMediaStreamAsync(path, cancellationToken);

    internal Task<Stream> OpenMediaFileForScanAsync(string path) =>
        OpenMediaFileForScanAsync(path, CancellationToken.None);

    internal Task<Stream> OpenMediaFileForScanAsync(string path, CancellationToken cancellationToken) =>
        IsVideoPath(path)
            ? client.OpenMediaStreamAsync(path, cancellationToken, readAheadEnabled: false)
            : OpenWithCacheAsync(path, audioBlockCache, cancellationToken, readAheadEnabled: false);

    public async Task<bool> FolderExistsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return (await client.GetEntryAsync(path, cancellationToken).ConfigureAwait(false))?.IsCollection == true;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<MediaFileProperties?> GetMediaFilePropertiesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var entry = await client.GetEntryAsync(path, cancellationToken).ConfigureAwait(false);
        return entry is null || entry.IsCollection
            ? null
            : new MediaFileProperties(
                entry.Name,
                entry.Path,
                ParentPath(entry.Path),
                entry.Length,
                entry.LastModifiedUtc);
    }

    public async Task<string?> ResolveFileRelativeToAsync(
        string sourceFilePath,
        string referencedFileName,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(sourceFilePath, UriKind.Absolute, out var source)
            || string.IsNullOrWhiteSpace(referencedFileName))
        {
            return null;
        }

        try
        {
            var directory = new Uri(source, ".");
            var candidate = new Uri(directory, referencedFileName.Replace('\\', '/'));
            var entry = await client.GetEntryAsync(candidate.AbsoluteUri, cancellationToken).ConfigureAwait(false);
            return entry is { IsCollection: false } ? entry.Path : null;
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Task<IFile[]> ListFilesFullNameInPathAsync(string path) =>
        ListFilesFullNameInPathAsync(path, CancellationToken.None);

    public async Task<IFile[]> ListFilesFullNameInPathAsync(string path, CancellationToken cancellationToken)
    {
        var entries = await client.ListAsync(path, cancellationToken).ConfigureAwait(false);
        return entries
            .Where(entry => !entry.IsCollection && !SamePath(entry.Path, path))
            .Select(entry => (IFile)new WebDavFile(entry))
            .ToArray();
    }

    public Task<string[]> ListFoldersFullNameInPathAsync(string path) =>
        ListFoldersFullNameInPathAsync(path, CancellationToken.None);

    public async Task<string[]> ListFoldersFullNameInPathAsync(string path, CancellationToken cancellationToken)
    {
        var entries = await client.ListAsync(path, cancellationToken).ConfigureAwait(false);
        return entries
            .Where(entry => entry.IsCollection && !SamePath(entry.Path, path))
            .Select(entry => entry.Path)
            .ToArray();
    }

    public Task<LibraryMediaInfo> GetMediaInfoAsync(string mediaFilePath) =>
        GetMediaInfoAsync(mediaFilePath, CancellationToken.None);

    public async Task<LibraryMediaInfo> GetMediaInfoAsync(string mediaFilePath, CancellationToken cancellationToken)
    {
        if (probes is not null)
            return await probes.GetMediaInfoAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        await using var stream = await OpenMediaFileForScanAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Map(FfmpegCodec.GetMediaInfoFromStream(stream)), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask YieldScanAsync(CancellationToken cancellationToken) =>
        probes?.YieldScanAsync(cancellationToken) ?? ValueTask.CompletedTask;

    public async Task<byte[]?> GetAlbumCoverAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        async Task<byte[]> Fetch(CancellationToken token)
        {
            try
            {
                byte[]? embedded;
                if (probes is not null)
                {
                    embedded = await probes.GetAlbumCoverAsync(mediaFilePath, token).ConfigureAwait(false);
                }
                else
                {
                    await using var stream = await OpenMediaFileForScanAsync(mediaFilePath, token).ConfigureAwait(false);
                    embedded = await Task.Run(() => FfmpegCodec.GetAlbumCoverFromStream(stream), token).ConfigureAwait(false);
                }
                if (embedded is { Length: > 0 })
                {
                    return embedded;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLog.Warning("WebDAV.Artwork", $"Could not inspect artwork in '{mediaFilePath}'.", exception);
            }

            var parent = ParentPath(mediaFilePath);
            if (parent is null)
            {
                return [];
            }

            var entries = await client.ListAsync(parent, token).ConfigureAwait(false);
            var files = entries
                .Where(entry => !entry.IsCollection)
                .ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var name in FolderArtworkNames)
            {
                if (files.TryGetValue(name, out var artwork))
                {
                    return await client.GetBytesAsync(artwork.Path, token).ConfigureAwait(false);
                }
            }

            return [];
        }

        var bytes = imageCache is null
            ? await Fetch(cancellationToken).ConfigureAwait(false)
            : await imageCache
                .GetAlbumArtAsync($"{client.EndpointIdentity}\n{mediaFilePath}", Fetch)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        return bytes.Length == 0 ? null : bytes;
    }

    public async Task<VideoProbeInfo?> ProbeVideoAsync(
        string mediaFilePath,
        bool withPreview = true,
        CancellationToken cancellationToken = default)
    {
        if (probes is not null)
            return await probes.ProbeVideoAsync(mediaFilePath, withPreview, cancellationToken).ConfigureAwait(false);
        await using var stream = await client.OpenMediaStreamAsync(
            mediaFilePath,
            cancellationToken,
            readAheadEnabled: false).ConfigureAwait(false);
        return await Task.Run(
            () => VideoPreviewFactory.Probe(stream, withPreview),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AudioStreamProbeInfo>> ProbeAudioStreamsAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        if (probes is not null)
            return await probes.ProbeAudioStreamsAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        await using var stream = await client.OpenMediaStreamAsync(
            mediaFilePath,
            cancellationToken,
            readAheadEnabled: false).ConfigureAwait(false);
        return await Task.Run(
            () => VideoPreviewFactory.ProbeAudioStreams(stream),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<MediaProbeReport?> InspectMediaAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        if (probes is not null)
            return await probes.InspectMediaAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        await using var stream = await OpenMediaFileAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () => MediaProbeFactory.Inspect(stream),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImageProbeInfo?> ProbeImageAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        if (probes is not null)
            return await probes.ProbeImageAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        await using var stream = await client.OpenMediaStreamAsync(
            mediaFilePath, cancellationToken, readAheadEnabled: false).ConfigureAwait(false);
        return await Task.Run(() => PhotoFolderService.ReadMetadata(stream), cancellationToken).ConfigureAwait(false);
    }

    public T GetConfigurationValue<T>(string key) => default!;

    public void SetConfigurationValue<T>(string key, T value)
    {
    }

    private async Task<Stream> OpenWithCacheAsync(
        string path,
        RemoteMediaBlockCache cache,
        CancellationToken cancellationToken,
        bool readAheadEnabled = true)
    {
        try
        {
            return await RemoteMediaStream.CreateAsync(
                client,
                cache,
                path,
                cancellationToken,
                cacheIdentity: $"{client.EndpointIdentity}\n{path}",
                readAheadEnabled: readAheadEnabled,
                downloadWholeFile: MediaContainerFormats.IsImage(
                    Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.LocalPath : path)).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            throw new RemoteLibraryIncompatibleException(
                Strings.Resources.RemoteLibraries_WebDavRangeRequired);
        }
    }

    private static bool IsVideoPath(string path)
    {
        var value = Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.AbsolutePath : path;
        return MediaContainerFormats.Video.Contains(Path.GetExtension(value));
    }

    private static string? ParentPath(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) ? new Uri(uri, ".").AbsoluteUri : null;

    private static bool SamePath(string first, string second) =>
        string.Equals(first.TrimEnd('/'), second.TrimEnd('/'), StringComparison.Ordinal);

    private static LibraryMediaInfo Map(FfmpegMediaInfo mediaInfo) => new()
    {
        Album = mediaInfo.Album,
        AlbumArtist = mediaInfo.AlbumArtist,
        Artist = mediaInfo.Artist,
        Date = mediaInfo.Date,
        Title = mediaInfo.Title,
        Comments = mediaInfo.Comments,
        Composer = mediaInfo.Composer,
        Copyright = mediaInfo.Copyright,
        Description = mediaInfo.Description,
        DiscNumber = mediaInfo.DiscNumber,
        Duration = mediaInfo.Duration,
        Genre = mediaInfo.Genre,
        Grouping = mediaInfo.Grouping,
        Performer = mediaInfo.Performer,
        TotalDiscs = mediaInfo.TotalDiscs,
        TotalTracks = mediaInfo.TotalTracks,
        TrackNumber = mediaInfo.TrackNumber,
        AllProperties = mediaInfo.AllProperties.ToDictionary(),
    };

    private sealed class WebDavFile(WebDavEntry entry) : IFile
    {
        public string Name { get; } = entry.Name;
        public string Path { get; } = entry.Path;
        public DateTime LastModifiedUtc { get; } = entry.LastModifiedUtc;
        public long SizeInBytes { get; } = entry.Length;
    }
}

internal sealed class WebDavMediaSourceProvider(
    WebDavMultimediaPlatformIO platform,
    string path) : IMediaSourceProvider, IStreamingMediaSourceProvider
{
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    public string Description => "WebDAV container";

    public StreamingTimeline Timeline { get; private set; }

    public event EventHandler? TimelineChanged;

    public StreamingLoadStage LoadStage { get; private set; } = StreamingLoadStage.Connecting;

    public event EventHandler? LoadStageChanged;

    public IPacketSource OpenMain()
    {
        var source = Open(path, buffered: true);
        Timeline = new StreamingTimeline(false, 0, source.Info.Duration.TotalSeconds, 0, source.CanSeek);
        TimelineChanged?.Invoke(this, EventArgs.Empty);
        return source;
    }

    public IPacketSource OpenSidecar(string sidecarPath) => Open(sidecarPath, buffered: true);

    public Stream OpenFile(string filePath) => platform
        .OpenVolatileMediaFileAsync(filePath, lifetime.Token)
        .GetAwaiter()
        .GetResult();

    public string ReadSubtitleScript(
        int streamIndex,
        string? sidecarPath,
        CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
        using var demuxer = OpenDemuxer(sidecarPath ?? path, linked.Token);
        return FfmpegSubtitleReader.Read(demuxer, streamIndex, linked.Token);
    }

    public BitmapSubtitleDocument ReadBitmapSubtitles(
        int streamIndex,
        string? sidecarPath,
        CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
        using var demuxer = OpenDemuxer(sidecarPath ?? path, linked.Token);
        return FfmpegSubtitleReader.ReadBitmaps(
            demuxer,
            streamIndex,
            linked.Token,
            preserveContainerTimestamps: sidecarPath is not null);
    }

    public void CancelPendingOperations()
    {
        if (!disposed)
            lifetime.Cancel();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private IPacketSource Open(string sourcePath, bool buffered)
    {
        SetLoadStage(StreamingLoadStage.Connecting);
        var stream = platform.OpenVolatileMediaFileAsync(sourcePath, lifetime.Token).GetAwaiter().GetResult();
        try
        {
            SetLoadStage(StreamingLoadStage.ReadingStreamInformation);
            var demuxer = FfmpegDemuxer.Open(stream, sourcePath, leaveOpen: false);
            return buffered && stream is HttpRangeStream remote
                ? new WebDavBufferedPacketSource(demuxer, remote)
                : demuxer;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private FfmpegDemuxer OpenDemuxer(string sourcePath, CancellationToken cancellationToken)
    {
        var stream = platform.OpenVolatileMediaFileAsync(sourcePath, cancellationToken).GetAwaiter().GetResult();
        try
        {
            return FfmpegDemuxer.Open(stream, sourcePath, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void SetLoadStage(StreamingLoadStage value)
    {
        if (LoadStage == value)
            return;
        LoadStage = value;
        LoadStageChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class WebDavBufferedPacketSource(
    FfmpegDemuxer source,
    HttpRangeStream stream) : IBufferedPacketSource
{
    public FfmpegPacketMediaInfo Info => source.Info;
    public int VideoStreamIndex => source.VideoStreamIndex;
    public int AudioStreamIndex => source.AudioStreamIndex;
    public bool CanSeek => source.CanSeek;

    public double BufferedSeconds => OffsetToSeconds(stream.BufferedEndOffset);

    public double HistoryStartSeconds => OffsetToSeconds(stream.BufferedStartOffset);

    public double EstimatedBandwidthBitsPerSecond => 0;

    public CodecParameters GetCodecParameters(int streamIndex) => source.GetCodecParameters(streamIndex);

    public void SelectAudioStream(int streamIndex) => source.SelectAudioStream(streamIndex);

    public PacketReadStatus Read(MediaPacket packet) => source.Read(packet);

    public bool Seek(double positionSeconds) => source.Seek(positionSeconds);

    public double ToSeconds(int streamIndex, long timestamp) => source.ToSeconds(streamIndex, timestamp);

    public void UpdatePlaybackPosition(double positionSeconds)
    {
    }

    public void Dispose() => source.Dispose();

    private double OffsetToSeconds(long offset)
    {
        var duration = Info.Duration.TotalSeconds;
        return stream.Length > 0 && duration > 0
            ? Math.Clamp(offset / (double)stream.Length * duration, 0, duration)
            : MediaTime.NoTimestamp;
    }
}