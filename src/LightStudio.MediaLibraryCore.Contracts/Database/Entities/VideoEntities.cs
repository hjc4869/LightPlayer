using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.MediaLibraryCore.Database.Entities;

/// <summary>
/// One indexed video source. Kept in its own table (and out of <see cref="DbMediaFile"/>) so the
/// music library keeps its audio-only shape: nothing here ever reaches an album, artist or queue.
/// </summary>
public class DbVideoFile
{
    [Key]
    public int Id { get; set; }

    /// <summary>Platform identifier of the video itself (local path, content URI, remote path).</summary>
    public string Path { get; set; }

    /// <summary>Whether <see cref="Path"/> identifies a regular file, DVD, or Blu-ray disc.</summary>
    public VideoMediaKind MediaKind { get; set; }

    /// <summary>One-based DVD title chosen during indexing; zero for other media.</summary>
    public int DiscTitle { get; set; }

    /// <summary>Identifier of the containing folder, used by the folder browsing view.</summary>
    public string FolderPath { get; set; }

    /// <summary>The configured video library folder this file was found under.</summary>
    public string LibraryRoot { get; set; }

    /// <summary>File or disc-root name; the display fallback when there is no title tag.</summary>
    public string FileName { get; set; }

    /// <summary>Container title tag, or null.</summary>
    public string Title { get; set; }

    public TimeSpan Duration { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public double FrameRate { get; set; }

    public string VideoCodec { get; set; }

    /// <summary>File size, or aggregate size of an extracted disc tree.</summary>
    public long FileSize { get; set; }

    /// <summary>Whole-file bits per second calculated from <see cref="FileSize"/> and <see cref="Duration"/>.</summary>
    public long BitRate { get; set; }

    /// <summary>Index of the video stream whose codec, geometry and frame rate are stored above.</summary>
    public int VideoStreamIndex { get; set; } = -1;

    /// <summary>
    /// Sidecar audio files discovered beside the video (<c>name.*.mka</c> and friends), newline
    /// separated. Resolved again at play time, so a stale entry only costs a failed open.
    /// </summary>
    public string ExternalAudioPaths { get; set; }

    /// <summary>Sidecar subtitle files discovered beside the video, newline separated.</summary>
    public string ExternalSubtitlePaths { get; set; }

    public DateTimeOffset DatabaseItemAddedDate { get; set; }

    public DateTimeOffset FileLastModifiedDate { get; set; }

    [NotMapped]
    public DateTimeOffset? FileCreatedDate { get; set; }

    /// <summary>
    /// Audio stream identities and codecs captured during scanning. A null source path belongs to
    /// this file; otherwise it names an external audio sidecar.
    /// </summary>
    [NotMapped]
    public DbVideoAudioStream[] AudioStreams { get; set; } = Array.Empty<DbVideoAudioStream>();

    [NotMapped]
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? FileName : Title;

    [NotMapped]
    [JsonIgnore]
    public IReadOnlyList<string> ExternalAudioList => SplitPaths(ExternalAudioPaths);

    [NotMapped]
    [JsonIgnore]
    public IReadOnlyList<string> ExternalSubtitleList => SplitPaths(ExternalSubtitlePaths);

    public static string JoinPaths(IEnumerable<string> paths)
    {
        if (paths is null)
            return null;
        var joined = string.Join('\n', paths);
        return string.IsNullOrEmpty(joined) ? null : joined;
    }

    private static IReadOnlyList<string> SplitPaths(string value)
        => string.IsNullOrEmpty(value)
            ? Array.Empty<string>()
            : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public override string ToString() => Path?.ToLowerInvariant() ?? string.Empty;
}

public class DbVideoAudioStream
{
    public string SourcePath { get; set; }

    public int StreamIndex { get; set; }

    public string CodecName { get; set; }
}
