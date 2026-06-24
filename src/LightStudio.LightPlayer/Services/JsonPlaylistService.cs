using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// File-backed playlist store. Each playlist is a single JSON document named
/// after its GUID, kept under the per-user application data directory so titles
/// can change without moving files. Writes are best-effort: IO failures are
/// swallowed so playlist edits never crash the UI.
/// </summary>
public sealed class JsonPlaylistService : IPlaylistService
{
    private const string FavoritesId = "favorites";
    private const string FavoritesTitle = "Favorites";

    private readonly string storageDirectory;
    private bool initialized;

    public JsonPlaylistService()
        : this(DefaultStorageDirectory())
    {
    }

    public JsonPlaylistService(string storageDirectory)
    {
        this.storageDirectory = storageDirectory;
        Playlists = new ObservableCollection<PlaylistModel>();
        Favorites = new PlaylistModel(FavoritesId, FavoritesTitle);
    }

    public ObservableCollection<PlaylistModel> Playlists { get; }

    public PlaylistModel Favorites { get; }

    public Task InitializeAsync()
    {
        if (initialized)
        {
            return Task.CompletedTask;
        }

        initialized = true;

        IReadOnlyList<PersistedPlaylist> loaded = LoadAll(storageDirectory);
        foreach (var persisted in loaded.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (string.Equals(persisted.Id, FavoritesId, StringComparison.Ordinal))
            {
                LoadFavorites(persisted);
                continue;
            }

            Playlists.Add(ToModel(persisted));
        }

        // Favorites is always present and listed first so the Like button has a
        // backing list the user can also browse and edit like any other playlist.
        Playlists.Insert(0, Favorites);

        return Task.CompletedTask;
    }

    public bool IsFavorite(PlaylistItemModel item) =>
        item is not null && Favorites.Items.Any(existing => existing.SameSource(item));

    public async Task AddToFavoritesAsync(PlaylistItemModel item)
    {
        if (item is null || IsFavorite(item))
        {
            return;
        }

        Favorites.Items.Add(item);
        await SaveAsync(Favorites);
    }

    public async Task RemoveFromFavoritesAsync(PlaylistItemModel item)
    {
        if (item is null)
        {
            return;
        }

        var existing = Favorites.Items.FirstOrDefault(candidate => candidate.SameSource(item));
        if (existing is null)
        {
            return;
        }

        Favorites.Items.Remove(existing);
        await SaveAsync(Favorites);
    }

    private void LoadFavorites(PersistedPlaylist persisted)
    {
        Favorites.Items.Clear();
        foreach (var item in persisted.Items)
        {
            Favorites.Items.Add(ToItemModel(item));
        }
    }

    public PlaylistModel? GetById(string id) =>
        Playlists.FirstOrDefault(playlist => string.Equals(playlist.Id, id, StringComparison.Ordinal));

    public async Task<PlaylistModel> CreateAsync(string title)
    {
        var model = new PlaylistModel(Guid.NewGuid().ToString("N"), NormalizeTitle(title));
        Playlists.Add(model);
        await SaveAsync(model);
        return model;
    }

    public async Task RenameAsync(string id, string newTitle)
    {
        var model = GetById(id);
        if (model is null)
        {
            return;
        }

        model.Title = NormalizeTitle(newTitle);
        await SaveAsync(model);
    }

    public async Task DeleteAsync(string id)
    {
        var model = GetById(id);
        if (model is null)
        {
            return;
        }

        if (string.Equals(id, FavoritesId, StringComparison.Ordinal))
        {
            // Favorites is permanent: clear it instead of removing the list so
            // the Like button always has a backing playlist.
            model.Items.Clear();
            await SaveAsync(model);
            return;
        }

        Playlists.Remove(model);
        await Task.Run(() => DeleteFile(id));
    }

    public async Task AddItemsAsync(string id, IEnumerable<PlaylistItemModel> items)
    {
        var model = GetById(id);
        if (model is null)
        {
            return;
        }

        foreach (var item in items)
        {
            model.Items.Add(item);
        }

        await SaveAsync(model);
    }

    public async Task ReplaceItemsAsync(string id, IEnumerable<PlaylistItemModel> items)
    {
        var model = GetById(id);
        if (model is null)
        {
            return;
        }

        model.Items.Clear();
        foreach (var item in items)
        {
            model.Items.Add(item);
        }

        await SaveAsync(model);
    }

    public async Task<PlaylistModel> ImportAsync(string filePath)
    {
        var items = await Task.Run(() => ParseImport(filePath));
        var title = Path.GetFileNameWithoutExtension(filePath);
        var model = new PlaylistModel(Guid.NewGuid().ToString("N"), NormalizeTitle(title));
        foreach (var item in items)
        {
            model.Items.Add(item);
        }

        Playlists.Add(model);
        await SaveAsync(model);
        return model;
    }

    public async Task ExportAsync(string id, string filePath)
    {
        var model = GetById(id);
        if (model is null)
        {
            return;
        }

        var title = model.Title;
        var items = model.Items.ToList();
        await Task.Run(() =>
        {
            if (filePath.EndsWith(".wpl", StringComparison.OrdinalIgnoreCase))
            {
                WplPlaylistFormat.Write(filePath, title, items);
            }
            else
            {
                M3uPlaylistFormat.Write(filePath, items);
            }
        });
    }

    private static IReadOnlyList<PlaylistItemModel> ParseImport(string filePath) =>
        filePath.EndsWith(".wpl", StringComparison.OrdinalIgnoreCase)
            ? WplPlaylistFormat.Parse(filePath)
            : M3uPlaylistFormat.Parse(filePath);

    private Task SaveAsync(PlaylistModel model)
    {
        var persisted = ToPersisted(model);
        return Task.Run(() => WriteFile(persisted));
    }

    private void WriteFile(PersistedPlaylist persisted)
    {
        try
        {
            Directory.CreateDirectory(storageDirectory);
            var json = JsonSerializer.Serialize(persisted, PlaylistJsonContext.Default.PersistedPlaylist);
            File.WriteAllText(FilePathFor(persisted.Id), json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort persistence; ignore write failures.
        }
    }

    private void DeleteFile(string id)
    {
        try
        {
            var path = FilePathFor(id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort deletion; ignore failures.
        }
    }

    private string FilePathFor(string id) => Path.Combine(storageDirectory, id + ".json");

    private static IReadOnlyList<PersistedPlaylist> LoadAll(string directory)
    {
        var result = new List<PersistedPlaylist>();
        try
        {
            if (!Directory.Exists(directory))
            {
                return result;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var loaded = JsonSerializer.Deserialize(json, PlaylistJsonContext.Default.PersistedPlaylist);
                    if (loaded is not null && !string.IsNullOrEmpty(loaded.Id))
                    {
                        result.Add(loaded);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Skip unreadable or corrupt playlist files.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No accessible storage; return whatever loaded.
        }

        return result;
    }

    private static string NormalizeTitle(string? title)
    {
        var trimmed = title?.Trim();
        return string.IsNullOrEmpty(trimmed) ? "Untitled playlist" : trimmed;
    }

    private static PlaylistModel ToModel(PersistedPlaylist persisted)
    {
        var model = new PlaylistModel(persisted.Id, persisted.Title ?? string.Empty);
        foreach (var item in persisted.Items)
        {
            model.Items.Add(ToItemModel(item));
        }

        return model;
    }

    private static PlaylistItemModel ToItemModel(PersistedPlaylistItem item) =>
        new(
            item.Title ?? string.Empty,
            item.Artist ?? string.Empty,
            item.Album ?? string.Empty,
            item.FilePath ?? string.Empty,
            TimeSpan.FromTicks(item.DurationTicks),
            item.StartTimeMs);

    private static PersistedPlaylist ToPersisted(PlaylistModel model) => new()
    {
        Id = model.Id,
        Title = model.Title,
        Items = model.Items.Select(item => new PersistedPlaylistItem
        {
            Title = item.Title,
            Artist = item.Artist,
            Album = item.Album,
            FilePath = item.FilePath,
            DurationTicks = item.Duration.Ticks,
            StartTimeMs = item.StartTimeMs,
        }).ToList(),
    };

    private static string DefaultStorageDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(root, "LightStudio.LightPlayer", "Playlists");
    }
}

public sealed class PersistedPlaylist
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public List<PersistedPlaylistItem> Items { get; set; } = [];
}

public sealed class PersistedPlaylistItem
{
    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string Album { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public long DurationTicks { get; set; }

    public int? StartTimeMs { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PersistedPlaylist))]
internal sealed partial class PlaylistJsonContext : JsonSerializerContext
{
}
