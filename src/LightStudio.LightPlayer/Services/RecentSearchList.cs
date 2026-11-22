using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// The search queries the user ran most recently, newest first, shared by every search page and
/// saved with the shell state.
/// </summary>
public sealed class RecentSearchList
{
    private const int Capacity = 8;

    private readonly IShellStateStore store;
    private readonly object saveGate = new();
    private List<string> items;

    public RecentSearchList(IShellStateStore store)
    {
        this.store = store;
        items = store.RecentSearches.Where(item => !string.IsNullOrWhiteSpace(item)).Take(Capacity).ToList();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<string> Items => items;

    public void Add(string query)
    {
        query = query.Trim();
        if (query.Length == 0 || (items.Count > 0 && items[0] == query))
        {
            return;
        }

        Update(items.Where(item => !Matches(item, query)).Prepend(query).Take(Capacity).ToList());
    }

    public void Remove(string query)
    {
        if (items.Any(item => Matches(item, query)))
        {
            Update(items.Where(item => !Matches(item, query)).ToList());
        }
    }

    public void Clear()
    {
        if (items.Count > 0)
        {
            Update([]);
        }
    }

    private static bool Matches(string first, string second) =>
        string.Equals(first, second, StringComparison.CurrentCultureIgnoreCase);

    private void Update(List<string> updated)
    {
        items = updated;
        Changed?.Invoke(this, EventArgs.Empty);

        // The store writes its file as the value is set, so the write stays off the calling thread.
        _ = Task.Run(() =>
        {
            lock (saveGate)
            {
                store.RecentSearches = items;
            }
        });
    }
}
