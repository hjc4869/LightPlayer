using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// Drives the browser's own full screen from the shell. The host has no window to resize, so the
/// document element is handed to the Fullscreen API instead; the browser can also leave full screen
/// by itself (the Esc key), which is reported back so the player's own state follows.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class BrowserFullScreen
{
    public static void Register()
    {
        PlatformServices.FullScreenRequested = value => _ = SetFullScreenAsync(value);
        _ = ListenAsync();
    }

    /// <summary>
    /// Reports every move in and out of full screen. Awaited as a promise rather than pushed through
    /// a marshalled callback, which multithreaded WebAssembly refuses to invoke from a JS event
    /// handler. The awaits are not configured away from the JS synchronization context, so the loop
    /// stays on the thread that owns the module.
    /// </summary>
    private static async Task ListenAsync()
    {
        while (true)
        {
            await WaitForChangeAsync();
            PlatformServices.FullScreenChanged?.Invoke(await IsFullScreenAsync());
        }
    }

    [JSImport("setFullScreen", "BrowserFullScreen")]
    private static partial Task SetFullScreenAsync(bool value);

    [JSImport("waitForChange", "BrowserFullScreen")]
    private static partial Task WaitForChangeAsync();

    [JSImport("isFullScreen", "BrowserFullScreen")]
    private static partial Task<bool> IsFullScreenAsync();
}
