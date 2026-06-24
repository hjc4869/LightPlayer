using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;
using LightStudio.LightPlayer.Models;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Songs page: lists every indexed media file as a <see cref="TrackRowModel"/>
/// and applies the persisted song sort/grouping settings.
/// </summary>
public sealed class SongsPageViewModel : LibraryBrowsePageViewModelBase<TrackRowModel, TrackGroupModel>
{
    private readonly TrackActionCallbacks callbacks;

    public SongsPageViewModel(TrackActionCallbacks callbacks)
        : base(LibraryItemKind.Song)
    {
        this.callbacks = callbacks;
        Title = "Music";
        EmptyHeader = "No music yet";
        EmptyDescription = "Add a library folder and scan to see your music.";
    }

    /// <summary>Highlights the row whose file is currently playing, matched by path.</summary>
    public void SetPlayingPath(string? filePath)
    {
        foreach (var row in Items)
        {
            row.IsPlaying = !string.IsNullOrEmpty(filePath)
                && string.Equals(row.FilePath, filePath, PathComparison);
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    protected override async Task<IReadOnlyList<TrackRowModel>> QueryItemsAsync()
    {
        if (GlobalLibraryCache.CachedDbMediaFile is null)
        {
            await GlobalLibraryCache.LoadMediaAsync();
        }

        var files = GlobalLibraryCache.CachedDbMediaFile ?? [];
        return files.Select(file => TrackRowFactory.CreateRow(file, callbacks, () => Items)).ToList();
    }

    protected override IComparer<TrackRowModel> CreateComparer(LibrarySortField field, bool ascending) =>
        new TrackRowComparer(field, ascending);

    protected override string GetGroupKey(TrackRowModel item, LibrarySortField field) => field switch
    {
        LibrarySortField.AlbumName => LibrarySortKeys.Fallback(item.Album, "Unknown Album"),
        LibrarySortField.ArtistName => LibrarySortKeys.Fallback(item.Artist, "Unknown Artist"),
        LibrarySortField.Genre => LibrarySortKeys.Fallback(item.Genre, "Unknown Genre"),
        LibrarySortField.Year => LibrarySortKeys.Fallback(YearLabel(item.Year), "Unknown Year"),
        LibrarySortField.DateAdded => DateAddedLabel(item.DateAdded),
        _ => LibrarySortKeys.AlphaKey(item.Title),
    };

    protected override TrackGroupModel CreateGroup(string key, IReadOnlyList<TrackRowModel> items) =>
        new(key, items);

    private static string YearLabel(string year)
    {
        var parsed = LibrarySortKeys.ParseLeadingInt(year);
        return parsed > 0 ? parsed.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private static string DateAddedLabel(DateTimeOffset dateAdded) =>
        dateAdded == default
            ? "Unknown"
            : dateAdded.ToLocalTime().ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    private sealed class TrackRowComparer : IComparer<TrackRowModel>
    {
        private readonly LibrarySortField field;
        private readonly bool ascending;

        public TrackRowComparer(LibrarySortField field, bool ascending)
        {
            this.field = field;
            this.ascending = ascending;
        }

        public int Compare(TrackRowModel? x, TrackRowModel? y)
        {
            if (x is null || y is null)
            {
                return 0;
            }

            var primary = field switch
            {
                LibrarySortField.AlbumName => LibrarySortKeys.CompareText(x.Album, y.Album),
                LibrarySortField.ArtistName => LibrarySortKeys.CompareText(x.Artist, y.Artist),
                LibrarySortField.Genre => LibrarySortKeys.CompareText(x.Genre, y.Genre),
                LibrarySortField.Year => LibrarySortKeys.CompareYear(x.Year, y.Year),
                LibrarySortField.DateAdded => x.DateAdded.CompareTo(y.DateAdded),
                _ => LibrarySortKeys.CompareText(x.Title, y.Title),
            };

            primary = ascending ? primary : -primary;
            if (primary != 0)
            {
                return primary;
            }

            // Within an album, keep natural disc/track order regardless of direction.
            if (field == LibrarySortField.AlbumName)
            {
                var disc = x.DiscNumber.CompareTo(y.DiscNumber);
                if (disc != 0)
                {
                    return disc;
                }

                var track = x.TrackNumber.CompareTo(y.TrackNumber);
                if (track != 0)
                {
                    return track;
                }
            }

            return LibrarySortKeys.CompareText(x.Title, y.Title);
        }
    }
}
