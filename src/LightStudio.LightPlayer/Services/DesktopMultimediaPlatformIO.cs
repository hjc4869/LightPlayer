using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LightStudio.FfmpegShim;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Services;

internal sealed class DesktopMultimediaPlatformIO : IMultiMediaPlatformIO
{
    private readonly object configurationLock = new();
    private Dictionary<string, JsonElement> configuration = new(StringComparer.OrdinalIgnoreCase);

    public DesktopMultimediaPlatformIO()
        : this(DefaultLocalDataPath())
    {
    }

    public DesktopMultimediaPlatformIO(string localDataPath)
    {
        LocalDataPath = Path.GetFullPath(localDataPath);
        Directory.CreateDirectory(LocalDataPath);
        ConfigPath = Path.Combine(LocalDataPath, "media-library-settings.json");
        LoadConfiguration();
    }

    public int OptimalIoThreadCount => Math.Max(1, Math.Min(Environment.ProcessorCount, 8));

    public string LocalDataPath { get; }

    public string ConfigPath { get; }

    public void InitializePlatform()
    {
        IMultiMediaPlatformIO.Instance = this;
    }

    public Task<Stream> OpenMediaFileAsync(string path)
    {
        return Task.FromResult<Stream>(File.OpenRead(Path.GetFullPath(path)));
    }

    public Task<IFile[]> ListFilesFullNameInPathAsync(string path)
    {
        var files = Directory.EnumerateFiles(path)
            .Select(file => new FileSystemFile(new FileInfo(file)))
            .Cast<IFile>()
            .ToArray();
        return Task.FromResult(files);
    }

    public Task<string[]> ListFoldersFullNameInPathAsync(string path)
    {
        return Task.FromResult(Directory.GetDirectories(path));
    }

    public Task<MediaInfo> GetMediaInfoAsync(string mediaFilePath)
    {
        var mediaInfo = FfmpegCodec.GetMediaInfoFromStream(File.OpenRead(mediaFilePath));
        return Task.FromResult(FfmpegMediaInfoMapper.Create(mediaInfo));
    }

    public async Task<byte[]> GetAlbumCoverAsync(string mediaFilePath)
    {
        var coverStream = FfmpegCodec.GetAlbumCoverFromStream(File.OpenRead(mediaFilePath));
        if (coverStream is null)
        {
            return null!;
        }

        await using (coverStream)
        {
            using var memory = new MemoryStream();
            await coverStream.CopyToAsync(memory);
            return memory.ToArray();
        }
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

    private void LoadConfiguration()
    {
        if (!File.Exists(ConfigPath))
        {
            return;
        }

        var loaded = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), ConfigurationJsonContext.Default.DictionaryStringJsonElement);
        configuration = loaded is null
            ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(loaded, StringComparer.OrdinalIgnoreCase);
    }

    private void SaveConfiguration()
    {
        Directory.CreateDirectory(LocalDataPath);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(configuration, ConfigurationJsonContext.Default.DictionaryStringJsonElement));
    }

    private sealed class FileSystemFile(FileInfo file) : IFile
    {
        public string Name { get; } = file.Name;

        public string Path { get; } = file.FullName;

        public DateTime LastModifiedUtc { get; } = file.LastWriteTimeUtc;
    }
}

internal static class FfmpegMediaInfoMapper
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