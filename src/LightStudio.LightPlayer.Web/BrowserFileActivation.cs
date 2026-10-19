using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// Delivers files from a PWA file-handler launch or share to the shell, like an "Open with"
/// invocation on an installed head.
/// </summary>
/// <remarks>
/// The files themselves are claimed by <c>file-handlers.js</c> as soon as the page loads, long
/// before the runtime exists, so a cold-start launch is not lost while the app downloads. Arrivals
/// are awaited as promises rather than pushed through a marshalled callback, which multithreaded
/// WebAssembly refuses to invoke from a JS event handler.
/// </remarks>
[SupportedOSPlatform("browser")]
internal static partial class BrowserFileActivation
{
    /// <summary>Starts listening. Must run on the main thread, and after Avalonia is set up.</summary>
    public static async Task StartAsync()
    {
        await BrowserAssets.ImportAsync("FileHandlers", "file-handlers.js");
        _ = ListenAsync();
    }

    private static async Task ListenAsync()
    {
        while (true)
        {
            await WaitForFilesAsync();

            var paths = new List<string>();
            foreach (var line in TakeFiles().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split('\t');
                if (fields.Length == 2 && fields[0].Length > 0)
                {
                    paths.Add(BrowserFileSystem.AdoptFile(fields[0], fields[1]));
                }
            }

            if (paths.Count == 0)
            {
                continue;
            }

            AppLog.Information("Web.Activation", $"Opening {paths.Count} file(s) handed to the app.");
            (Application.Current as App)?.OpenFiles(paths);
        }
    }

    [JSImport("waitForFiles", "FileHandlers")]
    private static partial Task WaitForFilesAsync();

    /// <summary>Returns "rootId\tname" per line for every file claimed since the last call.</summary>
    [JSImport("takeFiles", "FileHandlers")]
    private static partial string TakeFiles();
}
