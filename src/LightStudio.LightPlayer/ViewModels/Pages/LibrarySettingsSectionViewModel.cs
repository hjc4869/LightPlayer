using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.ViewModels.Pages;

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
    private bool scanLibraryOnStartup;

    public LibrarySettingsSectionViewModel(IAppSettingsStore settingsStore)
        : this(settingsStore, new AsyncRelayCommand(() => Task.CompletedTask))
    {
    }

    public LibrarySettingsSectionViewModel(IAppSettingsStore settingsStore, IAsyncRelayCommand scanLibraryCommand)
        : base("Library", "Choose the folders Light scans for music.")
    {
        this.settingsStore = settingsStore;

        LibraryFolders = new ObservableCollection<LibraryFolderModel>(
            settingsStore.LibraryFolderPaths.Select(CreateFolderModel));

        RemoveFolderCommand = new RelayCommand<LibraryFolderModel>(RemoveFolder);
        ScanLibraryCommand = scanLibraryCommand;
        enablePlaybackHistory = settingsStore.EnablePlaybackHistory;
        scanLibraryOnStartup = settingsStore.ScanLibraryOnStartup;
        isLoaded = true;
    }

    public ObservableCollection<LibraryFolderModel> LibraryFolders { get; }

    public IRelayCommand<LibraryFolderModel> RemoveFolderCommand { get; }

    public IAsyncRelayCommand ScanLibraryCommand { get; }

    /// <summary>Controls visibility of the manual "Scan now" button (hidden during first-run setup).</summary>
    public bool ShowScanButton { get; init; } = true;

    public bool HasLibraryFolders => LibraryFolders.Count > 0;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>
    /// Invoked when the user turns playback history off, so existing history is
    /// cleared. Null when no history service is available.
    /// </summary>
    public Func<Task>? ClearPlaybackHistoryAsync { get; init; }

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

    partial void OnScanLibraryOnStartupChanged(bool value)
    {
        if (!isLoaded)
        {
            return;
        }

        settingsStore.ScanLibraryOnStartup = value;
    }

    public async Task AddFolderAsync(IStorageProvider? storageProvider)
    {
        if (storageProvider is null || !storageProvider.CanPickFolder)
        {
            StatusMessage = "Folder picking is not available on this platform.";
            return;
        }

        var startLocation = await storageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Music);
        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add music folder",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
        });

        var folder = folders.FirstOrDefault();
        if (folder is null)
        {
            return;
        }

        var localPath = folder.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            StatusMessage = "Only local folders can be added to the desktop library.";
            return;
        }

        AddFolder(localPath);
    }

    /// <summary>
    /// Seeds the user's Music folder when the library has no folders yet (first run),
    /// so most users start with sensible content while still being free to edit it.
    /// </summary>
    public void EnsureDefaultMusicFolder()
    {
        if (LibraryFolders.Count > 0)
        {
            return;
        }

        var musicPath = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (!string.IsNullOrWhiteSpace(musicPath) && Directory.Exists(musicPath))
        {
            AddFolder(musicPath);
        }
    }

    private void AddFolder(string path)
    {
        if (!TryNormalizeDirectoryPath(path, out var normalizedPath))
        {
            StatusMessage = "Choose an existing local folder.";
            return;
        }

        var existingPaths = LibraryFolders.Select(folder => folder.Path).ToArray();
        if (existingPaths.Any(existing => IsSameOrSubPath(normalizedPath, existing)))
        {
            StatusMessage = "That folder is already covered by the library.";
            return;
        }

        if (existingPaths.Any(existing => IsSameOrSubPath(existing, normalizedPath)))
        {
            StatusMessage = "Remove nested library folders before adding their parent folder.";
            return;
        }

        LibraryFolders.Add(CreateFolderModel(normalizedPath));
        PersistFolders();
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(HasLibraryFolders));
        ScanLibraryCommand.NotifyCanExecuteChanged();
    }

    private void RemoveFolder(LibraryFolderModel? folder)
    {
        if (folder is null)
        {
            return;
        }

        if (LibraryFolders.Remove(folder))
        {
            PersistFolders();
            StatusMessage = string.Empty;
            OnPropertyChanged(nameof(HasLibraryFolders));
            ScanLibraryCommand.NotifyCanExecuteChanged();
        }
    }

    private void PersistFolders() =>
        settingsStore.SetLibraryFolderPaths(LibraryFolders.Select(folder => folder.Path));

    private static LibraryFolderModel CreateFolderModel(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return new LibraryFolderModel(string.IsNullOrWhiteSpace(name) ? path : name, path);
    }

    private static bool TryNormalizeDirectoryPath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            if (!Directory.Exists(fullPath))
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
        return string.Equals(normalizedPath, normalizedBasePath, comparer) ||
            normalizedPath.StartsWith(normalizedBasePath + Path.DirectorySeparatorChar, comparer);
    }

    private static bool TryNormalizeComparablePath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
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