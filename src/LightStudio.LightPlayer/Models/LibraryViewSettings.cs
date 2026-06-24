using System.Collections.Generic;
using System.Linq;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// The kind of library item a browse page shows.
/// </summary>
public enum LibraryItemKind
{
    Song,
    Album,
    Artist,
}

/// <summary>
/// The persisted sort field, direction, and grouping toggle for a single
/// browse page.
/// </summary>
public sealed record LibraryViewSettings(LibrarySortField Sort, bool Ascending, bool GroupingEnabled)
{
    public static LibraryViewSettings Default(LibraryItemKind kind) => kind switch
    {
        LibraryItemKind.Album => new LibraryViewSettings(LibrarySortField.AlbumName, true, false),
        LibraryItemKind.Artist => new LibraryViewSettings(LibrarySortField.ArtistName, true, false),
        _ => new LibraryViewSettings(LibrarySortField.Title, true, false),
    };

    /// <summary>
    /// The sort fields a given item kind supports, used to hide unsupported
    /// options from the shell sort menu.
    /// </summary>
    public static IReadOnlyList<LibrarySortField> SupportedSorts(LibraryItemKind kind) => kind switch
    {
        LibraryItemKind.Album =>
        [
            LibrarySortField.AlbumName,
            LibrarySortField.ArtistName,
            LibrarySortField.Year,
        ],
        LibraryItemKind.Artist =>
        [
            LibrarySortField.ArtistName,
        ],
        _ =>
        [
            LibrarySortField.Title,
            LibrarySortField.AlbumName,
            LibrarySortField.ArtistName,
            LibrarySortField.Genre,
            LibrarySortField.Year,
            LibrarySortField.DateAdded,
        ],
    };

    /// <summary>
    /// Clamps the sort field to one the kind supports, falling back to the
    /// kind default when the persisted value is no longer valid.
    /// </summary>
    public LibraryViewSettings Normalized(LibraryItemKind kind)
    {
        return SupportedSorts(kind).Contains(Sort)
            ? this
            : this with { Sort = Default(kind).Sort };
    }
}
