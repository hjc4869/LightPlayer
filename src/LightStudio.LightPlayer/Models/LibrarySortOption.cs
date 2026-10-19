using Strings = LightStudio.LightPlayer.Strings;

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
    Duration,
    FileType,
    FileSize,
}

/// <summary>
/// A selectable sort field plus the resource key of its display label (use
/// <c>nameof(Resources.Key)</c>) so the label follows the interface language.
/// </summary>
public sealed record LibrarySortOption(LibrarySortField Field, string TitleKey)
{
    public string Title => Strings.LocalizedStrings.Instance[TitleKey];
}

public static class LibrarySortOptions
{
    public static System.Collections.Generic.IReadOnlyList<LibrarySortOption> All { get; } =
    [
        new(LibrarySortField.Title, nameof(Strings.Resources.Shell_SortTitle)),
        new(LibrarySortField.AlbumName, nameof(Strings.Resources.Shell_SortAlbumName)),
        new(LibrarySortField.ArtistName, nameof(Strings.Resources.Shell_SortArtistName)),
        new(LibrarySortField.Genre, nameof(Strings.Resources.Shell_SortGenre)),
        new(LibrarySortField.Year, nameof(Strings.Resources.Shell_SortYear)),
        new(LibrarySortField.DateAdded, nameof(Strings.Resources.Shell_SortDateAdded)),
        new(LibrarySortField.Duration, nameof(Strings.Resources.Common_ColumnLength)),
        new(LibrarySortField.FileType, nameof(Strings.Resources.Shell_SortFileType)),
        new(LibrarySortField.FileSize, nameof(Strings.Resources.Shell_SortFileSize)),
    ];
}
