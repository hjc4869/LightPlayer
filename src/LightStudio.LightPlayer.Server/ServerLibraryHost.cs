using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.IO;
using LightStudio.Inference;

namespace LightStudio.LightPlayer.Server;

/// <summary>
/// Brings the local library services up against a deployment's data volume. Shared by the running
/// server and the configuration TUI, which indexes through the same services.
/// </summary>
internal static class ServerLibraryHost
{
    private static LocalServices.LocalServices? services;

    /// <summary>
    /// Creates the services on the first call and hands back that same instance afterwards: the
    /// library database and the FFmpeg bindings are process-wide, so only one host can own them.
    /// </summary>
    public static LocalServices.LocalServices GetOrCreate(ServerConfiguration config)
    {
        if (services is not null)
        {
            return services;
        }

        Program.InitializeNativeDependencies();
        Directory.CreateDirectory(config.DataPath);
        Directory.CreateDirectory(config.CachePath);
        PlatformServices.AppDataPath = config.DataPath;
        PlatformServices.CachePath = config.CachePath;

        // The default platform IO derives its data folder from the user profile, which in a
        // container leaves the library database next to the binary; pin it to the data volume.
        var created = new LocalServices.LocalServices(
            static (content, name) => new MediaLibraryCore.Lyrics.JsDownloadSource(content, name),
            () => new LocalServices.DesktopMultimediaPlatformIO(AppPaths.DataDirectory),
#if WINDOWS
            inferenceProvider: new WindowsInferenceProvider());
#else
            inferenceProvider: new OnnxInferenceProvider());
#endif
        PlatformServices.LocalServicesFactory = () => created;
        services = created;
        return created;
    }

    /// <summary>
    /// Points the library at the configured folders and returns the music folders that were applied.
    /// An empty result leaves the stored folders alone, so a mount that is temporarily missing never
    /// empties the index.
    /// </summary>
    public static IReadOnlyList<string> ApplyFolders(LocalServices.LocalServices services, ServerConfiguration config)
    {
        var videoFolders = config.VideoPaths
            .Select(path => path.Trim())
            .Where(Directory.Exists)
            .ToArray();
        services.Settings.SetVideoFolderPaths(videoFolders);

        var folders = config.LibraryPaths
            .Select(path => path.Trim())
            .Where(Directory.Exists)
            .ToArray();
        if (folders.Length == 0)
        {
            return [];
        }

        services.Settings.SetLibraryFolderPaths(folders);
        return folders;
    }
}
