using System.Collections.Generic;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Serializable metadata for a single persisted now-playing queue item: the
/// source file path plus the display tags so the queue can be restored without
/// re-decoding every file (which is what made restored items show up as bare
/// file names).
/// </summary>
public sealed record PlaybackQueueItemState(
    string FilePath,
    string Title,
    string Artist,
    string Album,
    long DurationTicks,
    int? StartTimeMs = null);

/// <summary>
/// Serializable snapshot of the now-playing queue persisted across launches:
/// the ordered items (path + tags), the active index, the last playback
/// position, the current track duration, the playback mode, and the volume.
/// Restored (without auto-play) on startup. The duration is kept so the progress
/// bar can render the restored position before playback starts.
/// </summary>
public sealed record PlaybackQueuePersistedState(
    IReadOnlyList<PlaybackQueueItemState> Items,
    int CurrentIndex,
    double PositionMs,
    double DurationMs,
    int Mode,
    double Volume);
