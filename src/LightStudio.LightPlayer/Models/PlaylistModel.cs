using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// What a playlist holds. Music and video playlists are kept in separate stores with the same
/// schema, so a music-only picker can never offer a video list as a destination and the other way
/// round.
/// </summary>
public enum PlaylistKind
{
    Music,
    Video,
}

/// <summary>
/// A user playlist: a stable identity, a display title, and an ordered list of
/// entries. The identity is a GUID so the on-disk file never moves when the
/// title changes.
/// </summary>
public sealed partial class PlaylistModel : ObservableObject
{
    [ObservableProperty]
    private string title;

    public PlaylistModel(string id, string title, bool isBuiltIn = false, PlaylistKind kind = PlaylistKind.Music)
    {
        Id = id;
        IsBuiltIn = isBuiltIn;
        Kind = kind;
        this.title = title ?? string.Empty;
        Items = new ObservableCollection<PlaylistItemModel>();
        Items.CollectionChanged += OnItemsChanged;
    }

    public string Id { get; }

    public bool IsBuiltIn { get; }

    public PlaylistKind Kind { get; }

    public bool IsVideo => Kind == PlaylistKind.Video;

    public bool IsMusic => Kind == PlaylistKind.Music;

    /// <summary>Icon resource key that tells the two kinds apart in the playlist list and headers.</summary>
    public string IconKey => IsVideo ? "IconVideoPlaylist" : "IconPlaylist";

    public ObservableCollection<PlaylistItemModel> Items { get; }

    public int ItemCount => Items.Count;

    public string Subtitle => Items.Count switch
    {
        0 => Strings.Resources.PlaylistModel_NoItems,
        1 => Strings.Resources.Common_OneItem,
        _ => string.Format(Strings.Resources.Common_ItemsFormat, Items.Count),
    };

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(Subtitle));
    }
}
