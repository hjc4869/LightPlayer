using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed class AlbumGroupingOption(string resourceKey) : ViewModelBase
{
    public string Title => Localized(resourceKey);
}

public partial class LibrarySettingsSectionViewModel : SettingsSectionViewModel
{
    private readonly IAppSettingsStore settingsStore;
    private bool isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private bool enablePlaybackHistory;

    [ObservableProperty]
    private int selectedAlbumGroupingIndex;

    [ObservableProperty]
    private bool scanLibraryOnStartup;

    [ObservableProperty]
    private bool autoScanLibrary;

    [ObservableProperty]
    private int autoScanIntervalMinutes;

    [ObservableProperty]
    private bool useSystemMusicLibrary;

    [ObservableProperty]
    private bool scanMusicFolders;

    [ObservableProperty]
    private bool useSystemMediaLibrary;

    [ObservableProperty]
    private bool scanMediaFolders;

    public LibrarySettingsSectionViewModel(IAppSettingsStore settingsStore)
        : this(settingsStore, new AsyncRelayCommand(() => Task.CompletedTask))
    {
    }

    public LibrarySettingsSectionViewModel(IAppSettingsStore settingsStore, IAsyncRelayCommand scanLibraryCommand)
        : base(nameof(Strings.Resources.SettingsSection_LibraryTitle), string.Empty)
    {
        this.settingsStore = settingsStore;

        LibraryFolders = new ObservableCollection<LibraryFolderModel>(
            settingsStore.LibraryFolderPaths.Select(path => CreateFolderModel(path)));
        VideoFolders = new ObservableCollection<LibraryFolderModel>(
            settingsStore.VideoFolderPaths.Select(path => CreateFolderModel(path)));

        RemoveFolderCommand = new RelayCommand<LibraryFolderModel>(RemoveFolder);
        RemoveVideoFolderCommand = new RelayCommand<LibraryFolderModel>(RemoveVideoFolder);
        GrantFolderAccessCommand = new AsyncRelayCommand<LibraryFolderModel>(GrantFolderAccessAsync);
        AddDefaultFoldersCommand = new AsyncRelayCommand(() => AddDefaultFoldersAsync(forVideo: false));
        AddDefaultVideoFoldersCommand = new AsyncRelayCommand(() => AddDefaultFoldersAsync(forVideo: true));
        ScanLibraryCommand = scanLibraryCommand;
        enablePlaybackHistory = settingsStore.EnablePlaybackHistory;
        selectedAlbumGroupingIndex = (int)settingsStore.AlbumGrouping;
        scanLibraryOnStartup = settingsStore.ScanLibraryOnStartup;
        autoScanLibrary = settingsStore.AutoScanLibrary;
        autoScanIntervalMinutes = settingsStore.AutoScanIntervalMinutes;
        useSystemMusicLibrary = settingsStore.NativeLibrary.UseSystemMusicLibrary;
        scanMusicFolders = settingsStore.NativeLibrary.ScanMusicFolders;
        useSystemMediaLibrary = settingsStore.NativeLibrary.UseSystemMediaLibrary;
        scanMediaFolders = settingsStore.NativeLibrary.ScanMediaFolders;
        isLoaded = true;
    }

    public IReadOnlyList<AlbumGroupingOption> AlbumGroupingOptions { get; } =
    [
        new(nameof(Strings.Resources.LibrarySettings_AlbumGroupingAlwaysCombine)),
        new(nameof(Strings.Resources.LibrarySettings_AlbumGroupingSameFolder)),
        new(nameof(Strings.Resources.LibrarySettings_AlbumGroupingSeparateArtists)),
    ];

    public ObservableCollection<LibraryFolderModel> LibraryFolders { get; }

    /// <summary>Folders scanned for video, kept separate from the music folders above.</summary>
    public ObservableCollection<LibraryFolderModel> VideoFolders { get; }

    public IRelayCommand<LibraryFolderModel> RemoveFolderCommand { get; }

    public IRelayCommand<LibraryFolderModel> RemoveVideoFolderCommand { get; }

    /// <summary>Re-asks the platform for a folder whose durable grant has lapsed (browser handles).</summary>
    public IAsyncRelayCommand<LibraryFolderModel> GrantFolderAccessCommand { get; }

    public IAsyncRelayCommand AddDefaultFoldersCommand { get; }

    public IAsyncRelayCommand AddDefaultVideoFoldersCommand { get; }

    public IAsyncRelayCommand ScanLibraryCommand { get; }

    /// <summary>Confirms the default folders before they are added. Null hides that action.</summary>
    public IDialogService? DialogService { get; init; }

    /// <summary>Whether the "add default folders" action can be offered on this head.</summary>
    public bool CanAddDefaultFolders => DialogService is not null && PlatformServices.CanManageLibraryFolders;

    public bool CanManageLibraryFolders => PlatformServices.CanManageLibraryFolders;

    public string? FolderAccessDescription => PlatformServices.LibraryFolderAccess?.AccessDescription;

    public bool HasFolderAccessDescription => !string.IsNullOrWhiteSpace(FolderAccessDescription);

    public bool SupportsSystemMusicLibrary => PlatformServices.NativeMediaLibrary?.SupportsMusic == true;

    public bool SupportsSystemMediaLibrary => PlatformServices.NativeMediaLibrary?.SupportsPhotosAndVideos == true;

    public string LibraryTitle => SupportsSystemMusicLibrary || SupportsSystemMediaLibrary
        ? Strings.Resources.SettingsSection_LibraryTitle : Strings.Resources.LibrarySettings_Title;

    public string LibrarySubtitle => SupportsSystemMusicLibrary || SupportsSystemMediaLibrary
        ? Strings.Resources.LibrarySettings_SourcesDescription : Strings.Resources.LibrarySettings_Subtitle;

    public string MusicLibraryHeader => SupportsSystemMusicLibrary
        ? Strings.Resources.LibrarySettings_MusicLibraryHeader : Strings.Resources.LibrarySettings_MusicFoldersHeader;

    public string MediaLibraryHeader => SupportsSystemMediaLibrary
        ? Strings.Resources.LibrarySettings_MediaLibraryHeader : Strings.Resources.LibrarySettings_VideoFoldersHeader;

    public string MusicLibraryDescription => SupportsSystemMusicLibrary ? string.Empty : Strings.Resources.LibrarySettings_FoldersDescription;

    public string MediaLibraryDescription => SupportsSystemMediaLibrary ? string.Empty : Strings.Resources.LibrarySettings_VideoFoldersDescription;

    public bool ShowMusicFolders => !SupportsSystemMusicLibrary || ScanMusicFolders;

    public bool ShowMediaFolders => !SupportsSystemMediaLibrary || ScanMediaFolders;

    public bool CanScanFolders => CanManageLibraryFolders && (ShowMusicFolders || ShowVideoFolders && ShowMediaFolders);

    public bool ShowStartupScanSetting => CanScanFolders && PlatformServices.SupportsStartupLibraryScan;

    public bool ShowScanAction => ShowScanButton && CanScanFolders;

    public bool ShowAlbumGrouping => CanManageLibraryFolders && ShowMusicFolders;

    public bool CanAutoScanLibrary => ShowStartupScanSetting;

    public bool ShowAutoScanInterval => CanAutoScanLibrary && !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

    /// <summary>Controls visibility of the manual "Scan now" button (hidden during first-run setup).</summary>
    public bool ShowScanButton { get; init; } = true;

    /// <summary>
    /// Optional list of the remote libraries combined with this device's own, shown under the
    /// library folders. Null on heads that cannot reach a remote library.
    /// </summary>
    public RemoteLibrariesSettingsSectionViewModel? RemoteLibraries { get; init; }

    /// <summary>Whether the remote-library list is present and should be displayed.</summary>
    public bool HasRemoteLibraries => RemoteLibraries is not null;

    /// <summary>
    /// Optional management of the caches this device keeps for the library. Null when no cache is
    /// manageable here, and during first-run setup.
    /// </summary>
    public StorageSettingsSectionViewModel? Storage { get; init; }

    /// <summary>Whether the storage section is present and should be displayed.</summary>
    public bool HasStorage => Storage is not null;

    public bool HasLibraryFolders => LibraryFolders.Count > 0;

    public bool HasVideoFolders => VideoFolders.Count > 0;

    /// <summary>The video library is only offered where the app can manage folders at all.</summary>
    public bool ShowVideoFolders { get; init; } = true;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>
    /// Invoked when the user turns playback history off, so existing history is
    /// cleared. Null when no history service is available.
    /// </summary>
    public Func<Task>? ClearPlaybackHistoryAsync { get; init; }

    partial void OnUseSystemMusicLibraryChanged(bool value)
    {
        if (!isLoaded)
            return;
        settingsStore.NativeLibrary = settingsStore.NativeLibrary with { UseSystemMusicLibrary = value };
        if (value)
            _ = RequestSystemAccessAsync(LibraryMediaCategory.Music);
    }

    partial void OnUseSystemMediaLibraryChanged(bool value)
    {
        if (!isLoaded)
            return;
        settingsStore.NativeLibrary = settingsStore.NativeLibrary with { UseSystemMediaLibrary = value };
        if (value)
            _ = RequestSystemAccessAsync(LibraryMediaCategory.Video);
    }

    partial void OnScanMusicFoldersChanged(bool value)
    {
        if (!isLoaded)
            return;
        settingsStore.NativeLibrary = settingsStore.NativeLibrary with { ScanMusicFolders = value };
        RefreshSourceVisibility();
    }

    partial void OnScanMediaFoldersChanged(bool value)
    {
        if (!isLoaded)
            return;
        settingsStore.NativeLibrary = settingsStore.NativeLibrary with { ScanMediaFolders = value };
        RefreshSourceVisibility();
    }

    private void RefreshSourceVisibility()
    {
        OnPropertyChanged(nameof(ShowMusicFolders));
        OnPropertyChanged(nameof(ShowMediaFolders));
        OnPropertyChanged(nameof(CanScanFolders));
        OnPropertyChanged(nameof(ShowStartupScanSetting));
        OnPropertyChanged(nameof(ShowScanAction));
        OnPropertyChanged(nameof(ShowAlbumGrouping));
        OnPropertyChanged(nameof(CanAutoScanLibrary));
        OnPropertyChanged(nameof(ShowAutoScanInterval));
    }

    [RelayCommand]
    private Task ManageSystemMusicAccessAsync() => RequestSystemAccessAsync(LibraryMediaCategory.Music, manageAccess: true);

    [RelayCommand]
    private Task ManageSystemMediaAccessAsync() => RequestSystemAccessAsync(LibraryMediaCategory.Video, manageAccess: true);

    private async Task RequestSystemAccessAsync(LibraryMediaCategory category, bool manageAccess = false)
    {
        if (PlatformServices.NativeMediaLibrary is not { } library)
            return;
        try
        {
            StatusMessage = await library.RequestAccessAsync(category, manageAccess)
                ? string.Empty : Strings.Resources.LibrarySettings_SystemLibraryAccessDenied;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLog.Warning("Library.Settings", "Could not request system media access.", exception);
            StatusMessage = exception.Message;
        }
    }

    partial void OnEnablePlaybackHistoryChanged(bool value)
    {
        if (!isLoaded)
        {
            return;
        }

        settingsStore.EnablePlaybackHistory = value;

        if (!value && ClearPlaybackHistoryAsync is not null)
        {
            _ = ClearPlaybackHistoryAsync();
        }
    }

    partial void OnSelectedAlbumGroupingIndexChanged(int value)
    {
        if (isLoaded && Enum.IsDefined((AlbumGroupingMode)value))
        {
            settingsStore.AlbumGrouping = (AlbumGroupingMode)value;
        }
    }

    partial void OnScanLibraryOnStartupChanged(bool value)
    {
        if (!isLoaded)
        {
            return;
        }

        settingsStore.ScanLibraryOnStartup = value;
    }

    partial void OnAutoScanLibraryChanged(bool value)
    {
        if (isLoaded)
        {
            settingsStore.AutoScanLibrary = value;
        }
    }

    partial void OnAutoScanIntervalMinutesChanged(int value)
    {
        if (isLoaded)
        {
            settingsStore.AutoScanIntervalMinutes = value;
        }
    }

    public async Task AddFolderAsync(IStorageProvider? storageProvider)
    {
        await AddFolderAsync(storageProvider, forVideo: false);
    }

    /// <summary>Picks a folder and adds it to either the music or the video library.</summary>
    public async Task AddFolderAsync(IStorageProvider? storageProvider, bool forVideo)
    {
        if (!PlatformServices.CanManageLibraryFolders)
        {
            throw new NotSupportedException("This host cannot keep a library folder, so none can be added.");
        }

        // Platforms that own their dialog go first, with nothing awaited in between: a browser only
        // opens its directory picker while the click is still the current user gesture.
        if (PlatformServices.LibraryFolderAccess is { ProvidesFolderPicker: true } picker)
        {
            if (await picker.PickFolderAsync(forVideo) is { } picked)
            {
                AddFolder(picked.Identifier, picked.DisplayName, forVideo: forVideo);
            }

            return;
        }

        if (storageProvider is null || !storageProvider.CanPickFolder)
        {
            StatusMessage = Strings.Resources.LibrarySettings_FolderPickingUnavailable;
            return;
        }

        var startLocation = await storageProvider.TryGetWellKnownFolderAsync(
            forVideo ? WellKnownFolder.Videos : WellKnownFolder.Music);
        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = forVideo
                ? Strings.Resources.LibrarySettings_AddVideoFolder
                : Strings.Resources.LibrarySettings_AddMusicFolderPickerTitle,
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
        });

        var folder = folders.FirstOrDefault();
        if (folder is null)
        {
            return;
        }

        var localPath = folder.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            AddFolder(localPath, forVideo: forVideo);
            return;
        }

        // No local filesystem path (e.g. an Android Storage Access Framework tree). Delegate to
        // the platform hook to secure durable access and obtain a persistable identifier (a
        // content-tree URI) to store as the folder path. Desktop has no hook and falls back to
        // the "local folders only" message.
        var access = PlatformServices.LibraryFolderAccess;
        if (access is null)
        {
            StatusMessage = Strings.Resources.LibrarySettings_OnlyLocalFolders;
            return;
        }

        var reference = await access.PersistFolderAccessAsync(folder);
        if (reference is null)
        {
            StatusMessage = Strings.Resources.LibrarySettings_OnlyLocalFolders;
            return;
        }

        AddFolder(reference.Identifier, reference.DisplayName, forVideo: forVideo);
    }

    /// <summary>
    /// Seeds the user's Music and Videos folders when the library has no folders yet (first run),
    /// so most users start with sensible content while still being free to edit it.
    /// </summary>
    public void EnsureDefaultFolders()
    {
        if (LibraryFolders.Count == 0)
        {
            foreach (var path in GetDefaultFolderPaths(forVideo: false))
            {
                AddFolder(path, requireExisting: false);
            }
        }

        if (VideoFolders.Count == 0)
        {
            foreach (var path in GetDefaultFolderPaths(forVideo: true))
            {
                AddFolder(path, requireExisting: false, forVideo: true);
            }
        }
    }

    /// <summary>
    /// Adds any default folder the library is currently missing, after listing them for the user
    /// to confirm. Folders the user added themselves are left untouched.
    /// </summary>
    private async Task AddDefaultFoldersAsync(bool forVideo)
    {
        if (DialogService is null)
        {
            return;
        }

        var target = forVideo ? VideoFolders : LibraryFolders;
        var defaults = GetDefaultFolderPaths(forVideo);
        var missing = new List<string>();
        foreach (var path in defaults)
        {
            if (!TryNormalizeFolderIdentifier(path, requireExisting: false, out var normalizedPath))
            {
                continue;
            }

            var covered = target.Any(folder => IsSameOrSubPath(normalizedPath, folder.Path)) ||
                missing.Any(pending => IsSameOrSubPath(normalizedPath, pending));
            if (!covered)
            {
                missing.Add(normalizedPath);
            }
        }

        if (missing.Count == 0)
        {
            StatusMessage = defaults.Count == 0
                ? (forVideo
                    ? Strings.Resources.LibrarySettings_NoDefaultVideoFolders
                    : Strings.Resources.LibrarySettings_NoDefaultFolders)
                : (forVideo
                    ? Strings.Resources.LibrarySettings_DefaultVideoFoldersAlreadyAdded
                    : Strings.Resources.LibrarySettings_DefaultFoldersAlreadyAdded);
            return;
        }

        var confirmed = await DialogService.ConfirmAsync(
            Strings.Resources.LibrarySettings_AddDefaultFoldersConfirmTitle,
            string.Format(
                CultureInfo.CurrentCulture,
                forVideo
                    ? Strings.Resources.LibrarySettings_AddDefaultVideoFoldersConfirmMessageFormat
                    : Strings.Resources.LibrarySettings_AddDefaultFoldersConfirmMessageFormat,
                string.Join(Environment.NewLine, missing)),
            Strings.Resources.Common_Add);
        if (!confirmed)
        {
            return;
        }

        foreach (var path in missing)
        {
            AddFolder(path, requireExisting: false, forVideo: forVideo);
        }
    }

    /// <summary>The folders this platform seeds the library with, or an empty list when it has none.</summary>
    private static IReadOnlyList<string> GetDefaultFolderPaths(bool forVideo)
    {
        var access = PlatformServices.LibraryFolderAccess;
        var platformDefaults = forVideo
            ? access?.GetDefaultVideoFolderPaths()
            : access?.GetDefaultMusicFolderPaths();
        if (platformDefaults is { Count: > 0 })
        {
            return platformDefaults;
        }

        var specialFolders = forVideo
            ? new[] { Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos }
            : [Environment.SpecialFolder.MyMusic];
        var pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return specialFolders
            .Select(Environment.GetFolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Distinct(pathComparer)
            .ToArray();
    }

    private void AddFolder(
        string path,
        string? displayName = null,
        bool requireExisting = true,
        bool forVideo = false)
    {
        if (!TryNormalizeFolderIdentifier(path, requireExisting, out var normalizedPath))
        {
            StatusMessage = Strings.Resources.LibrarySettings_ChooseExistingFolder;
            return;
        }

        var target = forVideo ? VideoFolders : LibraryFolders;
        var existingPaths = target.Select(folder => folder.Path).ToArray();
        if (existingPaths.Any(existing => IsSameOrSubPath(normalizedPath, existing)))
        {
            StatusMessage = Strings.Resources.LibrarySettings_FolderAlreadyCovered;
            return;
        }

        if (existingPaths.Any(existing => IsSameOrSubPath(existing, normalizedPath)))
        {
            StatusMessage = Strings.Resources.LibrarySettings_RemoveNestedFirst;
            return;
        }

        target.Add(CreateFolderModel(normalizedPath, displayName));
        PersistFolders(forVideo);
        StatusMessage = string.Empty;
        OnPropertyChanged(forVideo ? nameof(HasVideoFolders) : nameof(HasLibraryFolders));
        ScanLibraryCommand.NotifyCanExecuteChanged();
    }

    private void RemoveFolder(LibraryFolderModel? folder)
    {
        if (folder is not null && LibraryFolders.Remove(folder))
        {
            ReleaseFolderAccess(folder);
            PersistFolders(forVideo: false);
            StatusMessage = string.Empty;
            OnPropertyChanged(nameof(HasLibraryFolders));
            ScanLibraryCommand.NotifyCanExecuteChanged();
        }
    }

    private void RemoveVideoFolder(LibraryFolderModel? folder)
    {
        if (folder is not null && VideoFolders.Remove(folder))
        {
            ReleaseFolderAccess(folder);
            PersistFolders(forVideo: true);
            StatusMessage = string.Empty;
            OnPropertyChanged(nameof(HasVideoFolders));
            ScanLibraryCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Asks the platform to grant this folder again. Runs straight off the button so browsers still
    /// count it as a user gesture, which is the only time they answer the request.
    /// </summary>
    private async Task GrantFolderAccessAsync(LibraryFolderModel? folder)
    {
        if (folder is null || PlatformServices.LibraryFolderAccess is not { } access)
        {
            return;
        }

        if (await access.RequestAccessAsync(folder.Path))
        {
            folder.NeedsAccess = false;
            StatusMessage = string.Empty;
            return;
        }

        StatusMessage = Strings.Resources.LibrarySettings_FolderAccessRefused;
    }

    /// <summary>Drops a durable grant the platform is still holding for a folder just removed.</summary>
    private static void ReleaseFolderAccess(LibraryFolderModel folder)
    {
        if (PlatformServices.LibraryFolderAccess is { } access)
        {
            _ = access.ReleaseAsync(folder.Path);
        }
    }

    private void PersistFolders(bool forVideo)
    {
        if (forVideo)
        {
            settingsStore.SetVideoFolderPaths(VideoFolders.Select(folder => folder.Path));
        }
        else
        {
            settingsStore.SetLibraryFolderPaths(LibraryFolders.Select(folder => folder.Path));
        }
    }

    private static LibraryFolderModel CreateFolderModel(string path, string? displayName = null)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return new LibraryFolderModel(displayName, path);
        }

        if (IsUriIdentifier(path))
        {
            var access = PlatformServices.LibraryFolderAccess;
            return new LibraryFolderModel(
                access?.GetDisplayName(path) ?? DeriveUriFolderName(path),
                path,
                access?.RequiresAccessGrant(path) ?? false);
        }

        var name = new DirectoryInfo(path).Name;
        return new LibraryFolderModel(string.IsNullOrWhiteSpace(name) ? path : name, path);
    }

    /// <summary>True when the identifier is a URI (e.g. an Android SAF content tree) rather than a local path.</summary>
    private static bool IsUriIdentifier(string path) => path.Contains("://", StringComparison.Ordinal);

    /// <summary>Best-effort human-readable name from the last segment of a content URI.</summary>
    private static string DeriveUriFolderName(string uri)
    {
        var decoded = Uri.UnescapeDataString(uri);
        var separators = new[] { '/', ':', '\\' };
        var lastSep = decoded.TrimEnd(separators).LastIndexOfAny(separators);
        var tail = lastSep >= 0 && lastSep < decoded.Length - 1 ? decoded[(lastSep + 1)..] : decoded;
        tail = tail.Trim().Trim(separators);
        return string.IsNullOrWhiteSpace(tail) ? uri : tail;
    }

    /// <summary>
    /// Normalizes a folder identifier for storage. Local paths are resolved and, unless
    /// <paramref name="requireExisting"/> is false, required to exist; URI identifiers (Android SAF
    /// trees) are trusted as-is since their existence is verified at scan time by the platform I/O layer.
    /// </summary>
    private static bool TryNormalizeFolderIdentifier(string path, bool requireExisting, out string normalizedPath)
    {
        if (!string.IsNullOrWhiteSpace(path) && IsUriIdentifier(path))
        {
            normalizedPath = path.Trim();
            return true;
        }

        return TryNormalizeDirectoryPath(path, requireExisting, out normalizedPath);
    }

    private static bool TryNormalizeDirectoryPath(string path, bool requireExisting, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            if (requireExisting && !Directory.Exists(fullPath))
            {
                return false;
            }

            normalizedPath = Path.TrimEndingDirectorySeparator(fullPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSameOrSubPath(string path, string basePath)
    {
        if (!TryNormalizeComparablePath(path, out var normalizedPath) ||
            !TryNormalizeComparablePath(basePath, out var normalizedBasePath))
        {
            return false;
        }

        var comparer = GetPathComparison();
        if (string.Equals(normalizedPath, normalizedBasePath, comparer))
        {
            return true;
        }

        // URI trees separate segments with '/'; local paths use the platform separator.
        var separator = IsUriIdentifier(normalizedBasePath) ? '/' : Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedBasePath + separator, comparer);
    }

    private static bool TryNormalizeComparablePath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (IsUriIdentifier(path))
        {
            normalizedPath = path.Trim();
            return true;
        }

        try
        {
            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}