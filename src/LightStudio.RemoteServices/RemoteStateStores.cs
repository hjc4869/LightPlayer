using LightMediaRenderer.Avalonia.Controls;
using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightStudio.FfmpegShim.Core;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightMediaRenderer.Avalonia.Shaders;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.RemoteServices;

/// <summary>
/// <see cref="IAppSettingsStore"/> over an <see cref="IKeyValueStore"/>, for heads that have no
/// settings file of their own (the browser).
/// </summary>
public sealed class KeyValueAppSettingsStore(IKeyValueStore store) : IAppSettingsStore
{
    /// <summary>
    /// Folders this device indexes itself. Heads with no local library of their own simply never
    /// add any; a remote session hides them behind <see cref="RemoteAppSettingsStore"/>.
    /// </summary>
    public IReadOnlyList<string> LibraryFolderPaths => ReadFolderPaths("settings.libraryFolders");

    /// <inheritdoc cref="LibraryFolderPaths" />
    public IReadOnlyList<string> VideoFolderPaths => ReadFolderPaths("settings.videoFolders");

    public AppThemePreference ThemePreference
    {
        get => store.GetEnum("settings.theme", AppThemePreference.Default);
        set => store.SetEnum("settings.theme", value);
    }

    public string SelectedLightColorThemeId
    {
        get => store.Get("settings.colorTheme.light") ?? string.Empty;
        set => store.Set("settings.colorTheme.light", value ?? string.Empty);
    }

    public string SelectedDarkColorThemeId
    {
        get => store.Get("settings.colorTheme.dark") ?? string.Empty;
        set => store.Set("settings.colorTheme.dark", value ?? string.Empty);
    }

    public bool EnableMicaBackdrop
    {
        get => store.GetBoolean("settings.enableMicaBackdrop", defaultValue: true);
        set => store.SetBoolean("settings.enableMicaBackdrop", value);
    }

    public BackdropStyle BackdropStyle
    {
        get
        {
            BackdropStyle style = store.GetEnum("settings.backdropStyle", BackdropStyle.Blur);
            return Enum.IsDefined(style) ? style : BackdropStyle.Blur;
        }
        set => store.SetEnum("settings.backdropStyle", Enum.IsDefined(value) ? value : BackdropStyle.Blur);
    }

    public bool EnableAppleChrome
    {
        get => store.GetBoolean("settings.enableAppleChrome", defaultValue: true);
        set => store.SetBoolean("settings.enableAppleChrome", value);
    }

    public double LiquidGlassFrost
    {
        get => store.GetDouble("settings.liquidGlassFrost");
        set => store.SetDouble("settings.liquidGlassFrost", value);
    }

    public IReadOnlyList<CustomColorThemeRecord> CustomColorThemes => LoadCustomThemes();

    public void SaveCustomColorTheme(CustomColorThemeRecord record)
    {
        if (record is null || string.IsNullOrEmpty(record.Id))
        {
            return;
        }

        var themes = LoadCustomThemes();
        var index = themes.FindIndex(t => string.Equals(t.Id, record.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            themes[index] = record;
        }
        else
        {
            themes.Add(record);
        }

        StoreCustomThemes(themes);
    }

    public void DeleteCustomColorTheme(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var themes = LoadCustomThemes();
        if (themes.RemoveAll(t => string.Equals(t.Id, id, StringComparison.Ordinal)) > 0)
        {
            StoreCustomThemes(themes);
        }
    }

    private List<CustomColorThemeRecord> LoadCustomThemes()
    {
        var json = store.Get("settings.colorTheme.custom");
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize(json, RemoteStateJsonContext.Default.ListCustomColorThemeRecord) ?? [];
    }

    private void StoreCustomThemes(List<CustomColorThemeRecord> themes) =>
        store.Set(
            "settings.colorTheme.custom",
            JsonSerializer.Serialize(themes, RemoteStateJsonContext.Default.ListCustomColorThemeRecord));

    public AppRouteId DefaultRouteId
    {
        get => store.GetEnum("settings.defaultRoute", AppRouteId.Home);
        set => store.SetEnum("settings.defaultRoute", value);
    }

    public bool RememberLastPage
    {
        get => store.GetBoolean("settings.rememberLastPage");
        set => store.SetBoolean("settings.rememberLastPage", value);
    }

    public AppRouteId LastUsedRouteId
    {
        get => store.GetEnum("settings.lastUsedRoute", AppRouteId.Home);
        set => store.SetEnum("settings.lastUsedRoute", value);
    }

    public int PreferredSampleRate
    {
        get => store.GetInt32("settings.preferredSampleRate");
        set => store.SetInt32("settings.preferredSampleRate", value);
    }

    public bool AlwaysResample
    {
        get => store.GetBoolean("settings.alwaysResample");
        set => store.SetBoolean("settings.alwaysResample", value);
    }

    public VideoScalingQuality PreferredVideoScaling
    {
        get => store.GetEnum("settings.video.scaling", VideoScalingQuality.Bilinear);
        set => store.SetEnum("settings.video.scaling", value);
    }

    public VideoScalingQuality PreferredVideoDownscaling
    {
        get => store.GetEnum("settings.video.downscaling", VideoScalingQuality.Bilinear);
        set => store.SetEnum("settings.video.downscaling", value);
    }

    public bool PreferHardwareVideoProcessing
    {
        get => store.GetBoolean("settings.video.preferHardwareProcessing");
        set => store.SetBoolean("settings.video.preferHardwareProcessing", value);
    }

    public VideoDebandingQuality VideoDebanding
    {
        get => store.GetEnum("settings.video.debanding", VideoDebandingQuality.Off);
        set => store.SetEnum("settings.video.debanding", value);
    }

    public bool EnableHdrPassthrough
    {
        get => store.GetBoolean("settings.video.enableHdrPassthrough", defaultValue: true);
        set => store.SetBoolean("settings.video.enableHdrPassthrough", value);
    }

    public Bt709Interpretation VideoBt709Interpretation
    {
        get => store.GetEnum("settings.video.bt709Interpretation", Bt709Interpretation.PlatformPreferred);
        set => store.SetEnum("settings.video.bt709Interpretation", value);
    }

    public int DisplayPanelBitDepth
    {
        get => VideoPanelBitDepthSettings.Normalize(
            store.GetInt32("settings.video.displayPanelBitDepth", VideoPanelBitDepthSettings.Automatic));
        set => store.SetInt32(
            "settings.video.displayPanelBitDepth",
            VideoPanelBitDepthSettings.Normalize(value));
    }

    public TimeSpan VideoBufferDuration
    {
        get => TimeSpan.FromMilliseconds(store.GetInt32("settings.video.bufferMilliseconds", 250));
        set => store.SetInt32("settings.video.bufferMilliseconds", (int)value.TotalMilliseconds);
    }

    public bool AutoPlayNextVideo
    {
        get => store.GetBoolean("settings.video.autoPlayNext", defaultValue: true);
        set => store.SetBoolean("settings.video.autoPlayNext", value);
    }

    public int SubtitleFontSize
    {
        get => SubtitleFontSizeSettings.Normalize(
            store.GetInt32("settings.video.subtitleFontSize", SubtitleFontSizeSettings.Default));
        set => store.SetInt32("settings.video.subtitleFontSize", SubtitleFontSizeSettings.Normalize(value));
    }

    public string SubtitleFontFamily
    {
        get => store.Get("settings.video.subtitleFontFamily") ?? string.Empty;
        set => store.Set("settings.video.subtitleFontFamily", value?.Trim() ?? string.Empty);
    }

    public bool EnableHardwareDecoding
    {
        get => store.GetBoolean("settings.video.enableHardwareDecoding", defaultValue: true);
        set => store.SetBoolean("settings.video.enableHardwareDecoding", value);
    }

    public string PreferredHardwareDecoder
    {
        get => store.Get("settings.video.hardwareDecoder") ?? string.Empty;
        set => store.Set("settings.video.hardwareDecoder", value ?? string.Empty);
    }

    public bool PreferPhotoDecodePerformance
    {
        get => store.GetBoolean("settings.photo.preferDecodePerformance", defaultValue: false);
        set => store.SetBoolean("settings.photo.preferDecodePerformance", value);
    }

    public ToneMappingSettings ToneMapping
    {
        get
        {
            try
            {
                string? json = store.Get("settings.toneMapping");
                ToneMappingSettings settings = string.IsNullOrEmpty(json)
                    ? ToneMappingSettings.Default
                    : JsonSerializer.Deserialize(json, RemoteStateJsonContext.Default.ToneMappingSettings) ??
                        ToneMappingSettings.Default;
                settings.Validate();
                return settings;
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                AppLog.Warning("Settings", "Invalid tone mapping settings; using defaults.", exception);
                return ToneMappingSettings.Default;
            }
        }
    }

    public Task SetToneMappingAsync(ToneMappingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        string json = JsonSerializer.Serialize(settings, RemoteStateJsonContext.Default.ToneMappingSettings);
        if (store is IAsyncKeyValueStore asyncStore)
            return asyncStore.SetAsync("settings.toneMapping", json);
        store.Set("settings.toneMapping", json);
        return Task.CompletedTask;
    }

    /// <summary>This head registers no hardware profiles, so there is nothing to configure.</summary>
    public IReadOnlyDictionary<string, HardwareDecoderConfiguration> HardwareDecoderConfigurations { get; } =
        new Dictionary<string, HardwareDecoderConfiguration>();

    public void SetHardwareDecoderConfiguration(string deviceType, HardwareDecoderConfiguration configuration)
    {
    }

    public string AudioOutputDeviceId
    {
        get => IAudioOutputSettings.DefaultDeviceId;
        set { }
    }

    public IReadOnlyDictionary<string, string> AudioOutputOptions { get; } =
        new Dictionary<string, string>();

    public void SetAudioOutputOption(string key, string value)
    {
    }

    public WaylandMode WaylandMode
    {
        get => WaylandMode.Off;
        set { }
    }

    public string InterfaceLanguage
    {
        get => store.Get("settings.interfaceLanguage") ?? string.Empty;
        set => store.Set("settings.interfaceLanguage", value ?? string.Empty);
    }

    public bool EnableThirdPartyLyrics
    {
        get => store.GetBoolean("settings.enableThirdPartyLyrics");
        set => store.SetBoolean("settings.enableThirdPartyLyrics", value);
    }

    public bool EnablePlaybackHistory
    {
        get => store.GetBoolean("settings.enablePlaybackHistory", defaultValue: true);
        set => store.SetBoolean("settings.enablePlaybackHistory", value);
    }

    public AlbumGroupingMode AlbumGrouping
    {
        get
        {
            var mode = store.GetEnum("settings.albumGrouping", AlbumGroupingMode.SameFolder);
            return Enum.IsDefined(mode) ? mode : AlbumGroupingMode.SameFolder;
        }
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

            store.SetEnum("settings.albumGrouping", value);
            AlbumGroupingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? AlbumGroupingChanged;

    public NativeLibrarySettings NativeLibrary
    {
        get => store.Get("settings.nativeLibrary") is { } json
            ? JsonSerializer.Deserialize(json, RemoteStateJsonContext.Default.NativeLibrarySettings) ?? new()
            : new();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (NativeLibrary == value)
            {
                return;
            }

            store.Set("settings.nativeLibrary", JsonSerializer.Serialize(value, RemoteStateJsonContext.Default.NativeLibrarySettings));
            NativeLibraryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? NativeLibraryChanged;

    public bool ScanLibraryOnStartup
    {
        get => false;
        set { }
    }

    public bool AutoScanLibrary
    {
        get => false;
        set { }
    }

    public int AutoScanIntervalMinutes
    {
        get => 10;
        set { }
    }

    public event EventHandler? LibraryScanSettingsChanged
    {
        add { }
        remove { }
    }

    public bool InitialSetupCompleted
    {
        get => store.GetBoolean("settings.initialSetupCompleted");
        set => store.SetBoolean("settings.initialSetupCompleted", value);
    }

    public RemoteApiSettings RemoteApiSettings => RemoteApiSettings.Disabled;

    public void SetRemoteApiSettings(RemoteApiSettings settings)
    {
    }

    public RemoteTranscodeSettings RemoteTranscodeSettings => new()
    {
        Video = store.GetBoolean("settings.transcode.video"),
        Audio = store.GetBoolean("settings.transcode.audio"),
        VideoCodecs = store.Get("settings.transcode.videoCodecs") ?? RemoteTranscodeSettings.Default.VideoCodecs,
        VideoCodec = store.Get("settings.transcode.videoCodec") ?? string.Empty,
        FileBitrateThreshold = store.GetInt32(
            "settings.transcode.fileBitrateThreshold",
            (int)RemoteTranscodeSettings.Default.FileBitrateThreshold),
        VideoBitrate = store.GetInt32(
            "settings.transcode.videoBitrate",
            (int)RemoteTranscodeSettings.Default.VideoBitrate),
        VideoHeightThreshold = store.GetInt32(
            "settings.transcode.videoHeightThreshold",
            RemoteTranscodeSettings.Default.VideoHeightThreshold),
        VideoHeight = store.GetInt32("settings.transcode.videoHeight", RemoteTranscodeSettings.Default.VideoHeight),
        AudioCodec = store.Get("settings.transcode.audioCodec") ?? string.Empty,
        AudioCodecsToTranscode = store.Get("settings.transcode.audioCodecsToTranscode")
            ?? RemoteTranscodeSettings.Default.AudioCodecsToTranscode,
        AudioBitrate = store.GetInt32(
            "settings.transcode.audioBitrate",
            (int)RemoteTranscodeSettings.Default.AudioBitrate),
    };

    public event EventHandler? RemoteTranscodeSettingsChanged;

    public void SetRemoteTranscodeSettings(RemoteTranscodeSettings settings)
    {
        if (RemoteTranscodeSettings == settings)
        {
            return;
        }

        store.SetBoolean("settings.transcode.video", settings.Video);
        store.SetBoolean("settings.transcode.audio", settings.Audio);
        store.Set("settings.transcode.videoCodecs", settings.VideoCodecs);
        store.Set("settings.transcode.videoCodec", settings.VideoCodec);
        store.SetInt32("settings.transcode.fileBitrateThreshold", (int)settings.FileBitrateThreshold);
        store.SetInt32("settings.transcode.videoBitrate", (int)settings.VideoBitrate);
        store.SetInt32("settings.transcode.videoHeightThreshold", settings.VideoHeightThreshold);
        store.SetInt32("settings.transcode.videoHeight", settings.VideoHeight);
        store.Set("settings.transcode.audioCodec", settings.AudioCodec);
        store.Set("settings.transcode.audioCodecsToTranscode", settings.AudioCodecsToTranscode);
        store.SetInt32("settings.transcode.audioBitrate", (int)settings.AudioBitrate);
        RemoteTranscodeSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetLibraryFolderPaths(IEnumerable<string> folderPaths) =>
        WriteFolderPaths("settings.libraryFolders", folderPaths);

    public void SetVideoFolderPaths(IEnumerable<string> folderPaths) =>
        WriteFolderPaths("settings.videoFolders", folderPaths);

    // Newlines cannot occur in the identifiers the pickers produce, so no escaping is needed.
    private IReadOnlyList<string> ReadFolderPaths(string key) =>
        store.Get(key) is { Length: > 0 } stored
            ? stored.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [];

    private void WriteFolderPaths(string key, IEnumerable<string> folderPaths) =>
        store.Set(key, string.Join('\n', folderPaths.Where(path => !string.IsNullOrWhiteSpace(path))));

    public LibraryViewSettings GetLibraryViewSettings(LibraryItemKind kind) => new LibraryViewSettings(
        store.GetEnum(ViewKey(kind, "sort"), LibraryViewSettings.Default(kind).Sort),
        store.GetBoolean(ViewKey(kind, "ascending"), defaultValue: true))
    {
        GroupingEnabled = store.GetBoolean(ViewKey(kind, "groupingEnabled")),
        GroupBy = store.GetEnum(ViewKey(kind, "groupBy"), LibraryViewSettings.Default(kind).GroupBy),
    }.Normalized(kind);

    public void SetLibraryViewSettings(LibraryItemKind kind, LibraryViewSettings settings)
    {
        settings = settings.Normalized(kind);
        store.SetEnum(ViewKey(kind, "sort"), settings.Sort);
        store.SetBoolean(ViewKey(kind, "ascending"), settings.Ascending);
        store.SetBoolean(ViewKey(kind, "groupingEnabled"), settings.GroupingEnabled);
        store.SetEnum(ViewKey(kind, "groupBy"), settings.GroupBy);
    }

    public LibraryLayoutMode GetLibraryLayout(LibraryItemKind kind) =>
        store.GetEnum(ViewKey(kind, "layout"), LibraryLayoutModes.Default(kind));

    public void SetLibraryLayout(LibraryItemKind kind, LibraryLayoutMode layout) =>
        store.SetEnum(ViewKey(kind, "layout"), layout);

    public LibraryItemPresentation VideoNarrowPresentation
    {
        get
        {
            var value = store.GetEnum(ViewKey(LibraryItemKind.Video, "narrowPresentation"), LibraryItemPresentation.Grid);
            return Enum.IsDefined(value) ? value : LibraryItemPresentation.Grid;
        }
        set => store.SetEnum(ViewKey(LibraryItemKind.Video, "narrowPresentation"), value);
    }

    private static string ViewKey(LibraryItemKind kind, string name) =>
        $"settings.library.{kind}.{name}";
}

/// <summary>
/// Settings of a remote session: everything that describes this device (appearance, language,
/// audio output) keeps using the head's own store, while the library folders are reported by the
/// server and cannot be edited from here.
/// </summary>
public sealed class RemoteAppSettingsStore(
    IAppSettingsStore inner,
    IReadOnlyList<string> libraryFolderPaths,
    IReadOnlyList<string> videoFolderPaths)
    : IAppSettingsStore
{
    public IReadOnlyList<string> LibraryFolderPaths { get; } = libraryFolderPaths;

    public IReadOnlyList<string> VideoFolderPaths { get; } = videoFolderPaths;

    public NativeLibrarySettings NativeLibrary
    {
        get => inner.NativeLibrary;
        set => inner.NativeLibrary = value;
    }

    public event EventHandler? NativeLibraryChanged
    {
        add => inner.NativeLibraryChanged += value;
        remove => inner.NativeLibraryChanged -= value;
    }

    public void SetLibraryFolderPaths(IEnumerable<string> folderPaths) =>
        throw new NotSupportedException("Library folders belong to the server. Change them in the app running on it.");

    public void SetVideoFolderPaths(IEnumerable<string> folderPaths) =>
        throw new NotSupportedException("Library folders belong to the server. Change them in the app running on it.");

    /// <summary>Scanning is the server's job; a client never starts one at launch.</summary>
    public bool ScanLibraryOnStartup
    {
        get => false;
        set { }
    }

    public bool AutoScanLibrary
    {
        get => false;
        set { }
    }

    public int AutoScanIntervalMinutes
    {
        get => 10;
        set { }
    }

    public event EventHandler? LibraryScanSettingsChanged
    {
        add { }
        remove { }
    }

    /// <summary>A client does not serve its own API, so the head's server settings stay hidden.</summary>
    public RemoteApiSettings RemoteApiSettings => RemoteApiSettings.Disabled;

    public void SetRemoteApiSettings(RemoteApiSettings settings)
    {
    }

    /// <summary>Belongs to this device rather than to the session, so it stays in the head's store.</summary>
    public RemoteTranscodeSettings RemoteTranscodeSettings => inner.RemoteTranscodeSettings;

    public event EventHandler? RemoteTranscodeSettingsChanged
    {
        add => inner.RemoteTranscodeSettingsChanged += value;
        remove => inner.RemoteTranscodeSettingsChanged -= value;
    }

    public void SetRemoteTranscodeSettings(RemoteTranscodeSettings settings) =>
        inner.SetRemoteTranscodeSettings(settings);

    public AppThemePreference ThemePreference
    {
        get => inner.ThemePreference;
        set => inner.ThemePreference = value;
    }

    public string SelectedLightColorThemeId
    {
        get => inner.SelectedLightColorThemeId;
        set => inner.SelectedLightColorThemeId = value;
    }

    public string SelectedDarkColorThemeId
    {
        get => inner.SelectedDarkColorThemeId;
        set => inner.SelectedDarkColorThemeId = value;
    }

    public bool EnableMicaBackdrop
    {
        get => inner.EnableMicaBackdrop;
        set => inner.EnableMicaBackdrop = value;
    }

    public BackdropStyle BackdropStyle
    {
        get => inner.BackdropStyle;
        set => inner.BackdropStyle = value;
    }

    public bool EnableAppleChrome
    {
        get => inner.EnableAppleChrome;
        set => inner.EnableAppleChrome = value;
    }

    public double LiquidGlassFrost
    {
        get => inner.LiquidGlassFrost;
        set => inner.LiquidGlassFrost = value;
    }

    public IReadOnlyList<CustomColorThemeRecord> CustomColorThemes => inner.CustomColorThemes;

    public void SaveCustomColorTheme(CustomColorThemeRecord record) => inner.SaveCustomColorTheme(record);

    public void DeleteCustomColorTheme(string id) => inner.DeleteCustomColorTheme(id);

    public AppRouteId DefaultRouteId
    {
        get => inner.DefaultRouteId;
        set => inner.DefaultRouteId = value;
    }

    public bool RememberLastPage
    {
        get => inner.RememberLastPage;
        set => inner.RememberLastPage = value;
    }

    public AppRouteId LastUsedRouteId
    {
        get => inner.LastUsedRouteId;
        set => inner.LastUsedRouteId = value;
    }

    public int PreferredSampleRate
    {
        get => inner.PreferredSampleRate;
        set => inner.PreferredSampleRate = value;
    }

    public bool AlwaysResample
    {
        get => inner.AlwaysResample;
        set => inner.AlwaysResample = value;
    }

    public VideoScalingQuality PreferredVideoScaling
    {
        get => inner.PreferredVideoScaling;
        set => inner.PreferredVideoScaling = value;
    }

    public VideoScalingQuality PreferredVideoDownscaling
    {
        get => inner.PreferredVideoDownscaling;
        set => inner.PreferredVideoDownscaling = value;
    }

    public bool PreferHardwareVideoProcessing
    {
        get => inner.PreferHardwareVideoProcessing;
        set => inner.PreferHardwareVideoProcessing = value;
    }

    public VideoDebandingQuality VideoDebanding
    {
        get => inner.VideoDebanding;
        set => inner.VideoDebanding = value;
    }

    public bool EnableHdrPassthrough
    {
        get => inner.EnableHdrPassthrough;
        set => inner.EnableHdrPassthrough = value;
    }

    public Bt709Interpretation VideoBt709Interpretation
    {
        get => inner.VideoBt709Interpretation;
        set => inner.VideoBt709Interpretation = value;
    }

    public int DisplayPanelBitDepth
    {
        get => inner.DisplayPanelBitDepth;
        set => inner.DisplayPanelBitDepth = value;
    }

    public TimeSpan VideoBufferDuration
    {
        get => inner.VideoBufferDuration;
        set => inner.VideoBufferDuration = value;
    }

    public bool AutoPlayNextVideo
    {
        get => inner.AutoPlayNextVideo;
        set => inner.AutoPlayNextVideo = value;
    }

    public int SubtitleFontSize
    {
        get => inner.SubtitleFontSize;
        set => inner.SubtitleFontSize = value;
    }

    public string SubtitleFontFamily
    {
        get => inner.SubtitleFontFamily;
        set => inner.SubtitleFontFamily = value;
    }

    public bool EnableHardwareDecoding
    {
        get => inner.EnableHardwareDecoding;
        set => inner.EnableHardwareDecoding = value;
    }

    public string PreferredHardwareDecoder
    {
        get => inner.PreferredHardwareDecoder;
        set => inner.PreferredHardwareDecoder = value;
    }

    public bool PreferPhotoDecodePerformance
    {
        get => inner.PreferPhotoDecodePerformance;
        set => inner.PreferPhotoDecodePerformance = value;
    }

    public IReadOnlyDictionary<string, HardwareDecoderConfiguration> HardwareDecoderConfigurations =>
        inner.HardwareDecoderConfigurations;

    public ToneMappingSettings ToneMapping => inner.ToneMapping;

    public Task SetToneMappingAsync(ToneMappingSettings settings) => inner.SetToneMappingAsync(settings);

    public void SetHardwareDecoderConfiguration(string deviceType, HardwareDecoderConfiguration configuration) =>
        inner.SetHardwareDecoderConfiguration(deviceType, configuration);

    public string AudioOutputDeviceId
    {
        get => inner.AudioOutputDeviceId;
        set => inner.AudioOutputDeviceId = value;
    }

    public IReadOnlyDictionary<string, string> AudioOutputOptions => inner.AudioOutputOptions;

    public void SetAudioOutputOption(string key, string value) => inner.SetAudioOutputOption(key, value);

    public string InterfaceLanguage
    {
        get => inner.InterfaceLanguage;
        set => inner.InterfaceLanguage = value;
    }

    public WaylandMode WaylandMode
    {
        get => inner.WaylandMode;
        set => inner.WaylandMode = value;
    }

    public bool EnableThirdPartyLyrics
    {
        get => inner.EnableThirdPartyLyrics;
        set => inner.EnableThirdPartyLyrics = value;
    }

    public bool EnablePlaybackHistory
    {
        get => inner.EnablePlaybackHistory;
        set => inner.EnablePlaybackHistory = value;
    }

    public AlbumGroupingMode AlbumGrouping
    {
        get => inner.AlbumGrouping;
        set => inner.AlbumGrouping = value;
    }

    public event EventHandler? AlbumGroupingChanged
    {
        add => inner.AlbumGroupingChanged += value;
        remove => inner.AlbumGroupingChanged -= value;
    }

    public bool InitialSetupCompleted
    {
        get => inner.InitialSetupCompleted;
        set => inner.InitialSetupCompleted = value;
    }

    public LibraryViewSettings GetLibraryViewSettings(LibraryItemKind kind) => inner.GetLibraryViewSettings(kind);

    public void SetLibraryViewSettings(LibraryItemKind kind, LibraryViewSettings settings) =>
        inner.SetLibraryViewSettings(kind, settings);

    public LibraryLayoutMode GetLibraryLayout(LibraryItemKind kind) => inner.GetLibraryLayout(kind);

    public void SetLibraryLayout(LibraryItemKind kind, LibraryLayoutMode layout) => inner.SetLibraryLayout(kind, layout);

    public LibraryItemPresentation VideoNarrowPresentation
    {
        get => inner.VideoNarrowPresentation;
        set => inner.VideoNarrowPresentation = value;
    }
}

/// <summary>
/// Shell state of a remote session. The queue's contents name tracks of one particular library, so
/// they are stored per server; the window's placement and the pane's visibility describe this
/// device and stay in the head's own store.
/// </summary>
public sealed class RemoteShellStateStore(IShellStateStore device, IKeyValueStore serverState) : IShellStateStore
{
    public bool IsQueuePaneOpen
    {
        get => device.IsQueuePaneOpen;
        set => device.IsQueuePaneOpen = value;
    }

    public double MinimizedVideoWidth
    {
        get => device.MinimizedVideoWidth;
        set => device.MinimizedVideoWidth = value;
    }

    public WindowPlacement? WindowPlacement
    {
        get => device.WindowPlacement;
        set => device.WindowPlacement = value;
    }

    public PictureInPicturePlacement? PictureInPicturePlacement
    {
        get => device.PictureInPicturePlacement;
        set => device.PictureInPicturePlacement = value;
    }

    public PlaybackQueuePersistedState? PlaybackQueue
    {
        get => GetJson<PlaybackQueuePersistedState>("shell.playbackQueue");
        set => SetJson("shell.playbackQueue", value);
    }

    private T? GetJson<T>(string key)
    {
        var json = serverState.Get(key);
        return string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize(json, JsonTypeInfo<T>());
    }

    private void SetJson<T>(string key, T? value)
    {
        if (value is null)
        {
            serverState.Remove(key);
            return;
        }

        serverState.Set(key, JsonSerializer.Serialize(value, JsonTypeInfo<T>()));
    }

    private static JsonTypeInfo<T> JsonTypeInfo<T>() =>
        (JsonTypeInfo<T>?)RemoteStateJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new InvalidOperationException($"No generated remote-state metadata exists for {typeof(T)}.");
}

/// <summary>
/// <see cref="IShellStateStore"/> over an <see cref="IKeyValueStore"/>, for heads with no shell
/// state file of their own (the browser).
/// </summary>
public sealed class KeyValueShellStateStore(IKeyValueStore store) : IShellStateStore
{
    public bool IsQueuePaneOpen
    {
        get => store.GetBoolean("shell.queuePaneOpen");
        set => store.SetBoolean("shell.queuePaneOpen", value);
    }

    public double MinimizedVideoWidth
    {
        get => store.GetDouble("shell.minimizedVideoWidth");
        set => store.SetDouble("shell.minimizedVideoWidth", value);
    }

    public PlaybackQueuePersistedState? PlaybackQueue
    {
        get => GetJson<PlaybackQueuePersistedState>("shell.playbackQueue");
        set => SetJson("shell.playbackQueue", value);
    }

    public WindowPlacement? WindowPlacement
    {
        get => GetJson<WindowPlacement>("shell.windowPlacement");
        set => SetJson("shell.windowPlacement", value);
    }

    public PictureInPicturePlacement? PictureInPicturePlacement
    {
        get => GetJson<PictureInPicturePlacement>("shell.pictureInPicturePlacement");
        set => SetJson("shell.pictureInPicturePlacement", value);
    }

    private T? GetJson<T>(string key)
    {
        var json = store.Get(key);
        return string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize(json, JsonTypeInfo<T>());
    }

    private void SetJson<T>(string key, T? value)
    {
        if (value is null)
        {
            store.Remove(key);
            return;
        }

        store.Set(key, JsonSerializer.Serialize(value, JsonTypeInfo<T>()));
    }

    private static JsonTypeInfo<T> JsonTypeInfo<T>() =>
        (JsonTypeInfo<T>?)RemoteStateJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new InvalidOperationException($"No generated remote-state metadata exists for {typeof(T)}.");
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PlaybackQueuePersistedState))]
[JsonSerializable(typeof(PlaybackQueueItemState))]
[JsonSerializable(typeof(WindowPlacement))]
[JsonSerializable(typeof(PictureInPicturePlacement))]
[JsonSerializable(typeof(RemoteHistoryItem[]))]
[JsonSerializable(typeof(List<CustomColorThemeRecord>))]
[JsonSerializable(typeof(RemoteServerEntry[]))]
[JsonSerializable(typeof(RemoteLibraryDefinition[]))]
[JsonSerializable(typeof(LegacyRemoteLibraryDefinition[]))]
[JsonSerializable(typeof(LibraryVisibilitySettings))]
[JsonSerializable(typeof(NativeLibrarySettings))]
[JsonSerializable(typeof(ToneMappingSettings))]
[JsonSerializable(typeof(LegacyBrowserServerEntry[]))]
public sealed partial class RemoteStateJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NextcloudMusicCatalogState))]
internal sealed partial class NextcloudMusicJsonContext : JsonSerializerContext;
