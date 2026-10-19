using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;
using LightStudio.LightPlayer.ViewModels;

namespace LightStudio.LightPlayer.Models;

public sealed class MultiSelectionModel : ViewModelBase, IDisposable
{
    private IReadOnlyList<SelectionItemModel> items = [];
    private bool isActive;
    private bool isBusy;
    private int selectedCount;
    private int version;
    private readonly List<MultiSelectionAction> actions = [];
    private IEnumerable<SelectionItemModel>? observedItems;
    private bool disposed;

    public MultiSelectionModel()
    {
        EnterCommand = new RelayCommand<SelectionItemModel>(Enter);
        ToggleCommand = new RelayCommand<SelectionItemModel>(Toggle);
        SelectAllCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(SelectAllAsync), () => !IsBusy);
        SelectNoneCommand = new RelayCommand(SelectNone);
        ExitCommand = new RelayCommand(Exit);
    }

    public bool IsActive
    {
        get => isActive;
        private set
        {
            if (SetProperty(ref isActive, value))
            {
                foreach (var item in items)
                {
                    item.NotifySelectionModeChanged();
                }
            }
        }
    }

    public int SelectedCount
    {
        get => selectedCount;
        private set => SetProperty(ref selectedCount, value);
    }

    public string SelectedCountText => string.Format(Resources.Selection_Count, SelectedCount);

    public IReadOnlyList<SelectionItemModel> SelectedItems => items.Where(item => item.IsSelected).ToArray();

    public IReadOnlyList<SelectionItemModel> Items => items;

    public IReadOnlyList<MultiSelectionAction> Actions => actions;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                SelectAllCommand.NotifyCanExecuteChanged();
                UpdateActions();
            }
        }
    }

    public bool HasActions => actions.Any(action => action.IsAvailable);

    public Func<Task<bool>>? LoadAllItemsAsync { get; set; }

    public IRelayCommand<SelectionItemModel> EnterCommand { get; }

    public IRelayCommand<SelectionItemModel> ToggleCommand { get; }

    public IAsyncRelayCommand SelectAllCommand { get; }

    public IRelayCommand SelectNoneCommand { get; }

    public IRelayCommand ExitCommand { get; }

    public void AddAction(
        string resourceKey,
        Func<IReadOnlyList<SelectionItemModel>, Task> execute,
        Func<IReadOnlyList<SelectionItemModel>, bool>? canApply = null)
    {
        actions.Add(new MultiSelectionAction(this, resourceKey, execute, canApply));
        UpdateActions();
    }

    public void SetItems(IEnumerable<SelectionItemModel> source)
    {
        if (disposed)
        {
            return;
        }

        var replacement = source.ToArray();
        var retained = new HashSet<SelectionItemModel>(replacement);
        foreach (var item in items)
        {
            if (!retained.Contains(item))
            {
                item.IsSelected = false;
                item.Selection = null;
            }
        }

        items = replacement;
        foreach (var item in items)
        {
            item.Selection = this;
        }

        UpdateSelection();
    }

    public void ObserveItems(IEnumerable<SelectionItemModel> source)
    {
        if (disposed)
        {
            return;
        }

        if (observedItems is INotifyCollectionChanged previous)
        {
            previous.CollectionChanged -= OnItemsChanged;
        }

        observedItems = source;
        if (source is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged += OnItemsChanged;
        }

        SetItems(source);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs args) => SetItems(observedItems!);

    public void Enter(SelectionItemModel? item)
    {
        if (item?.Selection != this)
        {
            return;
        }

        IsActive = true;
        ++version;
        item.IsSelected = true;
        UpdateSelection();
    }

    public void Toggle(SelectionItemModel? item)
    {
        if (!IsActive || item?.Selection != this)
        {
            return;
        }

        ++version;
        item.IsSelected = !item.IsSelected;
        UpdateSelection();
    }

    public void SelectNone()
    {
        ++version;
        foreach (var item in items)
        {
            item.IsSelected = false;
        }

        UpdateSelection();
    }

    public void Exit()
    {
        SelectNone();
        IsActive = false;
    }

    private async Task SelectAllAsync()
    {
        var requestVersion = ++version;
        IsBusy = true;
        try
        {
            if (LoadAllItemsAsync is { } loadAll && !await loadAll())
            {
                return;
            }

            if (!IsActive || version != requestVersion)
            {
                return;
            }

            foreach (var item in items)
            {
                item.IsSelected = true;
            }

            UpdateSelection();
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task ExecuteAsync(Func<IReadOnlyList<SelectionItemModel>, Task> execute)
    {
        var snapshot = SelectedItems;
        if (IsBusy || snapshot.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await AppErrorReporter.GuardAsync(() => execute(snapshot));
        }
        finally
        {
            Exit();
            IsBusy = false;
        }
    }

    private void UpdateSelection()
    {
        SelectedCount = items.Count(item => item.IsSelected);
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(SelectedItems));
        UpdateActions();
    }

    public void UpdateActions()
    {
        var snapshot = SelectedItems;
        foreach (var action in actions)
        {
            action.Update(snapshot);
        }

        OnPropertyChanged(nameof(HasActions));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Exit();
        ObserveItems([]);
        LoadAllItemsAsync = null;
        actions.Clear();
        disposed = true;
    }
}