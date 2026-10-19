using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightStudio.FfmpegShim.Transcoding;

namespace LightStudio.LightPlayer.Server;

/// <summary>
/// Everything the browser client is not allowed to change. Values come from an optional JSON file
/// (<c>LIGHTPLAYER_CONFIG</c>, default <c>/config/lightplayer.json</c>); every one of them can be
/// overridden by the matching <c>LIGHTPLAYER_*</c> environment variable, which is what a container
/// deployment normally uses.
/// </summary>
public sealed class ServerConfiguration
{
    public const string DefaultConfigPath = "/config/lightplayer.json";

    public string Host { get; internal set; } = "0.0.0.0";

    public int Port { get; internal set; } = 47832;

    public IReadOnlyList<string> LibraryPaths { get; internal set; } = ["/music"];

    /// <summary>Folders scanned for video, indexed and served separately from the music library.</summary>
    public IReadOnlyList<string> VideoPaths { get; internal set; } = ["/video"];

    public string DataPath { get; internal set; } = "/data";

    public string CachePath { get; internal set; } = "/data/cache";

    /// <summary>Directory of the published browser client; empty serves the API only.</summary>
    public string StaticFilesPath { get; internal set; } =
        Path.Combine(AppContext.BaseDirectory, "wwwroot");

    public string Username { get; internal set; } = string.Empty;

    /// <summary>Plain password; hashed on startup and never persisted.</summary>
    public string Password { get; internal set; } = string.Empty;

    /// <summary>Pre-hashed password, preferred over <see cref="Password"/> when both are set.</summary>
    public string PasswordHash { get; internal set; } = string.Empty;

    /// <summary>
    /// Websites whose pages a browser may let read this API, as full origins
    /// (<c>https://lmp.hjc.im</c>) or bare host names. Empty answers no cross-origin page, which is
    /// all a client served by this server itself needs.
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins { get; internal set; } = [];

    public bool ScanOnStartup { get; internal set; } = true;

    /// <summary>Serve the browser client and relay API calls instead of hosting a local library.</summary>
    public bool ProxyOnly { get; internal set; }

    /// <summary>
    /// Re-encode tracks a client asks to have re-encoded. Off by default: it is the one thing this
    /// server does that a machine can be too small for, so it has to be asked for.
    /// </summary>
    public bool Transcode { get; internal set; }

    public bool TranscodeHardware { get; internal set; } = true;

    public bool TranscodeHardwareScaling { get; internal set; }

    public string TranscodeVaapiRateControl { get; internal set; } = VaapiEncoderOptions.DefaultRateControl;

    public string TranscodeVaapiQuality { get; internal set; } = VaapiEncoderOptions.DefaultQualityPreset;

    public string TranscodeMediaFoundationRateControl { get; internal set; } =
        MediaFoundationEncoderOptions.PreferredRateControl;

    public string TranscodeMediaFoundationQuality { get; internal set; } =
        MediaFoundationEncoderOptions.DefaultQualityPreset;

    public string TranscodeMediaFoundationScenario { get; internal set; } =
        MediaFoundationEncoderOptions.PreferredScenario;

    public string TranscodeAmfRateControl { get; internal set; } = AmfEncoderOptions.DefaultRateControl;

    public string TranscodeAmfQuality { get; internal set; } = AmfEncoderOptions.DefaultQualityPreset;

    public string TranscodeAmfUsage { get; internal set; } = AmfEncoderOptions.DefaultUsage;

    public string TranscodeNvencRateControl { get; internal set; } = NvencEncoderOptions.DefaultRateControl;

    public string TranscodeNvencPreset { get; internal set; } = NvencEncoderOptions.DefaultPreset;

    public string TranscodeNvencTune { get; internal set; } = NvencEncoderOptions.DefaultTune;

    public string TranscodeQsvPreset { get; internal set; } = QsvEncoderOptions.DefaultPreset;

    public string TranscodeQsvScenario { get; internal set; } = QsvEncoderOptions.DefaultScenario;

    /// <summary>
    /// FFmpeg encoder interface, e.g. <c>mf</c> or <c>vaapi</c>. Empty tries the platform's own.
    /// The name is retained for compatibility with existing configuration files.
    /// </summary>
    public string TranscodeDeviceType { get; internal set; } = string.Empty;

    /// <summary>
    /// Device to open, in whatever form that type takes it, such as a DRM render node for VA-API. Empty
    /// takes the platform's default.
    /// </summary>
    public string TranscodeDevice { get; internal set; } = string.Empty;

    /// <summary>
    /// Encode on the processor when no device can be used. Off by default: one such stream can cost
    /// more than everything else this server does put together.
    /// </summary>
    public bool TranscodeAllowSoftware { get; internal set; }

    /// <summary>The JSON file this deployment reads, whether or not it exists yet.</summary>
    public static string ConfigPath =>
        Environment.GetEnvironmentVariable("LIGHTPLAYER_CONFIG") is { Length: > 0 } path
            ? path
            : DefaultConfigPath;

    public static ServerConfiguration Load()
    {
        var config = new ServerConfiguration();
        var configPath = ConfigPath;
        if (File.Exists(configPath))
        {
            var file = JsonSerializer.Deserialize(
                File.ReadAllText(configPath),
                ServerConfigurationJsonContext.Default.ServerConfigurationFile);
            if (file is not null)
            {
                config.ApplyFile(file);
            }
        }

        config.ApplyEnvironment();
        return config;
    }

    /// <summary>
    /// Writes every setting to <paramref name="path"/>, creating the directory if needed. The plain
    /// password is never written: only the hash the server actually authenticates against.
    /// </summary>
    public void Save(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        var file = new ServerConfigurationFile
        {
            Host = Host,
            Port = Port,
            LibraryPaths = [.. LibraryPaths],
            VideoPaths = [.. VideoPaths],
            DataPath = DataPath,
            CachePath = CachePath,
            StaticFilesPath = StaticFilesPath,
            Username = Username,
            PasswordHash = PasswordHash,
            AllowedOrigins = [.. AllowedOrigins],
            ScanOnStartup = ScanOnStartup,
            ProxyOnly = ProxyOnly,
            Transcode = Transcode,
            TranscodeHardware = TranscodeHardware,
            TranscodeHardwareScaling = TranscodeHardwareScaling,
            TranscodeVaapiRateControl = TranscodeVaapiRateControl,
            TranscodeVaapiQuality = TranscodeVaapiQuality,
            TranscodeMediaFoundationRateControl = TranscodeMediaFoundationRateControl,
            TranscodeMediaFoundationQuality = TranscodeMediaFoundationQuality,
            TranscodeMediaFoundationScenario = TranscodeMediaFoundationScenario,
            TranscodeAmfRateControl = TranscodeAmfRateControl,
            TranscodeAmfQuality = TranscodeAmfQuality,
            TranscodeAmfUsage = TranscodeAmfUsage,
            TranscodeNvencRateControl = TranscodeNvencRateControl,
            TranscodeNvencPreset = TranscodeNvencPreset,
            TranscodeNvencTune = TranscodeNvencTune,
            TranscodeQsvPreset = TranscodeQsvPreset,
            TranscodeQsvScenario = TranscodeQsvScenario,
            TranscodeDeviceType = TranscodeDeviceType,
            TranscodeDevice = TranscodeDevice,
            TranscodeAllowSoftware = TranscodeAllowSoftware,
        };

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(file, ServerConfigurationJsonContext.Default.ServerConfigurationFile));
    }

    /// <summary>
    /// Names of the <c>LIGHTPLAYER_*</c> variables set in this process, which override anything the
    /// file holds and so decide what the server actually runs with.
    /// </summary>
    public static IReadOnlyList<string> OverridingEnvironmentVariables()
    {
        string[] names =
        [
            "LIGHTPLAYER_HOST", "LIGHTPLAYER_PORT", "LIGHTPLAYER_LIBRARY_PATHS", "LIGHTPLAYER_VIDEO_PATHS",
            "LIGHTPLAYER_DATA_PATH", "LIGHTPLAYER_CACHE_PATH", "LIGHTPLAYER_STATIC_PATH", "LIGHTPLAYER_USERNAME",
            "LIGHTPLAYER_PASSWORD", "LIGHTPLAYER_PASSWORD_HASH", "LIGHTPLAYER_ALLOWED_ORIGINS",
            "LIGHTPLAYER_SCAN_ON_STARTUP", "LIGHTPLAYER_PROXY_ONLY", "LIGHTPLAYER_TRANSCODE",
            "LIGHTPLAYER_TRANSCODE_HARDWARE", "LIGHTPLAYER_TRANSCODE_HARDWARE_SCALING",
            "LIGHTPLAYER_TRANSCODE_VAAPI_RATE_CONTROL", "LIGHTPLAYER_TRANSCODE_VAAPI_QUALITY",
            "LIGHTPLAYER_TRANSCODE_MF_RATE_CONTROL", "LIGHTPLAYER_TRANSCODE_MF_QUALITY",
            "LIGHTPLAYER_TRANSCODE_MF_SCENARIO", "LIGHTPLAYER_TRANSCODE_AMF_RATE_CONTROL",
            "LIGHTPLAYER_TRANSCODE_AMF_QUALITY", "LIGHTPLAYER_TRANSCODE_AMF_USAGE",
            "LIGHTPLAYER_TRANSCODE_NVENC_RATE_CONTROL", "LIGHTPLAYER_TRANSCODE_NVENC_PRESET",
            "LIGHTPLAYER_TRANSCODE_NVENC_TUNE", "LIGHTPLAYER_TRANSCODE_QSV_PRESET",
            "LIGHTPLAYER_TRANSCODE_QSV_SCENARIO", "LIGHTPLAYER_TRANSCODE_ENCODER",
            "LIGHTPLAYER_TRANSCODE_DEVICE_TYPE", "LIGHTPLAYER_TRANSCODE_DEVICE",
            "LIGHTPLAYER_TRANSCODE_ALLOW_SOFTWARE",
        ];

        var set = new List<string>();
        foreach (var name in names)
        {
            // An empty LIGHTPLAYER_STATIC_PATH is how a deployment turns the client off, so unlike
            // the others it overrides the file even when it carries no value.
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } ||
                (name == "LIGHTPLAYER_STATIC_PATH" && Environment.GetEnvironmentVariable(name) is not null))
            {
                set.Add(name);
            }
        }

        return set;
    }

    private void ApplyFile(ServerConfigurationFile file)
    {
        Host = file.Host ?? Host;
        Port = file.Port ?? Port;
        LibraryPaths = file.LibraryPaths ?? LibraryPaths;
        VideoPaths = file.VideoPaths ?? VideoPaths;
        DataPath = file.DataPath ?? DataPath;
        CachePath = file.CachePath ?? CachePath;
        StaticFilesPath = file.StaticFilesPath ?? StaticFilesPath;
        Username = file.Username ?? Username;
        Password = file.Password ?? Password;
        PasswordHash = file.PasswordHash ?? PasswordHash;
        AllowedOrigins = file.AllowedOrigins ?? AllowedOrigins;
        ScanOnStartup = file.ScanOnStartup ?? ScanOnStartup;
        ProxyOnly = file.ProxyOnly ?? ProxyOnly;
        Transcode = file.Transcode ?? Transcode;
        TranscodeHardware = file.TranscodeHardware ?? TranscodeHardware;
        TranscodeHardwareScaling = file.TranscodeHardwareScaling ?? TranscodeHardwareScaling;
        TranscodeVaapiRateControl = VaapiEncoderOptions.NormalizeRateControl(
            file.TranscodeVaapiRateControl ?? TranscodeVaapiRateControl);
        TranscodeVaapiQuality = VaapiEncoderOptions.NormalizeQualityPreset(
            file.TranscodeVaapiQuality ?? TranscodeVaapiQuality);
        TranscodeMediaFoundationRateControl = MediaFoundationEncoderOptions.NormalizeRateControl(
            file.TranscodeMediaFoundationRateControl ?? TranscodeMediaFoundationRateControl);
        TranscodeMediaFoundationQuality = MediaFoundationEncoderOptions.NormalizeQualityPreset(
            file.TranscodeMediaFoundationQuality ?? TranscodeMediaFoundationQuality);
        TranscodeMediaFoundationScenario = MediaFoundationEncoderOptions.NormalizeScenario(
            file.TranscodeMediaFoundationScenario ?? TranscodeMediaFoundationScenario);
        TranscodeAmfRateControl = AmfEncoderOptions.NormalizeRateControl(
            file.TranscodeAmfRateControl ?? TranscodeAmfRateControl);
        TranscodeAmfQuality = AmfEncoderOptions.NormalizeQualityPreset(
            file.TranscodeAmfQuality ?? TranscodeAmfQuality);
        TranscodeAmfUsage = AmfEncoderOptions.NormalizeUsage(file.TranscodeAmfUsage ?? TranscodeAmfUsage);
        TranscodeNvencRateControl = NvencEncoderOptions.NormalizeRateControl(
            file.TranscodeNvencRateControl ?? TranscodeNvencRateControl);
        TranscodeNvencPreset = NvencEncoderOptions.NormalizePreset(
            file.TranscodeNvencPreset ?? TranscodeNvencPreset);
        TranscodeNvencTune = NvencEncoderOptions.NormalizeTune(file.TranscodeNvencTune ?? TranscodeNvencTune);
        TranscodeQsvPreset = QsvEncoderOptions.NormalizePreset(file.TranscodeQsvPreset ?? TranscodeQsvPreset);
        TranscodeQsvScenario = QsvEncoderOptions.NormalizeScenario(
            file.TranscodeQsvScenario ?? TranscodeQsvScenario);
        TranscodeDeviceType = TranscodeEncoderInterfaces.NormalizeName(
            file.TranscodeDeviceType ?? TranscodeDeviceType);
        TranscodeDevice = file.TranscodeDevice ?? TranscodeDevice;
        TranscodeAllowSoftware = file.TranscodeAllowSoftware ?? TranscodeAllowSoftware;
    }

    private void ApplyEnvironment()
    {
        Host = Text("LIGHTPLAYER_HOST") ?? Host;
        Port = Integer("LIGHTPLAYER_PORT") ?? Port;
        LibraryPaths = List("LIGHTPLAYER_LIBRARY_PATHS", Path.PathSeparator, ',') ?? LibraryPaths;
        VideoPaths = List("LIGHTPLAYER_VIDEO_PATHS", Path.PathSeparator, ',') ?? VideoPaths;
        DataPath = Text("LIGHTPLAYER_DATA_PATH") ?? DataPath;
        CachePath = Text("LIGHTPLAYER_CACHE_PATH") ?? CachePath;
        StaticFilesPath = Environment.GetEnvironmentVariable("LIGHTPLAYER_STATIC_PATH") ?? StaticFilesPath;
        Username = Text("LIGHTPLAYER_USERNAME") ?? Username;
        Password = Text("LIGHTPLAYER_PASSWORD") ?? Password;
        PasswordHash = Text("LIGHTPLAYER_PASSWORD_HASH") ?? PasswordHash;
        AllowedOrigins = List("LIGHTPLAYER_ALLOWED_ORIGINS", ',', ';', ' ') ?? AllowedOrigins;
        ScanOnStartup = Boolean("LIGHTPLAYER_SCAN_ON_STARTUP") ?? ScanOnStartup;
        ProxyOnly = Boolean("LIGHTPLAYER_PROXY_ONLY") ?? ProxyOnly;
        Transcode = Boolean("LIGHTPLAYER_TRANSCODE") ?? Transcode;
        TranscodeHardware = Boolean("LIGHTPLAYER_TRANSCODE_HARDWARE") ?? TranscodeHardware;
        TranscodeHardwareScaling = Boolean("LIGHTPLAYER_TRANSCODE_HARDWARE_SCALING") ?? TranscodeHardwareScaling;
        TranscodeVaapiRateControl = VaapiEncoderOptions.NormalizeRateControl(
            Text("LIGHTPLAYER_TRANSCODE_VAAPI_RATE_CONTROL") ?? TranscodeVaapiRateControl);
        TranscodeVaapiQuality = VaapiEncoderOptions.NormalizeQualityPreset(
            Text("LIGHTPLAYER_TRANSCODE_VAAPI_QUALITY") ?? TranscodeVaapiQuality);
        TranscodeMediaFoundationRateControl = MediaFoundationEncoderOptions.NormalizeRateControl(
            Text("LIGHTPLAYER_TRANSCODE_MF_RATE_CONTROL") ?? TranscodeMediaFoundationRateControl);
        TranscodeMediaFoundationQuality = MediaFoundationEncoderOptions.NormalizeQualityPreset(
            Text("LIGHTPLAYER_TRANSCODE_MF_QUALITY") ?? TranscodeMediaFoundationQuality);
        TranscodeMediaFoundationScenario = MediaFoundationEncoderOptions.NormalizeScenario(
            Text("LIGHTPLAYER_TRANSCODE_MF_SCENARIO") ?? TranscodeMediaFoundationScenario);
        TranscodeAmfRateControl = AmfEncoderOptions.NormalizeRateControl(
            Text("LIGHTPLAYER_TRANSCODE_AMF_RATE_CONTROL") ?? TranscodeAmfRateControl);
        TranscodeAmfQuality = AmfEncoderOptions.NormalizeQualityPreset(
            Text("LIGHTPLAYER_TRANSCODE_AMF_QUALITY") ?? TranscodeAmfQuality);
        TranscodeAmfUsage = AmfEncoderOptions.NormalizeUsage(
            Text("LIGHTPLAYER_TRANSCODE_AMF_USAGE") ?? TranscodeAmfUsage);
        TranscodeNvencRateControl = NvencEncoderOptions.NormalizeRateControl(
            Text("LIGHTPLAYER_TRANSCODE_NVENC_RATE_CONTROL") ?? TranscodeNvencRateControl);
        TranscodeNvencPreset = NvencEncoderOptions.NormalizePreset(
            Text("LIGHTPLAYER_TRANSCODE_NVENC_PRESET") ?? TranscodeNvencPreset);
        TranscodeNvencTune = NvencEncoderOptions.NormalizeTune(
            Text("LIGHTPLAYER_TRANSCODE_NVENC_TUNE") ?? TranscodeNvencTune);
        TranscodeQsvPreset = QsvEncoderOptions.NormalizePreset(
            Text("LIGHTPLAYER_TRANSCODE_QSV_PRESET") ?? TranscodeQsvPreset);
        TranscodeQsvScenario = QsvEncoderOptions.NormalizeScenario(
            Text("LIGHTPLAYER_TRANSCODE_QSV_SCENARIO") ?? TranscodeQsvScenario);
        TranscodeDeviceType = TranscodeEncoderInterfaces.NormalizeName(
            Text("LIGHTPLAYER_TRANSCODE_ENCODER") ??
            Text("LIGHTPLAYER_TRANSCODE_DEVICE_TYPE") ??
            TranscodeDeviceType);
        TranscodeDevice = Text("LIGHTPLAYER_TRANSCODE_DEVICE") ?? TranscodeDevice;
        TranscodeAllowSoftware = Boolean("LIGHTPLAYER_TRANSCODE_ALLOW_SOFTWARE") ?? TranscodeAllowSoftware;
    }

    private static string? Text(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value.Trim() : null;

    private static int? Integer(string name) =>
        int.TryParse(Text(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool? Boolean(string name) =>
        bool.TryParse(Text(name), out var value) ? value : null;

    private static IReadOnlyList<string>? List(string name, params char[] separators) =>
        Text(name)?.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>On-disk shape of the optional JSON config file; every field is optional.</summary>
public sealed class ServerConfigurationFile
{
    public string? Host { get; set; }

    public int? Port { get; set; }

    public string[]? LibraryPaths { get; set; }

    public string[]? VideoPaths { get; set; }

    public string? DataPath { get; set; }

    public string? CachePath { get; set; }

    public string? StaticFilesPath { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? PasswordHash { get; set; }

    public string[]? AllowedOrigins { get; set; }

    public bool? ScanOnStartup { get; set; }

    public bool? ProxyOnly { get; set; }

    public bool? Transcode { get; set; }

    public bool? TranscodeHardware { get; set; }

    public bool? TranscodeHardwareScaling { get; set; }

    public string? TranscodeVaapiRateControl { get; set; }

    public string? TranscodeVaapiQuality { get; set; }

    public string? TranscodeMediaFoundationRateControl { get; set; }

    public string? TranscodeMediaFoundationQuality { get; set; }

    public string? TranscodeMediaFoundationScenario { get; set; }

    public string? TranscodeAmfRateControl { get; set; }

    public string? TranscodeAmfQuality { get; set; }

    public string? TranscodeAmfUsage { get; set; }

    public string? TranscodeNvencRateControl { get; set; }

    public string? TranscodeNvencPreset { get; set; }

    public string? TranscodeNvencTune { get; set; }

    public string? TranscodeQsvPreset { get; set; }

    public string? TranscodeQsvScenario { get; set; }

    public string? TranscodeDeviceType { get; set; }

    public string? TranscodeDevice { get; set; }

    public bool? TranscodeAllowSoftware { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ServerConfigurationFile))]
internal sealed partial class ServerConfigurationJsonContext : JsonSerializerContext;
