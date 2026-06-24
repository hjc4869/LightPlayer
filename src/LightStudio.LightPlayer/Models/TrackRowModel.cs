using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Presentation model for a single row in the item list. Carries the display
/// columns plus, for real library rows, the identity and context commands the
/// shell needs to queue and play the track.
/// </summary>
public partial class TrackRowModel : ObservableObject
{
    private readonly TrackActionCallbacks? callbacks;

    [ObservableProperty]
    private bool isPlaying;

    public TrackRowModel(string title, string artist, string album, string year, string genre, string duration)
    {
        Title = title;
        Artist = artist;
        Album = album;
        Year = year;
        Genre = genre;
        Duration = duration;

        PlayCommand = new RelayCommand(() => this.callbacks?.Play?.Invoke(this));
        PlayNextCommand = new RelayCommand(() => this.callbacks?.PlayNext?.Invoke(this));
        AddToQueueCommand = new RelayCommand(() => this.callbacks?.AddToQueue?.Invoke(this));
        AddToPlaylistCommand = new RelayCommand(() => this.callbacks?.AddToPlaylist?.Invoke(this));
        PropertiesCommand = new RelayCommand(() => this.callbacks?.ShowProperties?.Invoke(this));
        OpenFolderCommand = new RelayCommand(() => this.callbacks?.OpenContainingFolder?.Invoke(this));
        RemoveFromPlaylistCommand = new RelayCommand(() => this.callbacks?.RemoveFromPlaylist?.Invoke(this));
    }

    public TrackRowModel(
        int mediaFileId,
        string filePath,
        string title,
        string artist,
        string album,
        string year,
        string genre,
        string duration,
        TimeSpan durationTime,
        int discNumber,
        int trackNumber,
        DateTimeOffset dateAdded,
        TrackActionCallbacks? callbacks,
        int? startTimeMs = null)
        : this(title, artist, album, year, genre, duration)
    {
        MediaFileId = mediaFileId;
        FilePath = filePath;
        DurationTime = durationTime;
        DiscNumber = discNumber;
        TrackNumber = trackNumber;
        DateAdded = dateAdded;
        StartTimeMs = startTimeMs;
        this.callbacks = callbacks;
    }

    public string Title { get; }

    public string Artist { get; }

    public string Album { get; }

    public string Year { get; }

    public string Genre { get; }

    public string Duration { get; }

    public int MediaFileId { get; }

    public string FilePath { get; } = string.Empty;

    public TimeSpan DurationTime { get; }

    public int DiscNumber { get; }

    public int TrackNumber { get; }

    public DateTimeOffset DateAdded { get; }

    /// <summary>
    /// Cue start offset in milliseconds when this row is a CUE-sheet track (0 is valid for the
    /// first track); null for a plain media file. Carried into the playback queue so the engine
    /// decodes only the track's segment instead of the whole backing file.
    /// </summary>
    public int? StartTimeMs { get; }

    public IRelayCommand PlayCommand { get; }

    public IRelayCommand PlayNextCommand { get; }

    public IRelayCommand AddToQueueCommand { get; }

    public IRelayCommand AddToPlaylistCommand { get; }

    public IRelayCommand PropertiesCommand { get; }

    public IRelayCommand OpenFolderCommand { get; }

    public IRelayCommand RemoveFromPlaylistCommand { get; }

    /// <summary>True when this row belongs to a playlist, enabling the remove action.</summary>
    public bool IsPlaylistItem { get; init; }

    /// <summary>
    /// Resolves the sibling rows (in current display order) this row is shown
    /// alongside. Activating the row replaces the now-playing queue with this whole
    /// list and starts at this row.
    /// A null provider (ad-hoc rows) falls back to single-track playback.
    /// </summary>
    public Func<IReadOnlyList<TrackRowModel>>? PlaybackContext { get; set; }
}
