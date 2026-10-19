using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore;
using LightStudio.MediaLibraryCore.Database;
using Microsoft.Extensions.DependencyInjection;

namespace LightStudio.LocalServices;

public sealed class DevicePlaybackHistoryService : IPlaybackHistoryService
{
    private const int SchemaVersion = 1;
    private readonly object gate = new();
    private readonly IAppSettingsStore settings;
    private readonly Func<bool>? isIndexing;
    private readonly string filePath;
    private readonly List<DevicePlaybackHistoryEntry> entries;

    public DevicePlaybackHistoryService(
        IAppSettingsStore settings,
        Func<bool>? isIndexing = null,
        string? filePath = null)
    {
        this.settings = settings;
        this.isIndexing = isIndexing;
        this.filePath = filePath ?? Path.Combine(AppPaths.DataDirectory, "playback-history.json");
        entries = Load(this.filePath);
    }

    public event EventHandler<MusicPlaybackItemModel>? NewEntryAdded;

    public Task<IReadOnlyList<MusicPlaybackItemModel>> GetHistoryAsync(int count)
    {
        if (!settings.EnablePlaybackHistory)
        {
            return Task.FromResult<IReadOnlyList<MusicPlaybackItemModel>>([]);
        }

        lock (gate)
        {
            var take = count > 0 ? Math.Min(count, entries.Count) : entries.Count;
            return Task.FromResult<IReadOnlyList<MusicPlaybackItemModel>>(
                entries.Take(take).Select(ToModel).ToArray());
        }
    }

    public async Task AddHistoryAsync(MusicPlaybackItemModel item)
    {
        if (!settings.EnablePlaybackHistory || string.IsNullOrWhiteSpace(item.FilePath) ||
            (item.SourceId == LibrarySourceId.Local && isIndexing?.Invoke() == true))
        {
            return;
        }

        if (item.SourceId == LibrarySourceId.Local)
        {
            await using var scope = await ApplicationServiceBase.App.GetScopeAsync().ConfigureAwait(false);
            var database = scope.ServiceProvider.GetRequiredService<MediaLibraryDatabase>();
            var isLibraryFile = await database.GetMediaFileByPathAsync(item.FilePath).ConfigureAwait(false) is not null;
            if (!isLibraryFile || !settings.EnablePlaybackHistory || isIndexing?.Invoke() == true)
            {
                return;
            }
        }

        var entry = FromModel(item, DateTimeOffset.UtcNow);
        lock (gate)
        {
            entries.Insert(0, entry);
            if (entries.Count > IPlaybackHistoryService.HistoryEntryLimit)
            {
                entries.RemoveRange(
                    IPlaybackHistoryService.HistoryEntryLimit,
                    entries.Count - IPlaybackHistoryService.HistoryEntryLimit);
            }

            SaveLocked();
        }

        NewEntryAdded?.Invoke(this, ToModel(entry));
    }

    public Task ClearHistoryAsync()
    {
        lock (gate)
        {
            entries.Clear();
            SaveLocked();
        }

        return Task.CompletedTask;
    }

    private static List<DevicePlaybackHistoryEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var persisted = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                DevicePlaybackHistoryJsonContext.Default.PersistedDevicePlaybackHistory);
            return persisted?.SchemaVersion == SchemaVersion
                ? persisted.Entries
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                    .OrderByDescending(entry => entry.PlayedUtc)
                    .Take(IPlaybackHistoryService.HistoryEntryLimit)
                    .ToList()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var persisted = new PersistedDevicePlaybackHistory
            {
                SchemaVersion = SchemaVersion,
                Entries = entries,
            };
            File.WriteAllText(
                filePath,
                JsonSerializer.Serialize(
                    persisted,
                    DevicePlaybackHistoryJsonContext.Default.PersistedDevicePlaybackHistory));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static DevicePlaybackHistoryEntry FromModel(
        MusicPlaybackItemModel item,
        DateTimeOffset playedUtc) => new()
    {
        SourceId = item.SourceId.Value,
        Path = item.FilePath,
        StartTimeMs = item.StartTimeMs,
        Title = item.Title,
        Artist = item.Artist,
        Album = item.Album,
        DurationTicks = item.Duration.Ticks,
        PlayedUtc = playedUtc,
    };

    private static MusicPlaybackItemModel ToModel(DevicePlaybackHistoryEntry entry) => new(
        entry.Title ?? string.Empty,
        entry.Artist ?? string.Empty,
        entry.Album ?? string.Empty,
        TimeSpan.FromTicks(entry.DurationTicks),
        entry.Path ?? string.Empty,
        entry.StartTimeMs,
        LibrarySourceId.FromPersisted(entry.SourceId));
}

public sealed class PersistedDevicePlaybackHistory
{
    public int SchemaVersion { get; set; }

    public List<DevicePlaybackHistoryEntry> Entries { get; set; } = [];
}

public sealed class DevicePlaybackHistoryEntry
{
    public string SourceId { get; set; } = LibrarySourceId.Local.Value;

    public string Path { get; set; } = string.Empty;

    public int? StartTimeMs { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string Album { get; set; } = string.Empty;

    public long DurationTicks { get; set; }

    public DateTimeOffset PlayedUtc { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PersistedDevicePlaybackHistory))]
internal sealed partial class DevicePlaybackHistoryJsonContext : JsonSerializerContext;