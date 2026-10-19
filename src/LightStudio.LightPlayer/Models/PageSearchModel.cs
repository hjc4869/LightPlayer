using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;
using LightStudio.LightPlayer.ViewModels;

namespace LightStudio.LightPlayer.Models;

public sealed class PageSearchModel : ViewModelBase
{
    private readonly DispatcherTimer debounce;
    private readonly HashSet<object> matchingItems = new(ReferenceEqualityComparer.Instance);
    private IPageSearchSource? source;
    private IReadOnlyList<PageSearchEntry> matches = [];
    private string query = string.Empty;
    private bool isOpen;
    private bool isSearching;
    private bool isPartial;
    private int currentIndex = -1;
    private int version;
    private int runningVersion = -1;
    private int sourceRevision;

    public PageSearchModel()
    {
        debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        debounce.Tick += OnDebounceTick;
        NextCommand = new RelayCommand(() => Move(1), () => matches.Count > 0);
        PreviousCommand = new RelayCommand(() => Move(-1), () => matches.Count > 0);
        CloseCommand = new RelayCommand(Close);
    }

    public string Query
    {
        get => query;
        set
        {
            if (SetProperty(ref query, value))
            {
                Refresh(preserveCurrent: false);
            }
        }
    }

    public bool IsOpen
    {
        get => isOpen;
        private set => SetProperty(ref isOpen, value);
    }

    public IReadOnlyList<PageSearchEntry> Matches => matches;

    public bool IsSupported => source is not null;

    public bool IsMatch(object item) => matchingItems.Contains(item);

    public PageSearchEntry? CurrentMatch => currentIndex >= 0 && currentIndex < matches.Count
        ? matches[currentIndex]
        : null;

    public bool IsSearching
    {
        get => isSearching;
        internal set
        {
            if (SetProperty(ref isSearching, value))
            {
                NotifyStatus();
            }
        }
    }

    public bool IsPartial
    {
        get => isPartial;
        internal set
        {
            if (SetProperty(ref isPartial, value))
            {
                NotifyStatus();
            }
        }
    }

    public bool HasStatus => IsSearching || IsPartial;

    public string PositionText => $"{currentIndex + 1} / {matches.Count}{(HasStatus ? "+" : string.Empty)}";

    public string MatchCountText => string.Format(Resources.PageSearch_Matches, currentIndex + 1, matches.Count);

    public IRelayCommand NextCommand { get; }

    public IRelayCommand PreviousCommand { get; }

    public IRelayCommand CloseCommand { get; }

    public event EventHandler? ResultsChanged;

    public event Action<PageSearchEntry>? NavigateRequested;

    public void SetSource(IPageSearchSource value)
    {
        if (IsOpen && source is not null)
        {
            source.Changed -= OnSourceChanged;
        }

        source = value;
        OnPropertyChanged(nameof(IsSupported));
        if (IsOpen)
        {
            source.Changed += OnSourceChanged;
        }

        Refresh(preserveCurrent: false);
    }

    public void Open()
    {
        if (!IsOpen && source is not null)
        {
            source.Changed += OnSourceChanged;
        }

        IsOpen = true;
        Refresh();
    }

    public void Close()
    {
        version++;
        debounce.Stop();
        if (IsOpen && source is not null)
        {
            source.Changed -= OnSourceChanged;
        }

        IsOpen = false;
        IsSearching = false;
        IsPartial = false;
        matches = [];
        matchingItems.Clear();
        currentIndex = -1;
        NotifyResults();
    }

    public void Refresh(bool preserveCurrent = true)
    {
        if (!IsOpen)
        {
            return;
        }

        version++;
        debounce.Stop();
        IsPartial = false;
        IsSearching = !string.IsNullOrWhiteSpace(Query) && source is not null;
        if (!preserveCurrent || !IsSearching)
        {
            matches = [];
            matchingItems.Clear();
            currentIndex = -1;
            NotifyResults();
        }

        if (IsSearching)
        {
            debounce.Start();
        }
    }

    private void OnSourceChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSourceChanged(sender, args));
            return;
        }

        sourceRevision++;
        if (runningVersion != version || args is NotifyCollectionChangedEventArgs
            { Action: not NotifyCollectionChangedAction.Add })
        {
            Refresh();
        }
    }

    private void OnDebounceTick(object? sender, EventArgs args)
    {
        debounce.Stop();
        _ = SearchAsync(version);
    }

    private async Task SearchAsync(int requestVersion)
    {
        if (source is not { } searchSource || !IsCurrent(requestVersion))
        {
            return;
        }

        runningVersion = requestVersion;
        var waitingForSource = false;
        var canContinue = true;
        var searchText = Query.Trim();
        try
        {
            while (IsCurrent(requestVersion))
            {
                var revision = sourceRevision;
                var entries = searchSource.GetEntries().ToArray();
                var found = new List<PageSearchEntry>();
                for (var index = 0; index < entries.Length; index++)
                {
                    if (entries[index].Text.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))
                    {
                        found.Add(entries[index]);
                    }

                    if ((index + 1) % 256 == 0)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        if (!IsCurrent(requestVersion))
                        {
                            return;
                        }
                    }
                }

                if (revision != sourceRevision)
                {
                    continue;
                }

                ApplyMatches(found);
                if (searchSource.IsLoading)
                {
                    waitingForSource = true;
                    return;
                }

                if (!canContinue || !searchSource.CanLoadMore)
                {
                    IsPartial = !searchSource.IsComplete;
                    return;
                }

                canContinue = await searchSource.LoadMoreAsync();
            }
        }
        catch (OperationCanceledException) when (!IsCurrent(requestVersion))
        {
        }
        catch (Exception exception)
        {
            if (IsCurrent(requestVersion))
            {
                IsPartial = true;
            }

            AppErrorReporter.Report(Resources.PageSearch_Find, exception);
        }
        finally
        {
            if (IsCurrent(requestVersion))
            {
                runningVersion = -1;
                IsSearching = waitingForSource;
            }
        }
    }

    private bool IsCurrent(int requestVersion) => IsOpen && requestVersion == version;

    private void ApplyMatches(IReadOnlyList<PageSearchEntry> found)
    {
        var previous = CurrentMatch?.Item;
        matches = found;
        matchingItems.Clear();
        foreach (var entry in matches)
        {
            matchingItems.Add(entry.Item);
        }

        currentIndex = -1;
        if (previous is not null)
        {
            for (var index = 0; index < matches.Count; index++)
            {
                if (ReferenceEquals(matches[index].Item, previous))
                {
                    currentIndex = index;
                    break;
                }
            }
        }

        if (currentIndex < 0 && matches.Count > 0)
        {
            currentIndex = 0;
        }

        NotifyResults();
        if (CurrentMatch is { } match && !ReferenceEquals(match.Item, previous))
        {
            NavigateRequested?.Invoke(match);
        }
    }

    private void Move(int direction)
    {
        if (matches.Count == 0)
        {
            return;
        }

        currentIndex = (currentIndex + direction + matches.Count) % matches.Count;
        NotifyResults();
        NavigateRequested?.Invoke(CurrentMatch!);
    }

    private void NotifyResults()
    {
        OnPropertyChanged(nameof(Matches));
        OnPropertyChanged(nameof(CurrentMatch));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(MatchCountText));
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyStatus()
    {
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(PositionText));
    }
}