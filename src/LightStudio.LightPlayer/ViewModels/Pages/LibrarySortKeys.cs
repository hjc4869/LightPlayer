using System;
using System.Collections.Generic;
using System.Globalization;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Shared sort/group key helpers for the library browse pages.
/// </summary>
internal static class LibrarySortKeys
{
    public static int CompareText(string? a, string? b) =>
        string.Compare(a ?? string.Empty, b ?? string.Empty, StringComparison.CurrentCultureIgnoreCase);

    public static int CompareYear(string? a, string? b) =>
        ParseLeadingInt(a).CompareTo(ParseLeadingInt(b));

    public static int ParseLeadingInt(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var length = 0;
        while (length < value.Length && length < 9 && char.IsDigit(value[length]))
        {
            length++;
        }

        return length > 0 && int.TryParse(value.AsSpan(0, length), out var parsed) ? parsed : 0;
    }

    public static string AlphaKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "#";
        }

        var first = char.ToUpper(value.Trim()[0], CultureInfo.CurrentCulture);
        return char.IsLetter(first) ? first.ToString() : "#";
    }

    public static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value!;

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Sorts album and artist tiles by the selected field and direction, with a
/// stable title tie-break.
/// </summary>
internal sealed class LibraryTileComparer : IComparer<LibraryTileModel>
{
    private readonly LibrarySortField field;
    private readonly bool ascending;

    public LibraryTileComparer(LibrarySortField field, bool ascending)
    {
        this.field = field;
        this.ascending = ascending;
    }

    public int Compare(LibraryTileModel? x, LibraryTileModel? y)
    {
        if (x is null || y is null)
        {
            return 0;
        }

        var primary = field switch
        {
            LibrarySortField.ArtistName => LibrarySortKeys.CompareText(x.ArtistName, y.ArtistName),
            LibrarySortField.Year => LibrarySortKeys.CompareYear(x.Year, y.Year),
            _ => LibrarySortKeys.CompareText(x.Title, y.Title),
        };

        primary = ascending ? primary : -primary;
        return primary != 0 ? primary : LibrarySortKeys.CompareText(x.Title, y.Title);
    }
}
