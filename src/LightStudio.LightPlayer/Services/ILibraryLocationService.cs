using System.Collections.Generic;

namespace LightStudio.LightPlayer.Services;

public interface ILibraryLocationService
{
    /// <summary>Folders scanned for music. Never scanned for video.</summary>
    IReadOnlyList<string> GetLibraryFolderPaths();

    /// <summary>Folders scanned for video, configured separately from the music folders.</summary>
    IReadOnlyList<string> GetVideoFolderPaths();
}