using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Services;

/// <param name="TotalCount">The number of photos the search lists in total, or null while a source has yet to report it.</param>
public sealed record PhotoSearchBatch(IReadOnlyList<Sourced<PhotoSearchMatch>> Items, bool HasMore, string ErrorText, bool RestartRequired = false,
    int? TotalCount = null);

public sealed class PhotoSearchSession
{
    private readonly ILibrarySourceRegistry registry;
    private readonly LibrarySourceSnapshot snapshot;
    private readonly string query;
    private readonly SourceState[] sources;
    private bool namesLoaded;

    /// <param name="sourceIds">The sources to search, or null for every photo source.</param>
    public PhotoSearchSession(ILibrarySourceRegistry registry, string query,
        IReadOnlyCollection<LibrarySourceId>? sourceIds = null)
    {
        this.registry = registry;
        this.query = query;
        snapshot = registry.CreateSnapshot(LibraryMediaCategory.Video);
        sources = snapshot.Sources.Where(item => sourceIds is null || sourceIds.Contains(item.Source.Id))
            .OrderBy(item => item.Priority)
            .Select(item => new SourceState(item.Source, item.Priority)).ToArray();
    }

    public async Task<PhotoSearchBatch> ReadAsync(int count, CancellationToken cancellationToken = default)
    {
        if (snapshot.DefinitionsGeneration != registry.DefinitionsGeneration || snapshot.CatalogGeneration != registry.CatalogGeneration)
            return new PhotoSearchBatch([], false, "", RestartRequired: true);
        if (!namesLoaded)
        {
            await Task.WhenAll(sources.Select(source => LoadNamesAsync(source, cancellationToken))).ConfigureAwait(false);
            // The first semantic page of each source reports how many photos it matches.
            await Task.WhenAll(sources.Select(source => FillAsync(source, cancellationToken))).ConfigureAwait(false);
            namesLoaded = true;
        }
        var results = new List<Sourced<PhotoSearchMatch>>(count);
        foreach (var source in sources)
        {
            while (results.Count < count && source.NameItems.TryDequeue(out var item))
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new Sourced<PhotoSearchMatch>(source.Source.Id, item));
            }
        }
        if (results.Count < count)
            await Task.WhenAll(sources.Select(source => FillAsync(source, cancellationToken))).ConfigureAwait(false);
        while (results.Count < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sources.Any(source => source.RestartRequired))
                return new PhotoSearchBatch([], false, "", RestartRequired: true);
            var next = sources.Where(source => source.Items.Count > 0)
                .OrderByDescending(source => source.Items.Peek().Score).ThenBy(source => source.Priority).FirstOrDefault();
            if (next is null)
                break;
            results.Add(new Sourced<PhotoSearchMatch>(next.Source.Id, next.Items.Dequeue()));
            if (results.Count < count)
                await FillAsync(next, cancellationToken).ConfigureAwait(false);
        }
        int? total = 0;
        foreach (var source in sources)
            total += source.TotalCount;
        return new PhotoSearchBatch(results, sources.Any(source => source.NameItems.Count > 0 || source.Items.Count > 0 || !source.Finished),
            string.Join(Environment.NewLine, sources.Where(source => source.Error is not null).Select(source => source.Source.DisplayName + ": " + source.Error)),
            TotalCount: total);
    }

    private async Task LoadNamesAsync(SourceState source, CancellationToken cancellationToken)
    {
        try
        {
            var services = await source.Source.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            source.Service = services.PhotoSearch;
            source.Finished = source.Service is null;
            if (services.VideoLibraryQuery is not IMediaLibraryQueryService media)
                return;
            var images = await media.SearchImagesAsync(query, cancellationToken).ConfigureAwait(false);
            foreach (var image in images)
            {
                if (!source.SeenPaths.Add(image.Path))
                    continue;
                source.NameItems.Enqueue(new PhotoSearchMatch(image.Path, image.FileName, image.FileSize,
                    image.Width, image.Height, image.DateTaken?.ToString("O", CultureInfo.InvariantCulture), 0));
                source.ListedCount++;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Warning("PhotoSearch", $"Photo name search failed for {source.Source.DisplayName}.", exception);
            source.Error = exception.Message;
        }
    }

    private async Task FillAsync(SourceState source, CancellationToken cancellationToken)
    {
        if (source.Items.Count > 0 || source.Finished)
            return;
        try
        {
            source.Service ??= (await source.Source.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false)).PhotoSearch;
            if (source.Service is null)
            {
                source.Finished = true;
                return;
            }
            var page = await source.Service.SearchAsync(query, 100, source.Cursor, cancellationToken).ConfigureAwait(false);
            source.RestartRequired = page.RestartRequired;
            source.Finished = !page.IsAvailable || page.RestartRequired || page.NextCursor is null;
            if (!page.IsAvailable || page.RestartRequired)
                return;
            if (page.Items.Count > 100 || page.Items.Any(item => !double.IsFinite(item.Score)) || page.TotalCount < 0 ||
                page.NextCursor is not null && page.NextCursor == source.Cursor)
                throw new InvalidOperationException("The photo source returned an invalid search page.");
            source.Cursor = page.NextCursor;
            source.PendingCount = page.TotalCount - source.ReadCount - page.Items.Count;
            source.ReadCount += page.Items.Count;
            foreach (var item in page.Items)
            {
                if (!source.SeenPaths.Add(item.Path))
                    continue;
                source.Items.Enqueue(item);
                source.ListedCount++;
            }
            if (source.Items.Count == 0 && !source.Finished)
                await FillAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Warning("PhotoSearch", $"Photo search failed for {source.Source.DisplayName}.", exception);
            source.Error = source.Error is null ? exception.Message : source.Error + Environment.NewLine + exception.Message;
            source.Finished = true;
        }
    }

    private sealed class SourceState(ILibrarySource source, int priority)
    {
        internal ILibrarySource Source { get; } = source;
        internal int Priority { get; } = priority;
        internal IPhotoSearchService? Service { get; set; }
        internal Queue<PhotoSearchMatch> NameItems { get; } = new();
        internal HashSet<string> SeenPaths { get; } = new(StringComparer.Ordinal);
        internal Queue<PhotoSearchMatch> Items { get; } = new();
        internal string? Cursor { get; set; }
        internal bool Finished { get; set; }
        /// <summary>The distinct photos read from the source so far, by name or by meaning.</summary>
        internal int ListedCount { get; set; }
        /// <summary>The semantic matches read from the source so far, repeated photos included.</summary>
        internal int ReadCount { get; set; }
        /// <summary>The semantic matches the source has yet to return, or null where it does not report its total.</summary>
        internal int? PendingCount { get; set; }
        /// <summary>The photos the source lists in total; pending matches that repeat a listed photo leave it once they are read.</summary>
        internal int? TotalCount => Finished ? ListedCount : ListedCount + PendingCount;
        internal bool RestartRequired { get; set; }
        internal string? Error { get; set; }
    }
}