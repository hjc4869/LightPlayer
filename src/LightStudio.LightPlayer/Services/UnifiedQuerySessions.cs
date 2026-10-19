using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Services;

internal sealed class UnifiedQueryInvalidatedException(string message) : InvalidOperationException(message)
{
}

internal sealed class UnifiedQuerySessionStore
{
    private const int MaximumSessions = 64;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly Dictionary<string, IUnifiedQuerySession> sessions = new(StringComparer.Ordinal);

    public async Task<UnifiedPage<TOutput>> ReadAsync<TSource, TOutput>(
        string? cursor,
        string fingerprint,
        Func<string, MergeSession<TSource, TOutput>> create,
        CancellationToken cancellationToken)
    {
        MergeSession<TSource, TOutput> session;
        int? continuationSequence = null;
        if (string.IsNullOrEmpty(cursor))
        {
            var id = Guid.NewGuid().ToString("N");
            session = create(id);
            lock (gate)
            {
                PruneLocked();
                while (sessions.Count >= MaximumSessions)
                {
                    var oldest = sessions.Values.OrderBy(value => value.LastAccessUtc).First();
                    sessions.Remove(oldest.Id);
                    oldest.Dispose();
                }

                sessions.Add(id, session);
            }
        }
        else
        {
            var parsed = ParseCursor(cursor);
            continuationSequence = parsed.Sequence;
            lock (gate)
            {
                PruneLocked();
                if (!sessions.TryGetValue(parsed.Id, out var value) ||
                    value is not MergeSession<TSource, TOutput> typed ||
                    !string.Equals(value.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new UnifiedQueryInvalidatedException("The unified page cursor has expired.");
                }

                session = typed;
            }
        }

        var page = await session
            .ReadPageAsync(continuationSequence, cancellationToken)
            .ConfigureAwait(false);
        if (page.NextCursor is null)
        {
            lock (gate)
            {
                sessions.Remove(session.Id);
            }

            session.Dispose();
        }

        return page;
    }

    private void PruneLocked()
    {
        var threshold = DateTimeOffset.UtcNow - SessionLifetime;
        foreach (var expired in sessions.Values.Where(value => value.LastAccessUtc < threshold).ToArray())
        {
            sessions.Remove(expired.Id);
            expired.Dispose();
        }
    }

    private static (string Id, int Sequence) ParseCursor(string cursor)
    {
        var parts = cursor.Split(':');
        if (parts.Length != 3 || parts[0] != "f1" || parts[1].Length != 32 ||
            !int.TryParse(parts[2], out var sequence) || sequence < 1)
        {
            throw new ArgumentException("The unified page cursor is invalid.", nameof(cursor));
        }

        return (parts[1], sequence);
    }
}

internal interface IUnifiedQuerySession : IDisposable
{
    string Id { get; }

    string Fingerprint { get; }

    DateTimeOffset LastAccessUtc { get; }
}

internal sealed class MergeSession<TSource, TOutput> : IUnifiedQuerySession
{
    private const int MinimumFetchSize = 16;
    private const int MaximumFetchSize = 32;
    private static readonly TimeSpan BrowseDeadline = TimeSpan.FromSeconds(8);

    private readonly SemaphoreSlim consumer = new(1, 1);
    private readonly object fetchResultGate = new();
    private readonly ILibrarySourceRegistry registry;
    private readonly LibrarySourceSnapshot snapshot;
    private readonly List<SourceState> sources;
    private readonly IComparer<RankedSourceItem<TSource>> comparer;
    private readonly Func<LibrarySourceServices, ILibrarySource, string?, int, CancellationToken, Task<PagedResult<TSource>>> fetch;
    private readonly Func<IReadOnlyList<RankedSourceItem<TSource>>, TOutput> project;
    private readonly Func<TSource, string?>? groupKey;
    private readonly Func<TSource, string?>? duplicateKey;
    private readonly Func<TSource, string> identity;
    private readonly Func<RankedSourceItem<TSource>, UnifiedVisibilityRemoval> visibilityRemoval;
    private readonly int pageSize;
    private readonly bool suppressDuplicates;
    private readonly Dictionary<string, DuplicateOwner> duplicateOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmittedDuplicate> emittedDuplicates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<RankedSourceItem<TSource>>> emittedGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> emittedIdentities = new(StringComparer.Ordinal);
    private readonly List<UnifiedVisibilityRemoval> pendingRemovals = [];
    private int expectedSequence = 1;
    private int emittedCount;
    private int? totalCount;
    private bool totalCountIncomplete;
    private bool orderingIsProvisional;
    private bool forceRetryNextRead;
    private bool disposed;

    public MergeSession(
        string id,
        string fingerprint,
        ILibrarySourceRegistry registry,
        LibrarySourceSnapshot snapshot,
        int pageSize,
        IComparer<RankedSourceItem<TSource>> comparer,
        Func<LibrarySourceServices, ILibrarySource, string?, int, CancellationToken, Task<PagedResult<TSource>>> fetch,
        Func<IReadOnlyList<RankedSourceItem<TSource>>, TOutput> project,
        Func<TSource, string> identity,
        Func<RankedSourceItem<TSource>, UnifiedVisibilityRemoval> visibilityRemoval,
        bool suppressDuplicates = false,
        Func<TSource, string?>? duplicateKey = null,
        Func<TSource, string?>? groupKey = null)
    {
        Id = id;
        Fingerprint = fingerprint;
        this.registry = registry;
        this.snapshot = snapshot;
        this.pageSize = pageSize;
        this.comparer = comparer;
        this.fetch = fetch;
        this.project = project;
        this.identity = identity;
        this.visibilityRemoval = visibilityRemoval;
        this.suppressDuplicates = suppressDuplicates;
        this.duplicateKey = duplicateKey;
        this.groupKey = groupKey;
        sources = snapshot.Sources.Select(item => new SourceState(item.Source, item.Priority)).ToList();
        LastAccessUtc = DateTimeOffset.UtcNow;
    }

    public string Id { get; }

    public string Fingerprint { get; }

    public DateTimeOffset LastAccessUtc { get; private set; }

    public async Task<UnifiedPage<TOutput>> ReadPageAsync(
        int? continuationSequence,
        CancellationToken cancellationToken)
    {
        await consumer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfInvalidated();
            if (continuationSequence is { } sequence)
            {
                if (sequence != expectedSequence)
                {
                    throw new ArgumentException(
                        "The unified page cursor was already consumed.",
                        nameof(continuationSequence));
                }

                expectedSequence++;
            }

            LastAccessUtc = DateTimeOffset.UtcNow;
            var output = new List<TOutput>(pageSize);
            var forceRetry = forceRetryNextRead;
            forceRetryNextRead = false;
            while (output.Count < pageSize)
            {
                await EnsureHeadsAsync(cancellationToken, forceRetry).ConfigureAwait(false);
                forceRetry = false;
                var best = FindBestState();
                if (best is null)
                {
                    break;
                }

                var group = await ConsumeGroupAsync(best, cancellationToken).ConfigureAwait(false);
                if (group.Count == 0)
                {
                    continue;
                }

                if (groupKey is null && ShouldSuppress(group[0]))
                {
                    continue;
                }

                output.Add(project(group));
                emittedCount++;
                RegisterEmission(group);
            }

            var failures = sources
                .Where(source => source.Failure is not null)
                .Select(source => source.Failure!)
                .ToArray();
            var retryAfter = failures
                .Where(failure => failure.Retryable)
                .Select(failure => failure.RetryAfterUtc)
                .Where(value => value.HasValue)
                .Min();
            var hasPending = sources.Any(source => source.Buffer.Count > 0 || !source.Exhausted);
            forceRetryNextRead = output.Count == 0 && failures.Length > 0;
            var nextCursor = hasPending ? $"f1:{Id}:{expectedSequence}" : null;
            var removals = pendingRemovals.ToArray();
            pendingRemovals.Clear();
            return new UnifiedPage<TOutput>(
                output.ToArray(),
                nextCursor,
                totalCount,
                totalCountIncomplete || suppressDuplicates,
                orderingIsProvisional,
                failures,
                removals,
                retryAfter);
        }
        finally
        {
            consumer.Release();
        }
    }

    private async Task EnsureHeadsAsync(CancellationToken cancellationToken, bool forceRetry = false)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = sources
            .Where(source => source.Buffer.Count == 0 && !source.Exhausted && source.CanAttempt(now, forceRetry))
            .Select(source => FetchAsync(source, cancellationToken))
            .ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private SourceState? FindBestState()
    {
        SourceState? best = null;
        foreach (var source in sources)
        {
            if (source.Buffer.Count == 0)
            {
                continue;
            }

            if (best is null || comparer.Compare(source.Buffer.Peek(), best.Buffer.Peek()) < 0)
            {
                best = source;
            }
        }

        return best;
    }

    private async Task<List<RankedSourceItem<TSource>>> ConsumeGroupAsync(
        SourceState best,
        CancellationToken cancellationToken)
    {
        var first = best.Buffer.Dequeue();
        if (!emittedIdentities.Add(SourceIdentity(first)))
        {
            return [];
        }

        var result = new List<RankedSourceItem<TSource>> { first };
        if (groupKey?.Invoke(first.Value) is not { } key)
        {
            return result;
        }

        while (true)
        {
            await EnsureHeadsAsync(cancellationToken).ConfigureAwait(false);
            var added = false;
            foreach (var source in sources)
            {
                while (source.Buffer.Count > 0 &&
                    string.Equals(groupKey(source.Buffer.Peek().Value), key, StringComparison.OrdinalIgnoreCase))
                {
                    var item = source.Buffer.Dequeue();
                    if (emittedIdentities.Add(SourceIdentity(item)))
                    {
                        result.Add(item);
                    }

                    added = true;
                    if (source.Buffer.Count == 0 && !source.Exhausted && source.CanAttempt(DateTimeOffset.UtcNow))
                    {
                        await FetchAsync(source, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (!added)
            {
                break;
            }
        }

        if (emittedGroups.TryGetValue(key, out var previous))
        {
            pendingRemovals.Add(visibilityRemoval(previous[0]));
            result.InsertRange(0, previous);
        }

        return result;
    }

    private async Task FetchAsync(SourceState source, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(BrowseDeadline);
        try
        {
            var services = await source.Source.EnsureConnectedAsync(deadline.Token).ConfigureAwait(false);
            var page = await fetch(
                services,
                source.Source,
                source.Cursor,
                Math.Clamp(pageSize, MinimumFetchSize, MaximumFetchSize),
                deadline.Token).ConfigureAwait(false);
            lock (fetchResultGate)
            {
                if (source.HadFailure && emittedCount > 0)
                {
                    orderingIsProvisional = true;
                }

                source.Failure = null;
                source.RetryCount = 0;
                source.HadFailure = false;
                if (!source.CountReceived)
                {
                    source.CountReceived = true;
                    if (page.TotalCount.HasValue)
                    {
                        totalCount = checked((totalCount ?? 0) + page.TotalCount.Value);
                    }
                    else
                    {
                        totalCountIncomplete = true;
                    }
                }

                foreach (var value in page.Items)
                {
                    var item = new RankedSourceItem<TSource>(source.Source.Id, source.Priority, value);
                    source.Buffer.Enqueue(item);
                    RegisterDuplicate(item);
                }

                source.Cursor = page.NextCursor;
                source.Exhausted = page.NextCursor is null;
                if (page.Items.Length == 0 && page.NextCursor is not null)
                {
                    source.Exhausted = true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Warning(
                "Library.Query",
                $"A paged query against library source {source.Source.DisplayName} failed.",
                exception);
            lock (fetchResultGate)
            {
                source.HadFailure = true;
                source.RetryCount++;
                var retryable = source.Source.Status is not
                    (LibrarySourceStatus.SignInRequired or LibrarySourceStatus.Incompatible);
                var retryAfter = retryable
                    ? DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(5, source.RetryCount)))
                    : (DateTimeOffset?)null;
                source.Failure = new UnifiedSourceFailure(
                    source.Source.Id,
                    source.Source.DisplayName,
                    exception.Message,
                    retryable,
                    retryAfter);
                totalCountIncomplete = true;
            }
        }
    }

    private bool ShouldSuppress(RankedSourceItem<TSource> item)
    {
        if (!suppressDuplicates || duplicateKey?.Invoke(item.Value) is not { } key ||
            !duplicateOwners.TryGetValue(key, out var owner))
        {
            return false;
        }

        return owner.SourceId != item.SourceId && owner.Priority < item.Priority;
    }

    private void RegisterDuplicate(RankedSourceItem<TSource> item)
    {
        if (!suppressDuplicates || duplicateKey?.Invoke(item.Value) is not { } key)
        {
            return;
        }

        if (!duplicateOwners.TryGetValue(key, out var owner) || item.Priority < owner.Priority)
        {
            duplicateOwners[key] = new DuplicateOwner(item.SourceId, item.Priority);
            if (emittedDuplicates.TryGetValue(key, out var emitted) &&
                emitted.SourceId != item.SourceId && item.Priority < emitted.Priority)
            {
                pendingRemovals.Add(emitted.Removal);
                emittedDuplicates.Remove(key);
            }
        }
    }

    private void RegisterEmission(IReadOnlyList<RankedSourceItem<TSource>> group)
    {
        var item = group[0];
        if (suppressDuplicates && duplicateKey?.Invoke(item.Value) is { } key)
        {
            emittedDuplicates[key] = new EmittedDuplicate(
                item.SourceId,
                item.Priority,
                visibilityRemoval(item));
        }

        if (groupKey?.Invoke(item.Value) is { } groupIdentity)
        {
            emittedGroups[groupIdentity] = group.ToList();
        }
    }

    private string SourceIdentity(RankedSourceItem<TSource> item) =>
        $"{item.SourceId.Value}\u001f{identity(item.Value)}";

    private void ThrowIfInvalidated()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (registry.DefinitionsGeneration != snapshot.DefinitionsGeneration ||
            registry.PriorityGeneration != snapshot.PriorityGeneration ||
            registry.CatalogGeneration != snapshot.CatalogGeneration)
        {
            throw new UnifiedQueryInvalidatedException("The unified page cursor was invalidated by a library change.");
        }
    }

    public void Dispose()
    {
        disposed = true;
        consumer.Dispose();
    }

    private sealed class SourceState(ILibrarySource source, int priority)
    {
        public ILibrarySource Source { get; } = source;

        public int Priority { get; } = priority;

        public Queue<RankedSourceItem<TSource>> Buffer { get; } = new();

        public string? Cursor { get; set; }

        public bool Exhausted { get; set; }

        public bool CountReceived { get; set; }

        public bool HadFailure { get; set; }

        public int RetryCount { get; set; }

        public UnifiedSourceFailure? Failure { get; set; }

        public bool CanAttempt(DateTimeOffset now, bool forceRetry = false) => Failure is null ||
            (forceRetry && Failure.Retryable) ||
            (Failure.Retryable && Failure.RetryAfterUtc <= now) ||
            (!Failure.Retryable && Source.Status == LibrarySourceStatus.Online);
    }

    private sealed record DuplicateOwner(LibrarySourceId SourceId, int Priority);

    private sealed record EmittedDuplicate(
        LibrarySourceId SourceId,
        int Priority,
        UnifiedVisibilityRemoval Removal);
}