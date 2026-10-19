using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.Services;

public sealed partial class UnifiedLibraryQueryService : IUnifiedLibraryQueryService
{
    private static readonly TimeSpan QueryDeadline = TimeSpan.FromSeconds(8);
    private readonly ILibrarySourceRegistry registry;
    private readonly Func<LibraryVisibilitySettings> visibilitySettings;
    private readonly UnifiedQuerySessionStore sessions = new();

    public UnifiedLibraryQueryService(
        ILibrarySourceRegistry registry,
        Func<LibraryVisibilitySettings>? visibilitySettings = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.visibilitySettings = visibilitySettings ?? (() => new LibraryVisibilitySettings());
    }

    public Task<UnifiedPage<Sourced<DbMediaFile>>> GetMediaFilesPageAsync(
        MediaFilePageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var fingerprint = $"tracks:{request.Sort}:{request.Ascending}:{request.PageSize}:" +
            $"{request.FolderPath?.Length ?? -1}:{request.FolderPath}";
        return sessions.ReadAsync(
            request.Cursor,
            fingerprint,
            id =>
            {
                var snapshot = registry.CreateSnapshot(LibraryMediaCategory.Music);
                var suppress = visibilitySettings().PreferHigherPriorityDuplicates;
                return new MergeSession<DbMediaFile, Sourced<DbMediaFile>>(
                    id,
                    fingerprint,
                    registry,
                    snapshot,
                    request.PageSize,
                    FederatedLibraryOrdering.Tracks(request),
                    (services, _, cursor, size, token) => services.LibraryQuery.GetMediaFilesPageAsync(
                        request with { PageSize = size, Cursor = cursor },
                        token),
                    group => new Sourced<DbMediaFile>(group[0].SourceId, group[0].Value),
                    FederatedLibraryOrdering.TrackIdentity,
                    item => new UnifiedVisibilityRemoval(
                        "song",
                        item.SourceId,
                        item.Value.Path ?? string.Empty,
                        item.Value.StartTime),
                    suppress,
                    FederatedLibraryOrdering.TrackDuplicateKey);
            },
            cancellationToken);
    }

    public Task<UnifiedPage<Sourced<MediaLibraryAlbum>>> GetAlbumsPageAsync(
        AlbumPageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var fingerprint = $"albums:{request.Sort}:{request.Ascending}:{request.PageSize}";
        return sessions.ReadAsync(
            request.Cursor,
            fingerprint,
            id =>
            {
                var snapshot = registry.CreateSnapshot(LibraryMediaCategory.Music);
                var suppress = visibilitySettings().PreferHigherPriorityDuplicates;
                return new MergeSession<MediaLibraryAlbum, Sourced<MediaLibraryAlbum>>(
                    id,
                    fingerprint,
                    registry,
                    snapshot,
                    request.PageSize,
                    FederatedLibraryOrdering.Albums(request),
                    (services, _, cursor, size, token) => services.LibraryQuery.GetAlbumsPageAsync(
                        request with { PageSize = size, Cursor = cursor },
                        token),
                    group => new Sourced<MediaLibraryAlbum>(group[0].SourceId, group[0].Value),
                    FederatedLibraryOrdering.AlbumIdentity,
                    item => new UnifiedVisibilityRemoval(
                        "album",
                        item.SourceId,
                        item.Value.FirstFileInAlbum ?? string.Empty),
                    suppress,
                    FederatedLibraryOrdering.AlbumDuplicateKey);
            },
            cancellationToken);
    }

    public Task<UnifiedPage<UnifiedArtistBrowseItem>> GetArtistsPageAsync(
        ArtistPageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var fingerprint = $"artists:{request.Ascending}:{request.PageSize}";
        return sessions.ReadAsync(
            request.Cursor,
            fingerprint,
            id =>
            {
                var snapshot = registry.CreateSnapshot(LibraryMediaCategory.Music);
                return new MergeSession<MediaLibraryArtistBrowseItem, UnifiedArtistBrowseItem>(
                    id,
                    fingerprint,
                    registry,
                    snapshot,
                    request.PageSize,
                    FederatedLibraryOrdering.Artists(request),
                    (services, _, cursor, size, token) => services.LibraryQuery.GetArtistsPageAsync(
                        request with { PageSize = size, Cursor = cursor },
                        token),
                    MergeArtist,
                    item => $"{item.Artist.Name}\u001f{item.Artist.DatabaseItemAddedDate.UtcTicks}",
                    item => new UnifiedVisibilityRemoval(
                        "artist",
                        item.SourceId,
                        FederatedLibraryOrdering.NormalizeArtistName(item.Value.Artist.Name)),
                    groupKey: item => FederatedLibraryOrdering.NormalizeArtistName(item.Artist.Name));
            },
            cancellationToken);
    }

    public Task<UnifiedPage<Sourced<DbMediaFile>>> GetShuffledMediaFilesPageAsync(
        ShuffledMediaFilePageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var fingerprint = $"shuffle:{request.Seed}:{request.PageSize}";
        return sessions.ReadAsync(
            request.Cursor,
            fingerprint,
            id =>
            {
                var snapshot = registry.CreateSnapshot(LibraryMediaCategory.Music);
                var suppress = visibilitySettings().PreferHigherPriorityDuplicates;
                return new MergeSession<DbMediaFile, Sourced<DbMediaFile>>(
                    id,
                    fingerprint,
                    registry,
                    snapshot,
                    request.PageSize,
                    FederatedLibraryOrdering.Shuffle(request.Seed),
                    (services, source, cursor, size, token) => services.LibraryQuery.GetShuffledMediaFilesPageAsync(
                        new ShuffledMediaFilePageRequest(
                            FederatedLibraryOrdering.DeriveSourceSeed(request.Seed, source.Id),
                            size,
                            cursor),
                        token),
                    group => new Sourced<DbMediaFile>(group[0].SourceId, group[0].Value),
                    FederatedLibraryOrdering.TrackIdentity,
                    item => new UnifiedVisibilityRemoval(
                        "song",
                        item.SourceId,
                        item.Value.Path ?? string.Empty,
                        item.Value.StartTime),
                    suppress,
                    FederatedLibraryOrdering.TrackDuplicateKey);
            },
            cancellationToken);
    }

    public async Task<UnifiedLibrarySearchResults> SearchAsync(
        string keyword,
        CancellationToken cancellationToken = default)
    {
        keyword ??= string.Empty;
        var normalizedKeyword = keyword.Trim().ToLowerInvariant();
        if (normalizedKeyword.Length == 0)
        {
            return new UnifiedLibrarySearchResults([], [], [], []);
        }

        var snapshot = registry.CreateSnapshot(LibraryMediaCategory.Music);
        var responses = await Task.WhenAll(snapshot.Sources.Select(item =>
            SearchSourceAsync(item, keyword, cancellationToken))).ConfigureAwait(false);
        var failures = responses.Where(response => response.Failure is not null)
            .Select(response => response.Failure!)
            .ToArray();
        var successful = responses.Where(response => response.Result is not null).ToArray();

        var albums = successful.SelectMany(response => response.Result!.Albums.Select(value =>
                new RankedSourceItem<MediaLibraryAlbum>(response.Source.Source.Id, response.Source.Priority, value)))
            .ToList();
        var files = successful.SelectMany(response => response.Result!.Files.Select(value =>
                new RankedSourceItem<DbMediaFile>(response.Source.Source.Id, response.Source.Priority, value)))
            .ToList();
        if (visibilitySettings().PreferHigherPriorityDuplicates)
        {
            albums = FilterCompleteDuplicates(albums, FederatedLibraryOrdering.AlbumDuplicateKey);
            files = FilterCompleteDuplicates(files, FederatedLibraryOrdering.TrackDuplicateKey);
        }

        albums.Sort(SearchComparer<MediaLibraryAlbum>(normalizedKeyword, album => album.Title));
        files.Sort(SearchComparer<DbMediaFile>(normalizedKeyword, file => file.Title));
        var artists = successful
            .SelectMany(response => response.Result!.Artists.Select(value =>
                new RankedSourceItem<MediaLibraryArtist>(response.Source.Source.Id, response.Source.Priority, value)))
            .GroupBy(item => FederatedLibraryOrdering.NormalizeArtistName(item.Value.Name), StringComparer.OrdinalIgnoreCase)
            .Select(MergeSearchArtist)
            .OrderByDescending(reference => Similarity(reference.DisplayName.ToLowerInvariant(), normalizedKeyword))
            .ThenBy(reference => reference.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new UnifiedLibrarySearchResults(
            albums.Select(item => new Sourced<MediaLibraryAlbum>(item.SourceId, item.Value)).ToArray(),
            artists,
            files.Select(item => new Sourced<DbMediaFile>(item.SourceId, item.Value)).ToArray(),
            failures);
    }

    public async Task<Sourced<MediaLibraryAlbum>> GetAlbumAsync(
        LibrarySourceId sourceId,
        string? albumArtist,
        string? albumName,
        CancellationToken cancellationToken = default,
        string? albumGroupingKey = null)
    {
        var services = await ResolveSourceAsync(sourceId, cancellationToken).ConfigureAwait(false);
        var album = await services.LibraryQuery.GetAlbumByNameAsync(albumArtist, albumName, albumGroupingKey)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Sourced<MediaLibraryAlbum>(sourceId, album);
    }

    public async Task<IReadOnlyList<Sourced<DbMediaFile>>> GetAlbumTracksAsync(
        LibrarySourceId sourceId,
        string? albumName,
        string? artistName,
        CancellationToken cancellationToken = default,
        string? albumGroupingKey = null)
    {
        var services = await ResolveSourceAsync(sourceId, cancellationToken).ConfigureAwait(false);
        var tracks = await services.LibraryQuery
            .GetMediaFilesByAlbumAsync(albumName, artistName, albumGroupingKey)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return tracks.Select(track => new Sourced<DbMediaFile>(sourceId, track)).ToArray();
    }

    public async Task<Sourced<MediaLibraryAlbum>> GetAlbumByTrackAsync(
        MediaReference media,
        CancellationToken cancellationToken = default)
    {
        var services = await ResolveSourceAsync(media.SourceId, cancellationToken).ConfigureAwait(false);
        var album = await services.LibraryQuery.GetAlbumByTrackPathAsync(media.Path)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Sourced<MediaLibraryAlbum>(media.SourceId, album);
    }

    public async Task<UnifiedArtistReference> GetArtistByTrackAsync(
        MediaReference media,
        CancellationToken cancellationToken = default)
    {
        var services = await ResolveSourceAsync(media.SourceId, cancellationToken).ConfigureAwait(false);
        var artist = await services.LibraryQuery.GetArtistByTrackPathAsync(media.Path)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var name = artist.Name ?? string.Empty;
        return new UnifiedArtistReference(
            FederatedLibraryOrdering.NormalizeArtistName(name),
            name,
            [media.SourceId],
            new Dictionary<LibrarySourceId, string> { [media.SourceId] = name });
    }

    public async Task<UnifiedCollection<Sourced<DbMediaFile>>> GetArtistTracksAsync(
        UnifiedArtistReference artist,
        CancellationToken cancellationToken = default)
    {
        var responses = await Task.WhenAll(artist.SourceIds.Select(async (sourceId, priority) =>
        {
            try
            {
                var services = await ResolveSourceAsync(sourceId, cancellationToken).ConfigureAwait(false);
                var sourceName = artist.SourceNames.GetValueOrDefault(sourceId, artist.DisplayName);
                var sourceArtist = await services.LibraryQuery.GetArtistByNameAsync(sourceName)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                var values = await services.LibraryQuery.GetMediaFilesByArtistAsync(sourceArtist)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                return new DetailResponse<DbMediaFile>(sourceId, priority, values, null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new DetailResponse<DbMediaFile>(
                    sourceId,
                    priority,
                    [],
                    Failure(sourceId, exception));
            }
        })).ConfigureAwait(false);
        var values = responses.SelectMany(response => response.Values.Select(value =>
            new RankedSourceItem<DbMediaFile>(response.SourceId, response.Priority, value))).ToList();
        if (visibilitySettings().PreferHigherPriorityDuplicates)
        {
            values = FilterCompleteDuplicates(values, FederatedLibraryOrdering.TrackDuplicateKey);
        }

        values.Sort(FederatedLibraryOrdering.Tracks(new MediaFilePageRequest(MediaFileSortField.AlbumName)));
        return new UnifiedCollection<Sourced<DbMediaFile>>(
            values.Select(item => new Sourced<DbMediaFile>(item.SourceId, item.Value)).ToArray(),
            responses.Where(response => response.Failure is not null).Select(response => response.Failure!).ToArray());
    }

    public async Task<UnifiedCollection<Sourced<MediaLibraryAlbum>>> GetArtistAlbumsAsync(
        UnifiedArtistReference artist,
        CancellationToken cancellationToken = default)
    {
        var responses = await Task.WhenAll(artist.SourceIds.Select(async (sourceId, priority) =>
        {
            try
            {
                var services = await ResolveSourceAsync(sourceId, cancellationToken).ConfigureAwait(false);
                var sourceName = artist.SourceNames.GetValueOrDefault(sourceId, artist.DisplayName);
                var values = await services.LibraryQuery.GetAlbumsAsync(sourceName)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                return new DetailResponse<MediaLibraryAlbum>(sourceId, priority, values, null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new DetailResponse<MediaLibraryAlbum>(
                    sourceId,
                    priority,
                    [],
                    Failure(sourceId, exception));
            }
        })).ConfigureAwait(false);
        var values = responses.SelectMany(response => response.Values.Select(value =>
            new RankedSourceItem<MediaLibraryAlbum>(response.SourceId, response.Priority, value))).ToList();
        if (visibilitySettings().PreferHigherPriorityDuplicates)
        {
            values = FilterCompleteDuplicates(values, FederatedLibraryOrdering.AlbumDuplicateKey);
        }

        values.Sort(FederatedLibraryOrdering.Albums(new AlbumPageRequest()));
        return new UnifiedCollection<Sourced<MediaLibraryAlbum>>(
            values.Select(item => new Sourced<MediaLibraryAlbum>(item.SourceId, item.Value)).ToArray(),
            responses.Where(response => response.Failure is not null).Select(response => response.Failure!).ToArray());
    }

    private async Task<LibrarySourceServices> ResolveSourceAsync(
        LibrarySourceId sourceId,
        CancellationToken cancellationToken)
    {
        if (!registry.TryGetSource(sourceId, out var source))
        {
            throw new KeyNotFoundException($"Library source '{sourceId}' is no longer configured.");
        }

        return await source.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SearchResponse> SearchSourceAsync(
        LibrarySourceSnapshotItem source,
        string keyword,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(QueryDeadline);
        try
        {
            var services = await source.Source.EnsureConnectedAsync(deadline.Token).ConfigureAwait(false);
            var result = await services.LibraryQuery.SearchAsync(keyword).WaitAsync(deadline.Token).ConfigureAwait(false);
            return new SearchResponse(source, result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SearchResponse(source, null, Failure(source.Source.Id, exception));
        }
    }

    private UnifiedSourceFailure Failure(LibrarySourceId sourceId, Exception exception)
    {
        registry.TryGetSource(sourceId, out var source);
        AppLog.Warning(
            "Library.Query",
            $"A query against library source {source?.DisplayName ?? sourceId.Value} failed.",
            exception);
        var retryable = source?.Status is not
            (LibrarySourceStatus.SignInRequired or LibrarySourceStatus.Incompatible);
        return new UnifiedSourceFailure(sourceId, source?.DisplayName ?? sourceId.Value, exception.Message, retryable);
    }

    private static UnifiedArtistBrowseItem MergeArtist(
        IReadOnlyList<RankedSourceItem<MediaLibraryArtistBrowseItem>> group)
    {
        var ordered = group.OrderBy(item => item.Priority).ThenBy(item => item.SourceId.Value, StringComparer.Ordinal).ToArray();
        var first = ordered[0];
        var names = ordered
            .GroupBy(item => item.SourceId)
            .ToDictionary(values => values.Key, values => values.First().Value.Artist.Name ?? string.Empty);
        var sourceIds = ordered.Select(item => item.SourceId).Distinct().ToArray();
        var artwork = ordered.SelectMany(item => item.Value.ArtworkPaths
                .Where(path => !string.IsNullOrEmpty(path))
                .Select(path => new Sourced<string>(item.SourceId, path)))
            .ToArray();
        return new UnifiedArtistBrowseItem(
            new UnifiedArtistReference(
                FederatedLibraryOrdering.NormalizeArtistName(first.Value.Artist.Name),
                first.Value.Artist.Name ?? string.Empty,
                sourceIds,
                names),
            ordered.Sum(item => item.Value.Artist.FileCount),
            ordered.Sum(item => item.Value.Artist.AlbumCount),
            ordered.Min(item => item.Value.Artist.DatabaseItemAddedDate),
            artwork);
    }

    private static UnifiedArtistReference MergeSearchArtist(
        IGrouping<string, RankedSourceItem<MediaLibraryArtist>> group)
    {
        var ordered = group.OrderBy(item => item.Priority).ThenBy(item => item.SourceId.Value, StringComparer.Ordinal).ToArray();
        var names = ordered.GroupBy(item => item.SourceId)
            .ToDictionary(values => values.Key, values => values.First().Value.Name ?? string.Empty);
        return new UnifiedArtistReference(
            group.Key,
            ordered[0].Value.Name ?? string.Empty,
            ordered.Select(item => item.SourceId).Distinct().ToArray(),
            names);
    }

    private static List<RankedSourceItem<T>> FilterCompleteDuplicates<T>(
        List<RankedSourceItem<T>> items,
        Func<T, string?> keySelector)
    {
        var owners = items.Where(item => keySelector(item.Value) is not null)
            .GroupBy(item => keySelector(item.Value)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.Priority)
                    .ThenBy(item => item.SourceId.Value, StringComparer.Ordinal)
                    .First().SourceId,
                StringComparer.OrdinalIgnoreCase);
        return items.Where(item => keySelector(item.Value) is not { } key || owners[key] == item.SourceId).ToList();
    }

    private static IComparer<RankedSourceItem<T>> SearchComparer<T>(
        string keyword,
        Func<T, string?> value) =>
        Comparer<RankedSourceItem<T>>.Create((first, second) =>
        {
            var result = Similarity(value(second.Value)?.ToLowerInvariant() ?? string.Empty, keyword)
                .CompareTo(Similarity(value(first.Value)?.ToLowerInvariant() ?? string.Empty, keyword));
            if (result == 0)
            {
                result = first.Priority.CompareTo(second.Priority);
            }

            if (result == 0)
            {
                result = string.Compare(first.SourceId.Value, second.SourceId.Value, StringComparison.Ordinal);
            }

            return result;
        });

    private static double Similarity(string first, string second)
    {
        if (first.Length == 0 && second.Length == 0)
        {
            return 1;
        }

        var previous = new int[second.Length + 1];
        var current = new int[second.Length + 1];
        for (var index = 0; index <= second.Length; index++)
        {
            previous[index] = index;
        }

        for (var row = 1; row <= first.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= second.Length; column++)
            {
                current[column] = Math.Min(
                    Math.Min(previous[column] + 1, current[column - 1] + 1),
                    previous[column - 1] + (first[row - 1] == second[column - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return 1 - previous[second.Length] / (double)Math.Max(first.Length + 1, second.Length + 1);
    }

    private sealed record SearchResponse(
        LibrarySourceSnapshotItem Source,
        LibrarySearchResults? Result,
        UnifiedSourceFailure? Failure);

    private sealed record DetailResponse<T>(
        LibrarySourceId SourceId,
        int Priority,
        IReadOnlyList<T> Values,
        UnifiedSourceFailure? Failure);
}

public sealed partial class UnifiedVideoLibraryQueryService : IUnifiedVideoLibraryQueryService
{
    private static readonly TimeSpan QueryDeadline = TimeSpan.FromSeconds(8);
    private readonly ILibrarySourceRegistry registry;
    private readonly UnifiedQuerySessionStore sessions = new();

    /// <summary>Stands in for a source that cannot be browsed, keyed so its tree is built once.</summary>
    private readonly ConcurrentDictionary<LibrarySourceId, IVideoFolderBrowser> indexedBrowsers = new();

    public UnifiedVideoLibraryQueryService(ILibrarySourceRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public Task<UnifiedPage<Sourced<IndexedMediaFile>>> GetMediaItemsPageAsync(
        PageRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        var fingerprint = $"media:{request.PageSize}";
        return sessions.ReadAsync(request.Cursor, fingerprint,
            id => new MergeSession<IndexedMediaFile, Sourced<IndexedMediaFile>>(
                id, fingerprint, registry, registry.CreateSnapshot(LibraryMediaCategory.Video), request.PageSize,
                FederatedLibraryOrdering.Media(),
                (services, _, cursor, size, token) => LibraryBrowseIndexes.GetMediaItemsPageAsync(
                    services.VideoLibraryQuery, request with { PageSize = size, Cursor = cursor }, token),
                group => new Sourced<IndexedMediaFile>(group[0].SourceId, group[0].Value),
                item => $"{(item.Image is not null ? 1 : 0)}\u001f{item.Image?.Id ?? item.Video!.Id}\u001f{item.Path}",
                item => new UnifiedVisibilityRemoval("media", item.SourceId, item.Value.Path)), cancellationToken);
    }

    public async Task<IReadOnlyList<Sourced<DbImageFile>>> GetImagesInFolderAsync(
        LibrarySourceId sourceId, string folderPath, CancellationToken cancellationToken = default)
    {
        var services = await ResolveSourceAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (services.VideoLibraryQuery is not IMediaLibraryQueryService media)
        {
            return [];
        }

        var values = await media.GetImagesInFolderAsync(folderPath, cancellationToken).ConfigureAwait(false);
        return values.Select(value => new Sourced<DbImageFile>(sourceId, value)).ToArray();
    }

    public Task<UnifiedPage<Sourced<DbVideoFile>>> GetVideosPageAsync(
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var fingerprint = $"videos:{request.PageSize}";
        return sessions.ReadAsync(
            request.Cursor,
            fingerprint,
            id => new MergeSession<DbVideoFile, Sourced<DbVideoFile>>(
                id,
                fingerprint,
                registry,
                registry.CreateSnapshot(LibraryMediaCategory.Video),
                request.PageSize,
                FederatedLibraryOrdering.Videos(),
                (services, _, cursor, size, token) => services.VideoLibraryQuery.GetVideosPageAsync(
                    request with { PageSize = size, Cursor = cursor },
                    token),
                group => new Sourced<DbVideoFile>(group[0].SourceId, group[0].Value),
                FederatedLibraryOrdering.VideoIdentity,
                item => new UnifiedVisibilityRemoval("video", item.SourceId, item.Value.Path ?? string.Empty)),
            cancellationToken);
    }

    public async Task<IReadOnlyList<Sourced<DbVideoFile>>> GetVideosInFolderAsync(
        LibrarySourceId sourceId,
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var services = await ResolveSourceAsync(sourceId, cancellationToken).ConfigureAwait(false);
        var values = await services.VideoLibraryQuery.GetVideosInFolderAsync(folderPath, cancellationToken)
            .ConfigureAwait(false);
        return values.Select(value => new Sourced<DbVideoFile>(sourceId, value)).ToArray();
    }

    /// <summary>
    /// Asks every source for its own picks and keeps the best <paramref name="limit"/> of them: the
    /// newest across all sources, or an arbitrary sample drawn evenly from what each returned.
    /// </summary>
    public async Task<UnifiedCollection<Sourced<DbVideoFile>>> GetVideoPicksAsync(
        VideoPickOrder order,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return new UnifiedCollection<Sourced<DbVideoFile>>([], []);
        }

        var snapshot = registry.CreateSnapshot(LibraryMediaCategory.Video);
        var responses = await Task.WhenAll(snapshot.Sources.Select(async item =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(QueryDeadline);
            try
            {
                var services = await item.Source.EnsureConnectedAsync(deadline.Token).ConfigureAwait(false);
                var values = await services.VideoLibraryQuery
                    .GetVideoPicksAsync(order, limit, deadline.Token).ConfigureAwait(false);
                return (Item: item, Values: values, Failure: (UnifiedSourceFailure?)null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return (
                    Item: item,
                    Values: (IReadOnlyList<DbVideoFile>)[],
                    Failure: CreateFailure(item.Source, exception));
            }
        })).ConfigureAwait(false);

        var picks = responses
            .SelectMany(response => response.Values.Select(value =>
                new Sourced<DbVideoFile>(response.Item.Source.Id, value)))
            .ToList();
        if (order == VideoPickOrder.RecentlyAdded)
        {
            picks.Sort((first, second) =>
                second.Value.DatabaseItemAddedDate.CompareTo(first.Value.DatabaseItemAddedDate));
        }
        else
        {
            Shuffle(picks);
        }

        return new UnifiedCollection<Sourced<DbVideoFile>>(
            picks.Take(limit).ToArray(),
            responses.Where(response => response.Failure is not null).Select(response => response.Failure!).ToArray());
    }

    private static void Shuffle<T>(IList<T> values)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var swap = Random.Shared.Next(i + 1);
            (values[i], values[swap]) = (values[swap], values[i]);
        }
    }

    public async Task<VideoFolderListing> BrowseFolderAsync(
        LibrarySourceId sourceId,
        string? folderPath,
        CancellationToken cancellationToken = default)
    {
        var services = await ResolveSourceAsync(sourceId, cancellationToken).ConfigureAwait(false);
        var browser = services.VideoFolders ?? indexedBrowsers.GetOrAdd(
            sourceId,
            _ => new IndexedVideoFolderBrowser(services.VideoLibraryQuery));
        return await browser.ListAsync(folderPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches every configured video source at once. Unlike the paged browse there is no cursor to
    /// merge across, so the results are simply concatenated and put back into library order.
    /// </summary>
    public async Task<UnifiedCollection<Sourced<DbVideoFile>>> SearchVideosAsync(
        string keyword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return new UnifiedCollection<Sourced<DbVideoFile>>([], []);
        }

        var snapshot = registry.CreateSnapshot(LibraryMediaCategory.Video);
        var responses = await Task.WhenAll(snapshot.Sources.Select(async item =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(QueryDeadline);
            try
            {
                var services = await item.Source.EnsureConnectedAsync(deadline.Token).ConfigureAwait(false);
                var values = await services.VideoLibraryQuery
                    .SearchVideosAsync(keyword, deadline.Token).ConfigureAwait(false);
                return (Item: item, Values: values, Failure: (UnifiedSourceFailure?)null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return (
                    Item: item,
                    Values: (IReadOnlyList<DbVideoFile>)[],
                    Failure: CreateFailure(item.Source, exception));
            }
        })).ConfigureAwait(false);

        var values = responses.SelectMany(response => response.Values.Select(value =>
            new RankedSourceItem<DbVideoFile>(response.Item.Source.Id, response.Item.Priority, value))).ToList();
        values.Sort(FederatedLibraryOrdering.Videos());
        return new UnifiedCollection<Sourced<DbVideoFile>>(
            values.Select(item => new Sourced<DbVideoFile>(item.SourceId, item.Value)).ToArray(),
            responses.Where(response => response.Failure is not null).Select(response => response.Failure!).ToArray());
    }

    public async Task<Sourced<DbVideoFile>?> GetVideoByPathAsync(
        MediaReference media,
        CancellationToken cancellationToken = default)
    {
        var services = await ResolveSourceAsync(media.SourceId, cancellationToken).ConfigureAwait(false);
        var value = await services.VideoLibraryQuery.GetVideoByPathAsync(media.Path)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : new Sourced<DbVideoFile>(media.SourceId, value);
    }

    public async Task<byte[]?> GetVideoPreviewAsync(
        MediaReference media,
        CancellationToken cancellationToken = default)
    {
        var services = await ResolveSourceAsync(media.SourceId, cancellationToken).ConfigureAwait(false);
        return await services.VideoLibraryQuery
            .GetVideoPreviewAsync(media.Path, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<LibrarySourceServices> ResolveSourceAsync(
        LibrarySourceId sourceId,
        CancellationToken cancellationToken)
    {
        if (!registry.TryGetSource(sourceId, out var source))
        {
            throw new KeyNotFoundException($"Library source '{sourceId}' is no longer configured.");
        }

        return await source.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    private static UnifiedSourceFailure CreateFailure(ILibrarySource source, Exception exception)
    {
        AppLog.Warning("Library.Query", $"A query against library source {source.DisplayName} failed.", exception);
        return new(
            source.Id,
            source.DisplayName,
            exception.Message,
            source.Status is not (LibrarySourceStatus.SignInRequired or LibrarySourceStatus.Incompatible));
    }
}
