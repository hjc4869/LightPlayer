using LightStudio.MediaLibraryCore.Database.Entities;
using System;

namespace LightStudio.MediaLibraryCore.IO;

public static class MediaInfoExtensions
{
    public static DbMediaFile ToDbMediaFile(this MediaInfo info, DateTimeOffset lastModified)
    {
        return new DbMediaFile
        {
            Album = info.Album?.Trim(),
            AlbumArtist = info.AlbumArtist?.Trim(),
            Artist = info.Artist?.Trim(),
            Comments = info.Comments,
            Composer = info.Composer,
            Copyright = info.Copyright,
            Date = info.Date,
            Description = info.Description,
            DiscNumber = info.DiscNumber,
            Duration = info.Duration,
            Genre = info.Genre,
            Grouping = info.Grouping,
            Performer = info.Performer,
            Title = info.Title,
            TotalDiscs = info.TotalDiscs,
            TotalTracks = info.TotalTracks,
            TrackNumber = info.TrackNumber,
            FileLastModifiedDate = lastModified,
            DatabaseItemAddedDate = DateTimeOffset.UtcNow
        };
    }

    public static MediaInfo ToMediaInfo(this DbMediaFile info)
    {
        return new MediaInfo
        {
            Album = info.Album.Trim(),
            AlbumArtist = info.AlbumArtist.Trim(),
            Artist = info.Artist.Trim(),
            Comments = info.Comments,
            Composer = info.Composer,
            Copyright = info.Copyright,
            Date = info.Date,
            Description = info.Description,
            DiscNumber = info.DiscNumber,
            Duration = info.Duration,
            Genre = info.Genre,
            Grouping = info.Grouping,
            Performer = info.Performer,
            Title = info.Title,
            TotalDiscs = info.TotalDiscs,
            TotalTracks = info.TotalTracks,
            TrackNumber = info.TrackNumber
        };
    }
}
