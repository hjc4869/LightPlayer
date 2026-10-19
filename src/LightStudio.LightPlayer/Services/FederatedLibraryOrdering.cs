using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LightStudio.LightPlayer.Models;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.Services;

internal sealed record RankedSourceItem<T>(
    LibrarySourceId SourceId,
    int Priority,
    T Value);

internal static class FederatedLibraryOrdering
{
    public static IComparer<RankedSourceItem<DbMediaFile>> Tracks(MediaFilePageRequest request) =>
        Comparer<RankedSourceItem<DbMediaFile>>.Create((first, second) =>
        {
            var result = CompareTrackSemantic(first.Value, second.Value, request);
            return result != 0 ? result : CompareSourceTie(first, second, first.Value.Id, second.Value.Id);
        });

    public static IComparer<RankedSourceItem<MediaLibraryAlbum>> Albums(AlbumPageRequest request) =>
        Comparer<RankedSourceItem<MediaLibraryAlbum>>.Create((first, second) =>
        {
            var result = CompareAlbumSemantic(first.Value, second.Value, request);
            return result != 0 ? result : CompareSourceTie(first, second);
        });

    public static IComparer<RankedSourceItem<MediaLibraryArtistBrowseItem>> Artists(ArtistPageRequest request) =>
        Comparer<RankedSourceItem<MediaLibraryArtistBrowseItem>>.Create((first, second) =>
        {
            var result = Direction(
                CompareText(first.Value.Artist.Name, second.Value.Artist.Name),
                request.Ascending);
            if (result == 0)
            {
                result = CompareExact(first.Value.Artist.Name, second.Value.Artist.Name);
            }

            return result != 0 ? result : CompareSourceTie(first, second);
        });

    public static IComparer<RankedSourceItem<DbVideoFile>> Videos() =>
        Comparer<RankedSourceItem<DbVideoFile>>.Create((first, second) =>
        {
            var result = CompareText(first.Value.DisplayName, second.Value.DisplayName);
            if (result == 0)
            {
                result = CompareExact(first.Value.DisplayName, second.Value.DisplayName);
            }

            return result != 0 ? result : CompareSourceTie(first, second, first.Value.Id, second.Value.Id);
        });

    public static IComparer<RankedSourceItem<DbMediaFile>> Shuffle(long globalSeed) =>
        Comparer<RankedSourceItem<DbMediaFile>>.Create((first, second) =>
        {
            var firstRank = ShuffleRank(first.Value.Id, DeriveSourceSeed(globalSeed, first.SourceId));
            var secondRank = ShuffleRank(second.Value.Id, DeriveSourceSeed(globalSeed, second.SourceId));
            var result = firstRank.Phase.CompareTo(secondRank.Phase);
            if (result == 0)
            {
                result = firstRank.Key.CompareTo(secondRank.Key);
            }

            return result != 0 ? result : CompareSourceTie(first, second, first.Value.Id, second.Value.Id);
        });

    public static IComparer<RankedSourceItem<IndexedMediaFile>> Media() =>
        Comparer<RankedSourceItem<IndexedMediaFile>>.Create((first, second) =>
        {
            var result = CompareText(first.Value.DisplayName, second.Value.DisplayName);
            if (result == 0)
                result = CompareExact(first.Value.DisplayName, second.Value.DisplayName);
            if (result == 0)
                result = (first.Value.Image is not null).CompareTo(second.Value.Image is not null);
            return result != 0 ? result : CompareSourceTie(first, second,
                first.Value.Image?.Id ?? first.Value.Video!.Id, second.Value.Image?.Id ?? second.Value.Video!.Id);
        });

    public static long DeriveSourceSeed(long globalSeed, LibrarySourceId sourceId)
    {
        var hash = unchecked((ulong)globalSeed) ^ 14695981039346656037UL;
        foreach (var value in Encoding.UTF8.GetBytes(sourceId.Value ?? string.Empty))
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return unchecked((long)hash);
    }

    public static string NormalizeIdentity(string? value) => (value ?? string.Empty).Trim();

    public static string NormalizeArtistName(string? value) => NormalizeIdentity(value);

    public static string? AlbumDuplicateKey(MediaLibraryAlbum album)
    {
        var artist = NormalizeIdentity(
            string.IsNullOrWhiteSpace(album.ArtistForQuery) ? album.Artist : album.ArtistForQuery);
        var title = NormalizeIdentity(album.Title);
        return artist.Length == 0 || title.Length == 0
            ? null
            : $"{artist}\u001f{title}\u001f{album.FileCount}";
    }

    public static string? TrackDuplicateKey(DbMediaFile file)
    {
        var artist = NormalizeIdentity(file.Artist);
        var title = NormalizeIdentity(file.Title);
        return artist.Length == 0 || title.Length == 0
            ? null
            : $"{artist}\u001f{NormalizeIdentity(file.Album)}\u001f{title}";
    }

    public static string TrackIdentity(DbMediaFile file) =>
        $"{file.Id}\u001f{file.Path}\u001f{file.StartTime}";

    public static string AlbumIdentity(MediaLibraryAlbum album) =>
        $"{album.ArtistForQuery}\u001f{album.Title}\u001f{album.AlbumGroupingKey}\u001f{album.FirstFileInAlbum}";

    public static string VideoIdentity(DbVideoFile video) => $"{video.Id}\u001f{video.Path}";

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

    private static int CompareTrackSemantic(DbMediaFile first, DbMediaFile second, MediaFilePageRequest request)
    {
        var result = request.Sort switch
        {
            MediaFileSortField.AlbumName => CompareText(first.Album, second.Album),
            MediaFileSortField.ArtistName => CompareText(first.Artist, second.Artist),
            MediaFileSortField.Genre => CompareText(first.Genre, second.Genre),
            MediaFileSortField.Year => ParseLeadingInt(first.Date).CompareTo(ParseLeadingInt(second.Date)),
            MediaFileSortField.DateAdded => first.DatabaseItemAddedDate.UtcTicks.CompareTo(second.DatabaseItemAddedDate.UtcTicks),
            MediaFileSortField.Duration => first.Duration.CompareTo(second.Duration),
            _ => CompareText(TrackTitle(first), TrackTitle(second)),
        };
        result = Direction(result, request.Ascending);
        if (result != 0)
        {
            return result;
        }

        if (request.Sort == MediaFileSortField.AlbumName)
        {
            result = ParseLeadingInt(first.DiscNumber).CompareTo(ParseLeadingInt(second.DiscNumber));
            if (result == 0)
            {
                result = ParseLeadingInt(first.TrackNumber).CompareTo(ParseLeadingInt(second.TrackNumber));
            }
        }

        return result != 0 || request.Sort == MediaFileSortField.Title
            ? result
            : CompareText(TrackTitle(first), TrackTitle(second));
    }

    private static int CompareAlbumSemantic(
        MediaLibraryAlbum first,
        MediaLibraryAlbum second,
        AlbumPageRequest request)
    {
        var result = request.Sort switch
        {
            AlbumSortField.ArtistName => CompareText(first.Artist, second.Artist),
            AlbumSortField.Year => ParseLeadingInt(first.Date).CompareTo(ParseLeadingInt(second.Date)),
            _ => CompareText(first.Title, second.Title),
        };
        result = Direction(result, request.Ascending);
        if (result == 0 && request.Sort == AlbumSortField.Title)
        {
            result = CompareExact(first.Title, second.Title);
        }
        else if (result == 0 && request.Sort == AlbumSortField.ArtistName)
        {
            result = CompareExact(first.Artist, second.Artist);
            if (result == 0)
            {
                result = CompareText(first.Title, second.Title);
            }

            if (result == 0)
            {
                result = CompareExact(first.Title, second.Title);
            }
        }
        else if (result == 0 && request.Sort == AlbumSortField.Year)
        {
            result = CompareText(first.Title, second.Title);
            if (result == 0)
            {
                result = CompareExact(first.Title, second.Title);
            }
        }

        if (result == 0)
        {
            result = CompareText(first.ArtistForQuery, second.ArtistForQuery);
        }

        if (result == 0)
        {
            result = CompareExact(first.ArtistForQuery, second.ArtistForQuery);
        }

        return result == 0 ? CompareExact(first.AlbumGroupingKey, second.AlbumGroupingKey) : result;
    }

    private static int CompareSourceTie<T>(
        RankedSourceItem<T> first,
        RankedSourceItem<T> second,
        int firstLocalId = 0,
        int secondLocalId = 0)
    {
        var result = first.Priority.CompareTo(second.Priority);
        if (result == 0)
        {
            result = string.Compare(first.SourceId.Value, second.SourceId.Value, StringComparison.Ordinal);
        }

        return result == 0 ? firstLocalId.CompareTo(secondLocalId) : result;
    }

    private static string TrackTitle(DbMediaFile file) =>
        string.IsNullOrWhiteSpace(file.Title)
            ? string.IsNullOrWhiteSpace(file.Path) ? string.Empty : Path.GetFileNameWithoutExtension(file.Path)
            : file.Title;

    private static int CompareText(string? first, string? second) =>
        string.Compare(first ?? string.Empty, second ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static int CompareExact(string? first, string? second) =>
        string.Compare(first ?? string.Empty, second ?? string.Empty, StringComparison.Ordinal);

    private static int Direction(int value, bool ascending) => ascending ? value : -value;

    private static (int Phase, long Key) ShuffleRank(int id, long seed)
    {
        var key = unchecked((long)((uint)id * 2654435761u));
        var pivot = ShufflePivot(seed);
        return (key >= pivot ? 0 : 1, key);
    }

    private static long ShufflePivot(long seed)
    {
        var value = unchecked((ulong)seed + 0x9e3779b97f4a7c15UL);
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return (uint)(value ^ (value >> 31));
    }
}