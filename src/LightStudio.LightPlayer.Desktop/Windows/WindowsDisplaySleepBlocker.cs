using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LightStudio.LightPlayer.Desktop.Windows;

/// <summary>
/// Keeps the display and the machine awake while video plays, the way Windows expects a media
/// player to: an execution-state request rather than a synthesized input event.
/// </summary>
/// <remarks>
/// The requested state belongs to the calling thread and lasts only as long as that thread does, so
/// every call must come from the same long-lived thread. The UI thread provides that lifetime and
/// player raises its playback changes.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class WindowsDisplaySleepBlocker
{
    private const uint ContinuousState = 0x80000000;
    private const uint SystemRequired = 0x00000001;
    private const uint DisplayRequired = 0x00000002;

    public static void SetKeepScreenOn(bool value) =>
        SetThreadExecutionState(value
            ? ContinuousState | DisplayRequired | SystemRequired
            : ContinuousState);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial uint SetThreadExecutionState(uint esFlags);
}
