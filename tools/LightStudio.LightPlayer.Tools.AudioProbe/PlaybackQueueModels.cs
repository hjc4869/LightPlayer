namespace LightStudio.LightPlayer.Tools.AudioProbe;

public enum PlaybackMode
{
    Sequential = 0,
    ListLoop = 1,
    SingleTrackLoop = 2,
    Random = 3
}

public enum PlaybackQueuePlaybackState
{
    Stopped,
    Playing,
    Paused
}

public enum PlaybackQueueTrackEndReason
{
    Ended,
    Skipped,
    Stopped
}

public sealed record PlaybackQueueItem(string FilePath)
{
    public string DisplayName => Path.GetFileNameWithoutExtension(FilePath);
}

public sealed record PlaybackQueueStatus(
    PlaybackQueueItem? CurrentItem,
    int CurrentIndex,
    int Count,
    PlaybackMode Mode,
    PlaybackQueuePlaybackState State,
    TimeSpan SourcePosition,
    TimeSpan PlayedDuration,
    TimeSpan BufferedDuration,
    float Volume);

public sealed record PlaybackQueueStatusChangedEventArgs(string Message, PlaybackQueueStatus Status);

public sealed record PlaybackQueueStateChangedEventArgs(
    PlaybackQueuePlaybackState State,
    string Message,
    PlaybackQueueStatus Status);

public sealed record PlaybackQueueTrackStartedEventArgs(
    PlaybackQueueItem Item,
    int Index,
    int Count,
    string Title,
    TimeSpan Duration,
    PlaybackMode Mode,
    float Volume);

public sealed record PlaybackQueueTrackEndedEventArgs(
    PlaybackQueueItem Item,
    int Index,
    int Count,
    PlaybackQueueTrackEndReason Reason,
    long DecodedBytes,
    int FrameCount,
    TimeSpan DecodedDuration,
    TimeSpan PlayedDuration,
    TimeSpan Elapsed,
    int UnderrunCount);

public sealed record PlaybackQueueErrorEventArgs(string FilePath, string Message);

public sealed record PlaybackQueueSnapshot(
    IReadOnlyList<string> FilePaths,
    int CurrentIndex,
    PlaybackMode Mode,
    float Volume,
    TimeSpan Position);

public sealed record QueuePlaybackOptions(
    TimeSpan? DurationPerTrack,
    float Volume,
    TimeSpan BufferTarget);

public sealed class QueuePlaybackSummary
{
    public int TracksStarted { get; set; }

    public int TracksCompleted { get; set; }

    public int TracksSkipped { get; set; }

    public int Failures { get; set; }

    public long DecodedBytes { get; set; }

    public int FrameCount { get; set; }

    public TimeSpan DecodedDuration { get; set; }

    public TimeSpan PlayedDuration { get; set; }

    public TimeSpan Elapsed { get; set; }

    public int UnderrunCount { get; set; }
}