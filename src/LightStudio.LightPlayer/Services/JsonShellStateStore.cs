using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// File-backed shell state stored as JSON under the per-user application data
/// directory. Writes are best-effort: IO or serialization failures are swallowed
/// so layout persistence never blocks the UI.
/// </summary>
public sealed class JsonShellStateStore : IShellStateStore
{
    private readonly string filePath;
    private PersistedShellState state;
    private bool loaded;

    public JsonShellStateStore()
        : this(DefaultFilePath())
    {
    }

    public JsonShellStateStore(string filePath)
    {
        this.filePath = filePath;
        state = Load(filePath);
        loaded = true;
    }

    public bool IsQueuePaneOpen
    {
        get => state.IsQueuePaneOpen;
        set
        {
            if (state.IsQueuePaneOpen == value)
            {
                return;
            }

            state.IsQueuePaneOpen = value;
            Save();
        }
    }

    public bool IsNavigationExpanded
    {
        get => state.IsNavigationExpanded;
        set
        {
            if (state.IsNavigationExpanded == value)
            {
                return;
            }

            state.IsNavigationExpanded = value;
            Save();
        }
    }

    public PlaybackQueuePersistedState? PlaybackQueue
    {
        get
        {
            var saved = state.PlaybackQueue;
            if (saved is null)
            {
                return null;
            }

            var items = saved.Items.Select(item => new PlaybackQueueItemState(
                        item.FilePath,
                        item.Title,
                        item.Artist,
                        item.Album,
                        item.DurationTicks,
                        item.StartTimeMs))
                    .ToArray();
            if (items.Length == 0)
            {
                return null;
            }

            return new PlaybackQueuePersistedState(
                items,
                saved.CurrentIndex,
                saved.PositionMs,
                saved.DurationMs,
                saved.Mode,
                saved.Volume);
        }
        set
        {
            state.PlaybackQueue = value is null
                ? null
                : new PersistedPlaybackQueue
                {
                    Items = value.Items
                        .Select(item => new PersistedQueueItem
                        {
                            FilePath = item.FilePath,
                            Title = item.Title,
                            Artist = item.Artist,
                            Album = item.Album,
                            DurationTicks = item.DurationTicks,
                            StartTimeMs = item.StartTimeMs,
                        })
                        .ToList(),
                    CurrentIndex = value.CurrentIndex,
                    PositionMs = value.PositionMs,
                    DurationMs = value.DurationMs,
                    Mode = value.Mode,
                    Volume = value.Volume,
                };
            Save();
        }
    }

    private static string DefaultFilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(root, "LightStudio.LightPlayer", "shell-state.json");
    }

    private static PersistedShellState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize(json, ShellStateJsonContext.Default.PersistedShellState);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to defaults when the file is missing or unreadable.
        }

        return new PersistedShellState();
    }

    private void Save()
    {
        if (!loaded)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(state, ShellStateJsonContext.Default.PersistedShellState);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort persistence; ignore write failures.
        }
    }
}

public sealed class PersistedShellState
{
    public bool IsQueuePaneOpen { get; set; } = true;

    public bool IsNavigationExpanded { get; set; }

    public PersistedPlaybackQueue? PlaybackQueue { get; set; }
}

public sealed class PersistedPlaybackQueue
{
    public List<PersistedQueueItem> Items { get; set; } = [];

    public int CurrentIndex { get; set; } = -1;

    public double PositionMs { get; set; }

    public double DurationMs { get; set; }

    public int Mode { get; set; }

    public double Volume { get; set; } = 0.8;
}

public sealed class PersistedQueueItem
{
    public string FilePath { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string Album { get; set; } = string.Empty;

    public long DurationTicks { get; set; }

    /// <summary>Cue start offset in milliseconds; null for a plain (non-CUE) media file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StartTimeMs { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PersistedShellState))]
internal sealed partial class ShellStateJsonContext : JsonSerializerContext
{
}
