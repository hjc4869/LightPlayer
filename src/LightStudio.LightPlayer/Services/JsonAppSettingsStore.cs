using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private readonly string filePath;
    private PersistedAppSettings settings;

    public JsonAppSettingsStore()
        : this(DefaultFilePath())
    {
    }

    public JsonAppSettingsStore(string filePath)
    {
        this.filePath = filePath;
        settings = Load(filePath);
    }

    public IReadOnlyList<string> LibraryFolderPaths => settings.LibraryFolderPaths;

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

    public void SetLibraryFolderPaths(IEnumerable<string> folderPaths)
    {
        settings.LibraryFolderPaths = folderPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(GetPathComparer())
            .ToList();
        Save();
    }

    public LibraryViewSettings GetLibraryViewSettings(LibraryItemKind kind)
    {
        var (grouping, sorting, ascending) = ReadKind(kind);
        var field = Enum.TryParse<LibrarySortField>(sorting, ignoreCase: true, out var parsed)
            ? parsed
            : LibraryViewSettings.Default(kind).Sort;
        return new LibraryViewSettings(field, ascending, grouping).Normalized(kind);
    }

    public void SetLibraryViewSettings(LibraryItemKind kind, LibraryViewSettings value)
    {
        var normalized = value.Normalized(kind);
        switch (kind)
        {
            case LibraryItemKind.Album:
                settings.EnableAlbumGrouping = normalized.GroupingEnabled;
                settings.AlbumSorting = normalized.Sort.ToString();
                settings.AlbumSortAscending = normalized.Ascending;
                break;
            case LibraryItemKind.Artist:
                settings.EnableArtistGrouping = normalized.GroupingEnabled;
                settings.ArtistSorting = normalized.Sort.ToString();
                settings.ArtistSortAscending = normalized.Ascending;
                break;
            default:
                settings.EnableSongGrouping = normalized.GroupingEnabled;
                settings.SongSorting = normalized.Sort.ToString();
                settings.SongSortAscending = normalized.Ascending;
                break;
        }

        Save();
    }

    private (bool Grouping, string Sorting, bool Ascending) ReadKind(LibraryItemKind kind) => kind switch
    {
        LibraryItemKind.Album => (settings.EnableAlbumGrouping, settings.AlbumSorting, settings.AlbumSortAscending),
        LibraryItemKind.Artist => (settings.EnableArtistGrouping, settings.ArtistSorting, settings.ArtistSortAscending),
        _ => (settings.EnableSongGrouping, settings.SongSorting, settings.SongSortAscending),
    };

    private static string DefaultFilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(root, "LightStudio.LightPlayer", "settings.json");
    }

    private static PersistedAppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.PersistedAppSettings);
                if (loaded is not null)
                {
                    loaded.LibraryFolderPaths ??= [];
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to defaults when settings are missing or unreadable.
        }

        return new PersistedAppSettings();
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.PersistedAppSettings);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings persistence is best-effort; the UI stays usable if disk writes fail.
        }
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}

public sealed class PersistedAppSettings
{
    public List<string> LibraryFolderPaths { get; set; } = [];

    public string ThemePreference { get; set; } = nameof(AppThemePreference.Default);

    public string DefaultRouteId { get; set; } = nameof(AppRouteId.Home);

    public bool RememberLastPage { get; set; }

    public string LastUsedRouteId { get; set; } = nameof(AppRouteId.Home);

    public int PreferredSampleRate { get; set; }

    public bool AlwaysResample { get; set; }

    public string InterfaceLanguage { get; set; } = string.Empty;

    public bool EnableThirdPartyLyrics { get; set; }

    public bool EnablePlaybackHistory { get; set; }

    public bool ScanLibraryOnStartup { get; set; }

    public bool InitialSetupCompleted { get; set; }

    public bool EnableSongGrouping { get; set; }

    public string SongSorting { get; set; } = nameof(LibrarySortField.Title);

    public bool SongSortAscending { get; set; } = true;

    public bool EnableAlbumGrouping { get; set; }

    public string AlbumSorting { get; set; } = nameof(LibrarySortField.AlbumName);

    public bool AlbumSortAscending { get; set; } = true;

    public bool EnableArtistGrouping { get; set; }

    public string ArtistSorting { get; set; } = nameof(LibrarySortField.ArtistName);

    public bool ArtistSortAscending { get; set; } = true;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PersistedAppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}