using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Shared base for the music, albums, and artists pages. Owns the loading and
/// empty states, applies sort/grouping settings, and rebuilds the flat and
/// grouped collections the views bind to.
/// </summary>
public abstract partial class LibraryBrowsePageViewModelBase<TItem, TGroup> : PageViewModelBase, ILibraryBrowsePageViewModel
{
    private IReadOnlyList<TItem> source = [];
    private bool isLoaded;
    private int rebuildVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool isEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFlat))]
    [NotifyPropertyChangedFor(nameof(ShowGroups))]
    private bool isGrouped;

    [ObservableProperty]
    private IReadOnlyList<TItem> items = [];

    [ObservableProperty]
    private IReadOnlyList<TGroup> groups = [];

    /// <summary>
    /// The grouped view flattened into a single sequence of header + row entries. A linear list page
    /// binds this to one virtualizing <c>ItemsRepeater</c> so the whole grouped list virtualizes
    /// instead of a per-group control materializing every row up front.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<LibraryListEntry> groupedItems = [];

    protected LibraryBrowsePageViewModelBase(LibraryItemKind kind)
    {
        Kind = kind;
        IsLoading = true;
    }

    public LibraryItemKind Kind { get; }

    /// <inheritdoc />
    public double ScrollOffset { get; set; }

    public string EmptyHeader { get; protected init; } = "It's lonely here.";

    public string EmptyDescription { get; protected init; } = "Add a library folder to get started.";

    public bool ShowContent => !IsLoading && !IsEmpty;

    public bool ShowEmpty => !IsLoading && IsEmpty;

    public bool ShowFlat => !IsGrouped;

    public bool ShowGroups => IsGrouped;

    protected LibrarySortField SortField { get; private set; }

    protected bool Ascending { get; private set; } = true;

    public void ApplyViewSettings(LibraryViewSettings settings)
    {
        var normalized = settings.Normalized(Kind);
        SortField = normalized.Sort;
        Ascending = normalized.Ascending;
        IsGrouped = normalized.GroupingEnabled;

        if (isLoaded)
        {
            _ = RebuildAsync();
        }
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            // Query and project off the UI thread so the loading state can render
            // and a large library does not freeze the window.
            source = await Task.Run(QueryItemsAsync);
            isLoaded = true;
            await RebuildAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected abstract Task<IReadOnlyList<TItem>> QueryItemsAsync();

    protected abstract IComparer<TItem> CreateComparer(LibrarySortField field, bool ascending);

    protected abstract string GetGroupKey(TItem item, LibrarySortField field);

    protected abstract TGroup CreateGroup(string key, IReadOnlyList<TItem> items);

    private async Task RebuildAsync()
    {
        // Sorting and grouping thousands of items is heavy, so do it off the UI
        // thread; a version token discards a rebuild superseded by a newer one.
        var version = ++rebuildVersion;
        var snapshot = source;
        var field = SortField;
        var ascending = Ascending;
        var grouped = IsGrouped;

        var (sorted, builtGroups, flattened) = await Task.Run(() =>
        {
            var ordered = snapshot.OrderBy(item => item, CreateComparer(field, ascending)).ToList();
            IReadOnlyList<TGroup> groupList = Array.Empty<TGroup>();
            IReadOnlyList<LibraryListEntry> flatList = Array.Empty<LibraryListEntry>();
            if (grouped)
            {
                (groupList, flatList) = BuildGroups(ordered, field);
            }

            return ((IReadOnlyList<TItem>)ordered, groupList, flatList);
        });

        if (version != rebuildVersion)
        {
            return;
        }

        // Assign the lists wholesale so the virtualizing views bind once instead of
        // processing one change notification per item.
        Items = sorted;
        Groups = builtGroups;
        GroupedItems = flattened;
        IsEmpty = sorted.Count == 0;
    }

    private (IReadOnlyList<TGroup> Groups, IReadOnlyList<LibraryListEntry> Flattened) BuildGroups(
        IReadOnlyList<TItem> sorted,
        LibrarySortField field)
    {
        var groups = new List<TGroup>();
        var flattened = new List<LibraryListEntry>(sorted.Count);
        string? currentKey = null;
        List<TItem>? bucket = null;

        void FlushBucket()
        {
            if (bucket is null)
            {
                return;
            }

            var group = CreateGroup(currentKey!, bucket);
            groups.Add(group);
            flattened.Add(LibraryListEntry.Header(currentKey!));
            foreach (var item in bucket)
            {
                flattened.Add(LibraryListEntry.ForItem(item!));
            }
        }

        foreach (var item in sorted)
        {
            var key = GetGroupKey(item, field);
            if (bucket is null || !string.Equals(key, currentKey, StringComparison.CurrentCulture))
            {
                FlushBucket();
                currentKey = key;
                bucket = [];
            }

            bucket.Add(item);
        }

        FlushBucket();
        return (groups, flattened);
    }
}
