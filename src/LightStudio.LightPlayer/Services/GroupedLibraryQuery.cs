using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Strings;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Services;

public sealed record LibraryBrowseGroup(string Key, string Title, int Count);

public sealed class GroupedLibraryQuery<T>(
    IReadOnlyList<LibraryBrowseGroup> groups,
    Func<int, int, CancellationToken, Task<T[]>> read,
    IReadOnlyList<UnifiedSourceFailure>? failures = null)
{
    public IReadOnlyList<LibraryBrowseGroup> Groups { get; } = groups;

    public IReadOnlyList<UnifiedSourceFailure> SourceFailures { get; } = failures ?? [];

    public Task<T[]> ReadAsync(int offset, int count, CancellationToken cancellationToken) =>
        read(offset, count, cancellationToken);

    public GroupedLibraryQuery<TOutput> Select<TOutput>(Func<T, TOutput> project) => new(Groups,
        async (offset, count, token) =>
            (await ReadAsync(offset, count, token).ConfigureAwait(false)).Select(project).ToArray(),
        SourceFailures);

    public GroupedLibraryQuery<T> PrependGroup(string title, IReadOnlyList<T> items) => items.Count == 0 ? this : new(
        new[] { new LibraryBrowseGroup("\u0001", title, items.Count) }.Concat(Groups).ToArray(),
        async (offset, count, token) =>
        {
            var prefix = items.Skip(offset).Take(count).ToArray();
            var remaining = count - prefix.Length;
            return remaining == 0 ? prefix : prefix.Concat(await ReadAsync(Math.Max(0, offset - items.Count),
                remaining, token).ConfigureAwait(false)).ToArray();
        }, SourceFailures);
}

internal static class GroupedLibraryQueries
{
    public static async Task<GroupedLibraryQuery<TResult>> CreateAsync<TSource, TResult>(
        ILibrarySourceRegistry registry,
        LibraryMediaCategory category,
        LibraryItemKind kind,
        LibraryViewSettings settings,
        Func<LibrarySourceServices, ILibraryBrowseIndexQuery<TSource>?> getQuery,
        Func<LibrarySourceServices, ILibraryBrowseIndexQuery<TSource>> fallback,
        Func<IReadOnlyList<RankedSourceItem<TSource>>, TResult> project,
        bool suppressDuplicates,
        CancellationToken cancellationToken,
        LibrarySourceId? sourceId = null,
        string? folderPath = null)
    {
        var culture = CultureInfo.CurrentCulture;
        var snapshot = registry.CreateSnapshot(category);
        var responses = await Task.WhenAll(snapshot.Sources
            .Where(source => sourceId is null || source.Source.Id == sourceId)
            .Select(async source =>
            {
                try
                {
                    var services = await source.Source.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                    var query = getQuery(services);
                    LibraryBrowseIndex? index = null;
                    if (query is not null)
                    {
                        try
                        {
                            index = await query.GetBrowseIndexAsync(new(folderPath), cancellationToken).ConfigureAwait(false);
                        }
                        catch (NotSupportedException)
                        {
                        }
                    }
                    if (index is null)
                    {
                        query = fallback(services);
                        index = await query.GetBrowseIndexAsync(new(folderPath), cancellationToken).ConfigureAwait(false);
                    }
                    return new SourceIndex<TSource>(source, query!, index, null);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Warning("Library.Grouping", $"The browse index for {source.Source.DisplayName} failed.", exception);
                    return new SourceIndex<TSource>(source, null, null,
                        new UnifiedSourceFailure(source.Source.Id, source.Source.DisplayName, exception.Message, true));
                }
            })).ConfigureAwait(false);

        var groups = await Task.Run(() =>
        {
            var items = new List<LogicalItem<TSource>>();
            var identities = new Dictionary<string, LogicalItem<TSource>>(StringComparer.OrdinalIgnoreCase);
            foreach (var response in responses.OrderBy(response => response.Source.Priority))
            {
                if (response.Index is not { } index)
                    continue;
                for (var position = 0; position < index.Items.Length; position++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = index.Items[position];
                    var reference = new IndexReference<TSource>(response, position);
                    if (entry.DuplicateKey is { } key && identities.TryGetValue(key, out var owner))
                    {
                        if (kind == LibraryItemKind.Artist)
                        {
                            owner.References.Add(reference);
                            continue;
                        }
                        if (suppressDuplicates && owner.References[0].Source != response)
                            continue;
                    }
                    var item = new LogicalItem<TSource>(entry, [reference]);
                    items.Add(item);
                    if (entry.DuplicateKey is { } identity)
                        identities.TryAdd(identity, item);
                }
            }
            return Partition(items, item => item.Entry, settings, kind, culture);
        }, cancellationToken).ConfigureAwait(false);

        var orderedItems = groups.SelectMany(group => group.Items).ToArray();
        return new GroupedLibraryQuery<TResult>(
            groups.Select(group => new LibraryBrowseGroup(group.Key, group.Title, group.Items.Length)).ToArray(),
            async (offset, count, token) =>
            {
                if (registry.DefinitionsGeneration != snapshot.DefinitionsGeneration ||
                    registry.PriorityGeneration != snapshot.PriorityGeneration ||
                    registry.CatalogGeneration != snapshot.CatalogGeneration)
                {
                    throw new UnifiedQueryInvalidatedException("The grouped library snapshot has changed.");
                }
                var selected = orderedItems.Skip(offset).Take(count).ToArray();
                var batches = await Task.WhenAll(selected.SelectMany(item => item.References)
                    .GroupBy(reference => reference.Source)
                    .SelectMany(source => source.Chunk(MediaFilePageRequest.MaxPageSize))
                    .Select(async batch =>
                    {
                        var source = batch[0].Source;
                        var values = await source.Query!.GetBrowseItemsAsync(
                            new LibraryBrowseItemsRequest(source.Index!.Id, batch.Select(item => item.Position).ToArray()),
                            token).ConfigureAwait(false);
                        if (values.Length != batch.Length)
                            throw new InvalidOperationException("The browse index returned an incomplete item batch.");
                        return (References: batch, Values: values);
                    })).ConfigureAwait(false);
                var fetched = new Dictionary<IndexReference<TSource>, TSource>();
                foreach (var batch in batches)
                {
                    for (var position = 0; position < batch.Values.Length; position++)
                        fetched[batch.References[position]] = batch.Values[position];
                }
                return selected.Select(item => project(item.References.Select(reference =>
                    new RankedSourceItem<TSource>(reference.Source.Source.Source.Id,
                        reference.Source.Source.Priority, fetched[reference])).ToArray())).ToArray();
            },
            responses.Where(response => response.Failure is not null).Select(response => response.Failure!).ToArray());
    }

    public static GroupedLibraryQuery<T> FromItems<T>(IReadOnlyList<T> items,
        Func<T, LibraryBrowseIndexEntry> describe, LibraryViewSettings settings, LibraryItemKind kind) =>
        FromItems(items, describe, settings, kind, out _);

    public static GroupedLibraryQuery<T> FromItems<T>(IReadOnlyList<T> items,
        Func<T, LibraryBrowseIndexEntry> describe, LibraryViewSettings settings, LibraryItemKind kind,
        out T[] ordered)
    {
        var groups = Partition(items, describe, settings, kind, CultureInfo.CurrentCulture);
        var orderedItems = groups.SelectMany(group => group.Items).ToArray();
        ordered = orderedItems;
        return new GroupedLibraryQuery<T>(
            groups.Select(group => new LibraryBrowseGroup(group.Key, group.Title, group.Items.Length)).ToArray(),
            (offset, count, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(orderedItems.Skip(offset).Take(count).ToArray());
            });
    }

    private static IndexGroup<T>[] Partition<T>(IEnumerable<T> items, Func<T, LibraryBrowseIndexEntry> describe,
        LibraryViewSettings settings, LibraryItemKind kind, CultureInfo culture)
    {
        var simpleGrouping = PlatformServices.UseSimpleLibraryGrouping;
        var alphabet = new LocalizedGroupIndex(culture, simpleGrouping);
        var textComparer = simpleGrouping ? StringComparer.OrdinalIgnoreCase : StringComparer.Create(culture, true);
        var keyComparer = IsLocationGroup(settings.GroupBy) ? new LocationKeyComparer(textComparer) : textComparer;
        var groupComparer = EqualityComparer<(bool IsFolder, string Key)>.Create(
            (first, second) => first.IsFolder == second.IsFolder && keyComparer.Equals(first.Key, second.Key),
            key => HashCode.Combine(key.IsFolder, keyComparer.GetHashCode(key.Key)));
        var grouped = new Dictionary<(bool IsFolder, string Key), (string Title, List<IndexedItem<T>> Items)>(groupComparer);
        var ordinal = 0;
        foreach (var item in items)
        {
            var entry = describe(item);
            var (key, title) = GroupName(entry, settings.GroupBy, alphabet, culture);
            var identity = (entry.IsFolder, key);
            if (!grouped.TryGetValue(identity, out var group))
            {
                group = (title, []);
                grouped.Add(identity, group);
            }
            group.Items.Add(new IndexedItem<T>(item, entry, ordinal++));
        }
        var sorted = grouped.OrderBy(group => !group.Key.IsFolder)
            .ThenBy(group => group.Key.Key.Length == 0 || group.Key.Key == "#")
            .ThenBy(group => group.Key.Key, Comparer<string>.Create((first, second) =>
            {
                var result = IsDateGroup(settings.GroupBy)
                    ? StringComparer.Ordinal.Compare(first, second) : keyComparer.Compare(first, second);
                return IsDateGroup(settings.GroupBy) && !settings.Ascending ? -result : result;
            }));
        return sorted.Select(group =>
        {
            group.Value.Items.Sort((first, second) =>
            {
                var result = CompareItems(first.Entry, second.Entry, settings, kind, textComparer);
                return result != 0 ? result : first.Ordinal.CompareTo(second.Ordinal);
            });
            return new IndexGroup<T>(group.Key.Key, group.Value.Title, group.Value.Items.Select(item => item.Item).ToArray());
        }).ToArray();
    }

    private static bool IsDateGroup(LibraryGroupField field) =>
        field is LibraryGroupField.CreationDay or LibraryGroupField.CreationMonth or LibraryGroupField.CreationYear;

    private static bool IsLocationGroup(LibraryGroupField field) =>
        field is LibraryGroupField.Country or LibraryGroupField.State or LibraryGroupField.City;

    private static (string Key, string Title) GroupName(LibraryBrowseIndexEntry entry, LibraryGroupField field,
        LocalizedGroupIndex alphabet, CultureInfo culture)
    {
        if (entry.IsFolder)
            return ("\u0001", LocalizedStrings.Instance["Shell_GroupFolders"]);
        if (IsLocationGroup(field))
            return LocationGroupName(entry, field, culture);
        if (IsDateGroup(field))
        {
            if ((entry.DateTaken?.DateTime ?? entry.CreatedUtc?.LocalDateTime) is not { } date)
                return (string.Empty, LocalizedStrings.Instance["Shell_GroupUnknownDate"]);
            return field switch
            {
                LibraryGroupField.CreationDay =>
                    (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), date.ToString("d", culture)),
                LibraryGroupField.CreationYear =>
                    (date.ToString("yyyy", CultureInfo.InvariantCulture), date.ToString("yyyy", culture)),
                _ => (date.ToString("yyyy-MM", CultureInfo.InvariantCulture), date.ToString("MMM yyyy", culture)),
            };
        }
        if (field == LibraryGroupField.Name)
        {
            var initial = alphabet.Initial(entry.Name);
            return (initial, initial);
        }
        var value = (field == LibraryGroupField.Artist ? entry.Artist : entry.Album)?.Trim();
        return string.IsNullOrEmpty(value)
            ? (string.Empty, LocalizedStrings.Instance[field == LibraryGroupField.Artist
                ? "Common_UnknownArtist" : "Common_UnknownAlbum"])
            : (alphabet.NormalizeKey(value), value);
    }

    /// <summary>
    /// Groups by country, then state, then city. Keys list the parts from broadest to most specific,
    /// and titles combine them from most specific to broadest in the interface language's order.
    /// </summary>
    private static (string Key, string Title) LocationGroupName(LibraryBrowseIndexEntry entry, LibraryGroupField field,
        CultureInfo culture)
    {
        var country = entry.Country?.Trim();
        if (string.IsNullOrEmpty(country))
            return (string.Empty, LocalizedStrings.Instance["Shell_GroupUnknownLocation"]);
        string[] parts = field switch
        {
            LibraryGroupField.Country => [country],
            LibraryGroupField.State => [country, entry.State?.Trim() ?? string.Empty],
            _ => [country, entry.State?.Trim() ?? string.Empty, entry.City?.Trim() ?? string.Empty],
        };
        var title = country;
        var previous = country;
        foreach (var part in parts.AsSpan(1))
        {
            if (part.Length == 0 || string.Equals(part, previous, StringComparison.CurrentCultureIgnoreCase))
                continue;
            title = string.Format(culture, LocalizedStrings.Instance["Shell_GroupLocationFormat"], part, title);
            previous = part;
        }
        return (string.Join(LocationKeyComparer.Separator, parts), title);
    }

    private static int CompareItems(LibraryBrowseIndexEntry first, LibraryBrowseIndexEntry second,
        LibraryViewSettings settings, LibraryItemKind kind, StringComparer cultureComparer)
    {
        var comparer = kind == LibraryItemKind.Video ? cultureComparer : StringComparer.OrdinalIgnoreCase;
        var result = settings.Sort switch
        {
            LibrarySortField.AlbumName => comparer.Compare(first.Album ?? first.Name, second.Album ?? second.Name),
            LibrarySortField.ArtistName => comparer.Compare(first.Artist ?? first.Name, second.Artist ?? second.Name),
            LibrarySortField.Genre => comparer.Compare(first.Genre, second.Genre),
            LibrarySortField.Year => first.Year.CompareTo(second.Year),
            LibrarySortField.DateAdded => first.DateAdded.CompareTo(second.DateAdded),
            LibrarySortField.Duration => first.Duration.CompareTo(second.Duration),
            LibrarySortField.FileType => comparer.Compare(first.FileType, second.FileType),
            LibrarySortField.FileSize => first.FileSize.CompareTo(second.FileSize),
            _ => comparer.Compare(first.Name, second.Name),
        };
        if (result != 0)
            return settings.Ascending ? result : -result;
        if (kind == LibraryItemKind.Song && settings.Sort == LibrarySortField.AlbumName)
        {
            result = first.Disc.CompareTo(second.Disc);
            if (result == 0)
                result = first.Track.CompareTo(second.Track);
        }
        if (result == 0)
            result = comparer.Compare(first.Name, second.Name);
        return kind == LibraryItemKind.Video && !settings.Ascending ? -result : result;
    }

    private sealed record SourceIndex<T>(LibrarySourceSnapshotItem Source, ILibraryBrowseIndexQuery<T>? Query,
        LibraryBrowseIndex? Index, UnifiedSourceFailure? Failure);

    private sealed record IndexReference<T>(SourceIndex<T> Source, int Position);

    private sealed record LogicalItem<T>(LibraryBrowseIndexEntry Entry, List<IndexReference<T>> References);

    private sealed record IndexedItem<T>(T Item, LibraryBrowseIndexEntry Entry, int Ordinal);

    private sealed record IndexGroup<T>(string Key, string Title, T[] Items);

    /// <summary>
    /// Compares location keys part by part. Culture-aware comparison ignores control characters, so
    /// comparing whole keys could merge different places.
    /// </summary>
    private sealed class LocationKeyComparer(StringComparer parts) : StringComparer
    {
        public const char Separator = '\u001f';

        public override int Compare(string? x, string? y)
        {
            if (x is null || y is null)
                return x is null ? (y is null ? 0 : -1) : 1;
            var first = x.Split(Separator);
            var second = y.Split(Separator);
            for (var index = 0; index < Math.Min(first.Length, second.Length); index++)
            {
                var result = parts.Compare(first[index], second[index]);
                if (result != 0)
                    return result;
            }
            return first.Length.CompareTo(second.Length);
        }

        public override bool Equals(string? x, string? y) => Compare(x, y) == 0;

        public override int GetHashCode(string obj)
        {
            var hash = new HashCode();
            foreach (var part in obj.Split(Separator))
                hash.Add(part, parts);
            return hash.ToHashCode();
        }
    }
}
