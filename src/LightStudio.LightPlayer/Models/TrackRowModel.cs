using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Presentation model for a single row in the item list. Carries the display
/// columns plus, for real library rows, the identity and context commands the
/// shell needs to queue and play the track.
/// </summary>
public partial class TrackRowModel : SelectionItemModel
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
        OpenAlbumCommand = new RelayCommand(() => this.callbacks?.OpenAlbum?.Invoke(this));
        OpenArtistCommand = new RelayCommand(() => this.callbacks?.OpenArtist?.Invoke(this));
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
        int? startTimeMs = null,
        LibrarySourceId? sourceId = null)
        : this(title, artist, album, year, genre, duration)
    {
        MediaFileId = mediaFileId;
        FilePath = filePath;
        DurationTime = durationTime;
        DiscNumber = discNumber;
        TrackNumber = trackNumber;
        DateAdded = dateAdded;
        StartTimeMs = startTimeMs;
        SourceId = sourceId ?? LibrarySourceId.Local;
        this.callbacks = callbacks;
    }

    public TrackRowModel(
        MusicFolderReference folder,
        string title,
        Action<MusicFolderReference> open,
        TrackActionCallbacks? callbacks = null)
        : this(title, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty)
    {
        Folder = folder;
        FilePath = folder.Path ?? string.Empty;
        SourceId = folder.SourceId;
        this.callbacks = callbacks;
        PlayCommand = new RelayCommand(() => open(folder));
        OpenMusicFolderCommand = new RelayCommand(() => open(folder));
        FolderPlayCommand = new AsyncRelayCommand(() => callbacks?.PlayFolder?.Invoke(folder) ?? Task.CompletedTask);
        FolderPlayNextCommand = new AsyncRelayCommand(() => callbacks?.PlayFolderNext?.Invoke(folder) ?? Task.CompletedTask);
        FolderAddToQueueCommand = new AsyncRelayCommand(() => callbacks?.AddFolderToQueue?.Invoke(folder) ?? Task.CompletedTask);
        FolderAddToPlaylistCommand = new AsyncRelayCommand(() => callbacks?.AddFolderToPlaylist?.Invoke(folder) ?? Task.CompletedTask);
    }

    public MusicFolderReference? Folder { get; }

    public bool IsFolder => Folder is not null;

    public string Title { get; }

    public string Artist { get; }

    public string Album { get; }

    public string Year { get; }

    public string Genre { get; }

    public string Duration { get; }

    public int MediaFileId { get; }

    public string FilePath { get; } = string.Empty;

    public LibrarySourceId SourceId { get; } = LibrarySourceId.Local;

    public MediaReference Media => new(SourceId, FilePath);

    public MediaItemKey MediaKey => new(Media, StartTimeMs);

    public bool CanOpenContainingFolder =>
        !IsFolder && SourceId.IsLocal && PlatformServices.SupportsLocalFileAccess;

    public bool CanOpenAlbum => !IsFolder && !HideAlbum;

    public bool CanOpenArtist => !IsFolder && !HideArtist;

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

    public IRelayCommand? OpenMusicFolderCommand { get; }

    public IAsyncRelayCommand? FolderPlayCommand { get; }

    public IAsyncRelayCommand? FolderPlayNextCommand { get; }

    public IAsyncRelayCommand? FolderAddToQueueCommand { get; }

    public IAsyncRelayCommand? FolderAddToPlaylistCommand { get; }

    public ICommand RowPlayCommand => IsFolder ? FolderPlayCommand! : PlayCommand;

    public ICommand RowAddToQueueCommand => IsFolder ? FolderAddToQueueCommand! : AddToQueueCommand;

    public IRelayCommand PlayNextCommand { get; }

    public IRelayCommand AddToQueueCommand { get; }

    public IRelayCommand AddToPlaylistCommand { get; }

    public IRelayCommand OpenAlbumCommand { get; }

    public IRelayCommand OpenArtistCommand { get; }

    public IRelayCommand PropertiesCommand { get; }

    public IRelayCommand OpenFolderCommand { get; }

    public IRelayCommand RemoveFromPlaylistCommand { get; }

    /// <summary>True when this row belongs to a playlist, enabling the remove action.</summary>
    public bool IsPlaylistItem { get; init; }

    /// <summary>
    /// When true the album column and "Go to album" context action are hidden.
    /// Set by the album detail page where showing the album again on every row is redundant.
    /// </summary>
    public bool HideAlbum { get; set; }

    /// <summary>
    /// When true the artist column and "Go to artist" context action are hidden.
    /// Set by the artist detail page where the artist name is already shown in the page header.
    /// </summary>
    public bool HideArtist { get; set; }

    /// <summary>
    /// Secondary text shown beneath the title in the narrow (single-column) layout.
    /// Omits whichever fields are suppressed by <see cref="HideAlbum"/> / <see cref="HideArtist"/>.
    /// </summary>
    public string SubText => IsFolder ? string.Empty : (HideArtist, HideAlbum) switch
    {
        (true, true)  => string.Empty,
        (true, false) => Album,
        (false, true) => Artist,
        _             => $"{Artist} · {Album}",
    };

    /// <summary>
    /// Resolves the sibling rows (in current display order) this row is shown
    /// alongside. Activating the row replaces the now-playing queue with this whole
    /// list and starts at this row.
    /// A null provider (ad-hoc rows) falls back to single-track playback.
    /// </summary>
    public Func<IReadOnlyList<TrackRowModel>>? PlaybackContext { get; set; }

    /// <summary>
    /// Resolves a complete ordered playback context when the visible list is loaded incrementally.
    /// </summary>
    public Func<Task<IReadOnlyList<TrackRowModel>>>? AsyncPlaybackContext { get; set; }
}
