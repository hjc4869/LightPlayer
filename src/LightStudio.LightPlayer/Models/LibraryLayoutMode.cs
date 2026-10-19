namespace LightStudio.LightPlayer.Models;

public enum LibraryItemPresentation
{
    Grid,
    List,
}

/// <summary>
/// How a library browse page presents its items.
/// </summary>
public enum LibraryLayoutMode
{
    /// <summary>Flat items view, using tiles or track rows.</summary>
    Grid,

    /// <summary>Master-detail list with an inline detail pane (<c>ListItemsPageView</c>).</summary>
    List,

    /// <summary>Items browsed folder by folder.</summary>
    Folders,
}

/// <summary>
/// Helpers for the per-kind default layout.
/// </summary>
public static class LibraryLayoutModes
{
    /// <summary>
    /// The layout a kind uses until the user picks another: albums default to the
    /// grid, artists to the list, and videos to folder browsing.
    /// </summary>
    public static LibraryLayoutMode Default(LibraryItemKind kind) => kind switch
    {
        LibraryItemKind.Artist => LibraryLayoutMode.List,
        LibraryItemKind.Video => LibraryLayoutMode.Folders,
        _ => LibraryLayoutMode.Grid,
    };
}
