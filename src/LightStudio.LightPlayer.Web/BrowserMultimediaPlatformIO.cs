using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;
using LightStudio.FfmpegShim.Engine;
using LightStudio.LightPlayer.Services;
using LightStudio.LocalServices;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.Logging;
using LightMediaRenderer.Avalonia.Controls;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// Library I/O over the folders the user granted this page with the browser's file picker.
/// </summary>
/// <remarks>
/// Media identifiers are <see cref="BrowserMediaPath"/> strings rather than filesystem paths.
/// Media probes and embedded artwork are produced by the browser media worker.
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class BrowserMultimediaPlatformIO : IMultiMediaPlatformIO
{
    private static readonly string[] FolderArtworkNames =
    [
        "cover.jpg", "cover.jpeg", "folder.jpg", "folder.jpeg", "front.jpg", "front.jpeg",
        "cover.png", "folder.png", "front.png",
    ];

    /// <summary>
    /// Describes the database in <see cref="LocalDataPath"/>, so it must not outlive it. The only
    /// value the library keeps here is the schema migration level, and persisting that would claim
    /// a schema exists whenever the restored index does not. A first run that never scans has
    /// nothing to restore, and the browser may drop the stored copy at any time. Held in memory,
    /// the marker resets with the runtime's filesystem and the schema is re-created to match.
    /// </summary>
    private readonly Dictionary<string, string> configuration = [];
    private readonly Func<string, CancellationToken, Task<Stream>>? openMediaFile;

    public BrowserMultimediaPlatformIO(Func<string, CancellationToken, Task<Stream>>? openMediaFile = null)
    {
        this.openMediaFile = openMediaFile;
        Directory.CreateDirectory(LocalDataPath);
    }

    /// <summary>
    /// Probe concurrency follows the number of background media workers.
    /// </summary>
    public int OptimalIoThreadCount => BackgroundMediaJobs.Count;

    /// <summary>
    /// The logical application data root used by browser storage services.
    /// </summary>
    public string LocalDataPath => PlatformServices.AppDataPath;

    public void InitializePlatform() => IMultiMediaPlatformIO.Instance = this;

    public async Task<Stream> OpenMediaFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (openMediaFile is not null)
            return await openMediaFile(path, cancellationToken).ConfigureAwait(false);
        return await BrowserFileStream.OpenAsync(Parse(path), cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> FolderExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return BrowserMediaPath.TryParse(path, out var parsed)
            ? BrowserFileSystem.DirectoryExistsAsync(parsed)
            : Task.FromResult(false);
    }

    public async Task<MediaFileProperties?> GetMediaFilePropertiesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = Parse(path);
        var parent = parsed.Parent();
        if (parent is not { } directory)
        {
            return null;
        }

        var entry = (await BrowserFileSystem.ListAsync(directory, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(child => !child.IsDirectory && child.Name == parsed.Name);
        return entry.Name is null
            ? null
            : new MediaFileProperties(entry.Name, path, directory.ToString(), entry.Length, entry.LastModifiedUtc);
    }

    public async Task<string?> ResolveFileRelativeToAsync(
        string sourceFilePath,
        string referencedFileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(referencedFileName) ||
            !BrowserMediaPath.TryParse(sourceFilePath, out var parsed) ||
            parsed.Parent() is not { } directory)
        {
            return null;
        }

        // CUE sheets and playlists carry names written on another operating system, so only the
        // last segment is trusted and the match ignores case.
        var separator = Math.Max(referencedFileName.LastIndexOf('/'), referencedFileName.LastIndexOf('\\'));
        var name = separator >= 0 ? referencedFileName[(separator + 1)..] : referencedFileName;
        var entries = await BrowserFileSystem.ListAsync(directory, cancellationToken).ConfigureAwait(false);
        var match = entries.FirstOrDefault(child =>
            !child.IsDirectory && string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));
        return match.Name is null ? null : directory.Child(match.Name).ToString();
    }

    public Task<IFile[]> ListFilesFullNameInPathAsync(string path) =>
        ListFilesFullNameInPathAsync(path, CancellationToken.None);

    public async Task<IFile[]> ListFilesFullNameInPathAsync(string path, CancellationToken cancellationToken)
    {
        var parsed = Parse(path);
        var files = new List<IFile>();
        int count = 0;
        foreach (var entry in await BrowserFileSystem.ListAsync(parsed, cancellationToken).ConfigureAwait(false))
        {
            if (!entry.IsDirectory)
                files.Add(new BrowserFile(entry.Name, parsed.Child(entry.Name).ToString(), entry.LastModifiedUtc, entry.Length));
            if (++count % 64 == 0) await YieldScanAsync(cancellationToken);
        }
        return files.ToArray();
    }

    public Task<string[]> ListFoldersFullNameInPathAsync(string path) =>
        ListFoldersFullNameInPathAsync(path, CancellationToken.None);

    public async Task<string[]> ListFoldersFullNameInPathAsync(string path, CancellationToken cancellationToken)
    {
        var parsed = Parse(path);
        var folders = new List<string>();
        int count = 0;
        foreach (var entry in await BrowserFileSystem.ListAsync(parsed, cancellationToken).ConfigureAwait(false))
        {
            if (entry.IsDirectory) folders.Add(parsed.Child(entry.Name).ToString());
            if (++count % 64 == 0) await YieldScanAsync(cancellationToken);
        }
        return folders.ToArray();
    }

    public Task<MediaInfo> GetMediaInfoAsync(string mediaFilePath) =>
        GetMediaInfoAsync(mediaFilePath, CancellationToken.None);

    public async Task<MediaInfo> GetMediaInfoAsync(string mediaFilePath, CancellationToken cancellationToken)
    {
        var tags = await BrowserMediaOperations.ReadTagsAsync(Input(mediaFilePath), cancellationToken);
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in tags.Properties)
            properties.TryAdd(key, value);
        return FfmpegMediaInfoMapper.Create(new FfmpegMediaInfo(TimeSpan.FromTicks(tags.DurationTicks), properties));
    }

    public async ValueTask YieldScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Delay(1, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<VideoProbeInfo?> ProbeVideoAsync(
        string mediaFilePath,
        bool withPreview = true,
        CancellationToken cancellationToken = default)
    {
        var (probe, preview) = await BrowserMediaOperations.ProbeVideoAsync(Input(mediaFilePath), withPreview,
            VideoPreviewFactory.PreviewMaxDimension, cancellationToken);
        return probe is null ? null : VideoPreviewFactory.CreateProbeInfo(probe, preview);
    }

    public async Task<IReadOnlyList<AudioStreamProbeInfo>> ProbeAudioStreamsAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default) =>
        VideoPreviewFactory.MapAudioStreams(await BrowserMediaOperations.ProbeAudioStreamsAsync(Input(mediaFilePath), cancellationToken));

    public async Task<ImageProbeInfo?> ProbeImageAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        var probe = await BrowserMediaOperations.ProbeImageAsync(Input(mediaFilePath), cancellationToken);
        return new ImageProbeInfo(probe.Width, probe.Height,
            DateTimeOffset.TryParse(probe.DateTaken, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var taken)
                ? taken : null,
            probe.Latitude, probe.Longitude);
    }

    public async Task<MediaProbeReport?> InspectMediaAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default) =>
        await BrowserMediaOperations.InspectAsync(Input(mediaFilePath), cancellationToken) is { } inspection
            ? MediaProbeFactory.Map(inspection)
            : null;

    public Task<byte[]?> GetAlbumCoverAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default) =>
        ReadAlbumCoverAsync(
            mediaFilePath,
            static data => data.ToArray(),
            static async (stream, token) =>
            {
                var buffer = new byte[stream.Length];
                await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
                return buffer;
            },
            cancellationToken);

    public Task<ArtworkBuffer?> GetAlbumCoverBufferAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default) =>
        ReadAlbumCoverAsync(
            mediaFilePath,
            ArtworkBuffer.Copy,
            static (stream, token) => ArtworkBuffer.ReadAsync(stream, stream.Length, token),
            cancellationToken);

    internal Task<FastImageSource?> LoadArtworkAsync(string path, int pixelSize, CancellationToken cancellationToken) =>
        ReadAlbumCoverCoreAsync(path,
            token => BrowserImageDecoder.ReadArtworkAsync(path, token => OpenMediaFileAsync(path, token), true, pixelSize, token),
            (artwork, token) => BrowserImageDecoder.ReadArtworkAsync(artwork.ToString(), null, false, pixelSize, token),
            cancellationToken);

    private Task<T?> ReadAlbumCoverAsync<T>(
        string mediaFilePath,
        Func<ReadOnlySpan<byte>, T> readEmbedded,
        Func<Stream, CancellationToken, Task<T?>> readFile,
        CancellationToken cancellationToken)
        where T : class
        => ReadAlbumCoverCoreAsync(mediaFilePath,
            async token => await BrowserMediaOperations.ReadAlbumCoverAsync(Input(mediaFilePath), token) is { Length: > 0 } cover
                ? readEmbedded(cover)
                : null,
            async (path, token) =>
            {
                await using var artwork = await BrowserFileStream.OpenAsync(path, token).ConfigureAwait(false);
                if (artwork.Length > 32 * 1024 * 1024)
                    throw new InvalidDataException("Adjacent artwork exceeds the encoded byte budget.");
                return await readFile(artwork, token).ConfigureAwait(false);
            }, cancellationToken);

    private async Task<T?> ReadAlbumCoverCoreAsync<T>(string mediaFilePath,
        Func<CancellationToken, Task<T?>> readEmbedded,
        Func<BrowserMediaPath, CancellationToken, Task<T?>> readFile,
        CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BrowserMediaPath.TryParse(mediaFilePath, out var local) &&
            BrowserFileSystem.GetAccess(local.RootId) != BrowserFolderAccess.Granted)
        {
            return null;
        }

        try
        {
            var embedded = await readEmbedded(cancellationToken).ConfigureAwait(false);
            if (embedded is not null) return embedded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            AppLog.Warning("Web.Media", $"Could not read embedded artwork from {mediaFilePath}.", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!BrowserMediaPath.TryParse(mediaFilePath, out var parsed) || parsed.Parent() is not { } directory)
        {
            return null;
        }

        try
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in await BrowserFileSystem.ListAsync(directory, cancellationToken).ConfigureAwait(false))
            {
                if (!entry.IsDirectory)
                {
                    files.TryAdd(entry.Name, entry.Name);
                }
            }

            foreach (var name in FolderArtworkNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!files.TryGetValue(name, out var actual))
                {
                    continue;
                }

                return await readFile(directory.Child(actual), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            AppLog.Warning("Web.Media", $"Could not read adjacent artwork for {mediaFilePath}.", exception);
        }

        return null;
    }

    public T GetConfigurationValue<T>(string key)
    {
        string? stored;
        lock (configuration)
        {
            stored = configuration.GetValueOrDefault(key);
        }

        if (stored is null)
        {
            return default!;
        }

        if (typeof(T) == typeof(string[]))
        {
            return (T)(object)stored.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        try
        {
            return (T)Convert.ChangeType(stored, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            return default!;
        }
    }

    public void SetConfigurationValue<T>(string key, T value)
    {
        lock (configuration)
        {
            if (value is null)
            {
                configuration.Remove(key);
                return;
            }

            configuration[key] = value is string[] items
                ? string.Join('\n', items)
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    private MediaInput Input(string path) => BrowserSessionInput.Describe(path, token => OpenMediaFileAsync(path, token));

    private static BrowserMediaPath Parse(string path) =>
        BrowserMediaPath.TryParse(path, out var parsed)
            ? parsed
            : throw new FileNotFoundException($"'{path}' is not a local browser media identifier.", path);

    private sealed class BrowserFile(string name, string path, DateTime lastModifiedUtc, long sizeInBytes) : IFile
    {
        public string Name { get; } = name;

        public string Path { get; } = path;

        public DateTime LastModifiedUtc { get; } = lastModifiedUtc;

        public long SizeInBytes { get; } = sizeInBytes;
    }
}
