#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.IO;

public interface IMultiMediaPlatformIO
{
    public static IMultiMediaPlatformIO Instance { get; protected set; } = null!;
    int OptimalIoThreadCount { get; }
    string LocalDataPath { get; }

    /// <summary>Publishes this implementation as the process-wide <see cref="Instance"/>.</summary>
    void InitializePlatform();

    /// <summary>Prepares local index storage before reading configuration or opening databases.</summary>
    Task PrepareLibraryDatabaseAsync() => Task.CompletedTask;

    /// <summary>Opens a platform-specific media identifier supplied unchanged by the caller.</summary>
    /// <param name="path">A local path, remote-server path, content URI, or other platform identifier.</param>
    Task<Stream> OpenMediaFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Checks whether a platform-specific folder identifier exists and is accessible.</summary>
    Task<bool> FolderExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Returns a hierarchical path for a stored platform identifier, preserving access to the same item.</summary>
    Task<string> NormalizePathAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(path);

    /// <summary>Returns filesystem-like properties without interpreting the identifier on the caller's OS.</summary>
    Task<MediaFileProperties?> GetMediaFilePropertiesAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a file reference relative to the folder containing <paramref name="sourceFilePath"/>.
    /// Returns <see langword="null"/> when the reference is missing or this platform cannot resolve it.
    /// </summary>
    Task<string?> ResolveFileRelativeToAsync(
        string sourceFilePath,
        string referencedFileName,
        CancellationToken cancellationToken = default);

    Task<IFile[]> ListFilesFullNameInPathAsync(string path);
    Task<string[]> ListFoldersFullNameInPathAsync(string path);
    Task<MediaInfo> GetMediaInfoAsync(string mediaFilePath);

    async Task<IFile[]> ListFilesFullNameInPathAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = await ListFilesFullNameInPathAsync(path).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return files;
    }

    async Task<string[]> ListFoldersFullNameInPathAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folders = await ListFoldersFullNameInPathAsync(path).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return folders;
    }

    async Task<MediaInfo> GetMediaInfoAsync(string mediaFilePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = await GetMediaInfoAsync(mediaFilePath).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return info;
    }

    ValueTask YieldScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Gets encoded artwork for a media identifier, including any platform-supported conventional
    /// image beside the media when no embedded cover exists.
    /// </summary>
    Task<byte[]?> GetAlbumCoverAsync(string mediaFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the same artwork as <see cref="GetAlbumCoverAsync"/> in native memory, which the caller
    /// disposes. Platforms that can read covers without a managed array override this.
    /// </summary>
    async Task<ArtworkBuffer?> GetAlbumCoverBufferAsync(string mediaFilePath, CancellationToken cancellationToken = default)
    {
        byte[]? content = await GetAlbumCoverAsync(mediaFilePath, cancellationToken).ConfigureAwait(false);
        return content is { Length: > 0 } ? ArtworkBuffer.Copy(content) : null;
    }

    /// <summary>
    /// Inspects a media identifier that may hold video. Returns <see langword="null"/> when the file
    /// carries no video stream (attached cover art does not count), which is how the video indexer
    /// filters audio-only files out.
    /// </summary>
    /// <param name="withPreview">
    /// When true the platform also produces an encoded preview frame taken from a little way into the
    /// video. The preview is best-effort and may be null even on success.
    /// </param>
    Task<VideoProbeInfo?> ProbeVideoAsync(
        string mediaFilePath,
        bool withPreview = true,
        CancellationToken cancellationToken = default);

    Task<ImageProbeInfo?> ProbeImageAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default) => Task.FromResult<ImageProbeInfo?>(null);

    /// <summary>
    /// Reads audio stream identities and codecs for scan-time persistence without decoding media.
    /// Platforms that never host a library may return an empty list.
    /// </summary>
    Task<IReadOnlyList<AudioStreamProbeInfo>> ProbeAudioStreamsAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes the container and every elementary stream of a media identifier, at the level of
    /// detail a properties view shows. Returns <see langword="null"/> when the platform cannot open
    /// or inspect the file.
    /// </summary>
    Task<MediaProbeReport?> InspectMediaAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default);

    T GetConfigurationValue<T>(string key);
    void SetConfigurationValue<T>(string key, T value);
}

/// <summary>Implemented by platforms that can give FFmpeg a native DVD or Blu-ray path.</summary>
public interface IVideoDiscPlatformIO
{
    bool SupportsVideoDisc(VideoMediaKind mediaKind);
}
