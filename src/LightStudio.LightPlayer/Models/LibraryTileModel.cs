using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Presentation model for an album or artist tile shown in the library grids.
/// </summary>
public sealed partial class LibraryTileModel : ObservableObject
{
    [ObservableProperty]
    private IImage? image;

    /// <summary>
    /// Album covers composited into a square collage for artist tiles. Empty for
    /// album tiles (which use <see cref="Image"/>) and for artists with no
    /// available album art (which keep the placeholder glyph).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCollage))]
    private IReadOnlyList<IImage> collageImages = [];

    /// <summary>Column and row count of the square collage: 1, 2, or 3.</summary>
    [ObservableProperty]
    private int collageColumns = 1;

    private bool artRequested;

    public LibraryTileModel(string title, string subtitle, ThumbnailKind thumbnailKind)
    {
        Title = title;
        Subtitle = subtitle;
        ThumbnailKind = thumbnailKind;
    }

    public string Title { get; }

    public string Subtitle { get; }

    public ThumbnailKind ThumbnailKind { get; }

    /// <summary>True when the tile has one or more album covers to composite.</summary>
    public bool HasCollage => CollageImages.Count > 0;

    /// <summary>Album/artist name used for sorting and grouping.</summary>
    public string ArtistName { get; init; } = string.Empty;

    /// <summary>Release year used for album sorting and grouping.</summary>
    public string Year { get; init; } = string.Empty;

    /// <summary>
    /// Database identity for this album/artist, used to open its detail page.
    /// Null for tiles that have no backing entity.
    /// </summary>
    public MediaLibraryItemIdentifier? Identifier { get; init; }

    /// <summary>Invoked when the tile is activated, to open its detail page.</summary>
    public IRelayCommand? OpenCommand { get; set; }

    /// <summary>Invoked from the hover overlay to play this album/artist/track.</summary>
    public IRelayCommand? PlayCommand { get; set; }

    /// <summary>Invoked from the hover overlay to append this album/artist/track to the queue.</summary>
    public IRelayCommand? AddToQueueCommand { get; set; }

    /// <summary>Loads this tile's artwork. Invoked lazily when the tile is realized.</summary>
    public Func<LibraryTileModel, Task>? ArtLoader { get; set; }

    /// <summary>Triggers a one-time artwork load for this tile, if a loader is set.</summary>
    public void RequestArt()
    {
        if (artRequested || ArtLoader is null)
        {
            return;
        }

        artRequested = true;
        _ = ArtLoader(this);
    }
}
