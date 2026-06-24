using LightStudio.MediaLibraryCore.Database.Entities;
using System;

namespace LightStudio.MediaLibraryCore.Database;

public class MediaLibraryItemIdentifier
{
    public enum ItemType
    {
        MediaFile,
        Album,
        Artist,

        Unknown = int.MaxValue
    }

    public ItemType Type { get; set; }
    public int MediaFileEntityId { get; set; }
    public string ArtistName { get; set; }
    public string AlbumName { get; set; }

    public static implicit operator MediaLibraryItemIdentifier(DbMediaFile mediaFile)
    {
        return new MediaLibraryItemIdentifier
        {
            Type = ItemType.MediaFile,
            MediaFileEntityId = mediaFile.Id
        };
    }

    public static implicit operator MediaLibraryItemIdentifier(MediaLibraryAlbum album)
    {
        return new MediaLibraryItemIdentifier
        {
            Type = ItemType.Album,
            AlbumName = album.Title,
            ArtistName = album.ArtistForQuery
        };
    }

    public static implicit operator MediaLibraryItemIdentifier(MediaLibraryArtist artist)
    {
        return new MediaLibraryItemIdentifier
        {
            Type = ItemType.Artist,
            ArtistName = artist.Name
        };
    }
}

public class MediaLibraryAlbum
{
    public string Title { get; set; }

    public string ArtistForQuery { get; set; }

    public string Artist { get; set; }

    public string Genre { get; set; }

    public string Date { get; set; }

    public string FirstFileInAlbum { get; set; }

    public int FileCount { get; set; }

    public DateTimeOffset DatabaseItemAddedDate { get; set; }

    public override string ToString()
    {
        var description = Title;
        if (!string.IsNullOrWhiteSpace(Date)) description += $" ({Date})";
        if (!string.IsNullOrWhiteSpace(Artist)) description += $", {Artist}";
        return $"{description}, {FileCount} items";
    }
}

public class MediaLibraryArtist
{
    public string Name { get; set; }

    public int FileCount { get; set; }

    public int AlbumCount { get; set; }

    public DateTimeOffset DatabaseItemAddedDate { get; set; }

    public override string ToString()
    {
        var description = Name;
        return $"{description}, {AlbumCount} albums, {FileCount} items";
    }
}