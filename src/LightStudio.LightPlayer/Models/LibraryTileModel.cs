using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightMediaRenderer.Avalonia.Controls;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Presentation model for an album or artist tile shown in the library grids.
/// </summary>
public sealed partial class LibraryTileModel : SelectionItemModel
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

    private readonly object artGate = new();
    private CancellationTokenSource? artRequest;
    private bool artLoaded;
    private int artHolders;
    private IRelayCommand? playNextCommand;
    private IRelayCommand? addToPlaylistCommand;
    private IRelayCommand? openArtistCommand;
    private IRelayCommand? openFolderCommand;

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

    /// <summary>Album/artist name used for sorting.</summary>
    public string ArtistName { get; init; } = string.Empty;

    /// <summary>Release year used for album sorting.</summary>
    public string Year { get; init; } = string.Empty;

    /// <summary>
    /// Database identity for this album/artist, used to open its detail page.
    /// Null for tiles that have no backing entity.
    /// </summary>
    public MediaLibraryItemIdentifier? Identifier { get; init; }

    public object? ArtworkKey => ThumbnailKind == ThumbnailKind.Album && Identifier is { } identifier
        ? ((identifier as LibraryItemReference)?.SourceId ?? LibrarySourceId.Local,
            identifier.ArtistName, identifier.AlbumName, identifier.AlbumGroupingKey)
        : null;

    /// <summary>Source-qualified representative media, used for artwork and visibility updates.</summary>
    public MediaReference? Media { get; init; }

    /// <summary>
    /// The single track this tile represents (home Upcoming/Recent/All-music tiles).
    /// When set, the tile exposes the same per-track context actions and drag payload
    /// as a <see cref="TrackRowModel"/> row; null for album/artist tiles.
    /// </summary>
    public TrackRowModel? Track { get; set; }

    /// <summary>True when this tile is backed by a single track (see <see cref="Track"/>).</summary>
    public bool IsTrackTile => Track is not null;

    public bool CanOpenContainingFolder => Track?.CanOpenContainingFolder ??
        (OpenFolderCommand is not null && PlatformServices.SupportsLocalFileAccess &&
         (Media?.SourceId.IsLocal == true ||
          Identifier is LibraryItemReference { UnifiedArtist: { } artist } &&
          artist.SourceIds.Any(sourceId => sourceId.IsLocal)));

    public bool HasArtistNavigation => OpenArtistCommand is not null;

    public bool HasFileActions => IsTrackTile || CanOpenContainingFolder;

    /// <summary>
    /// Whether this tile exposes its secondary actions: the pointer-over art overlay
    /// (with its play/append buttons) and drag &amp; drop. Track tiles also expose their context menu.
    /// Disabled for the home upcoming/recent and now-playing upcoming lists.
    /// </summary>
    public bool EnableTileActions { get; init; } = true;

    /// <summary>Invoked when the tile is activated, to open its detail page.</summary>
    public IRelayCommand? OpenCommand { get; set; }

    public IRelayCommand? OpenAlbumCommand { get; init; }

    public bool HasAlbumNavigation => OpenAlbumCommand is not null;

    /// <summary>Invoked from the hover overlay to play this album/artist/track.</summary>
    public IRelayCommand? PlayCommand { get; set; }

    public IRelayCommand? PlayNextCommand
    {
        get => playNextCommand ?? Track?.PlayNextCommand;
        set => playNextCommand = value;
    }

    /// <summary>Invoked from the hover overlay to append this album/artist/track to the queue.</summary>
    public IRelayCommand? AddToQueueCommand { get; set; }

    public IRelayCommand? AddToPlaylistCommand
    {
        get => addToPlaylistCommand ?? Track?.AddToPlaylistCommand;
        set => addToPlaylistCommand = value;
    }

    public IRelayCommand? OpenArtistCommand
    {
        get => openArtistCommand ?? Track?.OpenArtistCommand;
        set => openArtistCommand = value;
    }

    public IRelayCommand? OpenFolderCommand
    {
        get => openFolderCommand ?? Track?.OpenFolderCommand;
        set => openFolderCommand = value;
    }

    /// <summary>Loads this tile's artwork. Invoked lazily when the tile is realized.</summary>
    public Func<LibraryTileModel, CancellationToken, Task>? ArtLoader { get; set; }

    internal int ArtLoadFifoWindowSize { get; private set; } = 1;

    /// <summary>Device pixels covered by the shorter side of the artwork where it is displayed.</summary>
    internal int ArtPixelSize { get; private set; } = AlbumArtService.DefaultThumbnailPixelSize;

    internal FastImageCache? ArtCachePool { get; private set; }

    /// <summary>
    /// Registers one displayed view of this tile and triggers a one-time artwork load, if a loader
    /// is set. Pair every call with <see cref="ReleaseArt"/>.
    /// </summary>
    public void RequestArt() => RequestArt(1, AlbumArtService.DefaultThumbnailPixelSize);

    internal void RequestArt(int fifoWindowSize, int pixelSize, FastImageCache? cachePool = null)
    {
        CancellationTokenSource request;
        lock (artGate)
        {
            artHolders++;

            if (artLoaded || artRequest is not null || ArtLoader is null)
            {
                return;
            }

            ArtLoadFifoWindowSize = Math.Max(1, fifoWindowSize);
            ArtPixelSize = Math.Max(1, pixelSize);
            ArtCachePool = cachePool;
            artRequest = request = new CancellationTokenSource();
        }

        // Never run the loader inline: RequestArt is raised on the UI thread as containers realize.
        _ = Task.Run(() => RunArtLoaderAsync(request));
    }

    /// <summary>
    /// Loads the artwork again for the views still showing this tile, after its pixels were lost.
    /// </summary>
    internal void ReloadArt()
    {
        CancellationTokenSource request;
        lock (artGate)
        {
            if (artHolders == 0 || ArtLoader is null)
            {
                return;
            }

            artRequest?.Cancel();
            artLoaded = false;
            artRequest = request = new CancellationTokenSource();
        }

        Image = null;
        CollageImages = [];
        _ = Task.Run(() => RunArtLoaderAsync(request));
    }

    /// <summary>
    /// Drops one displayed view of this tile, freeing the artwork once the last one goes: when the
    /// tile scrolls out of the virtualized window, or when its page is torn down. The tile reloads
    /// the art if it is shown again.
    /// </summary>
    public void ReleaseArt()
    {
        var hadArt = Image is not null || CollageImages.Count > 0;

        lock (artGate)
        {
            // The same tile can be shown by two hosts at once while the shell swaps one for the
            // other (the wide page host handing over to the narrow library pager); freeing on the
            // first release would blank the copy that is taking over.
            if (artHolders > 0 && --artHolders > 0)
            {
                return;
            }

            artHolders = 0;
            artRequest?.Cancel();
            artRequest = null;

            // A tile whose loader found nothing has no art to reload, so it stays marked as loaded
            // and scrolling back over it does not re-run the loader against the same empty source.
            if (hadArt)
            {
                artLoaded = false;
            }
        }

        Image = null;
        CollageImages = [];
    }

    partial void OnImageChanged(IImage? oldValue, IImage? newValue) =>
        (oldValue as IDisposable)?.Dispose();

    partial void OnCollageImagesChanged(IReadOnlyList<IImage>? oldValue, IReadOnlyList<IImage> newValue) =>
        AlbumArtService.ReleaseImages(oldValue ?? []);

    private async Task RunArtLoaderAsync(CancellationTokenSource request)
    {
        try
        {
            if (ArtLoader is { } loader)
            {
                await loader(this, request.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (artGate)
            {
                if (ReferenceEquals(artRequest, request))
                {
                    artLoaded = true;
                    artRequest = null;
                }

                request.Dispose();
            }
        }
    }
}
