using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// Keeps the display lit while video plays, through the browser's Screen Wake Lock API. The lock
/// needs a secure context (HTTPS, or localhost) and is silently unavailable without one.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class BrowserWakeLock
{
    public static void Register() =>
        PlatformServices.KeepScreenOnRequested = value => _ = SetKeepScreenOnAsync(value);

    [JSImport("setKeepScreenOn", "BrowserWakeLock")]
    private static partial Task SetKeepScreenOnAsync(bool value);
}
