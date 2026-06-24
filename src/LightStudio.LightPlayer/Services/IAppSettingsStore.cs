using System.Collections.Generic;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

public interface IAppSettingsStore
{
    IReadOnlyList<string> LibraryFolderPaths { get; }

    AppThemePreference ThemePreference { get; set; }

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

    /// <summary>BCP-47 interface language tag, or empty to follow the operating system.</summary>
    string InterfaceLanguage { get; set; }

    /// <summary>When true, bundled third-party lyric sources are provisioned and used.</summary>
    bool EnableThirdPartyLyrics { get; set; }

    /// <summary>When true, played tracks are recorded to the playback history shown on the home page.</summary>
    bool EnablePlaybackHistory { get; set; }

    /// <summary>When true, the library folders are scanned for changes automatically each time the app starts.</summary>
    bool ScanLibraryOnStartup { get; set; }

    /// <summary>When true, the first-run setup has been completed and is skipped on launch.</summary>
    bool InitialSetupCompleted { get; set; }

    void SetLibraryFolderPaths(IEnumerable<string> folderPaths);

    LibraryViewSettings GetLibraryViewSettings(LibraryItemKind kind);

    void SetLibraryViewSettings(LibraryItemKind kind, LibraryViewSettings settings);
}