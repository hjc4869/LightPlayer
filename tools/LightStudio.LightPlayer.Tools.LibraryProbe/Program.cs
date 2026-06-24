using System.Text.Json;
using LightStudio.FfmpegShim;
using LightStudio.MediaLibraryCore;
using LightStudio.MediaLibraryCore.CueIndex;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.MediaLibraryCore.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LightStudio.LightPlayer.Tools.LibraryProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var command = args[0];
            var options = CommandOptions.Parse(args.Skip(1).ToArray());

            return command switch
            {
                "startup-info" => await StartupInfoAsync(options),
                "info" => await InfoAsync(options),
                "cover" => await CoverAsync(options),
                "scan" => await ScanAsync(options),
                "query" => await QueryAsync(options),
                "cue" => await CueAsync(options),
                _ => UnknownCommand(command)
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<int> StartupInfoAsync(CommandOptions options)
    {
        var platform = InitializePlatform(options.RequireDataDir());
        await ApplicationServiceBase.App.ConfigureServicesAsync();

        var databasePath = ApplicationServiceBase.ResolveDatabasePath("library-v5.sqlite");
        Console.WriteLine($"OS: {Environment.OSVersion}");
        Console.WriteLine($"Process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Data directory: {platform.LocalDataPath}");
        Console.WriteLine($"Config file: {platform.ConfigPath}");
        Console.WriteLine($"Database file: {databasePath}");
        Console.WriteLine($"FFmpeg root: {FfmpegNativeInitializer.RootPath ?? "system loader"}");
        Console.WriteLine($"IO worker count: {platform.OptimalIoThreadCount}");
        return 0;
    }

    private static async Task<int> InfoAsync(CommandOptions options)
    {
        var mediaFile = options.RequireArgument("media-file");
        var platform = InitializePlatform(options.DataDirOrDefault());
        var info = await platform.GetMediaInfoAsync(mediaFile);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Path = Path.GetFullPath(mediaFile),
            info.Title,
            info.Artist,
            info.Album,
            info.AlbumArtist,
            info.Date,
            info.TrackNumber,
            info.TotalTracks,
            info.DiscNumber,
            info.TotalDiscs,
            info.Genre,
            Duration = info.Duration.ToString(),
            info.AllProperties
        }, JsonOptions));
        return 0;
    }

    private static async Task<int> CoverAsync(CommandOptions options)
    {
        var mediaFile = options.RequireArgument("media-file");
        var outputFile = options.RequireOption("out");
        var platform = InitializePlatform(options.DataDirOrDefault());

        var cover = await platform.GetAlbumCoverAsync(mediaFile);
        if (cover is null || cover.Length == 0)
        {
            Console.Error.WriteLine("No embedded album cover found.");
            return 3;
        }

        var outputPath = Path.GetFullPath(outputFile);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        await File.WriteAllBytesAsync(outputPath, cover);
        Console.WriteLine($"Wrote {cover.Length} bytes to {outputPath}");
        return 0;
    }

    private static async Task<int> ScanAsync(CommandOptions options)
    {
        var folder = Path.GetFullPath(options.RequireArgument("folder"));
        if (!Directory.Exists(folder))
        {
            throw new ArgumentException($"Folder does not exist: {folder}");
        }

        InitializePlatform(options.RequireDataDir());
        await ApplicationServiceBase.App.ConfigureServicesAsync();

        using var scope = ApplicationServiceBase.App.GetScope();
        var indexer = scope.ServiceProvider.GetRequiredService<FileIndexer>();
        indexer.IndexItemAdded += (_, count) => Console.WriteLine($"Indexed items: {count}");

        var thumbnail = options.HasFlag("no-thumbnails")
            ? new NoopThumbnailOperations()
            : new NoopThumbnailOperations();

        var result = await indexer.ScanAsync(new[] { folder }, thumbnail);
        Console.WriteLine($"Scan result: {result.Item1} indexed, {result.Item2.Count} warnings");
        foreach (var warning in result.Item2.Take(20))
        {
            Console.Error.WriteLine($"Warning: {warning.Item1}: {warning.Item2.GetBaseException().Message}");
        }

        return result.Item1 < 0 ? 1 : 0;
    }

    private static async Task<int> QueryAsync(CommandOptions options)
    {
        InitializePlatform(options.RequireDataDir());
        await ApplicationServiceBase.App.ConfigureServicesAsync();

        var anyQueryFlag = options.HasFlag("songs") || options.HasFlag("albums") || options.HasFlag("artists");
        var showSongs = options.HasFlag("songs") || !anyQueryFlag;
        var showAlbums = options.HasFlag("albums") || !anyQueryFlag;
        var showArtists = options.HasFlag("artists") || !anyQueryFlag;

        using var scope = ApplicationServiceBase.App.GetScope();
        await using var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>();

        if (showSongs)
        {
            var songCount = await context.MediaFiles.CountAsync();
            Console.WriteLine($"Songs: {songCount}");
            var songs = await context.MediaFiles
                .OrderBy(file => file.Artist)
                .ThenBy(file => file.Album)
                .ThenBy(file => file.DiscNumber)
                .ThenBy(file => file.TrackNumber)
                .Take(10)
                .Select(file => new { file.Id, file.Title, file.Artist, file.Album, file.Path })
                .ToListAsync();
            foreach (var song in songs)
            {
                Console.WriteLine($"  [{song.Id}] {song.Artist} - {song.Title} ({song.Album})");
                Console.WriteLine($"      {song.Path}");
            }
        }

        if (showAlbums)
        {
            var albums = await LibraryHelper.ListAlbumsAsync();
            Console.WriteLine($"Albums: {albums.Count}");
            foreach (var album in albums.OrderBy(album => album.Artist).ThenBy(album => album.Title).Take(10))
            {
                Console.WriteLine($"  {album.Artist} - {album.Title}: {album.FileCount} tracks");
            }
        }

        if (showArtists)
        {
            var artists = await LibraryHelper.ListArtistsAsync();
            Console.WriteLine($"Artists: {artists.Count}");
            foreach (var artist in artists.OrderBy(artist => artist.Name).Take(10))
            {
                Console.WriteLine($"  {artist.Name}: {artist.AlbumCount} albums, {artist.FileCount} tracks");
            }
        }

        return 0;
    }

    private static async Task<int> CueAsync(CommandOptions options)
    {
        var cueFile = Path.GetFullPath(options.RequireArgument("cue-file"));
        if (!File.Exists(cueFile))
        {
            throw new ArgumentException($"Cue file does not exist: {cueFile}");
        }

        await using var stream = File.OpenRead(cueFile);
        var cue = await CueFile.CreateFromStreamAsync(stream);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Path = cueFile,
            cue.FileName,
            Tracks = cue.Indices.Select(track => new
            {
                Start = track.StartTime.ToString(),
                Duration = track.Duration.ToString(),
                track.TrackInfo.Title,
                track.TrackInfo.Artist,
                track.TrackInfo.Album
            })
        }, JsonOptions));
        return 0;
    }

    private static FileSystemMultimediaPlatformIO InitializePlatform(string dataDir)
    {
        var platform = new FileSystemMultimediaPlatformIO(dataDir);
        platform.InitializePlatform();
        return platform;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("libraryprobe startup-info --data <data-dir>");
        Console.WriteLine("libraryprobe info <media-file> [--data <data-dir>]");
        Console.WriteLine("libraryprobe cover <media-file> --out <image-file> [--data <data-dir>]");
        Console.WriteLine("libraryprobe scan <folder> --data <data-dir> [--no-thumbnails]");
        Console.WriteLine("libraryprobe query --data <data-dir> [--songs] [--albums] [--artists]");
        Console.WriteLine("libraryprobe cue <cue-file>");
    }

    private sealed class CommandOptions
    {
        private readonly List<string> arguments = new();
        private readonly Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);

        public static CommandOptions Parse(string[] args)
        {
            var parsed = new CommandOptions();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    parsed.arguments.Add(arg);
                    continue;
                }

                var key = arg[2..];
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new ArgumentException("Option name cannot be empty.");
                }

                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    parsed.options[key] = args[++i];
                }
                else
                {
                    parsed.options[key] = null;
                }
            }

            return parsed;
        }

        public bool HasFlag(string name) => options.ContainsKey(name) && options[name] is null;

        public string RequireArgument(string name)
        {
            if (arguments.Count == 0)
            {
                throw new ArgumentException($"Missing required argument: {name}");
            }

            return arguments[0];
        }

        public string RequireOption(string name)
        {
            if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Missing required option: --{name}");
            }

            return value;
        }

        public string RequireDataDir() => Path.GetFullPath(RequireOption("data"));

        public string DataDirOrDefault()
        {
            return options.TryGetValue("data", out var value) && !string.IsNullOrWhiteSpace(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(Path.Combine("artifacts", "libraryprobe"));
        }
    }
}

internal sealed class FileSystemMultimediaPlatformIO : IMultiMediaPlatformIO
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object configurationLock = new();
    private Dictionary<string, JsonElement> configuration = new(StringComparer.OrdinalIgnoreCase);

    public FileSystemMultimediaPlatformIO(string localDataPath)
    {
        LocalDataPath = Path.GetFullPath(localDataPath);
        Directory.CreateDirectory(LocalDataPath);
        ConfigPath = Path.Combine(LocalDataPath, "libraryprobe.settings.json");
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

            return value.Deserialize<T>(JsonOptions)!;
        }
    }

    public void SetConfigurationValue<T>(string key, T value)
    {
        lock (configurationLock)
        {
            configuration[key] = JsonSerializer.SerializeToElement(value, JsonOptions);
            SaveConfiguration();
        }
    }

    private void LoadConfiguration()
    {
        if (!File.Exists(ConfigPath))
        {
            return;
        }

        var loaded = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(ConfigPath), JsonOptions);
        configuration = loaded is null
            ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(loaded, StringComparer.OrdinalIgnoreCase);
    }

    private void SaveConfiguration()
    {
        Directory.CreateDirectory(LocalDataPath);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(configuration, JsonOptions));
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
            AllProperties = mediaInfo.AllProperties.ToDictionary()
        };
    }
}

internal sealed class NoopThumbnailOperations : IThumbnailOperations
{
    public Task FetchAlbumAsync(string artist, string album, string filePath) => Task.CompletedTask;

    public Task RemoveAlbumAsync(string artist, string album) => Task.CompletedTask;

    public Task RemoveArtistAsync(string artist) => Task.CompletedTask;
}