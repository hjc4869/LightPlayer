#nullable enable

using System;

namespace LightStudio.MediaLibraryCore.Database;

/// <summary>The fields supported when paging through the complete track library.</summary>
public enum MediaFileSortField
{
    Title,
    AlbumName,
    ArtistName,
    Genre,
    Year,
    DateAdded,
    Duration,
}

/// <summary>The fields supported when paging through album tiles.</summary>
public enum AlbumSortField
{
    Title,
    ArtistName,
    Year,
}

/// <summary>A bounded request for items whose endpoint defines one fixed ordering.</summary>
public sealed record PageRequest(
    int PageSize = MediaFilePageRequest.DefaultPageSize,
    string? Cursor = null)
{
    public void Validate() => ValidatePage(PageSize, Cursor);

    internal static void ValidatePage(int pageSize, string? cursor)
    {
        if (pageSize is < 1 or > MediaFilePageRequest.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                $"Page size must be between 1 and {MediaFilePageRequest.MaxPageSize}.");
        }

        if (cursor?.Length > MediaFilePageRequest.MaxCursorLength)
        {
            throw new ArgumentException("The page cursor is too long.", nameof(cursor));
        }
    }
}

/// <summary>One bounded request for albums in a deterministic browse order.</summary>
public sealed record AlbumPageRequest(
    AlbumSortField Sort = AlbumSortField.Title,
    bool Ascending = true,
    int PageSize = MediaFilePageRequest.DefaultPageSize,
    string? Cursor = null)
{
    public void Validate() => PageRequest.ValidatePage(PageSize, Cursor);
}

/// <summary>One bounded request for artists ordered by name.</summary>
public sealed record ArtistPageRequest(
    bool Ascending = true,
    int PageSize = MediaFilePageRequest.DefaultPageSize,
    string? Cursor = null)
{
    public void Validate() => PageRequest.ValidatePage(PageSize, Cursor);
}

/// <summary>
/// One bounded request for a stable shuffled track sequence. Reusing <see cref="Seed"/> returns
/// the same order on every page.
/// </summary>
public sealed record ShuffledMediaFilePageRequest(
    long Seed,
    int PageSize = MediaFilePageRequest.DefaultPageSize,
    string? Cursor = null)
{
    public void Validate() => PageRequest.ValidatePage(PageSize, Cursor);
}

/// <summary>One bounded request for tracks in a deterministic browse order.</summary>
public sealed record MediaFilePageRequest(
    MediaFileSortField Sort = MediaFileSortField.Title,
    bool Ascending = true,
    int PageSize = 80,
    string? Cursor = null,
    string? FolderPath = null)
{
    public const int DefaultPageSize = 80;
    public const int MaxPageSize = 200;
    public const int MaxCursorLength = 4096;

    public void Validate()
    {
        PageRequest.ValidatePage(PageSize, Cursor);
    }
}

/// <summary>A bounded result and the opaque cursor for the following page, when one exists.</summary>
public sealed record PagedResult<T>(T[] Items, string? NextCursor, int? TotalCount = null);