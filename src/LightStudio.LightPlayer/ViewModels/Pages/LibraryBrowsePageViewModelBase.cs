using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Shared base for the music, albums, and artists pages. Owns the loading and
/// empty states, applies sorting settings, and rebuilds the collection the views bind to.
/// </summary>
public abstract partial class LibraryBrowsePageViewModelBase<TItem>
    : SelectionPageViewModelBase, ILibraryBrowsePageViewModel, IIncrementalLoadingViewModel, IDisposable
    where TItem : SelectionItemModel
{
    private IReadOnlyList<TItem> source = [];
    private CancellationTokenSource? pageCancellation;
    private string? nextCursor;
    private bool isLoaded;
    private int rebuildVersion;
    private int pageVersion;
    private bool disposed;
    private Task? loadMoreTask;
    private LibraryViewSettings viewSettings;

    [ObservableProperty]
    private GroupedItemsModel<TItem>? grouping;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    private bool isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool isEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    private bool isLoadingMore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    private bool hasMoreItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    private bool hasLoadMoreError;

    [ObservableProperty]
    private int totalItemCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartialFailures))]
    private string partialFailureText = string.Empty;

    protected LibraryBrowsePageViewModelBase(LibraryItemKind kind)
    {
        Kind = kind;
        viewSettings = LibraryViewSettings.Default(kind);
        SortField = viewSettings.Sort;
        LightStudio.LightPlayer.Strings.LocalizedStrings.Instance.PropertyChanged += OnLanguageChanged;
        IsLoading = true;
        RetryLoadMoreCommand = new AsyncRelayCommand(RetryLoadMoreAsync);
        Selection.ObserveItems(Items);
        Selection.LoadAllItemsAsync = LoadAllItemsAsync;
        PageSearch.SetSource(new PageSearchSource(
            () => PageSearchSource.Entries(Items), [Items], this,
            isLoading: () => IsLoading || IsLoadingMore,
            hasPendingItems: () => HasMoreItems || HasLoadMoreError || HasPartialFailures,
            pagination: this));
    }

    public LibraryItemKind Kind { get; }

    public bool GroupingEnabled => viewSettings.GroupingEnabled;

    public override bool CanHandleBack => Grouping?.IsPickerOpen == true || base.CanHandleBack;

    public override bool TryHandleBack()
    {
        if (Grouping?.IsPickerOpen == true)
        {
            Grouping.IsPickerOpen = false;
            return true;
        }
        return base.TryHandleBack();
    }

    public BulkObservableCollection<TItem> Items { get; } = [];

    public IAsyncRelayCommand RetryLoadMoreCommand { get; }

    public int LoadedItemCount => Items.Count;

    public bool CanLoadMore =>
        !disposed && SupportsIncrementalLoading && HasMoreItems &&
        !IsLoading && !IsLoadingMore && !HasLoadMoreError;

    /// <inheritdoc />
    public double ScrollOffset { get; set; }

    public string EmptyHeader => Localized(EmptyHeaderKey, "It's lonely here.");

    public string EmptyDescription => Localized(EmptyDescriptionKey, "Add a library folder to get started.");

    /// <summary>Resource key for <see cref="EmptyHeader"/> (use <c>nameof(Resources.Key)</c>).</summary>
    protected string? EmptyHeaderKey { get; init; }

    /// <summary>Resource key for <see cref="EmptyDescription"/> (use <c>nameof(Resources.Key)</c>).</summary>
    protected string? EmptyDescriptionKey { get; init; }

    public bool ShowContent => !IsLoading && (!IsEmpty || HasLoadMoreError);

    public bool ShowEmpty => !IsLoading && IsEmpty && !HasLoadMoreError;

    public bool HasPartialFailures => PartialFailureText.Length > 0;

    protected LibrarySortField SortField { get; private set; }

    protected bool Ascending { get; private set; } = true;

    public void ApplyViewSettings(LibraryViewSettings settings)
    {
        var normalized = settings.Normalized(Kind);
        var changed = SortField != normalized.Sort || Ascending != normalized.Ascending ||
            viewSettings.GroupingEnabled != normalized.GroupingEnabled ||
            normalized.GroupingEnabled && viewSettings.GroupBy != normalized.GroupBy;
        viewSettings = normalized;
        SortField = normalized.Sort;
        Ascending = normalized.Ascending;
        OnPropertyChanged(nameof(GroupingEnabled));

        if ((isLoaded || pageCancellation is not null) && changed)
        {
            ScrollOffset = 0;
            if (SupportsIncrementalLoading)
            {
                _ = ReloadIncrementalAsync();
            }
            else
            {
                _ = RebuildAsync();
            }
        }
    }

    public async Task LoadAsync()
    {
        if (disposed)
        {
            return;
        }

        Selection.Exit();
        if (SupportsIncrementalLoading)
        {
            await LoadFirstPageAsync();
            return;
        }

        IsLoading = true;
        try
        {
            // Query and project off the UI thread so the loading state can render
            // and a large library does not freeze the window.
            var discarded = source;
            source = await Task.Run(QueryItemsAsync);
            isLoaded = true;
            await RebuildAsync();
            ReleaseItems(discarded);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Called with items that have been replaced on screen, so a page can free the resources they
    /// hold. No-op by default.
    /// </summary>
    protected virtual void ReleaseItems(IReadOnlyList<TItem> discarded)
    {
    }

    protected virtual bool SupportsIncrementalLoading => false;

    protected virtual Task<IReadOnlyList<TItem>> QueryItemsAsync() =>
        throw new NotSupportedException("This browse page only supports incremental queries.");

    protected virtual Task<PagedResult<TItem>> QueryPageAsync(
        string? cursor,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This browse page does not support incremental queries.");

    protected virtual Task<GroupedLibraryQuery<TItem>> QueryGroupsAsync(
        LibraryViewSettings settings, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This browse page does not support grouped queries.");

    protected abstract IComparer<TItem> CreateComparer(LibrarySortField field, bool ascending);

    protected void ApplyUnifiedMetadata<T>(UnifiedPage<T> page)
    {
        PartialFailureText = page.SourceFailures is { Count: > 0 } failures
            ? string.Join(Environment.NewLine, failures.Select(failure =>
                $"{failure.SourceName}: {failure.Message}"))
            : string.Empty;
        if (page.Items.Length == 0 && page.SourceFailures is { Count: > 0 })
        {
            HasLoadMoreError = true;
        }
    }

    private async Task RebuildAsync()
    {
        // Sorting thousands of items is heavy, so do it off the UI thread; a version token
        // discards a rebuild superseded by a newer one.
        var version = ++rebuildVersion;
        var snapshot = source;
        var field = SortField;
        var ascending = Ascending;
        var sorted = await Task.Run<IReadOnlyList<TItem>>(() =>
            snapshot.OrderBy(item => item, CreateComparer(field, ascending)).ToList());

        if (version != rebuildVersion)
        {
            return;
        }

        Items.ReplaceAll(sorted);
        IsEmpty = sorted.Count == 0;
        OnItemsRebuilt();
    }

    private async Task LoadFirstPageAsync()
    {
        Selection.Exit();
        Grouping = null;
        var version = ++pageVersion;
        pageCancellation?.Cancel();
        pageCancellation?.Dispose();
        pageCancellation = new CancellationTokenSource();
        var cancellationToken = pageCancellation.Token;

        IsLoading = true;
        IsLoadingMore = false;
        HasLoadMoreError = false;
        HasMoreItems = false;
        try
        {
            if (GroupingEnabled)
            {
                var groupedQuery = await QueryGroupsAsync(viewSettings, cancellationToken);
                if (version != pageVersion)
                    return;
                var previousItems = Items.ToList();
                Items.Clear();
                ReleaseItems(previousItems);
                Grouping = new GroupedItemsModel<TItem>(groupedQuery, cancellationToken, ReleaseItems);
                TotalItemCount = Grouping.TotalItemCount;
                PartialFailureText = string.Join(Environment.NewLine,
                    groupedQuery.SourceFailures.Select(failure => $"{failure.SourceName}: {failure.Message}"));
                HasLoadMoreError = TotalItemCount == 0 && groupedQuery.SourceFailures.Count > 0;
                IsEmpty = TotalItemCount == 0;
                isLoaded = true;
                await Grouping.LoadMoreAsync();
                return;
            }
            var page = await QueryPageAsync(cursor: null, cancellationToken);
            if (version != pageVersion)
            {
                return;
            }

            var discarded = Items.ToList();
            Items.ReplaceAll(page.Items);
            OnPropertyChanged(nameof(LoadedItemCount));
            ReleaseItems(discarded);
            nextCursor = page.NextCursor;
            TotalItemCount = Math.Max(page.TotalCount ?? Items.Count, Items.Count);
            HasMoreItems = nextCursor is not null;
            IsEmpty = Items.Count == 0;
            isLoaded = true;
            OnItemsRebuilt();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (version == pageVersion && GroupingEnabled)
                HasLoadMoreError = true;
            throw;
        }
        finally
        {
            if (version == pageVersion)
            {
                IsLoading = false;
            }
        }
    }

    public Task LoadMoreAsync() => GroupingEnabled
        ? Grouping?.LoadMoreAsync() ?? Task.CompletedTask
        : loadMoreTask is { IsCompleted: false }
        ? loadMoreTask
        : loadMoreTask = LoadMoreCoreAsync();

    private async Task LoadMoreCoreAsync()
    {
        if (!CanLoadMore || pageCancellation is null)
        {
            return;
        }

        var version = pageVersion;
        var cancellationToken = pageCancellation.Token;
        IsLoadingMore = true;
        try
        {
            PagedResult<TItem> page;
            var restarted = false;
            try
            {
                page = await QueryPageAsync(nextCursor, cancellationToken);
            }
            catch (UnifiedQueryInvalidatedException) when (!cancellationToken.IsCancellationRequested)
            {
                if (version != pageVersion)
                {
                    return;
                }

                page = await QueryPageAsync(cursor: null, cancellationToken);
                restarted = true;
            }

            if (version != pageVersion)
            {
                return;
            }

            if (restarted)
            {
                if (page.Items.Length == 0 && HasLoadMoreError)
                {
                    return;
                }

                Selection.Exit();
                var discarded = Items.ToList();
                Items.ReplaceAll(page.Items);
                ReleaseItems(discarded);
            }
            else
            {
                Items.AddRange(page.Items);
            }

            OnPropertyChanged(nameof(LoadedItemCount));
            nextCursor = page.NextCursor;
            TotalItemCount = Math.Max(page.TotalCount ?? (restarted ? Items.Count : TotalItemCount), Items.Count);
            HasMoreItems = nextCursor is not null;
            IsEmpty = Items.Count == 0;
            OnItemsRebuilt();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == pageVersion)
            {
                HasLoadMoreError = true;
                AppErrorReporter.Report(LightStudio.LightPlayer.Strings.Resources.Shell_WarnLoadPage, exception);
            }
        }
        finally
        {
            if (version == pageVersion)
            {
                IsLoadingMore = false;
            }
        }
    }

    private async Task RetryLoadMoreAsync()
    {
        if (GroupingEnabled)
        {
            if (Grouping is null || Grouping.RequiresReload || TotalItemCount == 0)
                await ReloadIncrementalAsync();
            else
                await Grouping.RetryAsync();
            return;
        }
        HasLoadMoreError = false;
        await LoadMoreAsync();
    }

    private async Task<bool> LoadAllItemsAsync()
    {
        var version = pageVersion;
        while (!disposed && version == pageVersion && Selection.IsActive)
        {
            if (HasLoadMoreError)
            {
                return false;
            }

            if (!IsLoadingMore && !CanLoadMore)
            {
                return !IsLoading && !HasMoreItems;
            }

            await LoadMoreAsync();
        }

        return false;
    }

    private async Task ReloadIncrementalAsync()
    {
        try
        {
            await LoadFirstPageAsync();
        }
        catch (Exception exception)
        {
            AppErrorReporter.Report(LightStudio.LightPlayer.Strings.Resources.Shell_WarnLoadPage, exception);
        }
    }

    partial void OnGroupingChanged(GroupedItemsModel<TItem>? oldValue, GroupedItemsModel<TItem>? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.StateChanged -= OnGroupedStateChanged;
            oldValue.PropertyChanged -= OnGroupingPropertyChanged;
            oldValue.Dispose();
        }
        if (newValue is not null)
        {
            newValue.StateChanged += OnGroupedStateChanged;
            newValue.PropertyChanged += OnGroupingPropertyChanged;
        }
        OnPropertyChanged(nameof(CanHandleBack));
    }

    private void OnGroupingPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(GroupedItemsModel.IsPickerOpen))
            OnPropertyChanged(nameof(CanHandleBack));
    }

    private void OnGroupedStateChanged(object? sender, EventArgs args)
    {
        if (Grouping is not { } current || !ReferenceEquals(sender, current) || disposed)
            return;
        if (Items.Count != current.LoadedItemCount)
        {
            Items.ReplaceAll(current.LoadedItems);
            OnPropertyChanged(nameof(LoadedItemCount));
            OnItemsRebuilt();
        }
        IsLoadingMore = current.IsLoading && !IsLoading;
        HasMoreItems = current.HasMoreItems;
        HasLoadMoreError = current.HasError || TotalItemCount == 0 && HasPartialFailures;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (GroupingEnabled && pageCancellation is not null && !disposed)
            _ = ReloadIncrementalAsync();
    }

    public virtual void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        LightStudio.LightPlayer.Strings.LocalizedStrings.Instance.PropertyChanged -= OnLanguageChanged;
        Grouping = null;
        pageVersion++;
        rebuildVersion++;
        pageCancellation?.Cancel();
        pageCancellation?.Dispose();
        pageCancellation = null;
        PageSearch.Close();
        Selection.Dispose();
        ReleaseItems(Items);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Called after <see cref="Items"/> is rebuilt. Lets a page re-apply state that lives on the
    /// freshly ordered rows, such as the now-playing highlight. No-op by default.
    /// </summary>
    protected virtual void OnItemsRebuilt()
    {
    }
}
