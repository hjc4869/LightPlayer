using System;
using System.Collections.Generic;
using System.IO;

namespace LightStudio.LightPlayer.Services.Playback;

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

/// <summary>
/// A single entry in the playback queue. For a plain media file only
/// <see cref="FilePath"/> is set. For a CUE-sheet track several queue items share
/// the same <see cref="FilePath"/> but carve out a segment via <see cref="StartTime"/>
/// and <see cref="Duration"/>, and carry their own <see cref="Title"/>. The
/// (FilePath, StartTime) pair is what uniquely identifies a CUE track.
/// </summary>
public sealed record PlaybackQueueItem(string FilePath)
{
    /// <summary>Offset into <see cref="FilePath"/> at which this track starts. Zero for plain files.</summary>
    public TimeSpan StartTime { get; init; }

    /// <summary>
    /// Playable length of this track. <see cref="TimeSpan.Zero"/> means "play to the end of the
    /// file" (used for plain files and the final CUE track when its length is unknown).
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Track title carried from the CUE sheet; null for plain files (fall back to file name).</summary>
    public string? Title { get; init; }

    /// <summary>True when this entry is a CUE-sheet track that decodes only a segment of the file.</summary>
    public bool IsCueTrack { get; init; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(FilePath) : Title!;
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
    IReadOnlyList<PlaybackQueueItem> Items,
    int CurrentIndex,
    PlaybackMode Mode,
    float Volume,
    TimeSpan Position);

public sealed record QueuePlaybackOptions(
    TimeSpan? DurationPerTrack,
    float Volume,
    TimeSpan BufferTarget,
    int PreferredSampleRate = 0,
    bool AlwaysResample = false);

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
