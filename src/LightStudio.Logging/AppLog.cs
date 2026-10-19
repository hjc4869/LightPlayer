using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.Logging;

public enum AppLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

/// <summary>
/// Process-wide logging with no hosting or UI dependency, usable from NativeAOT and browser builds.
/// </summary>
public static class AppLog
{
    private const long MaximumFileBytes = 10 * 1024 * 1024;
    private const int RetainedFileCount = 3;

    private static readonly object Gate = new();
    private static StreamWriter? fileWriter;
    private static string? currentFilePath;
    private static bool consoleEnabled;
    private static AppLogLevel minimumLevel = AppLogLevel.Information;
    private static int exceptionHandlersAttached;

    public static string? CurrentFilePath
    {
        get
        {
            lock (Gate)
            {
                return currentFilePath;
            }
        }
    }

    public static void ConfigureConsole(AppLogLevel? level = null) =>
        Configure(writeToConsole: true, filePath: null, level);

    public static string? ConfigureClient(string appDataDirectory, AppLogLevel? level = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);

        string? path = OperatingSystem.IsBrowser()
            ? null
            : Path.Combine(appDataDirectory, "logs", "lightplayer.log");
        Configure(writeToConsole: true, path, level);
        return CurrentFilePath;
    }

    public static void Configure(bool writeToConsole, string? filePath, AppLogLevel? level = null)
    {
        Exception? fileError = null;
        lock (Gate)
        {
            DisposeFileWriter();
            consoleEnabled = writeToConsole;
            minimumLevel = level ?? LevelFromEnvironment();

            if (!string.IsNullOrWhiteSpace(filePath) && !OperatingSystem.IsBrowser())
            {
                try
                {
                    currentFilePath = Path.GetFullPath(filePath);
                    OpenFileWriter();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    currentFilePath = null;
                    fileError = exception;
                }
            }
        }

        AttachExceptionHandlers();
        Information(
            "Logging",
            $"Initialized at {minimumLevel}; process={Environment.ProcessId}; " +
            $"runtime={RuntimeInformation.FrameworkDescription}; os={RuntimeInformation.OSDescription}; " +
            $"architecture={RuntimeInformation.ProcessArchitecture}; file={CurrentFilePath ?? "none"}.");
        if (fileError is not null)
        {
            Warning("Logging", "Could not open the requested log file.", fileError);
        }
    }

    public static void Debug(string category, string message) =>
        Write(AppLogLevel.Debug, category, message, null);

    public static void Information(string category, string message) =>
        Write(AppLogLevel.Information, category, message, null);

    public static void Warning(string category, string message, Exception? exception = null) =>
        Write(AppLogLevel.Warning, category, message, exception);

    public static void Error(string category, string message, Exception? exception = null) =>
        Write(AppLogLevel.Error, category, message, exception);

    public static void Critical(string category, string message, Exception? exception = null) =>
        Write(AppLogLevel.Critical, category, message, exception);

    public static async Task<int> WriteArchiveAsync(
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        List<(FileStream Stream, long Length, string Name)> snapshots = [];
        try
        {
            lock (Gate)
            {
                if (currentFilePath is null)
                {
                    return 0;
                }

                fileWriter?.Flush();
                for (int index = 0; index <= RetainedFileCount; index++)
                {
                    string path = index == 0 ? currentFilePath : $"{currentFilePath}.{index}";
                    try
                    {
                        var stream = new FileStream(path, new FileStreamOptions
                        {
                            Mode = FileMode.Open,
                            Access = FileAccess.Read,
                            Share = FileShare.ReadWrite | FileShare.Delete,
                            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                        });
                        snapshots.Add((stream, stream.Length, Path.GetFileName(path)));
                    }
                    catch (FileNotFoundException)
                    {
                    }
                    catch (DirectoryNotFoundException)
                    {
                    }
                }
            }

            using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
            byte[] buffer = new byte[81920];
            foreach (var snapshot in snapshots)
            {
                ZipArchiveEntry entry = archive.CreateEntry(snapshot.Name, CompressionLevel.Fastest);
                await using Stream entryStream = entry.Open();
                long remaining = snapshot.Length;
                while (remaining > 0)
                {
                    int read = await snapshot.Stream.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    remaining -= read;
                }
            }

            return snapshots.Count;
        }
        finally
        {
            foreach (var snapshot in snapshots)
            {
                await snapshot.Stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            DisposeFileWriter();
        }
    }

    private static void Write(AppLogLevel level, string category, string message, Exception? exception)
    {
        if (level < minimumLevel)
        {
            return;
        }

        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:O} [{Abbreviation(level)}] [{Sanitize(category)}] " +
            $"[thread:{Environment.CurrentManagedThreadId}] {Sanitize(message)}");
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (Gate)
        {
            if (consoleEnabled)
            {
                try
                {
                    TextWriter writer = level >= AppLogLevel.Warning ? Console.Error : Console.Out;
                    writer.WriteLine(line);
                }
                catch
                {
                }
            }

            if (fileWriter is null)
            {
                return;
            }

            try
            {
                fileWriter.WriteLine(line);
                if (fileWriter.BaseStream.Length >= MaximumFileBytes)
                {
                    DisposeFileWriter(clearPath: false);
                    OpenFileWriter();
                }
            }
            catch (Exception fileError) when (fileError is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                DisposeFileWriter();
                if (consoleEnabled)
                {
                    try
                    {
                        Console.Error.WriteLine("Light Player logging disabled its file sink:");
                        Console.Error.WriteLine(fileError);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    private static void OpenFileWriter()
    {
        string path = currentFilePath
            ?? throw new InvalidOperationException("No log file path was configured.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RotateIfNeeded(path);

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        fileWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaximumFileBytes)
        {
            return;
        }

        for (int index = RetainedFileCount; index >= 1; index--)
        {
            string source = index == 1 ? path : $"{path}.{index - 1}";
            if (File.Exists(source))
            {
                File.Move(source, $"{path}.{index}", overwrite: true);
            }
        }
    }

    private static void DisposeFileWriter(bool clearPath = true)
    {
        try
        {
            fileWriter?.Dispose();
        }
        catch
        {
        }

        fileWriter = null;
        if (clearPath)
        {
            currentFilePath = null;
        }
    }

    private static AppLogLevel LevelFromEnvironment() =>
        Environment.GetEnvironmentVariable("LIGHTPLAYER_LOG_LEVEL")?.Trim().ToLowerInvariant() switch
        {
            "debug" or "trace" => AppLogLevel.Debug,
            "warn" or "warning" => AppLogLevel.Warning,
            "error" => AppLogLevel.Error,
            "critical" or "fatal" => AppLogLevel.Critical,
            _ => AppLogLevel.Information,
        };

    private static string Abbreviation(AppLogLevel level) => level switch
    {
        AppLogLevel.Debug => "DBG",
        AppLogLevel.Information => "INF",
        AppLogLevel.Warning => "WRN",
        AppLogLevel.Error => "ERR",
        AppLogLevel.Critical => "CRT",
        _ => "UNK",
    };

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);

    private static void AttachExceptionHandlers()
    {
        if (Interlocked.Exchange(ref exceptionHandlersAttached, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Exception? exception = args.ExceptionObject as Exception;
            Critical(
                "UnhandledException",
                $"An unhandled exception is terminating={args.IsTerminating}.",
                exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Error("UnobservedTaskException", "A task exception was never observed.", args.Exception);
    }
}