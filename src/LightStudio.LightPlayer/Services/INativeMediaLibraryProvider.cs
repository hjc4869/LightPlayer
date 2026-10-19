using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.Services;

public sealed record NativeLibrarySettings
{
    public bool UseSystemMusicLibrary { get; init; } = true;

    public bool ScanMusicFolders { get; init; }

    public bool UseSystemMediaLibrary { get; init; } = true;

    public bool ScanMediaFolders { get; init; }

    public bool IsFileSystemEnabled(LibraryMediaCategory category) => category == LibraryMediaCategory.Music
        ? PlatformServices.NativeMediaLibrary?.SupportsMusic != true || ScanMusicFolders
        : PlatformServices.NativeMediaLibrary?.SupportsPhotosAndVideos != true || ScanMediaFolders;
}

public interface INativeMediaLibraryProvider
{
    bool SupportsMusic { get; }

    bool SupportsPhotosAndVideos { get; }

    ILibrarySource CreateSource(IAppSettingsStore settingsStore, IPhotoSearchService? photoSearch);

    Task<IReadOnlyList<DbImageFile>> GetPhotosAsync(CancellationToken cancellationToken = default);

    Task<bool> RequestAccessAsync(
        LibraryMediaCategory category,
        bool manageAccess = false,
        CancellationToken cancellationToken = default);

    event EventHandler? CatalogChanged;
}
