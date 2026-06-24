using System.Collections.Generic;

namespace LightStudio.LightPlayer.Services;

public interface ILibraryLocationService
{
    IReadOnlyList<string> GetLibraryFolderPaths();
}

public sealed class AppSettingsLibraryLocationService(IAppSettingsStore settingsStore) : ILibraryLocationService
{
    public IReadOnlyList<string> GetLibraryFolderPaths() => settingsStore.LibraryFolderPaths;
}