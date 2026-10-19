using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Models;

public sealed record LibraryGroupHeader(string Title, int Count, int StartItem, int Index, int GroupIndex,
    IRelayCommand OpenCommand)
{
    public bool IsFolder { get; init; }
}

public sealed class GroupItemPlaceholder
{
    public static GroupItemPlaceholder Instance { get; } = new();
}

public abstract class GroupedItemsModel : ObservableObject, IList, INotifyCollectionChanged, IDisposable
{
    private bool isPickerOpen;

    protected GroupedItemsModel(IReadOnlyList<LibraryBrowseGroup> groups)
    {
        var start = 0;
        Groups = groups.Select((group, index) =>
        {
            var header = new LibraryGroupHeader(group.Title, group.Count, start, start + index, index,
                new RelayCommand(() => IsPickerOpen = true))
            {
                IsFolder = group.Key == "\u0001",
            };
            start += group.Count;
            return header;
        }).ToArray();
        TotalItemCount = start;
        ClosePickerCommand = new RelayCommand(() => IsPickerOpen = false);
        JumpToGroupCommand = new RelayCommand<LibraryGroupHeader>(group =>
        {
            if (group is null)
                return;
            IsPickerOpen = false;
            JumpRequested?.Invoke(group.Index);
        });
    }

    public IReadOnlyList<LibraryGroupHeader> Groups { get; }
    public int TotalItemCount { get; }
    public abstract int LoadedItemCount { get; }
    public abstract bool IsLoading { get; }
    public abstract bool HasError { get; }
    public bool HasMoreItems => LoadedItemCount < TotalItemCount;
    public bool IsPickerOpen
    {
        get => isPickerOpen;
        set => SetProperty(ref isPickerOpen, value);
    }
    public IRelayCommand ClosePickerCommand { get; }
    public IRelayCommand<LibraryGroupHeader> JumpToGroupCommand { get; }
    public event Action<int>? JumpRequested;
    public event EventHandler? StateChanged;
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public abstract Task LoadMoreAsync();
    public abstract Task RetryAsync();
    public abstract void EnsureRange(int first, int last);
    public abstract void Dispose();
    protected abstract object GetItem(int index);
    protected abstract int FindItem(object? value);

    protected void NotifyItemsChanged() =>
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    protected void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(LoadedItemCount));
        OnPropertyChanged(nameof(HasMoreItems));
        OnPropertyChanged(nameof(HasError));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public int GroupFromIndex(int index)
    {
        var low = 0;
        var high = Groups.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (Groups[middle].Index <= index)
                low = middle + 1;
            else
                high = middle;
        }
        return Math.Max(0, low - 1);
    }

    public int Count => TotalItemCount + Groups.Count;
    public object? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            var group = Groups[GroupFromIndex(index)];
            return index == group.Index ? group : GetItem(index - group.GroupIndex - 1);
        }
        set => throw new NotSupportedException();
    }
    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public int IndexOf(object? value)
    {
        if (value is LibraryGroupHeader header)
            return header.Index;
        var position = FindItem(value);
        if (position < 0)
            return -1;
        var low = 0;
        var high = Groups.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (Groups[middle].StartItem <= position)
                low = middle + 1;
            else
                high = middle;
        }
        return position + low;
    }
    public void CopyTo(Array array, int index)
    {
        foreach (var item in this)
            array.SetValue(item, index++);
    }
    public IEnumerator GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
            yield return this[index];
    }
}

public sealed class GroupedItemsModel<T> : GroupedItemsModel where T : SelectionItemModel
{
    private const int PageSize = MediaFilePageRequest.DefaultPageSize;
    private readonly GroupedLibraryQuery<T> query;
    private readonly CancellationTokenSource cancellation;
    private readonly Action<IReadOnlyList<T>> release;
    private readonly T?[] items;
    private readonly HashSet<int> failures = [];
    private readonly Dictionary<int, Task> loading = [];
    private int loadedCount;
    private int activeLoads;
    private int visibleFirst;
    private int visibleLast = -1;
    private bool scheduled;
    private bool disposed;

    public bool RequiresReload { get; private set; }

    public GroupedItemsModel(GroupedLibraryQuery<T> query, CancellationToken cancellationToken,
        Action<IReadOnlyList<T>> release, IReadOnlyList<T>? initialItems = null) : base(query.Groups)
    {
        this.query = query;
        this.release = release;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        items = new T?[TotalItemCount];
        if (initialItems is not null)
        {
            if (initialItems.Count != items.Length)
                throw new ArgumentException("The initial items must match the group directory.", nameof(initialItems));
            for (var index = 0; index < items.Length; index++)
                items[index] = initialItems[index];
            loadedCount = items.Length;
        }
    }

    public override int LoadedItemCount => loadedCount;
    public override bool IsLoading => activeLoads > 0;
    public override bool HasError => failures.Count > 0;
    public IReadOnlyList<T> LoadedItems => items.OfType<T>().ToArray();
    protected override object GetItem(int index) => (object?)items[index] ?? GroupItemPlaceholder.Instance;
    protected override int FindItem(object? value) => value is T item ? Array.IndexOf(items, item) : -1;

    public override void EnsureRange(int first, int last)
    {
        if (disposed || Count == 0 || last < first)
            return;
        first = Math.Clamp(first, 0, Count - 1);
        last = Math.Clamp(last, first, Count - 1);
        visibleFirst = Math.Max(0, first - GroupFromIndex(first) - 1) / PageSize;
        visibleLast = Math.Min(TotalItemCount - 1, last - GroupFromIndex(last) - 1) / PageSize;
        ScheduleVisibleLoads();
    }

    private void ScheduleVisibleLoads()
    {
        if (disposed || scheduled)
            return;
        scheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            scheduled = false;
            if (disposed)
                return;
            for (var page = visibleFirst; page <= visibleLast && activeLoads < 2; page++)
            {
                if (items[page * PageSize] is null && !failures.Contains(page))
                    _ = LoadPageAsync(page);
            }
        }, DispatcherPriority.Background);
    }

    public override Task LoadMoreAsync()
    {
        if (disposed)
            return Task.CompletedTask;
        for (var offset = 0; offset < items.Length; offset += PageSize)
        {
            if (items[offset] is null && !failures.Contains(offset / PageSize))
                return LoadPageAsync(offset / PageSize);
        }
        return Task.CompletedTask;
    }

    public override async Task RetryAsync()
    {
        foreach (var page in failures.ToArray())
        {
            failures.Remove(page);
            await LoadPageAsync(page);
        }
    }

    private Task LoadPageAsync(int page)
    {
        if (loading.TryGetValue(page, out var pending))
            return pending;
        var task = LoadPageCoreAsync(page);
        if (!task.IsCompleted)
            loading[page] = task;
        return task;
    }

    private async Task LoadPageCoreAsync(int page)
    {
        var offset = page * PageSize;
        if (disposed || items[offset] is not null)
            return;
        activeLoads++;
        NotifyStateChanged();
        try
        {
            var values = await query.ReadAsync(offset, Math.Min(PageSize, items.Length - offset), cancellation.Token);
            if (disposed || cancellation.IsCancellationRequested)
            {
                release(values);
                return;
            }
            if (values.Length != Math.Min(PageSize, items.Length - offset))
                throw new InvalidOperationException("The grouped item batch does not match its index.");
            for (var index = 0; index < values.Length; index++)
                items[offset + index] = values[index];
            loadedCount += values.Length;
            failures.Remove(page);
            NotifyItemsChanged();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!disposed)
            {
                failures.Add(page);
                RequiresReload |= exception is LibraryBrowseIndexExpiredException or UnifiedQueryInvalidatedException;
                AppErrorReporter.Report(Resources.Shell_WarnLoadPage, exception);
            }
        }
        finally
        {
            activeLoads--;
            loading.Remove(page);
            if (!disposed)
            {
                NotifyStateChanged();
                ScheduleVisibleLoads();
            }
        }
    }

    public override void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        cancellation.Cancel();
        cancellation.Dispose();
        Array.Clear(items);
    }
}
