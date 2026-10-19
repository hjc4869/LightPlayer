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

    /// <summary>The media library of photos and videos.</summary>
    Video,
}

public enum LibraryGroupField
{
    Name,
    Artist,
    Album,
    CreationMonth,
    CreationDay,
    CreationYear,
    Country,
    State,
    City,
}

/// <summary>
/// The persisted sorting and grouping settings for a single browse page.
/// </summary>
public sealed record LibraryViewSettings(LibrarySortField Sort, bool Ascending)
{
    public bool GroupingEnabled { get; init; }

    public LibraryGroupField GroupBy { get; init; }

    public static LibraryViewSettings Default(LibraryItemKind kind) => kind switch
    {
        LibraryItemKind.Album => new LibraryViewSettings(LibrarySortField.AlbumName, true),
        LibraryItemKind.Artist => new LibraryViewSettings(LibrarySortField.ArtistName, true),
        LibraryItemKind.Video => new LibraryViewSettings(LibrarySortField.Title, true)
        {
            GroupBy = LibraryGroupField.CreationMonth,
        },
        _ => new LibraryViewSettings(LibrarySortField.Title, true),
    };

    public static IReadOnlyList<LibraryGroupField> SupportedGroups(LibraryItemKind kind) => kind switch
    {
        LibraryItemKind.Song => [LibraryGroupField.Name, LibraryGroupField.Artist, LibraryGroupField.Album],
        LibraryItemKind.Album => [LibraryGroupField.Name, LibraryGroupField.Artist],
        LibraryItemKind.Artist => [LibraryGroupField.Name],
        LibraryItemKind.Video =>
        [
            LibraryGroupField.CreationDay, LibraryGroupField.CreationMonth, LibraryGroupField.CreationYear,
            LibraryGroupField.Country, LibraryGroupField.State, LibraryGroupField.City,
        ],
        _ => [],
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

        LibraryItemKind.Video =>
        [
            LibrarySortField.Title,
            LibrarySortField.DateAdded,
            LibrarySortField.Duration,
            LibrarySortField.FileType,
            LibrarySortField.FileSize,
        ],
        _ =>
        [
            LibrarySortField.Title,
            LibrarySortField.AlbumName,
            LibrarySortField.ArtistName,
            LibrarySortField.Genre,
            LibrarySortField.Year,
            LibrarySortField.DateAdded,
            LibrarySortField.Duration,
        ],
    };

    /// <summary>
    /// Clamps the sort field to one the kind supports, falling back to the
    /// kind default when the persisted value is no longer valid.
    /// </summary>
    public LibraryViewSettings Normalized(LibraryItemKind kind)
    {
        return this with
        {
            Sort = SupportedSorts(kind).Contains(Sort) ? Sort : Default(kind).Sort,
            GroupBy = SupportedGroups(kind).Contains(GroupBy) ? GroupBy : Default(kind).GroupBy,
        };
    }
}
