using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LocalServices;

/// <summary>
/// File-backed playlist store. Each playlist is a single JSON document named
/// after its GUID, kept under the per-user application data directory so titles
/// can change without moving files. Writes are best-effort: IO failures are
/// swallowed so playlist edits never crash the UI.
/// </summary>
/// <remarks>
/// One instance per <see cref="PlaylistKind"/>: music and video playlists share this schema but
/// live in directories of their own, so neither store can ever hand the other's lists to a picker.
/// </remarks>
public sealed class JsonPlaylistService : IPlaylistService
{
    private const string MusicFavoritesId = "favorites";
    private const string VideoFavoritesId = "favorite-video";

    /// <summary>The title shipped before favorites was split per media kind; upgraded on load.</summary>
    private const string LegacyFavoritesTitle = "Favorites";

    private readonly string storageDirectory;
    private bool initialized;

    public JsonPlaylistService(PlaylistKind kind = PlaylistKind.Music)
        : this(DefaultStorageDirectory(kind), kind)
    {
    }

    public JsonPlaylistService(string storageDirectory, PlaylistKind kind = PlaylistKind.Music)
    {
        this.storageDirectory = storageDirectory;
        Kind = kind;
        Playlists = new ObservableCollection<PlaylistModel>();
        Favorites = new PlaylistModel(FavoritesId(kind), DefaultFavoritesTitle(kind), isBuiltIn: true, kind);
    }

    public PlaylistKind Kind { get; }

    public ObservableCollection<PlaylistModel> Playlists { get; }

    public PlaylistModel Favorites { get; }

    public event EventHandler? Changed;

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
            if (string.Equals(persisted.Id, Favorites.Id, StringComparison.Ordinal))
            {
                LoadFavorites(persisted);
                continue;
            }

            Playlists.Add(ToModel(persisted, Kind));
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
        OnChanged();
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
        OnChanged();
    }

    private void LoadFavorites(PersistedPlaylist persisted)
    {
        // Favorites keeps its well-known id, but its title is user-editable like any other
        // playlist, so restore the persisted one when present. The pre-split "Favorites" title is
        // not a user choice, so it upgrades to the localized per-kind name instead of sticking.
        if (!string.IsNullOrWhiteSpace(persisted.Title) &&
            !string.Equals(persisted.Title, LegacyFavoritesTitle, StringComparison.Ordinal))
        {
            Favorites.Title = persisted.Title;
        }

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
        var model = new PlaylistModel(Guid.NewGuid().ToString("N"), NormalizeTitle(title), isBuiltIn: false, Kind);
        Playlists.Add(model);
        await SaveAsync(model);
        OnChanged();
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
        OnChanged();
    }

    public async Task DeleteAsync(string id)
    {
        var model = GetById(id);
        if (model is null)
        {
            return;
        }

        if (string.Equals(id, Favorites.Id, StringComparison.Ordinal))
        {
            // Favorites is permanent: clear it instead of removing the list so
            // the Like button always has a backing playlist.
            model.Items.Clear();
            await SaveAsync(model);
            OnChanged();
            return;
        }

        Playlists.Remove(model);
        await Task.Run(() => DeleteFile(id));
        OnChanged();
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
        OnChanged();
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
        OnChanged();
    }

    public async Task<PlaylistModel> ImportAsync(PlaylistFile file)
    {
        var items = await Task.Run(() => ParseImport(file));
        var title = Path.GetFileNameWithoutExtension(file.Name);
        var model = new PlaylistModel(Guid.NewGuid().ToString("N"), NormalizeTitle(title), isBuiltIn: false, Kind);
        foreach (var item in items)
        {
            model.Items.Add(item);
        }

        Playlists.Add(model);
        await SaveAsync(model);
        OnChanged();
        return model;
    }

    public Task<PlaylistFile?> ExportAsync(string id, string fileName)
    {
        var model = GetById(id);
        if (model is null)
        {
            return Task.FromResult<PlaylistFile?>(null);
        }

        var content = fileName.EndsWith(".wpl", StringComparison.OrdinalIgnoreCase)
            ? WplPlaylistFormat.Write(model.Title, model.Items)
            : M3uPlaylistFormat.Write(model.Items);
        return Task.FromResult<PlaylistFile?>(new PlaylistFile(fileName, content));
    }

    private static IReadOnlyList<PlaylistItemModel> ParseImport(PlaylistFile file) =>
        file.Name.EndsWith(".wpl", StringComparison.OrdinalIgnoreCase)
            ? WplPlaylistFormat.Parse(file.Content, file.BaseDirectory)
            : M3uPlaylistFormat.Parse(file.Content, file.BaseDirectory);

    private Task SaveAsync(PlaylistModel model)
    {
        var persisted = ToPersisted(model);
        return Task.Run(() => WriteFile(persisted));
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

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
        return string.IsNullOrEmpty(trimmed) ? Strings.Resources.Playlists_UntitledPlaylist : trimmed;
    }

    private static string FavoritesId(PlaylistKind kind) =>
        kind == PlaylistKind.Video ? VideoFavoritesId : MusicFavoritesId;

    private static string DefaultFavoritesTitle(PlaylistKind kind) =>
        kind == PlaylistKind.Video
            ? Strings.Resources.Playlists_FavoriteVideo
            : Strings.Resources.Playlists_FavoriteMusic;

    private static PlaylistModel ToModel(PersistedPlaylist persisted, PlaylistKind kind)
    {
        var model = new PlaylistModel(persisted.Id, persisted.Title ?? string.Empty, isBuiltIn: false, kind);
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
            item.StartTimeMs,
            LibrarySourceId.FromPersisted(item.SourceId));

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
            SourceId = item.SourceId.Value,
        }).ToList(),
    };

    private static string DefaultStorageDirectory(PlaylistKind kind)
    {
        return Path.Combine(
            PlatformServices.AppDataPath,
            "LightStudio.LightPlayer",
            kind == PlaylistKind.Video ? "VideoPlaylists" : "Playlists");
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
    public string SourceId { get; set; } = LibrarySourceId.Local.Value;

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
