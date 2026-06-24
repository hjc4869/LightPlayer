using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can replace its entire contents
/// while raising a single <see cref="NotifyCollectionChangedAction.Reset"/>
/// notification instead of one event per item. Used by the now-playing queue,
/// which is wholesale rebuilt from playback snapshots (restore, reshuffle): with
/// thousands of items the per-item add storm froze the UI.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Clears the collection and adds <paramref name="items"/>, raising a single
    /// reset notification once the new contents are in place.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
