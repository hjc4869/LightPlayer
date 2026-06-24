using System.Globalization;
using System.Text.Json;
using LightStudio.FfmpegShim;

namespace LightStudio.LightPlayer.Tools.AudioProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] AudioExtensions =
    {
        ".aac",
        ".aiff",
        ".alac",
        ".ape",
        ".flac",
        ".m4a",
        ".mp3",
        ".ogg",
        ".opus",
        ".wav",
        ".wma"
    };

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
                "metadata" => Metadata(options),
                "decode" => Decode(options, seek: false),
                "seek-decode" => Decode(options, seek: true),
                "play" => await PlayAsync(options),
                "queue-smoke" => await QueueSmokeAsync(options),
                "queue-play" => await QueuePlayAsync(options),
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

    private static int Metadata(CommandOptions options)
    {
        var mediaFile = RequireExistingMediaFile(options);

        using var decoder = new FfmpegAudioDecoder(File.OpenRead(mediaFile));
        var metadata = decoder.ReadMetadata();

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Path = mediaFile,
            FFmpegRoot = FfmpegNativeInitializer.RootPath ?? "system loader",
            Duration = metadata.Duration.ToString(),
            decoder.Format.SampleRate,
            decoder.Format.ChannelCount,
            decoder.Format.BitsPerSample,
            metadata.Title,
            metadata.Artist,
            metadata.Album,
            metadata.AlbumArtist,
            metadata.Date,
            metadata.TrackNumber,
            metadata.TotalTracks,
            metadata.DiscNumber,
            metadata.TotalDiscs,
            metadata.Genre,
            metadata.AllProperties
        }, JsonOptions));

        return 0;
    }

    private static int Decode(CommandOptions options, bool seek)
    {
        var mediaFile = RequireExistingMediaFile(options);
        var outputFile = Path.GetFullPath(options.RequireOption("out"));
        var duration = ParseSeconds(options.RequireOption("seconds"));
        var requestedStart = seek ? ParseTimeSpan(options.RequireOption("start"), "start") : TimeSpan.Zero;

        using var decoder = new FfmpegAudioDecoder(File.OpenRead(mediaFile));
        var metadata = decoder.ReadMetadata();
        var actualStart = seek ? decoder.Seek(requestedStart) : TimeSpan.Zero;

        using var wav = WavFileAudioOutputDevice.Create(outputFile, decoder.Format);
        var result = decoder.Decode(duration, wav);
        wav.FinalizeWaveFile();

        Console.WriteLine($"Input: {mediaFile}");
        Console.WriteLine($"Output: {outputFile}");
        Console.WriteLine($"FFmpeg root: {FfmpegNativeInitializer.RootPath ?? "system loader"}");
        Console.WriteLine($"Source duration: {metadata.Duration}");
        if (seek)
        {
            Console.WriteLine($"Requested start: {requestedStart}");
            Console.WriteLine($"Actual start: {actualStart}");
        }
        Console.WriteLine($"Format: {decoder.Format.SampleRate} Hz, {decoder.Format.ChannelCount} channel(s), {decoder.Format.BitsPerSample}-bit PCM");
        Console.WriteLine($"Decoded bytes: {result.DecodedBytes}");
        Console.WriteLine($"Frame count: {result.FrameCount}");
        Console.WriteLine($"Decoded duration: {result.DecodedDuration}");
        Console.WriteLine($"Elapsed: {result.Elapsed}");
        Console.WriteLine($"FFmpeg last error: {FormatFfmpegError(result.LastFfmpegError)}");

        return result.DecodedBytes == 0 ? 3 : 0;
    }

    private static async Task<int> PlayAsync(CommandOptions options)
    {
        var mediaFile = RequireExistingMediaFile(options);
        var playbackOptions = CreatePlaybackOptions(options, "seconds");
        var result = await AudioProbePlayer.PlayAsync(mediaFile, playbackOptions, Console.Out);
        PrintPlaybackResult(result);
        return result.DecodedBytes == 0 ? 3 : 0;
    }

    private static async Task<int> QueueSmokeAsync(CommandOptions options)
    {
        var folder = Path.GetFullPath(options.RequireArgument("folder"));
        if (!Directory.Exists(folder))
        {
            throw new ArgumentException($"Folder does not exist: {folder}");
        }

        var secondsPerTrack = ParseSeconds(options.RequireOption("seconds-per-track"));
        var maxTracks = ParseIntOption(options, "max-tracks", 5);
        var files = EnumerateAudioFiles(folder).Take(maxTracks).ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"No supported audio files found under {folder}");
            return 3;
        }

        var failures = 0;
        for (var i = 0; i < files.Length; i++)
        {
            Console.WriteLine($"Track started: {i + 1}/{files.Length}: {files[i]}");
            var playbackOptions = AudioProbePlayer.CreateOptions(
                secondsPerTrack,
                TimeSpan.Zero,
                ParseFloatOption(options, "volume", 1.0f),
                pauseAfter: null,
                pauseDuration: TimeSpan.Zero,
                seekAt: null,
                seekTo: null);
            var result = await AudioProbePlayer.PlayAsync(files[i], playbackOptions, Console.Out);
            PrintPlaybackResult(result);
            Console.WriteLine($"Track ended: {i + 1}/{files.Length}: decoded {result.DecodedDuration}, played {result.PlayedDuration}");
            if (result.DecodedBytes == 0)
            {
                failures++;
            }
        }

        return failures == 0 ? 0 : 3;
    }

    private static async Task<int> QueuePlayAsync(CommandOptions options)
    {
        var folder = Path.GetFullPath(options.RequireArgument("folder"));
        if (!Directory.Exists(folder))
        {
            throw new ArgumentException($"Folder does not exist: {folder}");
        }

        var maxTracks = ParseIntOption(options, "max-tracks", 0);
        var files = EnumerateAudioFiles(folder);
        if (maxTracks > 0)
        {
            files = files.Take(maxTracks);
        }

        var queue = new PlaybackQueueController();
        queue.AddRange(files);
        if (!queue.HasItems)
        {
            Console.Error.WriteLine($"No supported audio files found under {folder}");
            return 3;
        }

        if (options.TryGetOption("mode", out var modeValue))
        {
            if (!AudioProbeQueuePlayer.TryParseMode(modeValue, out var mode))
            {
                throw new ArgumentException($"Invalid --mode value: {modeValue}. Expected seq, loop, single, or random.");
            }

            queue.Mode = mode;
        }

        var playbackOptions = AudioProbeQueuePlayer.CreateOptions(
            ParseOptionalSeconds(options, "seconds-per-track"),
            ParseFloatOption(options, "volume", 1.0f));
        var result = await AudioProbeQueuePlayer.PlayAsync(queue, playbackOptions, Console.In, Console.Out);
        PrintQueuePlaybackResult(result);
        return result.Failures == 0 ? 0 : 3;
    }

    private static string RequireExistingMediaFile(CommandOptions options)
    {
        var mediaFile = Path.GetFullPath(options.RequireArgument("media-file"));
        if (!File.Exists(mediaFile))
        {
            throw new ArgumentException($"Media file does not exist: {mediaFile}");
        }

        return mediaFile;
    }

    private static TimeSpan ParseSeconds(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            throw new ArgumentException($"Invalid --seconds value: {value}");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static PlaybackOptions CreatePlaybackOptions(CommandOptions options, string durationOptionName)
    {
        var pauseAfter = ParseOptionalSeconds(options, "pause-after");
        var seekAt = ParseOptionalSeconds(options, "seek-at");
        var seekTo = options.TryGetOption("seek-to", out var seekToValue)
            ? ParseTimeSpan(seekToValue, "seek-to")
            : (TimeSpan?)null;

        if (seekAt.HasValue != seekTo.HasValue)
        {
            throw new ArgumentException("--seek-at and --seek-to must be provided together.");
        }

        return AudioProbePlayer.CreateOptions(
            ParseSeconds(options.RequireOption(durationOptionName)),
            options.TryGetOption("start", out var startValue) ? ParseTimeSpan(startValue, "start") : TimeSpan.Zero,
            ParseFloatOption(options, "volume", 1.0f),
            pauseAfter,
            TimeSpan.FromMilliseconds(ParseIntOption(options, "pause-ms", 750)),
            seekAt,
            seekTo);
    }

    private static TimeSpan? ParseOptionalSeconds(CommandOptions options, string name)
    {
        return options.TryGetOption(name, out var value)
            ? ParseSeconds(value)
            : null;
    }

    private static int ParseIntOption(CommandOptions options, string name, int defaultValue)
    {
        if (!options.TryGetOption(name, out var value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new ArgumentException($"Invalid --{name} value: {value}");
        }

        return parsed;
    }

    private static float ParseFloatOption(CommandOptions options, string name, float defaultValue)
    {
        if (!options.TryGetOption(name, out var value))
        {
            return defaultValue;
        }

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed < 0 || parsed > 1)
        {
            throw new ArgumentException($"Invalid --{name} value: {value}");
        }

        return parsed;
    }

    private static TimeSpan ParseTimeSpan(string value, string optionName)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var timeSpan) && timeSpan >= TimeSpan.Zero)
        {
            return timeSpan;
        }

        throw new ArgumentException($"Invalid --{optionName} value: {value}");
    }

    private static string FormatFfmpegError(int error)
    {
        return error == 0
            ? "none"
            : $"{error} ({FfmpegNativeInitializer.GetErrorMessage(error)})";
    }

    private static IEnumerable<string> EnumerateAudioFiles(string folder)
    {
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(file => AudioExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);
    }

    private static void PrintPlaybackResult(PlaybackResult result)
    {
        Console.WriteLine($"Decoded bytes: {result.DecodedBytes}");
        Console.WriteLine($"Frame count: {result.FrameCount}");
        Console.WriteLine($"Decoded duration: {result.DecodedDuration}");
        Console.WriteLine($"Played duration estimate: {result.PlayedDuration}");
        Console.WriteLine($"Elapsed: {result.Elapsed}");
        Console.WriteLine($"Underruns: {result.UnderrunCount}");
        Console.WriteLine($"FFmpeg last error: {FormatFfmpegError(result.LastFfmpegError)}");
    }

    private static void PrintQueuePlaybackResult(QueuePlaybackSummary result)
    {
        Console.WriteLine($"Tracks started: {result.TracksStarted}");
        Console.WriteLine($"Tracks completed: {result.TracksCompleted}");
        Console.WriteLine($"Tracks skipped: {result.TracksSkipped}");
        Console.WriteLine($"Failures: {result.Failures}");
        Console.WriteLine($"Decoded bytes: {result.DecodedBytes}");
        Console.WriteLine($"Frame count: {result.FrameCount}");
        Console.WriteLine($"Decoded duration: {result.DecodedDuration}");
        Console.WriteLine($"Played duration estimate: {result.PlayedDuration}");
        Console.WriteLine($"Elapsed: {result.Elapsed}");
        Console.WriteLine($"Underruns: {result.UnderrunCount}");
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("audioprobe metadata <media-file>");
        Console.WriteLine("audioprobe decode <media-file> --seconds 10 --out decoded.wav");
        Console.WriteLine("audioprobe seek-decode <media-file> --start 00:01:00 --seconds 5 --out segment.wav");
        Console.WriteLine("audioprobe play <media-file> --seconds 15 [--start 00:01:00] [--volume 0.8]");
        Console.WriteLine("audioprobe play <media-file> --seconds 15 [--pause-after 3 --pause-ms 750] [--seek-at 5 --seek-to 00:01:00]");
        Console.WriteLine("audioprobe queue-smoke <folder> --seconds-per-track 2 [--max-tracks 5]");
        Console.WriteLine("audioprobe queue-play <folder> [--mode seq|loop|single|random] [--seconds-per-track 30] [--max-tracks 25] [--volume 0.8]");
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

        public bool TryGetOption(string name, out string value)
        {
            if (options.TryGetValue(name, out var optionValue) && !string.IsNullOrWhiteSpace(optionValue))
            {
                value = optionValue;
                return true;
            }

            value = string.Empty;
            return false;
        }
    }
}