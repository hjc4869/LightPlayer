using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Receives playback state while the application is running so a platform can prepare for
/// lifecycle events where calling back into managed code is not possible.
/// </summary>
public interface IPlaybackStateObserver
{
    Task UpdateSnapshotAsync(PlaybackQueuePersistedState? snapshot, bool isPlaying);

    Task UpdateProgressAsync(bool isPlaying, double positionMs, double durationMs);
}