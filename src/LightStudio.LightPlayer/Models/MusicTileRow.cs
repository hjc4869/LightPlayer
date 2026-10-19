using System;
using System.Collections.Generic;
using System.Linq;

namespace LightStudio.LightPlayer.Models;

internal sealed class MusicTileRow(
    Func<MusicPlaybackItemModel, LibraryTileModel> createTile,
    bool matchByReference = true)
{
    private readonly List<Entry> entries = [];

    public BulkObservableCollection<LibraryTileModel> Tiles { get; } = [];

    public void Update(IEnumerable<MusicPlaybackItemModel> items)
    {
        var available = entries.ToList();
        var next = new List<Entry>();
        foreach (var item in items)
        {
            var index = available.FindIndex(entry =>
                (!matchByReference || ReferenceEquals(entry.Item, item)) &&
                entry.Item.Media == item.Media && entry.Item.StartTimeMs == item.StartTimeMs &&
                entry.Tile.Title == item.Title && entry.Tile.Subtitle == item.Artist &&
                entry.Album == item.Album && entry.Duration == item.Duration);
            if (index >= 0)
            {
                next.Add(available[index]);
                available.RemoveAt(index);
            }
            else
            {
                next.Add(new Entry(item, createTile(item), item.Album, item.Duration));
            }
        }

        var retained = next.Select(entry => entry.Tile).ToHashSet();
        for (var index = Tiles.Count - 1; index >= 0; index--)
        {
            var tile = Tiles[index];
            if (!retained.Contains(tile))
            {
                Tiles.RemoveAt(index);
                tile.ReleaseArt();
            }
        }

        for (var index = 0; index < next.Count; index++)
        {
            var tile = next[index].Tile;
            if (index < Tiles.Count && ReferenceEquals(Tiles[index], tile))
            {
                continue;
            }

            var oldIndex = Tiles.IndexOf(tile);
            if (oldIndex < 0)
            {
                Tiles.Insert(index, tile);
            }
            else
            {
                tile.RequestArt();
                try
                {
                    Tiles.Move(oldIndex, index);
                }
                finally
                {
                    tile.ReleaseArt();
                }
            }
        }

        entries.Clear();
        entries.AddRange(next);
    }

    private sealed record Entry(
        MusicPlaybackItemModel Item,
        LibraryTileModel Tile,
        string Album,
        TimeSpan Duration);
}