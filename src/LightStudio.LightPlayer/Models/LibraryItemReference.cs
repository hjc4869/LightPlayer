using System.Linq;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Models;

public sealed class LibraryItemReference : MediaLibraryItemIdentifier
{
    public LibrarySourceId SourceId { get; init; } = LibrarySourceId.Local;

    public UnifiedArtistReference? UnifiedArtist { get; init; }

    public static LibraryItemReference Album(LibrarySourceId sourceId, MediaLibraryAlbum album) => new()
    {
        Type = ItemType.Album,
        SourceId = sourceId,
        ArtistName = album.ArtistForQuery,
        AlbumName = album.Title,
        AlbumGroupingKey = album.AlbumGroupingKey,
    };

    public static LibraryItemReference Artist(UnifiedArtistReference artist) => new()
    {
        Type = ItemType.Artist,
        SourceId = artist.SourceIds.FirstOrDefault(LibrarySourceId.Local),
        ArtistName = artist.DisplayName,
        UnifiedArtist = artist,
    };

    public static LibraryItemReference Local(MediaLibraryItemIdentifier identifier) => new()
    {
        Type = identifier.Type,
        MediaFileEntityId = identifier.MediaFileEntityId,
        ArtistName = identifier.ArtistName,
        AlbumName = identifier.AlbumName,
        AlbumGroupingKey = identifier.AlbumGroupingKey,
    };

    public static LibraryItemReference Normalize(MediaLibraryItemIdentifier identifier) =>
        identifier as LibraryItemReference ?? Local(identifier);
}