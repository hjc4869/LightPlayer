using System;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// A single entry in a playlist. Carries the display metadata and the source
/// file path the playback engine decodes.
/// </summary>
public sealed class PlaylistItemModel
{
    public PlaylistItemModel(
        string title,
        string artist,
        string album,
        string filePath,
        TimeSpan duration,
        int? startTimeMs = null,
        LibrarySourceId? sourceId = null)
    {
        Title = title ?? string.Empty;
        Artist = artist ?? string.Empty;
        Album = album ?? string.Empty;
        FilePath = filePath ?? string.Empty;
        Duration = duration;
        StartTimeMs = startTimeMs;
        SourceId = sourceId ?? LibrarySourceId.Local;
    }

    public string Title { get; }

    public string Artist { get; }

    public string Album { get; }

    /// <summary>Absolute path of the media file backing this entry.</summary>
    public string FilePath { get; }

    public LibrarySourceId SourceId { get; }

    public MediaReference Media => new(SourceId, FilePath);

    public TimeSpan Duration { get; }

    /// <summary>Optional cue start offset, in milliseconds, for split-album sources.</summary>
    public int? StartTimeMs { get; }

    /// <summary>Builds a playlist entry from a database media file.</summary>
    public static PlaylistItemModel FromMediaFile(DbMediaFile file) =>
        new(
            file.Title ?? string.Empty,
            file.Artist ?? string.Empty,
            file.Album ?? string.Empty,
            file.Path ?? string.Empty,
            file.Duration,
            file.StartTime);

    /// <summary>Builds a playlist entry from a queue item.</summary>
    public static PlaylistItemModel FromPlaybackItem(MusicPlaybackItemModel item) =>
        new(item.Title, item.Artist, item.Album, item.FilePath, item.Duration, item.StartTimeMs, item.SourceId);

    /// <summary>
    /// Builds a video playlist entry. Videos carry no artist or album, so those stay empty and the
    /// entry keeps the same shape as a music one.
    /// </summary>
    public static PlaylistItemModel FromVideo(LibrarySourceId sourceId, DbVideoFile video) =>
        new(
            video.DisplayName ?? video.FileName ?? string.Empty,
            string.Empty,
            string.Empty,
            video.Path ?? string.Empty,
            video.Duration,
            startTimeMs: null,
            sourceId);

    /// <summary>Builds a queue item from this playlist entry.</summary>
    public MusicPlaybackItemModel ToPlaybackItem() =>
        new(Title, Artist, Album, Duration, FilePath, StartTimeMs, SourceId);

    public bool SameSource(PlaylistItemModel other)
    {
        if (other is null)
        {
            return false;
        }

        return other.SourceId == SourceId &&
            string.Equals(other.FilePath, FilePath, StringComparison.Ordinal) &&
            other.StartTimeMs == StartTimeMs;
    }
}
