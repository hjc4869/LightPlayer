using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

/// <summary>What a recently viewed entry points at.</summary>
public enum VideoHistoryEntryKind
{
    Folder,

    Video,

    Photo,
}

/// <summary>
/// One entry of the video library's recently viewed list. Only the reference is kept: names,
/// counts and previews are read from the library again when the list is shown, so an entry whose
/// folder, video or picture has left the library simply drops out of it.
/// </summary>
public sealed record VideoHistoryEntry
{
    public VideoHistoryEntryKind Kind { get; set; }

    public string SourceId { get; set; } = LibrarySourceId.Local.Value;

    /// <summary>Folder, video or picture path; null is the source's own root folder.</summary>
    public string? Path { get; set; }

    public DateTimeOffset ViewedUtc { get; set; }

    [JsonIgnore]
    public LibrarySourceId Source => LibrarySourceId.FromPersisted(SourceId);
}

/// <summary>The video library's recently viewed folders, videos and pictures, newest first.</summary>
public interface IVideoHistoryStore
{
    /// <summary>Empty while playback history is turned off.</summary>
    IReadOnlyList<VideoHistoryEntry> GetEntries();

    /// <summary>
    /// Records a browsed folder. Descending from the folder recorded last replaces it, so walking
    /// down a tree leaves the deepest folder reached rather than one entry per level.
    /// </summary>
    void RecordFolder(LibrarySourceId sourceId, string? path);

    /// <summary>Records a played video. Videos never take the place of the folder above them.</summary>
    void RecordVideo(LibrarySourceId sourceId, string path);

    /// <summary>Records an opened picture.</summary>
    void RecordPhoto(LibrarySourceId sourceId, string path);

    void Clear();
}

/// <summary>
/// The recently viewed bookkeeping, over whatever storage a head has for it. Gated behind the same
/// <see cref="IAppSettingsStore.EnablePlaybackHistory"/> setting as the music history, and capped
/// at the same <see cref="IPlaybackHistoryService.HistoryEntryLimit"/> -- counted here over folders,
/// videos and pictures together, and separately from the music list.
/// </summary>
public abstract class VideoHistoryStoreBase(IAppSettingsStore settings) : IVideoHistoryStore
{
    private const int SchemaVersion = 1;

    private readonly object gate = new();
    private List<VideoHistoryEntry>? entries;

    public IReadOnlyList<VideoHistoryEntry> GetEntries()
    {
        if (!settings.EnablePlaybackHistory)
        {
            return [];
        }

        lock (gate)
        {
            return Entries.ToArray();
        }
    }

    public void RecordFolder(LibrarySourceId sourceId, string? path) =>
        Record(new VideoHistoryEntry
        {
            Kind = VideoHistoryEntryKind.Folder,
            SourceId = sourceId.Value,
            Path = path,
        });

    public void RecordVideo(LibrarySourceId sourceId, string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            Record(new VideoHistoryEntry
            {
                Kind = VideoHistoryEntryKind.Video,
                SourceId = sourceId.Value,
                Path = path,
            });
        }
    }

    public void RecordPhoto(LibrarySourceId sourceId, string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            Record(new VideoHistoryEntry
            {
                Kind = VideoHistoryEntryKind.Photo,
                SourceId = sourceId.Value,
                Path = path,
            });
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            Entries.Clear();
            PersistLocked();
        }
    }

    /// <summary>Returns the stored payload, or null when there is none to read.</summary>
    protected abstract string? Read();

    protected abstract void Write(string json);

    private List<VideoHistoryEntry> Entries => entries ??= Load();

    private void Record(VideoHistoryEntry entry)
    {
        if (!settings.EnablePlaybackHistory)
        {
            return;
        }

        entry.ViewedUtc = DateTimeOffset.UtcNow;
        lock (gate)
        {
            var list = Entries;
            list.RemoveAll(value => IsSame(value, entry));
            if (list.Count > 0 && IsDescendantFolder(entry, list[0]))
            {
                list.RemoveAt(0);
            }

            list.Insert(0, entry);
            if (list.Count > IPlaybackHistoryService.HistoryEntryLimit)
            {
                list.RemoveRange(
                    IPlaybackHistoryService.HistoryEntryLimit,
                    list.Count - IPlaybackHistoryService.HistoryEntryLimit);
            }

            PersistLocked();
        }
    }

    private static bool IsSame(VideoHistoryEntry left, VideoHistoryEntry right) =>
        left.Kind == right.Kind &&
        string.Equals(left.SourceId, right.SourceId, StringComparison.Ordinal) &&
        string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="entry"/> is a folder somewhere below <paramref name="last"/>.</summary>
    private static bool IsDescendantFolder(VideoHistoryEntry entry, VideoHistoryEntry last)
    {
        if (entry.Kind != VideoHistoryEntryKind.Folder || last.Kind != VideoHistoryEntryKind.Folder ||
            !string.Equals(entry.SourceId, last.SourceId, StringComparison.Ordinal) ||
            entry.Path is not { Length: > 0 } path)
        {
            return false;
        }

        // A null path is the source's own root, which every folder of that source sits under.
        return last.Path is not { Length: > 0 } ancestor || VideoFolderTree.IsUnder(path, ancestor);
    }

    private List<VideoHistoryEntry> Load()
    {
        try
        {
            if (Read() is not { Length: > 0 } json)
            {
                return [];
            }

            var persisted = JsonSerializer.Deserialize(json, VideoHistoryJsonContext.Default.PersistedVideoHistory);
            return persisted?.SchemaVersion == SchemaVersion
                ? persisted.Entries
                    .OrderByDescending(entry => entry.ViewedUtc)
                    .Take(IPlaybackHistoryService.HistoryEntryLimit)
                    .ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void PersistLocked() => Write(JsonSerializer.Serialize(
        new PersistedVideoHistory { SchemaVersion = SchemaVersion, Entries = Entries },
        VideoHistoryJsonContext.Default.PersistedVideoHistory));
}

/// <summary>
/// <see cref="IVideoHistoryStore"/> over a single JSON file next to the other device state. Reads
/// and writes are best-effort: a file that cannot be reached only costs the recently viewed list.
/// </summary>
public sealed class JsonVideoHistoryStore(IAppSettingsStore settings, string? filePath = null)
    : VideoHistoryStoreBase(settings)
{
    private readonly string filePath = filePath ?? Path.Combine(AppPaths.DataDirectory, "video-history.json");

    protected override string? Read()
    {
        try
        {
            return File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    protected override void Write(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class PersistedVideoHistory
{
    public int SchemaVersion { get; set; }

    public List<VideoHistoryEntry> Entries { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PersistedVideoHistory))]
internal sealed partial class VideoHistoryJsonContext : JsonSerializerContext;
