using LightStudio.MediaLibraryCore.CueIndex;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace LightStudio.MediaLibraryCore.Database.Entities;

/// <summary>
/// Standard media file entity class.
/// </summary>
public class DbMediaFile
{
    [Key]
    public int Id { get; set; }

    public TimeSpan Duration { get; set; }

    public string TotalDiscs { get; set; }

    public string DiscNumber { get; set; }

    public string TotalTracks { get; set; }

    public string Description { get; set; }

    public string Copyright { get; set; }

    public string Comments { get; set; }

    public string Grouping { get; set; }

    public string Genre { get; set; }

    public string TrackNumber { get; set; }

    public string AlbumArtist { get; set; }

    public string Performer { get; set; }

    public string Composer { get; set; }

    public string Date { get; set; }

    public string Album { get; set; }

    public string Artist { get; set; }

    public string Title { get; set; }

    public string Path { get; set; }

    public DateTimeOffset DatabaseItemAddedDate { get; set; }

    public DateTimeOffset FileLastModifiedDate { get; set; }

    [NotMapped]
    public bool IsExternal { get; set; }

    [NotMapped]
    public Guid ExternalFileId { get; set; }

    [NotMapped]
    [JsonIgnore]
    public ManagedAudioIndexCue MediaCue
    {
        get
        {
            if (StartTime.HasValue)
            {
                return new ManagedAudioIndexCue
                {
                    Duration = Duration,
                    StartTime = TimeSpan.FromMilliseconds(StartTime.Value)
                };
            }
            else
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Start time in milliseconds.
    /// store cue track info.
    /// set to null when the media file is not associated to any CUE file.
    /// </summary>
    public int? StartTime { get; set; }

    /// <summary>
    /// Returns the string that can be used to uniquely identify a DbMediaFile entity.
    /// </summary>
    /// <returns>DbMediaFile identifier</returns>
    public override string ToString()
    {
        if (StartTime.HasValue)
        {
            return $"{Path.ToLower()}|{StartTime.Value}|{Duration.TotalMilliseconds}";
        }
        else
        {
            return Path.ToLower();
        }
    }
}

/// <summary>
/// Playback history entity class
/// </summary>
public class DbPlaybackHistory
{
    [Key]
    public int Id { get; set; }

    public int RelatedMediaFileId { get; set; }
    public DbMediaFile RelatedMediaFile { get; set; }
}