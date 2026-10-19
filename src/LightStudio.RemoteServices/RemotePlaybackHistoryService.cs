using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Remote;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.RemoteServices;

public sealed class RemotePlaybackHistoryFactory(
    IAppSettingsStore settings,
    ILibraryQueryService libraryQuery,
    IKeyValueStore serverState) : IPlaybackHistoryServiceFactory
{
    public IPlaybackHistoryService Create(Func<bool> isIndexing) =>
        new RemotePlaybackHistoryService(settings, libraryQuery, serverState, isIndexing);
}

/// <summary>
/// Playback history of one remote server, kept on this device.
/// </summary>
/// <remarks>
/// The history belongs to the library that produced it, so it is written to the server-scoped
/// store rather than the shared one: connecting to another server shows that server's history.
/// </remarks>
public sealed class RemotePlaybackHistoryService : IPlaybackHistoryService
{
    private const string StorageKey = "history.items";

    private readonly IAppSettingsStore settings;
    private readonly ILibraryQueryService libraryQuery;
    private readonly IKeyValueStore serverState;
    private readonly Func<bool> isIndexing;
    private readonly List<RemoteHistoryItem> items;
    private Dictionary<string, DbMediaFile>? filesByPath;

    public RemotePlaybackHistoryService(
        IAppSettingsStore settings,
        ILibraryQueryService libraryQuery,
        IKeyValueStore serverState,
        Func<bool> isIndexing)
    {
        this.settings = settings;
        this.libraryQuery = libraryQuery;
        this.serverState = serverState;
        this.isIndexing = isIndexing;
        items = Load();
    }

    public event EventHandler<MusicPlaybackItemModel>? NewEntryAdded;

    public Task<IReadOnlyList<MusicPlaybackItemModel>> GetHistoryAsync(int count)
    {
        if (!settings.EnablePlaybackHistory)
        {
            return Task.FromResult<IReadOnlyList<MusicPlaybackItemModel>>([]);
        }

        var result = items
            .Take(count > 0 ? count : items.Count)
            .Select(value => value.ToModel())
            .ToArray();
        return Task.FromResult<IReadOnlyList<MusicPlaybackItemModel>>(result);
    }

    public Task AddHistoryAsync(MusicPlaybackItemModel item)
    {
        if (!settings.EnablePlaybackHistory || isIndexing() || string.IsNullOrWhiteSpace(item.FilePath))
        {
            return Task.CompletedTask;
        }

        items.Insert(0, RemoteHistoryItem.FromModel(item));
        if (items.Count > IPlaybackHistoryService.HistoryEntryLimit)
        {
            items.RemoveRange(IPlaybackHistoryService.HistoryEntryLimit, items.Count - IPlaybackHistoryService.HistoryEntryLimit);
        }

        Save();
        NewEntryAdded?.Invoke(this, item);
        return Task.CompletedTask;
    }

    public Task ClearHistoryAsync()
    {
        items.Clear();
        serverState.Remove(StorageKey);
        return Task.CompletedTask;
    }

    private async Task<DbMediaFile?> FindFileAsync(string path)
    {
        if (filesByPath is null || !filesByPath.TryGetValue(path, out var file))
        {
            filesByPath = (await libraryQuery.GetMediaFilesAsync().ConfigureAwait(false))
                .GroupBy(value => value.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            filesByPath.TryGetValue(path, out file);
        }

        return file;
    }

    private List<RemoteHistoryItem> Load()
    {
        try
        {
            var json = serverState.Get(StorageKey);
            return string.IsNullOrEmpty(json)
                ? []
                : (JsonSerializer.Deserialize(json, RemoteStateJsonContext.Default.RemoteHistoryItemArray) ?? []).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Save() =>
        serverState.Set(
            StorageKey,
            JsonSerializer.Serialize(items.ToArray(), RemoteStateJsonContext.Default.RemoteHistoryItemArray));
}
