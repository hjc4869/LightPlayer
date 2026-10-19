using LightStudio.LightPlayer.Video.Pipeline;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LightStudio.FfmpegShim;
using LightStudio.LightPlayer.Services;
using LightMediaRenderer.Avalonia.Pipeline;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.MediaLibraryCore.Database.Constant;

namespace LightStudio.LocalServices;

public sealed class DesktopMultimediaPlatformIO : IMultiMediaPlatformIO, IVideoDiscPlatformIO
{
    private static readonly string[] FolderArtworkNames =
    [
        "cover.jpg", "cover.jpeg", "folder.jpg", "folder.jpeg", "front.jpg", "front.jpeg",
        "cover.png", "folder.png", "front.png",
    ];

    private readonly object configurationLock = new();
    private readonly Task databasePreparation;
    private Dictionary<string, JsonElement> configuration = new(StringComparer.OrdinalIgnoreCase);

    public DesktopMultimediaPlatformIO()
        : this(DefaultLocalDataPath())
    {
    }

    public DesktopMultimediaPlatformIO(string localDataPath)
    {
        LocalDataPath = Path.GetFullPath(localDataPath);
        Directory.CreateDirectory(LocalDataPath);
        ConfigPath = Path.Combine(LocalDataPath, DatabaseConstants.DbConfigName);
        databasePreparation = Task.Run(() =>
        {
            IndexDatabaseReset.ApplyPending(LocalDataPath);
            LoadConfiguration();
        });
    }

    public int OptimalIoThreadCount => Math.Max(1, Math.Min(Environment.ProcessorCount, 8));

    public string LocalDataPath { get; }

    public bool SupportsVideoDisc(VideoMediaKind mediaKind) => DiscMediaSource.IsSupported(mediaKind);

    public string ConfigPath { get; }

    public void InitializePlatform()
    {
        IMultiMediaPlatformIO.Instance = this;
    }

    public Task PrepareLibraryDatabaseAsync() => databasePreparation;

    public Task<Stream> OpenMediaFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run<Stream>(() => File.OpenRead(Path.GetFullPath(path)), cancellationToken);
    }

    public Task<bool> FolderExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            try
            {
                using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                entries.MoveNext();
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }, cancellationToken);
    }

    public Task<MediaFileProperties?> GetMediaFilePropertiesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run<MediaFileProperties?>(() =>
        {
            var file = new FileInfo(Path.GetFullPath(path));
            return file.Exists
                ? new MediaFileProperties(file.Name, file.FullName, file.DirectoryName, file.Length, file.LastWriteTimeUtc)
                : null;
        }, cancellationToken);
    }

    public Task<string?> ResolveFileRelativeToAsync(
        string sourceFilePath,
        string referencedFileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => ResolveFileRelativeTo(sourceFilePath, referencedFileName), cancellationToken);
    }

    public Task<IFile[]> ListFilesFullNameInPathAsync(string path)
    {
        return Task.Run(() => ListAccessibleEntries<IFile>(path, Directory.EnumerateFiles,
            file => new FileSystemFile(new FileInfo(file))));
    }

    public Task<string[]> ListFoldersFullNameInPathAsync(string path)
    {
        return Task.Run(() => ListAccessibleEntries(path, Directory.EnumerateDirectories, folder =>
        {
            using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            entries.MoveNext();
            return folder;
        }));
    }

    private static T[] ListAccessibleEntries<T>(string path, Func<string, IEnumerable<string>> enumerate, Func<string, T> read)
    {
        var result = new List<T>();
        try
        {
            foreach (var entry in enumerate(path))
            {
                try
                {
                    if (Path.GetFileName(entry).StartsWith('.') ||
                        (File.GetAttributes(entry) & FileAttributes.Hidden) != 0)
                    {
                        continue;
                    }
                    result.Add(read(entry));
                }
                catch (Exception exception) when (FileAccessErrors.IsAccessDenied(exception))
                {
                    AppLog.Warning("Library.FileAccess", $"Skipping inaccessible item {entry}.", exception);
                }
            }
        }
        catch (Exception exception) when (FileAccessErrors.IsAccessDenied(exception))
        {
            AppLog.Warning("Library.FileAccess", $"Could not enumerate inaccessible folder {path}.", exception);
        }
        return result.ToArray();
    }

    public Task<MediaInfo> GetMediaInfoAsync(string mediaFilePath)
    {
        return Task.Run(() =>
        {
            string fullPath = Path.GetFullPath(mediaFilePath);
            if (IsDiscSource(fullPath))
            {
                using var demuxer = DiscMediaSource.TryOpenDetected(fullPath, out _, out _);
                return demuxer is null
                    ? new MediaInfo { AllProperties = new Dictionary<string, string>() }
                    : FfmpegMediaInfoMapper.Create(demuxer.Info);
            }

            var mediaInfo = FfmpegCodec.GetMediaInfoFromStream(File.OpenRead(fullPath));
            return FfmpegMediaInfoMapper.Create(mediaInfo);
        });
    }

    public Task<VideoProbeInfo?> ProbeVideoAsync(
        string mediaFilePath,
        bool withPreview = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            string fullPath = Path.GetFullPath(mediaFilePath);
            if (IsDiscSource(fullPath))
            {
                return DiscMediaSource.Probe(fullPath, withPreview);
            }

            using var stream = File.OpenRead(fullPath);
            return VideoPreviewFactory.Probe(stream, withPreview, leaveOpen: true);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AudioStreamProbeInfo>> ProbeAudioStreamsAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run<IReadOnlyList<AudioStreamProbeInfo>>(() =>
        {
            using var stream = File.OpenRead(Path.GetFullPath(mediaFilePath));
            return VideoPreviewFactory.ProbeAudioStreams(stream, leaveOpen: true);
        }, cancellationToken);
    }

    public Task<ImageProbeInfo?> ProbeImageAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default) =>
        PhotoFolderService.ProbeMetadataAsync(this, mediaFilePath, cancellationToken);

    public Task<MediaProbeReport?> InspectMediaAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            string fullPath = Path.GetFullPath(mediaFilePath);
            if (IsDiscSource(fullPath))
            {
                using var demuxer = DiscMediaSource.TryOpenDetected(fullPath, out _, out _);
                return demuxer is null ? null : MediaProbeFactory.Inspect(demuxer);
            }

            using var stream = File.OpenRead(fullPath);
            return MediaProbeFactory.Inspect(stream, leaveOpen: true);
        }, cancellationToken);
    }

    private static bool IsDiscSource(string path) =>
        Directory.Exists(path) || MediaContainerFormats.DiscImage.Contains(Path.GetExtension(path));

    public Task<byte[]?> GetAlbumCoverAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => ReadAlbumCover(mediaFilePath, static data => data.ToArray(), File.ReadAllBytes, cancellationToken),
            cancellationToken);
    }

    public Task<ArtworkBuffer?> GetAlbumCoverBufferAsync(
        string mediaFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => ReadAlbumCover(
                mediaFilePath,
                ArtworkBuffer.Copy,
                static path =>
                {
                    using var stream = File.OpenRead(path);
                    return ArtworkBuffer.Read(stream, stream.Length);
                },
                cancellationToken),
            cancellationToken);
    }

    private static T? ReadAlbumCover<T>(
        string mediaFilePath,
        Func<ReadOnlySpan<byte>, T> readEmbedded,
        Func<string, T?> readFile,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var stream = File.OpenRead(Path.GetFullPath(mediaFilePath));
            if (FfmpegCodec.ReadAlbumCoverFromStream(stream, readEmbedded) is { } embedded)
            {
                return embedded;
            }
        }
        catch (Exception)
        {
            // Fall through to conventional artwork next to the media file.
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(mediaFilePath));
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                files.TryAdd(Path.GetFileName(path), path);
            }

            foreach (var name in FolderArtworkNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (files.TryGetValue(name, out var path))
                {
                    return readFile(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    public T GetConfigurationValue<T>(string key)
    {
        lock (configurationLock)
        {
            if (!configuration.TryGetValue(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return default!;
            }

            return value.Deserialize((JsonTypeInfo<T>)ConfigurationJsonContext.Default.GetTypeInfo(typeof(T))!)!;
        }
    }

    public void SetConfigurationValue<T>(string key, T value)
    {
        lock (configurationLock)
        {
            configuration[key] = JsonSerializer.SerializeToElement(value, (JsonTypeInfo<T>)ConfigurationJsonContext.Default.GetTypeInfo(typeof(T))!);
            SaveConfiguration();
        }
    }

    private static string DefaultLocalDataPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        return Path.Combine(root, "LightStudio.LightPlayer");
    }

    private static string? ResolveFileRelativeTo(string sourceFilePath, string referencedFileName)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || string.IsNullOrWhiteSpace(referencedFileName))
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(sourceFilePath));
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            var platformReference = referencedFileName
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.IsPathRooted(platformReference)
                ? platformReference
                : Path.Combine(directory, platformReference);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            // CUE sheets are often moved between case-sensitive and case-insensitive systems or
            // retain separators from another OS. Fall back to a name-only match in the same folder.
            var name = PortableFileName(referencedFileName);
            return Directory.EnumerateFiles(directory)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string PortableFileName(string path)
    {
        var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return separator >= 0 ? path[(separator + 1)..] : path;
    }

    private void LoadConfiguration()
    {
        if (!File.Exists(ConfigPath))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), ConfigurationJsonContext.Default.DictionaryStringJsonElement);
            configuration = loaded is null
                ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException exception)
        {
            var backupPath = $"{ConfigPath}.corrupt-{Guid.NewGuid():N}";
            File.Move(ConfigPath, backupPath);
            AppLog.Warning("Library.Configuration", $"Invalid configuration at {ConfigPath}; preserved at {backupPath}. Using defaults.", exception);
        }
    }

    private void SaveConfiguration()
    {
        Directory.CreateDirectory(LocalDataPath);
        var temporaryPath = $"{ConfigPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, configuration, ConfigurationJsonContext.Default.DictionaryStringJsonElement);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, ConfigPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Warning("Library.Configuration", $"Could not remove temporary configuration at {temporaryPath}.", exception);
            }
        }
    }

    private sealed class FileSystemFile(FileInfo file) : IFile
    {
        public string Name { get; } = file.Name;

        public string Path { get; } = file.FullName;

        public DateTime LastModifiedUtc { get; } = file.LastWriteTimeUtc;

        public long SizeInBytes { get; } = file.Length;

        public DateTime? CreatedUtc { get; } = file.CreationTimeUtc > DateTime.UnixEpoch ? file.CreationTimeUtc : null;
    }
}

/// <summary>Maps FFmpeg's tag reading onto the library's own metadata record.</summary>
public static class FfmpegMediaInfoMapper
{
    public static MediaInfo Create(FfmpegMediaInfo mediaInfo)
    {
        return new MediaInfo
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
    }

    public static MediaInfo Create(LightStudio.FfmpegShim.Media.MediaInfo mediaInfo)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo);
        var metadata = new Dictionary<string, string>(mediaInfo.Metadata, StringComparer.OrdinalIgnoreCase);
        string Metadata(params string[] names)
        {
            foreach (string name in names)
                if (metadata.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value))
                    return value;
            return string.Empty;
        }

        string[] track = Metadata("track", "tracknumber").Split('/', 2, StringSplitOptions.TrimEntries);
        string[] disc = Metadata("disc", "discnumber").Split('/', 2, StringSplitOptions.TrimEntries);
        string totalTracks = Metadata("tracks", "totaltracks", "tracktotal");
        string totalDiscs = Metadata("discs", "totaldiscs", "disctotal");

        return new MediaInfo
        {
            Album = Metadata("album"),
            AlbumArtist = Metadata("album_artist", "albumartist", "album artist"),
            Artist = Metadata("artist"),
            Comments = Metadata("comment"),
            Composer = Metadata("composer"),
            Copyright = Metadata("copyright"),
            Date = Metadata("date"),
            Description = Metadata("description"),
            DiscNumber = disc[0],
            Duration = mediaInfo.Duration,
            Genre = Metadata("genre"),
            Grouping = Metadata("grouping"),
            Performer = Metadata("performer"),
            Title = Metadata("title"),
            TotalDiscs = totalDiscs.Length > 0 ? totalDiscs : disc.Length > 1 ? disc[1] : string.Empty,
            TotalTracks = totalTracks.Length > 0 ? totalTracks : track.Length > 1 ? track[1] : string.Empty,
            TrackNumber = track[0],
            AllProperties = metadata,
        };
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class ConfigurationJsonContext : JsonSerializerContext
{
}