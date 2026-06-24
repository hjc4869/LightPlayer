using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// A user playlist: a stable identity, a display title, and an ordered list of
/// entries. The identity is a GUID so the on-disk file never moves when the
/// title changes.
/// </summary>
public sealed partial class PlaylistModel : ObservableObject
{
    [ObservableProperty]
    private string title;

    public PlaylistModel(string id, string title)
    {
        Id = id;
        this.title = title ?? string.Empty;
        Items = new ObservableCollection<PlaylistItemModel>();
        Items.CollectionChanged += OnItemsChanged;
    }

    public string Id { get; }

    public ObservableCollection<PlaylistItemModel> Items { get; }

    public int ItemCount => Items.Count;

    public string Subtitle => Items.Count switch
    {
        0 => "No items",
        1 => "1 item",
        _ => $"{Items.Count} items",
    };

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(Subtitle));
    }
}
