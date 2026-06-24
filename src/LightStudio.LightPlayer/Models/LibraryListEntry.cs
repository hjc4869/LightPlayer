namespace LightStudio.LightPlayer.Models;

/// <summary>
/// One entry in the flattened, virtualized grouped list: either a group header
/// (carrying <see cref="HeaderKey"/>) or a row (carrying <see cref="Item"/>).
///
/// A single item template switches on <see cref="IsHeader"/>, so every realized
/// element has the same shape. That lets one virtualizing <c>ItemsRepeater</c>
/// render headers and rows together without the cross-type container recycling
/// that leaves blank rows/headers when distinct templates share a recycle pool.
/// </summary>
public sealed class LibraryListEntry
{
    private LibraryListEntry(bool isHeader, string? headerKey, object? item)
    {
        IsHeader = isHeader;
        HeaderKey = headerKey;
        Item = item;
    }

    /// <summary>True for a group header, false for a row.</summary>
    public bool IsHeader { get; }

    /// <summary>The group header text; null for a row.</summary>
    public string? HeaderKey { get; }

    /// <summary>The row payload (a row presentation model); null for a header.</summary>
    public object? Item { get; }

    public static LibraryListEntry Header(string key) => new(true, key, null);

    public static LibraryListEntry ForItem(object item) => new(false, null, item);
}
