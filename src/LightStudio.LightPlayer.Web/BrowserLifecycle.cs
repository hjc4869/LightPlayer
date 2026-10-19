using System.Text.Json;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.RemoteServices;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal sealed class BrowserLifecycle : IPlaybackStateObserver
{
    public Task UpdateSnapshotAsync(PlaybackQueuePersistedState? snapshot, bool isPlaying)
    {
        var json = snapshot is null
            ? null
            : JsonSerializer.Serialize(snapshot, RemoteStateJsonContext.Default.PlaybackQueuePersistedState);
        return BrowserLifecycleInterop.UpdateSnapshotAsync(json, isPlaying);
    }

    public Task UpdateProgressAsync(bool isPlaying, double positionMs, double durationMs) =>
        BrowserLifecycleInterop.UpdateProgressAsync(isPlaying, positionMs, durationMs);
}

[SupportedOSPlatform("browser")]
internal static partial class BrowserLifecycleInterop
{
    [JSImport("reloadAfterIndexDatabaseReset", "BrowserLifecycle")]
    internal static partial void ReloadAfterIndexDatabaseReset();

    [JSImport("updateSnapshot", "BrowserLifecycle")]
    internal static partial Task UpdateSnapshotAsync(string? snapshotJson, bool isPlaying);

    [JSImport("updateProgress", "BrowserLifecycle")]
    internal static partial Task UpdateProgressAsync(bool isPlaying, double positionMs, double durationMs);
}