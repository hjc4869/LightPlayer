using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Subtitles;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

/// <summary>How the external subtitle behind a saved choice is reached when the video opens again.</summary>
public enum SubtitleSourceKind
{
    /// <summary>One of the video's own streams, or a sidecar the library index already knows.</summary>
    Indexed,

    /// <summary>A copy this device made when the file was picked off its own storage.</summary>
    Cached,

    /// <summary>A file in the server's video library, fetched again each time the video is opened.</summary>
    Remote,
}

/// <summary>Display progress for a video, and which of its tracks the user picked.</summary>
public sealed class VideoPlaybackState
{
    /// <summary>
    /// Last playback position shown on the library tile. A completed video stores its duration
    /// here, but is filtered out before the player offers to resume.
    /// </summary>
    public double PositionSeconds { get; set; }

    public double DurationSeconds { get; set; }

    /// <summary>Stream index of the chosen built-in sound track, or null when the user never picked one.</summary>
    public int? AudioStreamIndex { get; set; }

    /// <summary>
    /// Stream index of the chosen subtitle inside <see cref="SubtitleSource"/> (or inside the video
    /// itself when that is null), -1 for a script file, and null when the user never picked one.
    /// </summary>
    public int? SubtitleStreamIndex { get; set; }

    /// <summary>The file holding the chosen subtitle, or null for one of the video's own streams.</summary>
    public string? SubtitleSource { get; set; }

    public SubtitleSourceKind SubtitleSourceKind { get; set; }

    /// <summary>Set when the user turned subtitles off, which is not the same as having no preference.</summary>
    public bool SubtitlesOff { get; set; }

    /// <summary>How subtitle luminance is placed on an HDR surface, or null for the default.</summary>
    public SubtitleBrightnessMode? SubtitleBrightnessMode { get; set; }

    /// <summary>
    /// Null follows automatic package priority, <c>$off</c> disables pre-scaling, otherwise an
    /// installed shader package id is forced for this video.
    /// </summary>
    public string? ShaderPackageOverride { get; set; }

    public RemoteTranscodeOverride? TranscodeOverride { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>Clears resume metadata without changing the user's audio or subtitle choices.</summary>
    public void ClearProgress()
    {
        PositionSeconds = 0;
        DurationSeconds = 0;
    }
}

/// <summary>Rules for recording tile progress and deciding whether it is resumable.</summary>
public static class VideoPlaybackProgressPolicy
{
    private const double ResumeMinimumSeconds = 15;
    private const double ResumeTailSeconds = 15;

    public static void Record(
        VideoPlaybackState state,
        double positionSeconds,
        double durationSeconds,
        bool reachedEnd)
    {
        var wasComplete = IsComplete(state);
        var hasPosition = double.IsFinite(positionSeconds) && positionSeconds >= ResumeMinimumSeconds;
        var hasDuration = double.IsFinite(durationSeconds) && durationSeconds > 0;

        // Merely opening a completed video must not erase its watched marker. Once the rewatch gets
        // meaningfully underway, the new partial position below replaces it normally.
        if (!reachedEnd && !hasPosition && wasComplete)
        {
            return;
        }

        state.DurationSeconds = hasDuration ? durationSeconds : 0;
        if (reachedEnd && hasDuration)
        {
            state.PositionSeconds = durationSeconds;
        }
        else if (hasPosition)
        {
            state.PositionSeconds = hasDuration
                ? Math.Min(positionSeconds, durationSeconds)
                : positionSeconds;
        }
        else
        {
            state.PositionSeconds = 0;
        }
    }

    public static bool ShouldOfferResume(VideoPlaybackState? state, double currentDurationSeconds)
    {
        if (state is null || !double.IsFinite(state.PositionSeconds) ||
            state.PositionSeconds < ResumeMinimumSeconds || IsComplete(state))
        {
            return false;
        }

        return !double.IsFinite(currentDurationSeconds) || currentDurationSeconds <= 0 ||
            state.PositionSeconds < currentDurationSeconds - ResumeTailSeconds;
    }

    /// <summary>
    /// Records a watched marker without the video having been played. Returns false when no duration
    /// is known, which leaves nothing for <see cref="IsComplete"/> to measure the position against.
    /// </summary>
    public static bool MarkWatched(VideoPlaybackState state, double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            return false;
        }

        state.DurationSeconds = durationSeconds;
        state.PositionSeconds = durationSeconds;
        return true;
    }

    /// <summary>True once the position is close enough to the end to count the video as watched.</summary>
    public static bool IsComplete(VideoPlaybackState? state) =>
        state is not null &&
        double.IsFinite(state.PositionSeconds) && double.IsFinite(state.DurationSeconds) &&
        state.DurationSeconds > 0 && state.PositionSeconds >= state.DurationSeconds - ResumeTailSeconds;
}

/// <summary>One video the user left part-way through, and the progress recorded for it.</summary>
public sealed record VideoPlaybackProgress(MediaReference Video, VideoPlaybackState State);

/// <summary>
/// How a video is named inside a playback state store. Local videos are keyed by their path and
/// everything else by its source-qualified URI, which is what lets a stored key be read back as the
/// video it stands for.
/// </summary>
public static class VideoPlaybackStateKeys
{
    public static string Format(MediaReference video) =>
        video.SourceId == LibrarySourceId.Local ? video.Path : MediaReferenceUri.Format(video);

    public static MediaReference Parse(string key) =>
        MediaReferenceUri.TryParse(key, out var item) ? item.Media : MediaReference.Local(key);

    /// <summary>
    /// The videos left part-way through, most recently played first. Watched-through and
    /// too-early-to-matter positions are dropped: they have nothing to continue.
    /// </summary>
    public static IReadOnlyList<VideoPlaybackProgress> SelectInProgress(
        IReadOnlyDictionary<string, VideoPlaybackState> states,
        int limit) =>
        limit <= 0
            ? []
            : states
                .Where(entry => VideoPlaybackProgressPolicy.ShouldOfferResume(
                    entry.Value,
                    entry.Value.DurationSeconds))
                .OrderByDescending(entry => entry.Value.UpdatedUtc)
                .Take(limit)
                .Select(entry => new VideoPlaybackProgress(Parse(entry.Key), entry.Value))
                .ToArray();
}

/// <summary>Per-video display progress and track preferences, keyed by the video's path.</summary>
public interface IVideoPlaybackStateStore
{
    VideoPlaybackState? Get(MediaReference video);

    void Save(MediaReference video, VideoPlaybackState state);

    /// <summary>
    /// Applies several updates behind a single persist. Both stores rewrite their whole payload per
    /// save, so marking a folder watched one video at a time would be quadratic.
    /// </summary>
    void SaveAll(IReadOnlyList<(MediaReference Video, VideoPlaybackState State)> updates);

    /// <summary>
    /// At most <paramref name="limit"/> videos the user left part-way through, most recently played
    /// first. Backs the home page's continue watching row.
    /// </summary>
    IReadOnlyList<VideoPlaybackProgress> GetInProgress(int limit);
}

/// <summary>
/// <see cref="IVideoPlaybackStateStore"/> over a single JSON file in the cache directory. Every
/// operation is best-effort: this is disposable data, so a file that cannot be read or written
/// only costs the resume prompt.
/// </summary>
public sealed class JsonVideoPlaybackStateStore : IVideoPlaybackStateStore
{
    /// <summary>Least recently used entries are dropped past this many videos.</summary>
    private const int MaxEntries = 500;

    private readonly string filePath;
    private readonly Dictionary<string, VideoPlaybackState> states;

    public JsonVideoPlaybackStateStore()
    {
        filePath = Path.Combine(AppPaths.CacheDirectory, "video-playback.json");
        states = Load(filePath);
    }

    public VideoPlaybackState? Get(MediaReference video) =>
        string.IsNullOrEmpty(video.Path) ? null : states.GetValueOrDefault(Key(video));

    public void Save(MediaReference video, VideoPlaybackState state)
    {
        if (string.IsNullOrEmpty(video.Path))
        {
            return;
        }

        state.UpdatedUtc = DateTimeOffset.UtcNow;
        states[Key(video)] = state;
        Trim();
        Write();
    }

    public void SaveAll(IReadOnlyList<(MediaReference Video, VideoPlaybackState State)> updates)
    {
        var changed = false;
        foreach (var (video, state) in updates)
        {
            if (string.IsNullOrEmpty(video.Path))
            {
                continue;
            }

            state.UpdatedUtc = DateTimeOffset.UtcNow;
            states[Key(video)] = state;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        Trim();
        Write();
    }

    public IReadOnlyList<VideoPlaybackProgress> GetInProgress(int limit) =>
        VideoPlaybackStateKeys.SelectInProgress(states, limit);

    private void Trim()
    {
        if (states.Count <= MaxEntries)
        {
            return;
        }

        foreach (var key in states
                     .OrderBy(entry => entry.Value.UpdatedUtc)
                     .Take(states.Count - MaxEntries)
                     .Select(entry => entry.Key)
                     .ToList())
        {
            states.Remove(key);
        }
    }

    private static Dictionary<string, VideoPlaybackState> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize(json, VideoPlaybackJsonContext.Default.PersistedVideoPlaybackStates);
                if (loaded?.Videos is { } videos)
                {
                    return new Dictionary<string, VideoPlaybackState>(videos, GetPathComparer());
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to an empty cache when the file is missing or unreadable.
        }

        return new Dictionary<string, VideoPlaybackState>(GetPathComparer());
    }

    private void Write()
    {
        try
        {
            var payload = new PersistedVideoPlaybackStates
            {
                Videos = new Dictionary<string, VideoPlaybackState>(states, StringComparer.Ordinal),
            };
            var json = JsonSerializer.Serialize(payload, VideoPlaybackJsonContext.Default.PersistedVideoPlaybackStates);
            File.WriteAllText(filePath, json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort persistence; a failed write only loses the resume point.
        }
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static string Key(MediaReference video) =>
        VideoPlaybackStateKeys.Format(video);
}

public sealed class PersistedVideoPlaybackStates
{
    public Dictionary<string, VideoPlaybackState> Videos { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PersistedVideoPlaybackStates))]
internal sealed partial class VideoPlaybackJsonContext : JsonSerializerContext;
