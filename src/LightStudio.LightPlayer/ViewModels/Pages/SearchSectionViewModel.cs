using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>One kind of result on the search page: its heading and how it is presented.</summary>
public abstract partial class SearchSectionViewModel : ObservableObject
{
    private readonly string titleKey;

    [ObservableProperty]
    private bool isVisible;

    /// <summary>Whether the section shows its heading, as it does among the other kinds.</summary>
    [ObservableProperty]
    private bool showHeader;

    /// <summary>Whether the section has more results than the preview it shows.</summary>
    [ObservableProperty]
    private bool hasMore;

    /// <summary>The number of items the section's panel reserves room for, or 0 for the items it holds.</summary>
    [ObservableProperty]
    private int extentCount;

    protected SearchSectionViewModel(SearchScope scope, string titleKey, Action<SearchScope> showAll)
    {
        Scope = scope;
        this.titleKey = titleKey;
        ShowAllCommand = new RelayCommand(() => showAll(scope));
    }

    public SearchScope Scope { get; }

    public string Title => LocalizedStrings.Instance[titleKey];

    public IRelayCommand ShowAllCommand { get; }

    public void RefreshText() => OnPropertyChanged(nameof(Title));
}

/// <summary>
/// A search section over its results, building the item models for the leading results it shows.
/// </summary>
public sealed class SearchSectionViewModel<TResult, TItem> : SearchSectionViewModel
{
    private readonly Func<TResult, TItem> create;
    private readonly Action<TItem> release;
    private List<TResult> results = [];

    public SearchSectionViewModel(SearchScope scope, string titleKey, Action<SearchScope> showAll,
        Func<TResult, TItem> create, Action<TItem> release)
        : base(scope, titleKey, showAll)
    {
        this.create = create;
        this.release = release;
    }

    public BulkObservableCollection<TItem> Items { get; } = [];

    public IReadOnlyList<TResult> Results => results;

    public int ResultCount => results.Count;

    public void SetResults(IEnumerable<TResult> values)
    {
        Show(0);
        results = [.. values];
    }

    public void Append(IEnumerable<TResult> values) => results.AddRange(values);

    /// <summary>Shows the first <paramref name="count"/> results, or all of them when there are fewer.</summary>
    public void Show(int count)
    {
        count = Math.Clamp(count, 0, results.Count);
        if (count > Items.Count)
        {
            Items.AddRange(results.Skip(Items.Count).Take(count - Items.Count).Select(create).ToList());
        }
        else if (count < Items.Count)
        {
            var removed = Items.Skip(count).ToArray();
            Items.ReplaceAll(Items.Take(count).ToArray());
            foreach (var item in removed)
            {
                release(item);
            }
        }
    }
}

/// <summary>A result kind the search page can be narrowed to.</summary>
public sealed partial class SearchScopeOption : ObservableObject
{
    private readonly string labelKey;

    [ObservableProperty]
    private bool isSelected;

    /// <summary>The number of matching results, or empty where it is not known.</summary>
    [ObservableProperty]
    private string countText = string.Empty;

    /// <summary>Whether the current query has no results of this kind.</summary>
    [ObservableProperty]
    private bool isEmpty;

    public SearchScopeOption(SearchScope scope, string labelKey, Action<SearchScope> select)
    {
        Scope = scope;
        this.labelKey = labelKey;
        SelectCommand = new RelayCommand(() => select(scope));
    }

    public SearchScope Scope { get; }

    public string Label => LocalizedStrings.Instance[labelKey];

    public IRelayCommand SelectCommand { get; }

    public void RefreshText() => OnPropertyChanged(nameof(Label));
}

/// <summary>A library source the search page can be limited to.</summary>
public sealed partial class SearchSourceOption : ObservableObject
{
    private readonly Action<SearchSourceOption> changed;

    [ObservableProperty]
    private bool isSelected;

    public SearchSourceOption(LibrarySourceId id, string name, bool isSelected, Action<SearchSourceOption> changed)
    {
        Id = id;
        Name = name;
        this.isSelected = isSelected;
        this.changed = changed;
    }

    public LibrarySourceId Id { get; }

    public string Name { get; }

    partial void OnIsSelectedChanged(bool value) => changed(this);
}

/// <summary>A recent query offered before the user types one.</summary>
public sealed class RecentSearchItem(string text, Action<string> select, Action<string> remove)
{
    public string Text { get; } = text;

    public IRelayCommand SelectCommand { get; } = new RelayCommand(() => select(text));

    public IRelayCommand RemoveCommand { get; } = new RelayCommand(() => remove(text));
}
