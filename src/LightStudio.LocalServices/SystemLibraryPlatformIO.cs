using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LocalServices;

/// <summary>Reads system media library items whose identifiers are not filesystem paths.</summary>
public interface ISystemMediaReader
{
    bool Owns(string path);

    /// <summary>Opens a seekable stream over the item's locally stored bytes.</summary>
    Task<Stream> OpenAsync(string path, CancellationToken cancellationToken);

    Task<MediaFileProperties?> GetPropertiesAsync(string path, CancellationToken cancellationToken);

    Task<byte[]?> GetArtworkAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Routes system media library identifiers to their reader and everything else to the file platform.</summary>
public sealed class SystemLibraryPlatformIO(IMultiMediaPlatformIO files, ISystemMediaReader reader)
    : IMultiMediaPlatformIO, IVideoDiscPlatformIO
{
    public int OptimalIoThreadCount => files.OptimalIoThreadCount;

    public string LocalDataPath => files.LocalDataPath;

    public bool SupportsVideoDisc(VideoMediaKind mediaKind) =>
        files is IVideoDiscPlatformIO disc && disc.SupportsVideoDisc(mediaKind);

    public void InitializePlatform()
    {
        files.InitializePlatform();
        IMultiMediaPlatformIO.Instance = this;
    }

    public Task PrepareLibraryDatabaseAsync() => files.PrepareLibraryDatabaseAsync();

    public Task<Stream> OpenMediaFileAsync(string path, CancellationToken cancellationToken = default) =>
        reader.Owns(path) ? reader.OpenAsync(path, cancellationToken) : files.OpenMediaFileAsync(path, cancellationToken);

    public Task<bool> FolderExistsAsync(string path, CancellationToken cancellationToken = default) =>
        reader.Owns(path) ? Task.FromResult(false) : files.FolderExistsAsync(path, cancellationToken);

    public Task<string> NormalizePathAsync(string path, CancellationToken cancellationToken = default) =>
        reader.Owns(path) ? Task.FromResult(path) : files.NormalizePathAsync(path, cancellationToken);

    public Task<MediaFileProperties?> GetMediaFilePropertiesAsync(string path, CancellationToken cancellationToken = default) =>
        reader.Owns(path)
            ? reader.GetPropertiesAsync(path, cancellationToken)
            : files.GetMediaFilePropertiesAsync(path, cancellationToken);

    public Task<string?> ResolveFileRelativeToAsync(
        string sourceFilePath, string referencedFileName, CancellationToken cancellationToken = default) =>
        reader.Owns(sourceFilePath)
            ? Task.FromResult<string?>(null)
            : files.ResolveFileRelativeToAsync(sourceFilePath, referencedFileName, cancellationToken);

    public Task<IFile[]> ListFilesFullNameInPathAsync(string path) => files.ListFilesFullNameInPathAsync(path);

    public Task<string[]> ListFoldersFullNameInPathAsync(string path) => files.ListFoldersFullNameInPathAsync(path);

    public Task<IFile[]> ListFilesFullNameInPathAsync(string path, CancellationToken cancellationToken) =>
        files.ListFilesFullNameInPathAsync(path, cancellationToken);

    public Task<string[]> ListFoldersFullNameInPathAsync(string path, CancellationToken cancellationToken) =>
        files.ListFoldersFullNameInPathAsync(path, cancellationToken);

    public async Task<MediaInfo> GetMediaInfoAsync(string mediaFilePath)
    {
        if (!reader.Owns(mediaFilePath))
            return await files.GetMediaInfoAsync(mediaFilePath).ConfigureAwait(false);
        var stream = await reader.OpenAsync(mediaFilePath, CancellationToken.None).ConfigureAwait(false);
        return await Task.Run(() => FfmpegMediaInfoMapper.Create(FfmpegCodec.GetMediaInfoFromStream(stream))).ConfigureAwait(false);
    }

    public ValueTask YieldScanAsync(CancellationToken cancellationToken) => files.YieldScanAsync(cancellationToken);

    public Task<byte[]?> GetAlbumCoverAsync(string mediaFilePath, CancellationToken cancellationToken = default) =>
        reader.Owns(mediaFilePath)
            ? reader.GetArtworkAsync(mediaFilePath, cancellationToken)
            : files.GetAlbumCoverAsync(mediaFilePath, cancellationToken);

    public async Task<ArtworkBuffer?> GetAlbumCoverBufferAsync(string mediaFilePath, CancellationToken cancellationToken = default)
    {
        if (!reader.Owns(mediaFilePath))
            return await files.GetAlbumCoverBufferAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        var content = await reader.GetArtworkAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        return content is { Length: > 0 } ? ArtworkBuffer.Copy(content) : null;
    }

    public async Task<VideoProbeInfo?> ProbeVideoAsync(
        string mediaFilePath, bool withPreview = true, CancellationToken cancellationToken = default)
    {
        if (!reader.Owns(mediaFilePath))
            return await files.ProbeVideoAsync(mediaFilePath, withPreview, cancellationToken).ConfigureAwait(false);
        await using var stream = await reader.OpenAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => VideoPreviewFactory.Probe(stream, withPreview, leaveOpen: true), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<ImageProbeInfo?> ProbeImageAsync(string mediaFilePath, CancellationToken cancellationToken = default) =>
        reader.Owns(mediaFilePath)
            ? PhotoFolderService.ProbeMetadataAsync(this, mediaFilePath, cancellationToken)
            : files.ProbeImageAsync(mediaFilePath, cancellationToken);

    public async Task<IReadOnlyList<AudioStreamProbeInfo>> ProbeAudioStreamsAsync(
        string mediaFilePath, CancellationToken cancellationToken = default)
    {
        if (!reader.Owns(mediaFilePath))
            return await files.ProbeAudioStreamsAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        await using var stream = await reader.OpenAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => VideoPreviewFactory.ProbeAudioStreams(stream, leaveOpen: true), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MediaProbeReport?> InspectMediaAsync(string mediaFilePath, CancellationToken cancellationToken = default)
    {
        if (!reader.Owns(mediaFilePath))
            return await files.InspectMediaAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        await using var stream = await reader.OpenAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => MediaProbeFactory.Inspect(stream, leaveOpen: true), cancellationToken).ConfigureAwait(false);
    }

    public T GetConfigurationValue<T>(string key) => files.GetConfigurationValue<T>(key);

    public void SetConfigurationValue<T>(string key, T value) => files.SetConfigurationValue(key, value);
}
