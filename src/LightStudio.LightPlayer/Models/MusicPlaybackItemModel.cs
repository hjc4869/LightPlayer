using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Presentation model for a single track shown in the now-playing queue. Adds
/// the source file path the playback engine decodes. Title/artist/album/duration
/// are observable so items added by file path (file picker, restore) can be
/// enriched with real tags once the decoder reports them.
/// </summary>
public partial class MusicPlaybackItemModel : ObservableObject
{
    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArtistAlbum))]
    private string title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArtistAlbum))]
    private string artist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArtistAlbum))]
    private string album;

    [ObservableProperty]
    private TimeSpan duration;

    public MusicPlaybackItemModel(string title, string artist, string album, TimeSpan duration, string filePath = "", int? startTimeMs = null)
    {
        this.title = title;
        this.artist = artist;
        this.album = album;
        this.duration = duration;
        FilePath = filePath;
        StartTimeMs = startTimeMs;
    }

    /// <summary>Absolute path of the media file backing this queue item.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Cue start offset, in milliseconds, when this entry is a CUE-sheet track (0 is a valid
    /// value for the first track). Null for a plain media file. Several queue items that share
    /// the same <see cref="FilePath"/> but differ in <see cref="StartTimeMs"/> are distinct
    /// CUE tracks of one audio file.
    /// </summary>
    public int? StartTimeMs { get; }

    /// <summary>True when this entry decodes only a CUE segment of <see cref="FilePath"/>.</summary>
    public bool IsCueTrack => StartTimeMs.HasValue;

    public string ArtistAlbum =>
        string.IsNullOrWhiteSpace(Album) ? Artist
        : string.IsNullOrWhiteSpace(Artist) ? Album
        : $"{Artist}, {Album}";
}
