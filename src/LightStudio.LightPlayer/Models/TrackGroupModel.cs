using System.Collections.Generic;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// A header key plus the track rows that fall under it when the tracks page is
/// grouped.
/// </summary>
public sealed class TrackGroupModel
{
    public TrackGroupModel(string key, IReadOnlyList<TrackRowModel> items)
    {
        Key = key;
        Items = items;
    }

    public string Key { get; }

    public IReadOnlyList<TrackRowModel> Items { get; }
}
