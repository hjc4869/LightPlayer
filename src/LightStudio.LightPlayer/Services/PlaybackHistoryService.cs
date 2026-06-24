using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.MediaLibraryCore;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Records and reads back the most recently played library tracks. History is
/// gated behind the <see cref="IAppSettingsStore.EnablePlaybackHistory"/> 
/// setting, rows are stored in the shared <c>PlaybackHistory</c> table,
/// external (non-library) files are ignored, and writes are skipped while the
/// library is being indexed. The home page subscribes to
/// <see cref="NewEntryAdded"/> to show fresh plays live.
/// </summary>
public sealed class PlaybackHistoryService
{
    /// <summary>
    /// Maximum number of entries kept. Only the home page shows history (a single
    /// horizontal list), so a fixed cap is enough and keeps the table small.
    /// </summary>
    public const int HistoryEntryLimit = 20;

    private readonly IAppSettingsStore settingsStore;
    private readonly Func<bool>? isIndexing;
    private readonly object cacheLock = new();
    private List<MusicPlaybackItemModel>? cache;

    public PlaybackHistoryService(IAppSettingsStore settingsStore, Func<bool>? isIndexing = null)
    {
        this.settingsStore = settingsStore;
        this.isIndexing = isIndexing;
    }

    /// <summary>Raised (off the UI thread) when a newly played track is recorded.</summary>
    public event EventHandler<MusicPlaybackItemModel>? NewEntryAdded;

    private bool EnablePlaybackHistory => settingsStore.EnablePlaybackHistory;

    /// <summary>
    /// Returns up to <paramref name="count"/> of the most recently played tracks,
    /// newest first. Returns an empty list when history is disabled.
    /// </summary>
    public async Task<IReadOnlyList<MusicPlaybackItemModel>> GetHistoryAsync(int count)
    {
        if (!EnablePlaybackHistory)
        {
            return Array.Empty<MusicPlaybackItemModel>();
        }

        lock (cacheLock)
        {
            if (cache is not null)
            {
                return Snapshot(count);
            }
        }

        var loaded = await Task.Run(() => QueryHistory(count));
        lock (cacheLock)
        {
            cache = loaded;
            return Snapshot(count);
        }
    }

    /// <summary>
    /// Records the track at <paramref name="filePath"/> as just played. No-op when
    /// history is disabled, while indexing, or when the file is not part of the
    /// library (external files have no database row).
    /// </summary>
    public async Task AddHistoryAsync(string filePath)
    {
        if (!EnablePlaybackHistory || string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        if (isIndexing?.Invoke() == true)
        {
            return;
        }

        var added = await Task.Run(() =>
        {
            try
            {
                using var scope = ApplicationServiceBase.App.GetScope();
                var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>();
                var file = context.MediaFiles.FirstOrDefault(f => f.Path == filePath);
                if (file is null)
                {
                    // Not a library track (e.g. a file opened directly)
                    return null;
                }

                context.PlaybackHistory.Add(new DbPlaybackHistory { RelatedMediaFileId = file.Id });
                context.SaveChanges();

                // Bound database growth: drop entries older than the most recent limit.
                var stale = context.PlaybackHistory
                    .OrderByDescending(history => history.Id)
                    .Skip(HistoryEntryLimit)
                    .ToList();
                if (stale.Count > 0)
                {
                    context.PlaybackHistory.RemoveRange(stale);
                    context.SaveChanges();
                }

                return CreateModel(file);
            }
            catch
            {
                return null;
            }
        });

        if (added is null)
        {
            return;
        }

        lock (cacheLock)
        {
            if (cache is not null)
            {
                cache.Insert(0, added);
                if (cache.Count > HistoryEntryLimit)
                {
                    cache.RemoveRange(HistoryEntryLimit, cache.Count - HistoryEntryLimit);
                }
            }
        }

        NewEntryAdded?.Invoke(this, added);
    }

    /// <summary>
    /// Removes every recorded entry. Invoked when the user turns the feature off so
    /// no listening history is retained.
    /// </summary>
    public async Task ClearHistoryAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                using var scope = ApplicationServiceBase.App.GetScope();
                var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>();
                context.PlaybackHistory.RemoveRange(context.PlaybackHistory);
                context.SaveChanges();
            }
            catch
            {
                // Best-effort: clearing history must not crash the settings toggle.
            }
        });

        lock (cacheLock)
        {
            cache?.Clear();
        }
    }

    private List<MusicPlaybackItemModel> QueryHistory(int count)
    {
        try
        {
            using var scope = ApplicationServiceBase.App.GetScope();
            var context = scope.ServiceProvider.GetRequiredService<MediaLibraryDbContext>();

            IQueryable<DbPlaybackHistory> query = context.PlaybackHistory
                .Include(history => history.RelatedMediaFile)
                .OrderByDescending(history => history.Id);

            var limited = count > 0 ? query.Take(count) : query;
            return limited
                .Where(history => history.RelatedMediaFile != null)
                .AsEnumerable()
                .Select(history => CreateModel(history.RelatedMediaFile))
                .ToList();
        }
        catch
        {
            return new List<MusicPlaybackItemModel>();
        }
    }

    private IReadOnlyList<MusicPlaybackItemModel> Snapshot(int count)
    {
        var items = cache ?? new List<MusicPlaybackItemModel>();
        return count > 0 && items.Count > count
            ? items.Take(count).ToList()
            : items.ToList();
    }

    private static MusicPlaybackItemModel CreateModel(DbMediaFile file) =>
        new(
            ResolveTitle(file),
            file.Artist ?? string.Empty,
            file.Album ?? string.Empty,
            file.Duration,
            file.Path ?? string.Empty);

    private static string ResolveTitle(DbMediaFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.Title))
        {
            return file.Title;
        }

        return string.IsNullOrWhiteSpace(file.Path)
            ? "Unknown title"
            : Path.GetFileNameWithoutExtension(file.Path);
    }
}
