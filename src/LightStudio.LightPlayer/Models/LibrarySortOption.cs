namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Library sort fields.
/// </summary>
public enum LibrarySortField
{
    Title,
    AlbumName,
    ArtistName,
    Genre,
    Year,
    DateAdded,
}

/// <summary>
/// A selectable sort field plus its display label.
/// </summary>
public sealed record LibrarySortOption(LibrarySortField Field, string Title);

public static class LibrarySortOptions
{
    public static System.Collections.Generic.IReadOnlyList<LibrarySortOption> All { get; } =
    [
        new(LibrarySortField.Title, "Title"),
        new(LibrarySortField.AlbumName, "Album Name"),
        new(LibrarySortField.ArtistName, "Artist Name"),
        new(LibrarySortField.Genre, "Genre"),
        new(LibrarySortField.Year, "Year"),
        new(LibrarySortField.DateAdded, "Date added"),
    ];
}
