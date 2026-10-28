using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.NativeBindings;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Transcoding;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.RemoteApi;

namespace LightStudio.LightPlayer.Server;

[Include("libavutil/avutil.h")]
[Include("libavcodec/avcodec.h")]
[Include("libavformat/avformat.h")]
[Include("libavfilter/avfilter.h")]
[Include("libswscale/swscale.h")]
[Include("libswresample/swresample.h")]
internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        switch (args.Length > 0 ? args[0] : string.Empty)
        {
            case "hash-password":
                return HashPassword(args);

            case "configure":
                return Tui.ServerConfigurationTui.Run();

            case "photo-index":
                return await PhotoIndexCommand.RunAsync(args).ConfigureAwait(false);
        }

        AppLog.ConfigureConsole();
        AppLog.Information("Server.Startup", "Starting Light Player standalone server.");
        var config = ServerConfiguration.Load();
        AppLog.Information(
            "Server.Configuration",
            $"host={config.Host}; port={config.Port}; proxyOnly={config.ProxyOnly}; " +
            $"dataPath={config.DataPath}; cachePath={config.CachePath}; staticPath={config.StaticFilesPath}; " +
            $"libraryPaths=[{string.Join(", ", config.LibraryPaths)}]; videoPaths=[{string.Join(", ", config.VideoPaths)}]; " +
            $"scanOnStartup={config.ScanOnStartup}; authenticationConfigured={config.Username.Length > 0}; " +
            $"allowedOrigins=[{string.Join(", ", config.AllowedOrigins)}]; " +
            $"transcode={config.Transcode}; hardware={config.TranscodeHardware}; " +
            $"hardwareScaling={config.TranscodeHardwareScaling}; softwareFallback={config.TranscodeAllowSoftware}; " +
            $"vaapiRateControl={config.TranscodeVaapiRateControl}; vaapiQuality={config.TranscodeVaapiQuality}; " +
            $"encoderInterface={(config.TranscodeDeviceType.Length == 0 ? "auto" : config.TranscodeDeviceType)}; " +
            $"device={(config.TranscodeDevice.Length == 0 ? "default" : config.TranscodeDevice)}.");
        var staticFilesPath = ResolveStaticFilesPath(config);
        if (config.ProxyOnly)
        {
            using var proxy = new RemoteApiProxyServer(ListenerPrefix(config), staticFilesPath);
            proxy.Start();

            Console.WriteLine($"Light Media Center proxy listening on http://{config.Host}:{config.Port}/");
            Console.WriteLine(staticFilesPath is null
                ? "Serving the API relay only."
                : $"Serving the browser client from {staticFilesPath}.");
            Console.WriteLine($"Forwarding API requests selected by the {RemoteApiProxyServer.ForwardTargetHeader} header.");

            await WaitForShutdownAsync().ConfigureAwait(false);
            Console.WriteLine("Shutting down.");
            AppLog.Information("Server.Startup", "Standalone proxy stopped.");
            AppLog.Shutdown();
            return 0;
        }

        var tokenSecret = RemoteApiTokenSecret.LoadOrCreate(config.DataPath);
        var credentials = ResolveCredentials(config, tokenSecret);
        if (credentials is null)
        {
            Console.Error.WriteLine(
                "error: set LIGHTPLAYER_USERNAME and LIGHTPLAYER_PASSWORD (or LIGHTPLAYER_PASSWORD_HASH). " +
                "Authentication is mandatory for the standalone server.");
            AppLog.Error("Server.Startup", "Authentication is mandatory but no usable credentials were configured.");
            AppLog.Shutdown();
            return 1;
        }

        var services = ServerLibraryHost.GetOrCreate(config);
        if (ServerLibraryHost.ApplyFolders(services, config).Count == 0)
        {
            Console.Error.WriteLine(
                $"warning: none of the configured library paths exist ({string.Join(", ", config.LibraryPaths)}).");
        }

        if (services.PhotoIndex is { Status.Options.Enabled: true } photoIndex)
            await photoIndex.InitializeAsync().ConfigureAwait(false);

        using var server = new RemoteApiServer(services, IMultiMediaPlatformIO.Instance, new RemoteApiHostOptions
        {
            ListenerPrefix = ListenerPrefix(config),
            StaticFilesPath = staticFilesPath,
            Credentials = credentials,
            TokenSecret = tokenSecret,
            AllowedOrigins = config.AllowedOrigins,
            Transcoding = ResolveTranscoding(config),
        });
        server.Start();

        Console.WriteLine($"Light Media Center server listening on http://{config.Host}:{config.Port}/");
        Console.WriteLine(staticFilesPath is null
            ? "Serving the REST API only."
            : $"Serving the browser client from {staticFilesPath}.");
        Console.WriteLine(config.Transcode
            ? "Re-encoding is offered to clients that ask for it."
            : "Re-encoding is off; every track is sent as it is stored.");

        if (config.ScanOnStartup)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await services.LibraryScan.ScanAsync().ConfigureAwait(false);
                    Console.WriteLine(
                        $"Library scan finished: {result.IndexedCount} tracks, {result.VideoIndexedCount} videos, and {result.ImageIndexedCount} photos indexed.");
                }
                catch (Exception ex)
                {
                    AppLog.Error("Server.LibraryScan", "Startup library scan failed.", ex);
                    Console.Error.WriteLine($"Library scan failed: {ex.GetBaseException().Message}");
                }
            });
        }

        await WaitForShutdownAsync().ConfigureAwait(false);
        Console.WriteLine("Shutting down.");
        AppLog.Information("Server.Startup", "Standalone server stopped.");
        AppLog.Shutdown();
        return 0;
    }

    /// <summary>
    /// Turns the deployment's transcoding settings into the arguments the encode path passes to
    /// FFmpeg. The device string is keyed by device type exactly as the decode path keys its own,
    /// so one settings shape serves both.
    /// </summary>
    private static TranscodeOptions ResolveTranscoding(ServerConfiguration config)
    {
        if (!config.Transcode)
        {
            return TranscodeOptions.Disabled;
        }

        string encoderInterface = TranscodeEncoderInterfaces.NormalizeName(config.TranscodeDeviceType);
        TranscodeEncoderInterface? encoder = TranscodeEncoderInterfaces.Find(encoderInterface);
        if (encoder is null && config.TranscodeDevice.Length > 0)
        {
            var preferred = TranscodeEncoderInterfaces.GetPreferredForCurrentPlatform();
            encoder = preferred.Count > 0 ? preferred[0] : null;
        }

        var hardware = new HardwareDecoderSettingsBuilder();
        if (encoder is not null && config.TranscodeDevice.Length > 0)
        {
            hardware.SetDevice(encoder.DeviceType, config.TranscodeDevice);
        }

        HardwareDecoderSettings hardwareSettings = VaapiEncoderOptions.Apply(
            hardware.Build(),
            config.TranscodeVaapiRateControl,
            config.TranscodeVaapiQuality);
        hardwareSettings = MediaFoundationEncoderOptions.Apply(
            hardwareSettings,
            config.TranscodeMediaFoundationRateControl,
            config.TranscodeMediaFoundationQuality,
            config.TranscodeMediaFoundationScenario);
        hardwareSettings = AmfEncoderOptions.Apply(
            hardwareSettings,
            config.TranscodeAmfRateControl,
            config.TranscodeAmfQuality,
            config.TranscodeAmfUsage);
        hardwareSettings = NvencEncoderOptions.Apply(
            hardwareSettings,
            config.TranscodeNvencRateControl,
            config.TranscodeNvencPreset,
            config.TranscodeNvencTune);
        hardwareSettings = QsvEncoderOptions.Apply(
            hardwareSettings,
            config.TranscodeQsvPreset,
            config.TranscodeQsvScenario);

        return new TranscodeOptions
        {
            Enabled = true,
            EnableHardware = config.TranscodeHardware,
            EnableHardwareScaling = config.TranscodeHardwareScaling,
            AllowSoftwareEncoding = config.TranscodeAllowSoftware,
            HardwareEncoder = encoder?.Name,
            HardwareSettings = hardwareSettings,
        };
    }

    private static int HashPassword(string[] args)
    {
        var password = args.Length > 1 ? args[1] : Console.In.ReadToEnd().TrimEnd('\r', '\n');
        if (password.Length == 0)
        {
            Console.Error.WriteLine("usage: LightStudio.LightPlayer.Server hash-password <password>");
            return 1;
        }

        Console.WriteLine(RemoteApiCredentials.HashPassword(password));
        return 0;
    }

    private static RemoteApiCredentials? ResolveCredentials(ServerConfiguration config, byte[] tokenSecret)
    {
        if (config.Username.Length == 0)
        {
            return null;
        }

        var hash = config.PasswordHash.Length > 0
            ? config.PasswordHash
            : config.Password.Length > 0
                ? RemoteApiCredentials.HashPassword(config.Password, RemoteApiTokenSecret.PasswordSalt(tokenSecret))
                : string.Empty;
        return hash.Length > 0 ? new RemoteApiCredentials(config.Username, hash) : null;
    }

    private static string ListenerPrefix(ServerConfiguration config)
    {
        // HttpListener needs the wildcard host to accept requests on every interface.
        var host = config.Host.Trim().Trim('[', ']');
        var listenerHost = host is "0.0.0.0" or "::" or "*" or ""
            ? "*"
            : host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        return $"http://{listenerHost}:{config.Port}/";
    }

    private static string? ResolveStaticFilesPath(ServerConfiguration config)
    {
        if (config.StaticFilesPath.Length == 0)
        {
            return null;
        }

        var full = Path.GetFullPath(config.StaticFilesPath);
        return File.Exists(Path.Combine(full, "index.html")) ? full : null;
    }

    internal static void InitializeNativeDependencies()
    {
        SQLitePCL.Batteries_V2.Init();
        AppLog.Information(
            "FFmpeg.Native",
            $"Loaded versions: avutil={NativeVersion("avutil", avutil_version)}; " +
            $"avcodec={NativeVersion("avcodec", avcodec_version)}; " +
            $"avformat={NativeVersion("avformat", avformat_version)}; " +
            $"avfilter={NativeVersion("avfilter", avfilter_version)}; " +
            $"swscale={NativeVersion("swscale", swscale_version)}; " +
            $"swresample={NativeVersion("swresample", swresample_version)}; " +
            $"hardwareTypes=[{string.Join(", ", FfmpegHardwareDevices.GetAvailableTypes())}].");
    }

    private static string NativeVersion(string component, Func<uint> getVersion)
    {
        try
        {
            uint version = getVersion();
            return $"{version >> 16}.{(version >> 8) & 0xff}.{version & 0xff}";
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
            NotSupportedException or TypeInitializationException)
        {
            AppLog.Error(
                "FFmpeg.Native",
                $"The {component} library or a required symbol is unavailable. Check the runtime FFmpeg packages and binding major.",
                exception);
            return "unavailable";
        }
    }

    /// <summary>Blocks until the container runtime asks the process to stop.</summary>
    private static async Task WaitForShutdownAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handle(PosixSignalContext context)
        {
            context.Cancel = true;
            completion.TrySetResult();
        }

        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handle);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Handle);
        await completion.Task.ConfigureAwait(false);
    }
}
