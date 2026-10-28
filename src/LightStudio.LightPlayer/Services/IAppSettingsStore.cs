using LightMediaRenderer.Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightStudio.FfmpegShim.Core;
using LightStudio.LightPlayer.Models;
using LightMediaRenderer.Avalonia.Shaders;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// The depth the picture is finally quantized to, which the surface does not always know.
/// </summary>
/// <remarks>
/// A float surface leaves the quantization to the compositor, and a compositor can hand an 8-bit
/// signal to a panel that only resolves six of those bits. Neither is visible from inside the
/// process, so the depth to dither towards is something only the user can settle, and
/// <see cref="Automatic"/> leaves it to the surface as before.
/// </remarks>
public static class VideoPanelBitDepthSettings
{
    /// <summary>Follow whatever the output surface quantizes to.</summary>
    public const int Automatic = 0;

    /// <summary>The depths the user can pin the dither target to.</summary>
    public static IReadOnlyList<int> Supported { get; } = [6, 8, 10];

    public static int Normalize(int value) => value is 6 or 8 or 10 ? value : Automatic;
}

/// <summary>Font size of plain text subtitles such as SubRip, in script pixels of a 288-line frame.</summary>
public static class SubtitleFontSizeSettings
{
    public const int Default = 18;

    public const int Minimum = 8;

    public const int Maximum = 48;

    public static int Normalize(int value) => Math.Clamp(value, Minimum, Maximum);
}

public interface IAppSettingsStore
{
    IReadOnlyList<string> LibraryFolderPaths { get; }

    /// <summary>Folders scanned for video, kept separate from <see cref="LibraryFolderPaths"/>.</summary>
    IReadOnlyList<string> VideoFolderPaths { get; }

    NativeLibrarySettings NativeLibrary { get; set; }

    event EventHandler? NativeLibraryChanged;

    AppThemePreference ThemePreference { get; set; }

    /// <summary>Id of the color theme applied for the light appearance.</summary>
    string SelectedLightColorThemeId { get; set; }

    /// <summary>Id of the color theme applied for the dark appearance.</summary>
    string SelectedDarkColorThemeId { get; set; }

    /// <summary>
    /// Whether the shell paints itself over the system backdrop (Mica on Windows 11) instead of
    /// its own opaque page color. Inert on heads that cannot provide a backdrop.
    /// </summary>
    bool EnableMicaBackdrop { get; set; }

    BackdropStyle BackdropStyle { get; set; }

    /// <summary>
    /// Whether the shell uses its native Apple UI style. Inert where
    /// <see cref="PlatformServices.SupportsAppleChrome"/> is false.
    /// </summary>
    bool EnableAppleChrome { get; set; }

    /// <summary>How frosted liquid glass appears, from 0 for clear to 1 for frosted.</summary>
    double LiquidGlassFrost { get; set; }

    /// <summary>The user-authored color themes, in creation order.</summary>
    IReadOnlyList<CustomColorThemeRecord> CustomColorThemes { get; }

    /// <summary>Adds a custom color theme, or replaces the existing one with the same id.</summary>
    void SaveCustomColorTheme(CustomColorThemeRecord record);

    /// <summary>Removes the custom color theme with the given id, if present.</summary>
    void DeleteCustomColorTheme(string id);

    AppRouteId DefaultRouteId { get; set; }

    /// <summary>
    /// When true, the shell opens the last page the user visited instead of <see cref="DefaultRouteId"/>.
    /// </summary>
    bool RememberLastPage { get; set; }

    /// <summary>
    /// The most recently visited top-level page, used when <see cref="RememberLastPage"/> is enabled.
    /// </summary>
    AppRouteId LastUsedRouteId { get; set; }

    /// <summary>Preferred audio output sample rate in Hz, or 0 to follow the system rate. Used only when <see cref="AlwaysResample"/> is on.</summary>
    int PreferredSampleRate { get; set; }

    /// <summary>Master switch: when true, audio is resampled to the preferred rate (or system rate when 0); when false, the source rate is used.</summary>
    bool AlwaysResample { get; set; }

    /// <summary>Filter used when video is enlarged or rendered at its source size.</summary>
    VideoScalingQuality PreferredVideoScaling { get; set; }

    /// <summary>Filter used when video is reduced in physical output pixels.</summary>
    VideoScalingQuality PreferredVideoDownscaling { get; set; }

    /// <summary>
    /// Whether a hardware decoder may resize the picture to output size on its own device, leaving
    /// the filters above with one sample per pixel to draw. Only possible where the decoder's
    /// surfaces reach the renderer untouched and nothing else needs the picture at its coded size.
    /// </summary>
    bool PreferHardwareVideoProcessing { get; set; }

    /// <summary>Strength of source-gradient reconstruction before video is scaled.</summary>
    VideoDebandingQuality VideoDebanding { get; set; }

    /// <summary>
    /// Whether video uses the platform's wide-gamut, extended-range surface. Turning it off uses a
    /// standard 8-bit sRGB surface with built-in tone mapping and dithering.
    /// </summary>
    bool EnableHdrPassthrough { get; set; }

    Bt709Interpretation VideoBt709Interpretation { get; set; }

    /// <summary>
    /// Bit depth the display panel resolves, or <see cref="VideoPanelBitDepthSettings.Automatic"/>
    /// to follow the output surface. Video is dithered towards it when the picture carries more
    /// precision than it can show.
    /// </summary>
    int DisplayPanelBitDepth { get; set; }

    /// <summary>
    /// How much decoded video to keep ahead of playback. Frames are derived from the source's frame
    /// rate, so this buffers the same amount of time whatever the content runs at.
    /// </summary>
    TimeSpan VideoBufferDuration { get; set; }

    /// <summary>Whether reaching the end of a video opens the next entry of its list by itself.</summary>
    bool AutoPlayNextVideo { get; set; }

    /// <summary>Font size of subtitles without authored styles; see <see cref="SubtitleFontSizeSettings"/>.</summary>
    int SubtitleFontSize { get; set; }

    /// <summary>Font family of subtitles without authored styles, or empty for the generated font.</summary>
    string SubtitleFontFamily { get; set; }

    /// <summary>Whether newly opened videos may use a hardware decoder.</summary>
    bool EnableHardwareDecoding { get; set; }

    /// <summary>Preferred FFmpeg hardware device type, or empty for automatic selection.</summary>
    string PreferredHardwareDecoder { get; set; }

    bool PreferPhotoDecodePerformance { get; set; }

    /// <summary>Named HDR tone mapping profiles and the defaults for images and videos.</summary>
    ToneMappingSettings ToneMapping { get; }

    Task SetToneMappingAsync(ToneMappingSettings settings);

    /// <summary>Device and per-call arguments chosen for each hardware device type.</summary>
    IReadOnlyDictionary<string, HardwareDecoderConfiguration> HardwareDecoderConfigurations { get; }

    /// <summary>Persists the configuration of one hardware device type.</summary>
    void SetHardwareDecoderConfiguration(string deviceType, HardwareDecoderConfiguration configuration);

    /// <summary>Stable backend-defined audio endpoint identifier.</summary>
    string AudioOutputDeviceId { get; set; }

    /// <summary>Persisted backend-defined key/value options.</summary>
    IReadOnlyDictionary<string, string> AudioOutputOptions { get; }

    /// <summary>Persists one backend-defined audio option.</summary>
    void SetAudioOutputOption(string key, string value);

    /// <summary>BCP-47 interface language tag, or empty to follow the operating system.</summary>
    string InterfaceLanguage { get; set; }

    /// <summary>Windowing backend chosen on the Linux desktop head. Inert on every other head.</summary>
    WaylandMode WaylandMode { get; set; }

    /// <summary>When true, bundled third-party lyric sources are provisioned and used.</summary>
    bool EnableThirdPartyLyrics { get; set; }

    /// <summary>When true, played tracks are recorded to the playback history shown on the home page.</summary>
    bool EnablePlaybackHistory { get; set; }

    AlbumGroupingMode AlbumGrouping { get; set; }

    event EventHandler? AlbumGroupingChanged;

    /// <summary>When true, the library folders are scanned for changes automatically each time the app starts.</summary>
    bool ScanLibraryOnStartup { get; set; }

    bool AutoScanLibrary { get; set; }

    int AutoScanIntervalMinutes { get; set; }

    event EventHandler? LibraryScanSettingsChanged;

    /// <summary>When true, the first-run setup has been completed and is skipped on launch.</summary>
    bool InitialSetupCompleted { get; set; }

    /// <summary>Desktop HTTP server configuration. Hidden and inert on non-desktop heads.</summary>
    RemoteApiSettings RemoteApiSettings { get; }

    void SetRemoteApiSettings(RemoteApiSettings settings);

    /// <summary>
    /// What this device asks a server to re-encode for it. Read whenever a remote video is opened;
    /// inert while the device is playing its own library.
    /// </summary>
    RemoteTranscodeSettings RemoteTranscodeSettings { get; }

    void SetRemoteTranscodeSettings(RemoteTranscodeSettings settings);

    event EventHandler? RemoteTranscodeSettingsChanged;

    void SetLibraryFolderPaths(IEnumerable<string> folderPaths);

    void SetVideoFolderPaths(IEnumerable<string> folderPaths);

    LibraryViewSettings GetLibraryViewSettings(LibraryItemKind kind);

    void SetLibraryViewSettings(LibraryItemKind kind, LibraryViewSettings settings);

    /// <summary>The persisted grid/list layout for a browse kind (albums / artists).</summary>
    LibraryLayoutMode GetLibraryLayout(LibraryItemKind kind);

    void SetLibraryLayout(LibraryItemKind kind, LibraryLayoutMode layout);

    LibraryItemPresentation VideoNarrowPresentation { get; set; }
}