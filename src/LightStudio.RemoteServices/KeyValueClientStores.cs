using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Linq;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore;
using LightStudio.MediaLibraryCore.Database;
using Microsoft.Extensions.DependencyInjection;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.RemoteServices;

public sealed class KeyValuePlaylistService : IPlaylistService
{
    private const string MusicStorageKey = "playlists.v2";
    private const string VideoStorageKey = "videoplaylists.v1";
    private const string MusicFavoritesId = "favorites";
    private const string VideoFavoritesId = "favorite-video";

    /// <summary>The title shipped before favorites was split per media kind; upgraded on load.</summary>
    private const string LegacyFavoritesTitle = "Favorites";

    private readonly IKeyValueStore store;
    private readonly string storageKey;
    private bool initialized;

    public KeyValuePlaylistService(IKeyValueStore store, PlaylistKind kind = PlaylistKind.Music)
    {
        this.store = store;
        Kind = kind;
        storageKey = kind == PlaylistKind.Video ? VideoStorageKey : MusicStorageKey;
        Favorites = new PlaylistModel(
            kind == PlaylistKind.Video ? VideoFavoritesId : MusicFavoritesId,
            kind == PlaylistKind.Video
                ? Strings.Resources.Playlists_FavoriteVideo
                : Strings.Resources.Playlists_FavoriteMusic,
            isBuiltIn: true,
            kind);
    }

    public event EventHandler? Changed;

    public PlaylistKind Kind { get; }

    public ObservableCollection<PlaylistModel> Playlists { get; } = [];

    public PlaylistModel Favorites { get; }

    public Task InitializeAsync()
    {
        if (initialized)
        {
            return Task.CompletedTask;
        }

        initialized = true;
        var loaded = Load();
        foreach (var persisted in loaded.OrderBy(value => value.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (persisted.Id == Favorites.Id)
            {
                if (!string.IsNullOrWhiteSpace(persisted.Title) &&
                    !string.Equals(persisted.Title, LegacyFavoritesTitle, StringComparison.Ordinal))
                {
                    Favorites.Title = persisted.Title;
                }

                AddItems(Favorites, persisted.Items);
            }
            else
            {
                var playlist = new PlaylistModel(persisted.Id, persisted.Title, isBuiltIn: false, Kind);
                AddItems(playlist, persisted.Items);
                Playlists.Add(playlist);
            }
        }

        Playlists.Insert(0, Favorites);
        return Task.CompletedTask;
    }

    public bool IsFavorite(PlaylistItemModel item) =>
        item is not null && Favorites.Items.Any(existing => existing.SameSource(item));

    public Task AddToFavoritesAsync(PlaylistItemModel item)
    {
        if (!IsFavorite(item))
        {
            Favorites.Items.Add(item);
            Save();
        }

        return Task.CompletedTask;
    }

    public Task RemoveFromFavoritesAsync(PlaylistItemModel item)
    {
        var existing = Favorites.Items.FirstOrDefault(value => value.SameSource(item));
        if (existing is not null)
        {
            Favorites.Items.Remove(existing);
            Save();
        }

        return Task.CompletedTask;
    }

    public PlaylistModel? GetById(string id) =>
        Playlists.FirstOrDefault(value => string.Equals(value.Id, id, StringComparison.Ordinal));

    public Task<PlaylistModel> CreateAsync(string title)
    {
        var playlist = new PlaylistModel(Guid.NewGuid().ToString("N"), NormalizeTitle(title), isBuiltIn: false, Kind);
        Playlists.Add(playlist);
        Save();
        return Task.FromResult(playlist);
    }

    public Task RenameAsync(string id, string newTitle)
    {
        if (GetById(id) is { } playlist)
        {
            playlist.Title = NormalizeTitle(newTitle);
            Save();
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id)
    {
        if (GetById(id) is { } playlist)
        {
            if (playlist.IsBuiltIn)
            {
                playlist.Items.Clear();
            }
            else
            {
                Playlists.Remove(playlist);
            }

            Save();
        }

        return Task.CompletedTask;
    }

    public Task AddItemsAsync(string id, IEnumerable<PlaylistItemModel> items)
    {
        if (GetById(id) is { } playlist)
        {
            foreach (var item in items)
            {
                playlist.Items.Add(item);
            }

            Save();
        }

        return Task.CompletedTask;
    }

    public Task ReplaceItemsAsync(string id, IEnumerable<PlaylistItemModel> items)
    {
        if (GetById(id) is { } playlist)
        {
            playlist.Items.Clear();
            foreach (var item in items)
            {
                playlist.Items.Add(item);
            }

            Save();
        }

        return Task.CompletedTask;
    }

    public async Task<PlaylistModel> ImportAsync(PlaylistFile file)
    {
        var items = await Task.Run(() => Parse(file)).ConfigureAwait(false);
        var playlist = new PlaylistModel(
            Guid.NewGuid().ToString("N"),
            NormalizeTitle(Path.GetFileNameWithoutExtension(file.Name)),
            isBuiltIn: false,
            Kind);
        foreach (var item in items)
        {
            playlist.Items.Add(item);
        }

        Playlists.Add(playlist);
        Save();
        return playlist;
    }

    public Task<PlaylistFile?> ExportAsync(string id, string fileName)
    {
        var playlist = GetById(id);
        if (playlist is null)
        {
            return Task.FromResult<PlaylistFile?>(null);
        }

        var content = fileName.EndsWith(".wpl", StringComparison.OrdinalIgnoreCase)
            ? WriteWpl(playlist)
            : WriteM3u(playlist.Items);
        return Task.FromResult<PlaylistFile?>(new PlaylistFile(fileName, content));
    }

    private List<KeyValuePlaylistRecord> Load()
    {
        try
        {
            var json = store.Get(storageKey);
            return string.IsNullOrEmpty(json)
                ? []
                : JsonSerializer.Deserialize(json, KeyValueClientStoreJsonContext.Default.ListKeyValuePlaylistRecord) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Save()
    {
        var records = Playlists.Select(playlist => new KeyValuePlaylistRecord
        {
            Id = playlist.Id,
            Title = playlist.Title,
            Items = playlist.Items.Select(ToRecord).ToList(),
        }).ToList();
        store.Set(
            storageKey,
            JsonSerializer.Serialize(records, KeyValueClientStoreJsonContext.Default.ListKeyValuePlaylistRecord));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void AddItems(PlaylistModel playlist, IEnumerable<KeyValuePlaylistItemRecord> items)
    {
        foreach (var item in items)
        {
            playlist.Items.Add(ToModel(item));
        }
    }

    private static KeyValuePlaylistItemRecord ToRecord(PlaylistItemModel item) => new()
    {
        SourceId = item.SourceId.Value,
        FilePath = item.FilePath,
        StartTimeMs = item.StartTimeMs,
        Title = item.Title,
        Artist = item.Artist,
        Album = item.Album,
        DurationTicks = item.Duration.Ticks,
    };

    private static PlaylistItemModel ToModel(KeyValuePlaylistItemRecord item) => new(
        item.Title,
        item.Artist,
        item.Album,
        item.FilePath,
        TimeSpan.FromTicks(item.DurationTicks),
        item.StartTimeMs,
        LibrarySourceId.FromPersisted(item.SourceId));

    private static List<PlaylistItemModel> Parse(PlaylistFile file)
    {
        var sources = file.Name.EndsWith(".wpl", StringComparison.OrdinalIgnoreCase)
            ? XDocument.Parse(file.Content).Descendants("media")
                .Select(element => element.Attribute("src")?.Value)
            : file.Content.Split('\n')
                .Select(value => value.Trim())
                .Where(value => value.Length > 0 && !value.StartsWith('#'));
        return sources.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ParseItem(value!, file.BaseDirectory))
            .ToList();
    }

    private static PlaylistItemModel ParseItem(string value, string? baseDirectory)
    {
        var media = MediaReferenceUri.TryParse(value, out var parsed)
            ? parsed
            : new MediaItemKey(MediaReference.Local(ResolvePath(value, baseDirectory)), null);
        return new PlaylistItemModel(
            Path.GetFileNameWithoutExtension(media.Media.Path),
            string.Empty,
            string.Empty,
            media.Media.Path,
            TimeSpan.Zero,
            media.StartTimeMs,
            media.Media.SourceId);
    }

    private static string WriteM3u(IEnumerable<PlaylistItemModel> items)
    {
        var content = new StringBuilder("#EXTM3U\n");
        foreach (var item in items)
        {
            content.Append("#EXTINF:")
                .Append(item.Duration > TimeSpan.Zero ? (int)item.Duration.TotalSeconds : -1)
                .Append(',').Append(item.Artist).Append(" - ").AppendLine(item.Title)
                .AppendLine(item.SourceId == LibrarySourceId.Local
                    ? item.FilePath
                    : MediaReferenceUri.Format(item.Media, item.StartTimeMs));
        }

        return content.ToString();
    }

    private static string WriteWpl(PlaylistModel playlist) => new XDocument(
        new XProcessingInstruction("wpl", "version=\"1.0\""),
        new XElement("smil",
            new XElement("head", new XElement("title", playlist.Title)),
            new XElement("body", new XElement("seq", playlist.Items.Select(item =>
                new XElement("media", new XAttribute(
                    "src",
                    item.SourceId == LibrarySourceId.Local
                        ? item.FilePath
                        : MediaReferenceUri.Format(item.Media, item.StartTimeMs))))))))
        .ToString();

    private static string ResolvePath(string value, string? baseDirectory)
    {
        if (string.IsNullOrEmpty(baseDirectory) || Path.IsPathRooted(value))
        {
            return value;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(baseDirectory, value));
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    private static string NormalizeTitle(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Strings.Resources.Playlists_UntitledPlaylist : value.Trim();
}

public sealed class KeyValuePlaybackHistoryService(
    IKeyValueStore store,
    IAppSettingsStore settings) : IPlaybackHistoryService
{
    private const string StorageKey = "history.device.v2";
    private List<KeyValueHistoryRecord> entries = Load(store);

    public event EventHandler<MusicPlaybackItemModel>? NewEntryAdded;

    public Task<IReadOnlyList<MusicPlaybackItemModel>> GetHistoryAsync(int count)
    {
        if (!settings.EnablePlaybackHistory)
        {
            return Task.FromResult<IReadOnlyList<MusicPlaybackItemModel>>([]);
        }

        return Task.FromResult<IReadOnlyList<MusicPlaybackItemModel>>(entries
            .Take(count > 0 ? count : entries.Count)
            .Select(value => value.ToModel()).ToArray());
    }

    public async Task AddHistoryAsync(MusicPlaybackItemModel item)
    {
        if (!settings.EnablePlaybackHistory || string.IsNullOrWhiteSpace(item.FilePath))
        {
            return;
        }

        if (item.SourceId == LibrarySourceId.Local)
        {
            await using var scope = await ApplicationServiceBase.App.GetScopeAsync().ConfigureAwait(false);
            var database = scope.ServiceProvider.GetRequiredService<MediaLibraryDatabase>();
            var isLibraryFile = await database.GetMediaFileByPathAsync(item.FilePath).ConfigureAwait(false) is not null;
            if (!isLibraryFile || !settings.EnablePlaybackHistory)
            {
                return;
            }
        }

        entries.Insert(0, KeyValueHistoryRecord.FromModel(item));
        if (entries.Count > IPlaybackHistoryService.HistoryEntryLimit)
        {
            entries.RemoveRange(IPlaybackHistoryService.HistoryEntryLimit, entries.Count - IPlaybackHistoryService.HistoryEntryLimit);
        }

        Save();
        NewEntryAdded?.Invoke(this, item);
    }

    public Task ClearHistoryAsync()
    {
        entries.Clear();
        store.Remove(StorageKey);
        return Task.CompletedTask;
    }

    private static List<KeyValueHistoryRecord> Load(IKeyValueStore store)
    {
        try
        {
            var json = store.Get(StorageKey);
            return string.IsNullOrEmpty(json)
                ? []
                : JsonSerializer.Deserialize(json, KeyValueClientStoreJsonContext.Default.ListKeyValueHistoryRecord) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Save() => store.Set(
        StorageKey,
        JsonSerializer.Serialize(entries, KeyValueClientStoreJsonContext.Default.ListKeyValueHistoryRecord));
}

public sealed class KeyValueVideoPlaybackStateStore(IKeyValueStore store) : IVideoPlaybackStateStore
{
    private const string StorageKey = "videoPlayback.v2";
    private const int MaxEntries = 500;
    private Dictionary<string, VideoPlaybackState> states = Load(store);

    public VideoPlaybackState? Get(MediaReference video) =>
        string.IsNullOrEmpty(video.Path) ? null : states.GetValueOrDefault(Key(video));

    public void Save(MediaReference video, VideoPlaybackState state) => SaveAll([(video, state)]);

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

        if (states.Count > MaxEntries)
        {
            foreach (var key in states.OrderBy(entry => entry.Value.UpdatedUtc)
                .Take(states.Count - MaxEntries).Select(entry => entry.Key).ToArray())
            {
                states.Remove(key);
            }
        }

        store.Set(
            StorageKey,
            JsonSerializer.Serialize(states, KeyValueClientStoreJsonContext.Default.DictionaryStringVideoPlaybackState));
    }

    public IReadOnlyList<VideoPlaybackProgress> GetInProgress(int limit) =>
        VideoPlaybackStateKeys.SelectInProgress(states, limit);

    private static Dictionary<string, VideoPlaybackState> Load(IKeyValueStore store)
    {
        try
        {
            var json = store.Get(StorageKey);
            return string.IsNullOrEmpty(json)
                ? new Dictionary<string, VideoPlaybackState>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize(
                    json,
                    KeyValueClientStoreJsonContext.Default.DictionaryStringVideoPlaybackState)
                    ?? new Dictionary<string, VideoPlaybackState>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, VideoPlaybackState>(StringComparer.Ordinal);
        }
    }

    private static string Key(MediaReference video) =>
        VideoPlaybackStateKeys.Format(video);
}

public sealed class KeyValueVideoHistoryStore(IKeyValueStore store, IAppSettingsStore settings)
    : VideoHistoryStoreBase(settings)
{
    private const string StorageKey = "videoHistory.v1";

    protected override string? Read() => store.Get(StorageKey);

    protected override void Write(string json) => store.Set(StorageKey, json);
}

public sealed class KeyValuePlaylistRecord
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<KeyValuePlaylistItemRecord> Items { get; set; } = [];
}

public class KeyValuePlaylistItemRecord
{
    public string SourceId { get; set; } = LibrarySourceId.Local.Value;
    public string FilePath { get; set; } = string.Empty;
    public int? StartTimeMs { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public long DurationTicks { get; set; }
}

public sealed class KeyValueHistoryRecord : KeyValuePlaylistItemRecord
{
    public DateTimeOffset PlayedUtc { get; set; }

    public static KeyValueHistoryRecord FromModel(MusicPlaybackItemModel item) => new()
    {
        SourceId = item.SourceId.Value,
        FilePath = item.FilePath,
        StartTimeMs = item.StartTimeMs,
        Title = item.Title,
        Artist = item.Artist,
        Album = item.Album,
        DurationTicks = item.Duration.Ticks,
        PlayedUtc = DateTimeOffset.UtcNow,
    };

    public MusicPlaybackItemModel ToModel() => new(
        Title,
        Artist,
        Album,
        TimeSpan.FromTicks(DurationTicks),
        FilePath,
        StartTimeMs,
        LibrarySourceId.FromPersisted(SourceId));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<KeyValuePlaylistRecord>))]
[JsonSerializable(typeof(List<KeyValueHistoryRecord>))]
[JsonSerializable(typeof(Dictionary<string, VideoPlaybackState>))]
internal sealed partial class KeyValueClientStoreJsonContext : JsonSerializerContext;
