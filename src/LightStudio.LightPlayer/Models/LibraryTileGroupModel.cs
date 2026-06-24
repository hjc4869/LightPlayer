using System.Collections.Generic;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// A header key plus the album/artist tiles that fall under it when a tile page
/// is grouped.
/// </summary>
public sealed class LibraryTileGroupModel
{
    public LibraryTileGroupModel(string key, IReadOnlyList<LibraryTileModel> items)
    {
        Key = key;
        Items = items;
    }

    public string Key { get; }

    public IReadOnlyList<LibraryTileModel> Items { get; }
}
