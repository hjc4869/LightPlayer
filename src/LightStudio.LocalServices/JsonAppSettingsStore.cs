using LightMediaRenderer.Avalonia.Controls;
using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Transcoding;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightMediaRenderer.Avalonia.Shaders;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LocalServices;

public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private readonly string filePath;
    private readonly bool persistChanges = true;
    private PersistedAppSettings settings;

    public JsonAppSettingsStore()
        : this(Path.Combine(PlatformServices.AppDataPath, "LightStudio.LightPlayer", "settings.json"))
    {
    }

    public JsonAppSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = filePath;
        settings = Load(filePath);
    }

    private JsonAppSettingsStore(bool persistChanges) : this()
    {
        this.persistChanges = persistChanges;
    }

    public static JsonAppSettingsStore LoadSnapshot() => new(persistChanges: false);

    public IReadOnlyList<string> LibraryFolderPaths => settings.LibraryFolderPaths;

    public IReadOnlyList<string> VideoFolderPaths => settings.VideoFolderPaths;

    public NativeLibrarySettings NativeLibrary
    {
        get => settings.NativeLibrary ?? new();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (NativeLibrary == value)
            {
                return;
            }

            settings.NativeLibrary = value;
            Save();
            NativeLibraryChanged?.Invoke(this, EventArgs.Empty);
            LibraryScanSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? NativeLibraryChanged;

    public AppThemePreference ThemePreference
    {
        get => Enum.TryParse<AppThemePreference>(settings.ThemePreference, ignoreCase: true, out var preference)
            ? preference
            : AppThemePreference.Default;
        set
        {
            var serialized = value.ToString();
            if (string.Equals(settings.ThemePreference, serialized, StringComparison.Ordinal))
            {
                return;
            }

            settings.ThemePreference = serialized;
            Save();
        }
    }

    public string SelectedLightColorThemeId
    {
        get => settings.SelectedLightColorThemeId ?? string.Empty;
        set
        {
            var serialized = value ?? string.Empty;
            if (string.Equals(settings.SelectedLightColorThemeId, serialized, StringComparison.Ordinal))
            {
                return;
            }

            settings.SelectedLightColorThemeId = serialized;
            Save();
        }
    }

    public string SelectedDarkColorThemeId
    {
        get => settings.SelectedDarkColorThemeId ?? string.Empty;
        set
        {
            var serialized = value ?? string.Empty;
            if (string.Equals(settings.SelectedDarkColorThemeId, serialized, StringComparison.Ordinal))
            {
                return;
            }

            settings.SelectedDarkColorThemeId = serialized;
            Save();
        }
    }

    public bool EnableMicaBackdrop
    {
        get => settings.EnableMicaBackdrop;
        set
        {
            if (settings.EnableMicaBackdrop == value)
            {
                return;
            }

            settings.EnableMicaBackdrop = value;
            Save();
        }
    }

    public BackdropStyle BackdropStyle
    {
        get => Enum.TryParse(settings.BackdropStyle, ignoreCase: true, out BackdropStyle style) && Enum.IsDefined(style)
            ? style
            : BackdropStyle.Blur;
        set
        {
            string serialized = Enum.IsDefined(value) ? value.ToString() : nameof(BackdropStyle.Blur);
            if (string.Equals(settings.BackdropStyle, serialized, StringComparison.Ordinal))
                return;

            settings.BackdropStyle = serialized;
            Save();
        }
    }

    public bool EnableAppleChrome
    {
        get => settings.EnableAppleChrome;
        set
        {
            if (settings.EnableAppleChrome == value)
            {
                return;
            }

            settings.EnableAppleChrome = value;
            Save();
        }
    }

    public IReadOnlyList<CustomColorThemeRecord> CustomColorThemes => settings.CustomColorThemes;

    public void SaveCustomColorTheme(CustomColorThemeRecord record)
    {
        if (record is null || string.IsNullOrEmpty(record.Id))
        {
            return;
        }

        var index = settings.CustomColorThemes.FindIndex(
            t => string.Equals(t.Id, record.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            settings.CustomColorThemes[index] = record;
        }
        else
        {
            settings.CustomColorThemes.Add(record);
        }

        Save();
    }

    public void DeleteCustomColorTheme(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var removed = settings.CustomColorThemes.RemoveAll(
            t => string.Equals(t.Id, id, StringComparison.Ordinal));
        if (removed > 0)
        {
            Save();
        }
    }

    public AppRouteId DefaultRouteId
    {
        get => Enum.TryParse<AppRouteId>(settings.DefaultRouteId, ignoreCase: true, out var routeId)
            && Enum.IsDefined(routeId)
                ? routeId
                : AppRouteId.Home;
        set
        {
            var serialized = Enum.IsDefined(value) ? value.ToString() : AppRouteId.Home.ToString();
            if (string.Equals(settings.DefaultRouteId, serialized, StringComparison.Ordinal))
            {
                return;
            }

            settings.DefaultRouteId = serialized;
            Save();
        }
    }

    public bool RememberLastPage
    {
        get => settings.RememberLastPage;
        set
        {
            if (settings.RememberLastPage == value)
            {
                return;
            }

            settings.RememberLastPage = value;
            Save();
        }
    }

    public AppRouteId LastUsedRouteId
    {
        get => Enum.TryParse<AppRouteId>(settings.LastUsedRouteId, ignoreCase: true, out var routeId)
            && Enum.IsDefined(routeId)
                ? routeId
                : AppRouteId.Home;
        set
        {
            var serialized = Enum.IsDefined(value) ? value.ToString() : AppRouteId.Home.ToString();
            if (string.Equals(settings.LastUsedRouteId, serialized, StringComparison.Ordinal))
            {
                return;
            }

            settings.LastUsedRouteId = serialized;
            Save();
        }
    }

    public int PreferredSampleRate
    {
        get => settings.PreferredSampleRate;
        set
        {
            if (settings.PreferredSampleRate == value)
            {
                return;
            }

            settings.PreferredSampleRate = value;
            Save();
        }
    }

    public bool AlwaysResample
    {
        get => settings.AlwaysResample;
        set
        {
            if (settings.AlwaysResample == value)
            {
                return;
            }

            settings.AlwaysResample = value;
            Save();
        }
    }

    public VideoScalingQuality PreferredVideoScaling
    {
        get => Enum.TryParse(settings.PreferredVideoScaling, ignoreCase: true, out VideoScalingQuality quality)
            && Enum.IsDefined(quality)
                ? quality
                : VideoScalingQuality.Bilinear;
        set
        {
            string serialized = Enum.IsDefined(value) ? value.ToString() : VideoScalingQuality.Bilinear.ToString();
            if (string.Equals(settings.PreferredVideoScaling, serialized, StringComparison.Ordinal))
                return;

            settings.PreferredVideoScaling = serialized;
            Save();
        }
    }

    public VideoScalingQuality PreferredVideoDownscaling
    {
        get => Enum.TryParse(settings.PreferredVideoDownscaling, ignoreCase: true, out VideoScalingQuality quality)
            && Enum.IsDefined(quality)
                ? quality
                : VideoScalingQuality.Bilinear;
        set
        {
            string serialized = Enum.IsDefined(value) ? value.ToString() : VideoScalingQuality.Bilinear.ToString();
            if (string.Equals(settings.PreferredVideoDownscaling, serialized, StringComparison.Ordinal))
                return;

            settings.PreferredVideoDownscaling = serialized;
            Save();
        }
    }

    public VideoDebandingQuality VideoDebanding
    {
        get => Enum.TryParse(settings.VideoDebanding, ignoreCase: true, out VideoDebandingQuality quality)
            && Enum.IsDefined(quality)
                ? quality
                : VideoDebandingQuality.Off;
        set
        {
            string serialized = Enum.IsDefined(value) ? value.ToString() : VideoDebandingQuality.Off.ToString();
            if (string.Equals(settings.VideoDebanding, serialized, StringComparison.Ordinal))
                return;

            settings.VideoDebanding = serialized;
            Save();
        }
    }

    public bool PreferHardwareVideoProcessing
    {
        get => settings.PreferHardwareVideoProcessing;
        set
        {
            if (settings.PreferHardwareVideoProcessing == value)
                return;

            settings.PreferHardwareVideoProcessing = value;
            Save();
        }
    }

    public bool EnableHdrPassthrough
    {
        get => settings.EnableHdrPassthrough;
        set
        {
            if (settings.EnableHdrPassthrough == value)
                return;

            settings.EnableHdrPassthrough = value;
            Save();
        }
    }

    public Bt709Interpretation VideoBt709Interpretation
    {
        get => Enum.TryParse(settings.VideoBt709Interpretation, ignoreCase: true, out Bt709Interpretation interpretation)
            && Enum.IsDefined(interpretation)
                ? interpretation
                : Bt709Interpretation.PlatformPreferred;
        set
        {
            string serialized = Enum.IsDefined(value) ? value.ToString() : Bt709Interpretation.PlatformPreferred.ToString();
            if (string.Equals(settings.VideoBt709Interpretation, serialized, StringComparison.Ordinal))
                return;

            settings.VideoBt709Interpretation = serialized;
            Save();
        }
    }

    public int DisplayPanelBitDepth
    {
        get => VideoPanelBitDepthSettings.Normalize(settings.DisplayPanelBitDepth);
        set
        {
            int depth = VideoPanelBitDepthSettings.Normalize(value);
            if (settings.DisplayPanelBitDepth == depth)
                return;

            settings.DisplayPanelBitDepth = depth;
            Save();
        }
    }

    public TimeSpan VideoBufferDuration
    {
        get => TimeSpan.FromMilliseconds(Math.Clamp(settings.VideoBufferMilliseconds, 50, 5000));
        set
        {
            int milliseconds = Math.Clamp((int)value.TotalMilliseconds, 50, 5000);
            if (settings.VideoBufferMilliseconds == milliseconds)
                return;

            settings.VideoBufferMilliseconds = milliseconds;
            Save();
        }
    }

    public bool AutoPlayNextVideo
    {
        get => settings.AutoPlayNextVideo;
        set
        {
            if (settings.AutoPlayNextVideo == value)
                return;

            settings.AutoPlayNextVideo = value;
            Save();
        }
    }

    public bool EnableHardwareDecoding
    {
        get => settings.EnableHardwareDecoding;
        set
        {
            if (settings.EnableHardwareDecoding == value)
                return;

            settings.EnableHardwareDecoding = value;
            Save();
        }
    }

    public string PreferredHardwareDecoder
    {
        get => settings.PreferredHardwareDecoder ?? string.Empty;
        set
        {
            string serialized = value ?? string.Empty;
            if (string.Equals(settings.PreferredHardwareDecoder, serialized, StringComparison.Ordinal))
                return;

            settings.PreferredHardwareDecoder = serialized;
            Save();
        }
    }

    public bool PreferPhotoDecodePerformance
    {
        get => settings.PreferPhotoDecodePerformance;
        set
        {
            if (settings.PreferPhotoDecodePerformance == value)
                return;

            settings.PreferPhotoDecodePerformance = value;
            Save();
        }
    }

    public IReadOnlyDictionary<string, HardwareDecoderConfiguration> HardwareDecoderConfigurations =>
        settings.HardwareDecoderConfigurations;

    public VideoSdrToneMappingOptions? PreferredPhotoToneMapping => settings.PreferredPhotoToneMapping;

    public async Task SetPreferredPhotoToneMappingAsync(VideoSdrToneMappingOptions? options)
    {
        options?.Validate();
        await Task.Run(() =>
        {
            var writable = new JsonAppSettingsStore(filePath);
            writable.settings.PreferredPhotoToneMapping = options;
            writable.Save();
        }).ConfigureAwait(false);
        settings.PreferredPhotoToneMapping = options;
    }

    public void SetHardwareDecoderConfiguration(string deviceType, HardwareDecoderConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(deviceType) || configuration is null)
        {
            return;
        }

        settings.HardwareDecoderConfigurations[deviceType] = configuration;
        Save();
    }

    public string AudioOutputDeviceId
    {
        get => string.IsNullOrWhiteSpace(settings.AudioOutputDeviceId)
            ? IAudioOutputSettings.DefaultDeviceId
            : settings.AudioOutputDeviceId;
        set
        {
            var serialized = string.IsNullOrWhiteSpace(value)
                ? IAudioOutputSettings.DefaultDeviceId
                : value;
            if (string.Equals(settings.AudioOutputDeviceId, serialized, StringComparison.Ordinal))
            {
                return;
            }

            settings.AudioOutputDeviceId = serialized;
            Save();
        }
    }

    public IReadOnlyDictionary<string, string> AudioOutputOptions => settings.AudioOutputOptions;

    public void SetAudioOutputOption(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var serialized = value ?? string.Empty;
        if (settings.AudioOutputOptions.TryGetValue(key, out var current) &&
            string.Equals(current, serialized, StringComparison.Ordinal))
        {
            return;
        }

        settings.AudioOutputOptions[key] = serialized;
        Save();
    }

    public string InterfaceLanguage
    {
        get => settings.InterfaceLanguage ?? string.Empty;
        set
        {
            var serialized = value ?? string.Empty;
            if (string.Equals(settings.InterfaceLanguage, serialized, StringComparison.Ordinal))
            {
                return;
            }

            settings.InterfaceLanguage = serialized;
            Save();
        }
    }

    public WaylandMode WaylandMode
    {
        get => Enum.TryParse<WaylandMode>(settings.WaylandMode, ignoreCase: true, out var mode)
            ? mode
            : WaylandMode.Auto;
        set
        {
            var serialized = value.ToString();
            if (string.Equals(settings.WaylandMode, serialized, StringComparison.Ordinal))
            {
                return;
            }

            settings.WaylandMode = serialized;
            Save();
        }
    }

    public bool EnableThirdPartyLyrics
    {
        get => settings.EnableThirdPartyLyrics;
        set
        {
            if (settings.EnableThirdPartyLyrics == value)
            {
                return;
            }

            settings.EnableThirdPartyLyrics = value;
            Save();
        }
    }

    public bool EnablePlaybackHistory
    {
        get => settings.EnablePlaybackHistory;
        set
        {
            if (settings.EnablePlaybackHistory == value)
            {
                return;
            }

            settings.EnablePlaybackHistory = value;
            Save();
        }
    }

    public AlbumGroupingMode AlbumGrouping
    {
        get => Enum.TryParse<AlbumGroupingMode>(settings.AlbumGrouping, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : AlbumGroupingMode.SameFolder;
        set
        {
            if (!Enum.IsDefined(value))
            {
                value = AlbumGroupingMode.SameFolder;
            }

            if (AlbumGrouping == value)
            {
                return;
            }

            settings.AlbumGrouping = value.ToString();
            Save();
            AlbumGroupingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? AlbumGroupingChanged;

    public bool ScanLibraryOnStartup
    {
        get => settings.ScanLibraryOnStartup;
        set
        {
            if (settings.ScanLibraryOnStartup == value)
            {
                return;
            }

            settings.ScanLibraryOnStartup = value;
            Save();
        }
    }

    public bool AutoScanLibrary
    {
        get => settings.AutoScanLibrary ?? OperatingSystem.IsWindows();
        set
        {
            if (settings.AutoScanLibrary == value)
            {
                return;
            }

            settings.AutoScanLibrary = value;
            Save();
            LibraryScanSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int AutoScanIntervalMinutes
    {
        get => Math.Clamp(settings.AutoScanIntervalMinutes, 1, 1440);
        set
        {
            value = Math.Clamp(value, 1, 1440);
            if (settings.AutoScanIntervalMinutes == value)
            {
                return;
            }

            settings.AutoScanIntervalMinutes = value;
            Save();
            LibraryScanSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? LibraryScanSettingsChanged;

    public bool InitialSetupCompleted
    {
        get => settings.InitialSetupCompleted;
        set
        {
            if (settings.InitialSetupCompleted == value)
            {
                return;
            }

            settings.InitialSetupCompleted = value;
            Save();
        }
    }

    public RemoteApiSettings RemoteApiSettings => new()
    {
        Enabled = settings.RemoteApiEnabled,
        Host = settings.RemoteApiHost ?? string.Empty,
        Port = settings.RemoteApiPort,
        RequireAuthentication = settings.RemoteApiRequireAuthentication,
        Username = settings.RemoteApiUsername ?? string.Empty,
        PasswordHash = settings.RemoteApiPasswordHash ?? string.Empty,
        AllowedOrigins = settings.RemoteApiAllowedOrigins ?? string.Empty,
        TranscodeEnabled = settings.RemoteApiTranscodeEnabled,
        TranscodeHardware = settings.RemoteApiTranscodeHardware,
        TranscodeHardwareScaling = settings.RemoteApiTranscodeHardwareScaling,
        TranscodeVaapiRateControl = VaapiEncoderOptions.NormalizeRateControl(
            settings.RemoteApiTranscodeVaapiRateControl),
        TranscodeVaapiQuality = VaapiEncoderOptions.NormalizeQualityPreset(
            settings.RemoteApiTranscodeVaapiQuality),
        TranscodeMediaFoundationRateControl = MediaFoundationEncoderOptions.NormalizeRateControl(
            settings.RemoteApiTranscodeMediaFoundationRateControl),
        TranscodeMediaFoundationQuality = MediaFoundationEncoderOptions.NormalizeQualityPreset(
            settings.RemoteApiTranscodeMediaFoundationQuality),
        TranscodeMediaFoundationScenario = MediaFoundationEncoderOptions.NormalizeScenario(
            settings.RemoteApiTranscodeMediaFoundationScenario),
        TranscodeAmfRateControl = AmfEncoderOptions.NormalizeRateControl(
            settings.RemoteApiTranscodeAmfRateControl),
        TranscodeAmfQuality = AmfEncoderOptions.NormalizeQualityPreset(
            settings.RemoteApiTranscodeAmfQuality),
        TranscodeAmfUsage = AmfEncoderOptions.NormalizeUsage(settings.RemoteApiTranscodeAmfUsage),
        TranscodeNvencRateControl = NvencEncoderOptions.NormalizeRateControl(
            settings.RemoteApiTranscodeNvencRateControl),
        TranscodeNvencPreset = NvencEncoderOptions.NormalizePreset(settings.RemoteApiTranscodeNvencPreset),
        TranscodeNvencTune = NvencEncoderOptions.NormalizeTune(settings.RemoteApiTranscodeNvencTune),
        TranscodeQsvPreset = QsvEncoderOptions.NormalizePreset(settings.RemoteApiTranscodeQsvPreset),
        TranscodeQsvScenario = QsvEncoderOptions.NormalizeScenario(settings.RemoteApiTranscodeQsvScenario),
        TranscodeDeviceType = TranscodeEncoderInterfaces.NormalizeName(settings.RemoteApiTranscodeDeviceType),
        TranscodeDevice = settings.RemoteApiTranscodeDevice ?? string.Empty,
        TranscodeAllowSoftware = settings.RemoteApiTranscodeAllowSoftware,
    };

    public void SetRemoteApiSettings(RemoteApiSettings value)
    {
        if (RemoteApiSettings == value)
        {
            return;
        }

        settings.RemoteApiEnabled = value.Enabled;
        settings.RemoteApiHost = value.Host?.Trim() ?? string.Empty;
        settings.RemoteApiPort = value.Port;
        settings.RemoteApiRequireAuthentication = value.RequireAuthentication;
        settings.RemoteApiUsername = value.Username;
        settings.RemoteApiPasswordHash = value.PasswordHash;
        settings.RemoteApiAllowedOrigins = value.AllowedOrigins?.Trim() ?? string.Empty;
        settings.RemoteApiTranscodeEnabled = value.TranscodeEnabled;
        settings.RemoteApiTranscodeHardware = value.TranscodeHardware;
        settings.RemoteApiTranscodeHardwareScaling = value.TranscodeHardwareScaling;
        settings.RemoteApiTranscodeVaapiRateControl = VaapiEncoderOptions.NormalizeRateControl(
            value.TranscodeVaapiRateControl);
        settings.RemoteApiTranscodeVaapiQuality = VaapiEncoderOptions.NormalizeQualityPreset(
            value.TranscodeVaapiQuality);
        settings.RemoteApiTranscodeMediaFoundationRateControl = MediaFoundationEncoderOptions.NormalizeRateControl(
            value.TranscodeMediaFoundationRateControl);
        settings.RemoteApiTranscodeMediaFoundationQuality = MediaFoundationEncoderOptions.NormalizeQualityPreset(
            value.TranscodeMediaFoundationQuality);
        settings.RemoteApiTranscodeMediaFoundationScenario = MediaFoundationEncoderOptions.NormalizeScenario(
            value.TranscodeMediaFoundationScenario);
        settings.RemoteApiTranscodeAmfRateControl = AmfEncoderOptions.NormalizeRateControl(
            value.TranscodeAmfRateControl);
        settings.RemoteApiTranscodeAmfQuality = AmfEncoderOptions.NormalizeQualityPreset(
            value.TranscodeAmfQuality);
        settings.RemoteApiTranscodeAmfUsage = AmfEncoderOptions.NormalizeUsage(value.TranscodeAmfUsage);
        settings.RemoteApiTranscodeNvencRateControl = NvencEncoderOptions.NormalizeRateControl(
            value.TranscodeNvencRateControl);
        settings.RemoteApiTranscodeNvencPreset = NvencEncoderOptions.NormalizePreset(value.TranscodeNvencPreset);
        settings.RemoteApiTranscodeNvencTune = NvencEncoderOptions.NormalizeTune(value.TranscodeNvencTune);
        settings.RemoteApiTranscodeQsvPreset = QsvEncoderOptions.NormalizePreset(value.TranscodeQsvPreset);
        settings.RemoteApiTranscodeQsvScenario = QsvEncoderOptions.NormalizeScenario(value.TranscodeQsvScenario);
        settings.RemoteApiTranscodeDeviceType = TranscodeEncoderInterfaces.NormalizeName(value.TranscodeDeviceType);
        settings.RemoteApiTranscodeDevice = value.TranscodeDevice;
        settings.RemoteApiTranscodeAllowSoftware = value.TranscodeAllowSoftware;
        Save();
    }

    public RemoteTranscodeSettings RemoteTranscodeSettings => new()
    {
        Video = settings.RemoteTranscodeVideo,
        Audio = settings.RemoteTranscodeAudio,
        VideoCodecs = settings.RemoteTranscodeVideoCodecs ?? RemoteTranscodeSettings.Default.VideoCodecs,
        VideoCodec = settings.RemoteTranscodeVideoCodec ?? string.Empty,
        FileBitrateThreshold = settings.RemoteTranscodeFileBitrateThreshold,
        VideoBitrate = settings.RemoteTranscodeVideoBitrate,
        VideoHeightThreshold = settings.RemoteTranscodeVideoHeightThreshold,
        VideoHeight = settings.RemoteTranscodeVideoHeight,
        AudioCodec = settings.RemoteTranscodeAudioCodec ?? string.Empty,
        AudioCodecsToTranscode = settings.RemoteTranscodeAudioCodecsToTranscode
            ?? RemoteTranscodeSettings.Default.AudioCodecsToTranscode,
        AudioBitrate = settings.RemoteTranscodeAudioBitrate,
    };

    public event EventHandler? RemoteTranscodeSettingsChanged;

    public void SetRemoteTranscodeSettings(RemoteTranscodeSettings value)
    {
        if (RemoteTranscodeSettings == value)
        {
            return;
        }

        settings.RemoteTranscodeVideo = value.Video;
        settings.RemoteTranscodeAudio = value.Audio;
        settings.RemoteTranscodeVideoCodecs = value.VideoCodecs;
        settings.RemoteTranscodeVideoCodec = value.VideoCodec;
        settings.RemoteTranscodeFileBitrateThreshold = value.FileBitrateThreshold;
        settings.RemoteTranscodeVideoBitrate = value.VideoBitrate;
        settings.RemoteTranscodeVideoHeightThreshold = value.VideoHeightThreshold;
        settings.RemoteTranscodeVideoHeight = value.VideoHeight;
        settings.RemoteTranscodeAudioCodec = value.AudioCodec;
        settings.RemoteTranscodeAudioCodecsToTranscode = value.AudioCodecsToTranscode;
        settings.RemoteTranscodeAudioBitrate = value.AudioBitrate;
        Save();
        RemoteTranscodeSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetLibraryFolderPaths(IEnumerable<string> folderPaths)
    {
        settings.LibraryFolderPaths = folderPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(GetPathComparer())
            .ToList();
        Save();
        LibraryScanSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetVideoFolderPaths(IEnumerable<string> folderPaths)
    {
        settings.VideoFolderPaths = folderPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(GetPathComparer())
            .ToList();
        Save();
        LibraryScanSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public LibraryViewSettings GetLibraryViewSettings(LibraryItemKind kind)
    {
        var (sorting, ascending, grouping, groupBy) = ReadKind(kind);
        var field = Enum.TryParse<LibrarySortField>(sorting, ignoreCase: true, out var parsed)
            ? parsed
            : LibraryViewSettings.Default(kind).Sort;
        return new LibraryViewSettings(field, ascending)
        {
            GroupingEnabled = grouping,
            GroupBy = Enum.TryParse<LibraryGroupField>(groupBy, ignoreCase: true, out var parsedGroup)
                ? parsedGroup : LibraryViewSettings.Default(kind).GroupBy,
        }.Normalized(kind);
    }

    public void SetLibraryViewSettings(LibraryItemKind kind, LibraryViewSettings value)
    {
        var normalized = value.Normalized(kind);
        switch (kind)
        {
            case LibraryItemKind.Video:
                settings.VideoSorting = normalized.Sort.ToString();
                settings.VideoSortAscending = normalized.Ascending;
                settings.VideoGroupingEnabled = normalized.GroupingEnabled;
                settings.VideoGroupBy = normalized.GroupBy.ToString();
                break;
            case LibraryItemKind.Album:
                settings.AlbumSorting = normalized.Sort.ToString();
                settings.AlbumSortAscending = normalized.Ascending;
                settings.AlbumGroupingEnabled = normalized.GroupingEnabled;
                settings.AlbumGroupBy = normalized.GroupBy.ToString();
                break;
            case LibraryItemKind.Artist:
                settings.ArtistSorting = normalized.Sort.ToString();
                settings.ArtistSortAscending = normalized.Ascending;
                settings.ArtistGroupingEnabled = normalized.GroupingEnabled;
                settings.ArtistGroupBy = normalized.GroupBy.ToString();
                break;
            default:
                settings.SongSorting = normalized.Sort.ToString();
                settings.SongSortAscending = normalized.Ascending;
                settings.SongGroupingEnabled = normalized.GroupingEnabled;
                settings.SongGroupBy = normalized.GroupBy.ToString();
                break;
        }

        Save();
    }

    private (string Sorting, bool Ascending, bool Grouping, string GroupBy) ReadKind(LibraryItemKind kind) => kind switch
    {
        LibraryItemKind.Video => (settings.VideoSorting, settings.VideoSortAscending, settings.VideoGroupingEnabled, settings.VideoGroupBy),
        LibraryItemKind.Album => (settings.AlbumSorting, settings.AlbumSortAscending, settings.AlbumGroupingEnabled, settings.AlbumGroupBy),
        LibraryItemKind.Artist => (settings.ArtistSorting, settings.ArtistSortAscending, settings.ArtistGroupingEnabled, settings.ArtistGroupBy),
        _ => (settings.SongSorting, settings.SongSortAscending, settings.SongGroupingEnabled, settings.SongGroupBy),
    };

    public LibraryLayoutMode GetLibraryLayout(LibraryItemKind kind)
    {
        var stored = kind switch
        {
            LibraryItemKind.Video => settings.VideoLayout,
            LibraryItemKind.Song => settings.SongLayout,
            LibraryItemKind.Album => settings.AlbumLayout,
            LibraryItemKind.Artist => settings.ArtistLayout,
            _ => null,
        };

        return Enum.TryParse<LibraryLayoutMode>(stored, ignoreCase: true, out var parsed)
            ? parsed
            : LibraryLayoutModes.Default(kind);
    }

    public void SetLibraryLayout(LibraryItemKind kind, LibraryLayoutMode layout)
    {
        switch (kind)
        {
            case LibraryItemKind.Video:
                settings.VideoLayout = layout.ToString();
                break;
            case LibraryItemKind.Song:
                settings.SongLayout = layout.ToString();
                break;
            case LibraryItemKind.Album:
                settings.AlbumLayout = layout.ToString();
                break;
            case LibraryItemKind.Artist:
                settings.ArtistLayout = layout.ToString();
                break;
            default:
                return;
        }

        Save();
    }

    public LibraryItemPresentation VideoNarrowPresentation
    {
        get => Enum.TryParse<LibraryItemPresentation>(settings.VideoNarrowPresentation, ignoreCase: true, out var value)
            && Enum.IsDefined(value) ? value : LibraryItemPresentation.Grid;
        set
        {
            settings.VideoNarrowPresentation = value.ToString();
            Save();
        }
    }

    private static PersistedAppSettings Load(string path)
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.PersistedAppSettings);
            if (loaded is not null)
            {
                loaded.LibraryFolderPaths ??= [];
                loaded.VideoFolderPaths ??= [];
                loaded.AudioOutputOptions ??= new Dictionary<string, string>(StringComparer.Ordinal);
                loaded.CustomColorThemes ??= [];
                loaded.PreferredPhotoToneMapping?.Validate();
                loaded.HardwareDecoderConfigurations ??= new Dictionary<string, HardwareDecoderConfiguration>(StringComparer.OrdinalIgnoreCase);
                return loaded;
            }
        }

        return new PersistedAppSettings();
    }

    private void Save()
    {
        if (!persistChanges)
            return;

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.PersistedAppSettings);
        File.WriteAllText(filePath, json);
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}

public sealed class PersistedAppSettings
{
    // Platforms where hardware decoding is reliable enough to enable out of the box.
    // The browser head persists settings through KeyValueAppSettingsStore, not this store.
    private static readonly bool DefaultEnableHardwareDecoding =
        OperatingSystem.IsWindows() || OperatingSystem.IsAndroid() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();

    public List<string> LibraryFolderPaths { get; set; } = [];

    public List<string> VideoFolderPaths { get; set; } = [];

    public NativeLibrarySettings? NativeLibrary { get; set; }

    public string ThemePreference { get; set; } = nameof(AppThemePreference.Default);

    public string SelectedLightColorThemeId { get; set; } = string.Empty;

    public string SelectedDarkColorThemeId { get; set; } = string.Empty;

    public bool EnableMicaBackdrop { get; set; } = true;

    public string BackdropStyle { get; set; } = nameof(LightMediaRenderer.Avalonia.Controls.BackdropStyle.Blur);

    public bool EnableAppleChrome { get; set; } = true;

    public List<CustomColorThemeRecord> CustomColorThemes { get; set; } = [];

    public string DefaultRouteId { get; set; } = nameof(AppRouteId.Home);

    public bool RememberLastPage { get; set; }

    public string LastUsedRouteId { get; set; } = nameof(AppRouteId.Home);

    public int PreferredSampleRate { get; set; }

    public bool AlwaysResample { get; set; }

    public string PreferredVideoScaling { get; set; } = nameof(VideoScalingQuality.Bilinear);

    public string PreferredVideoDownscaling { get; set; } = nameof(VideoScalingQuality.Bilinear);

    public bool PreferHardwareVideoProcessing { get; set; }

    public string VideoDebanding { get; set; } = nameof(VideoDebandingQuality.Off);

    public bool EnableHdrPassthrough { get; set; } = true;

    public string VideoBt709Interpretation { get; set; } = nameof(Bt709Interpretation.PlatformPreferred);

    public int DisplayPanelBitDepth { get; set; } = VideoPanelBitDepthSettings.Automatic;

    public int VideoBufferMilliseconds { get; set; } = 250;

    public bool AutoPlayNextVideo { get; set; } = true;

    public bool EnableHardwareDecoding { get; set; } = DefaultEnableHardwareDecoding;

    public string PreferredHardwareDecoder { get; set; } = string.Empty;

    public bool PreferPhotoDecodePerformance { get; set; }

    public VideoSdrToneMappingOptions? PreferredPhotoToneMapping { get; set; }

    public Dictionary<string, HardwareDecoderConfiguration> HardwareDecoderConfigurations { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string AudioOutputDeviceId { get; set; } = IAudioOutputSettings.DefaultDeviceId;

    public Dictionary<string, string> AudioOutputOptions { get; set; } = new(StringComparer.Ordinal);

    public string InterfaceLanguage { get; set; } = string.Empty;

    public string WaylandMode { get; set; } = nameof(LightPlayer.Services.WaylandMode.Auto);

    public bool EnableThirdPartyLyrics { get; set; }

    public bool EnablePlaybackHistory { get; set; } = true;

    public string AlbumGrouping { get; set; } = nameof(AlbumGroupingMode.SameFolder);

    public bool ScanLibraryOnStartup { get; set; }

    public bool? AutoScanLibrary { get; set; }

    public int AutoScanIntervalMinutes { get; set; } = 10;

    public bool InitialSetupCompleted { get; set; }

    public bool RemoteApiEnabled { get; set; }

    public string RemoteApiHost { get; set; } = string.Empty;

    public int RemoteApiPort { get; set; }

    public bool RemoteApiRequireAuthentication { get; set; }

    public string RemoteApiUsername { get; set; } = string.Empty;

    public string RemoteApiPasswordHash { get; set; } = string.Empty;

    public string RemoteApiAllowedOrigins { get; set; } = string.Empty;

    public bool RemoteApiTranscodeEnabled { get; set; }

    public bool RemoteApiTranscodeHardware { get; set; } = true;

    public bool RemoteApiTranscodeHardwareScaling { get; set; }

    public string RemoteApiTranscodeVaapiRateControl { get; set; } = VaapiEncoderOptions.DefaultRateControl;

    public string RemoteApiTranscodeVaapiQuality { get; set; } = VaapiEncoderOptions.DefaultQualityPreset;

    public string RemoteApiTranscodeMediaFoundationRateControl { get; set; } =
        MediaFoundationEncoderOptions.PreferredRateControl;

    public string RemoteApiTranscodeMediaFoundationQuality { get; set; } =
        MediaFoundationEncoderOptions.DefaultQualityPreset;

    public string RemoteApiTranscodeMediaFoundationScenario { get; set; } =
        MediaFoundationEncoderOptions.PreferredScenario;

    public string RemoteApiTranscodeAmfRateControl { get; set; } = AmfEncoderOptions.DefaultRateControl;

    public string RemoteApiTranscodeAmfQuality { get; set; } = AmfEncoderOptions.DefaultQualityPreset;

    public string RemoteApiTranscodeAmfUsage { get; set; } = AmfEncoderOptions.DefaultUsage;

    public string RemoteApiTranscodeNvencRateControl { get; set; } = NvencEncoderOptions.DefaultRateControl;

    public string RemoteApiTranscodeNvencPreset { get; set; } = NvencEncoderOptions.DefaultPreset;

    public string RemoteApiTranscodeNvencTune { get; set; } = NvencEncoderOptions.DefaultTune;

    public string RemoteApiTranscodeQsvPreset { get; set; } = QsvEncoderOptions.DefaultPreset;

    public string RemoteApiTranscodeQsvScenario { get; set; } = QsvEncoderOptions.DefaultScenario;

    public string RemoteApiTranscodeDeviceType { get; set; } = string.Empty;

    public string RemoteApiTranscodeDevice { get; set; } = string.Empty;

    public bool RemoteApiTranscodeAllowSoftware { get; set; }

    public bool RemoteTranscodeVideo { get; set; }

    public bool RemoteTranscodeAudio { get; set; }

    public string RemoteTranscodeVideoCodecs { get; set; } = RemoteTranscodeSettings.Default.VideoCodecs;

    public string RemoteTranscodeVideoCodec { get; set; } = string.Empty;

    public long RemoteTranscodeFileBitrateThreshold { get; set; } =
        RemoteTranscodeSettings.Default.FileBitrateThreshold;

    public long RemoteTranscodeVideoBitrate { get; set; } = RemoteTranscodeSettings.Default.VideoBitrate;

    public int RemoteTranscodeVideoHeightThreshold { get; set; } =
        RemoteTranscodeSettings.Default.VideoHeightThreshold;

    public int RemoteTranscodeVideoHeight { get; set; } = RemoteTranscodeSettings.Default.VideoHeight;

    public string RemoteTranscodeAudioCodec { get; set; } = string.Empty;

    public string RemoteTranscodeAudioCodecsToTranscode { get; set; } =
        RemoteTranscodeSettings.Default.AudioCodecsToTranscode;

    public long RemoteTranscodeAudioBitrate { get; set; } = RemoteTranscodeSettings.Default.AudioBitrate;

    public string SongSorting { get; set; } = nameof(LibrarySortField.Title);

    public bool SongSortAscending { get; set; } = true;

    public string AlbumSorting { get; set; } = nameof(LibrarySortField.AlbumName);

    public bool AlbumSortAscending { get; set; } = true;

    public string ArtistSorting { get; set; } = nameof(LibrarySortField.ArtistName);

    public bool ArtistSortAscending { get; set; } = true;

    public string VideoSorting { get; set; } = nameof(LibrarySortField.Title);

    public bool VideoSortAscending { get; set; } = true;

    public bool SongGroupingEnabled { get; set; }
    public string SongGroupBy { get; set; } = nameof(LibraryGroupField.Name);
    public bool AlbumGroupingEnabled { get; set; }
    public string AlbumGroupBy { get; set; } = nameof(LibraryGroupField.Name);
    public bool ArtistGroupingEnabled { get; set; }
    public string ArtistGroupBy { get; set; } = nameof(LibraryGroupField.Name);
    public bool VideoGroupingEnabled { get; set; }
    public string VideoGroupBy { get; set; } = nameof(LibraryGroupField.CreationMonth);

    public string SongLayout { get; set; } = nameof(LibraryLayoutMode.Grid);

    public string AlbumLayout { get; set; } = nameof(LibraryLayoutMode.Grid);

    public string ArtistLayout { get; set; } = nameof(LibraryLayoutMode.List);

    public string VideoLayout { get; set; } = nameof(LibraryLayoutMode.Folders);

    public string VideoNarrowPresentation { get; set; } = nameof(LibraryItemPresentation.Grid);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PersistedAppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}