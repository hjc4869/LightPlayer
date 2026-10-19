using LightMediaRenderer.Avalonia.Controls;
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Controls;

public partial class LibraryTile : UserControl
{
    private readonly DragInitiator drag = new();
    private LibraryTileModel? boundTile;
    private LibraryTileModel? artHolder;
    private bool attached;

    public LibraryTile()
    {
        InitializeComponent();

        // Drag an album/artist tile onto the now-playing queue to insert its tracks.
        AddHandler(PointerPressedEvent, OnTilePointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnTilePointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnTilePointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnTilePointerCaptureLost, handledEventsToo: true);

        // Suppress the right-click menu on shortcut tiles before the ContextFlyout opens.
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        AddHandler(FastImage.SourceLostEvent, (_, _) => artHolder?.ReloadArt());
    }

    private void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Shortcut tiles (home upcoming/recent, now-playing upcoming) aren't draggable.
        if (DataContext is LibraryTileModel { EnableTileActions: false })
        {
            drag.Disarm();
            return;
        }

        // Let the hover overlay's play/append buttons handle their own presses.
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            drag.Disarm();
            return;
        }

        drag.Arm(e, this);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is LibraryTileModel { EnableTileActions: false, Track: null })
        {
            e.Handled = true;
        }
    }

    private void OnTilePointerMoved(object? sender, PointerEventArgs e) =>
        drag.Update(e, this, BuildDragData, DragDropEffects.Copy);

    private void OnTilePointerReleased(object? sender, PointerReleasedEventArgs e) => drag.Disarm();

    private void OnTilePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => drag.Disarm();

    private IDataTransfer? BuildDragData()
    {
        // A track tile drags as its single track; an album/artist tile drags by identity.
        // Tiles without either (no backing entity) aren't draggable.
        LibraryInsertPayload? payload = DataContext switch
        {
            LibraryTileModel { Track: { } track } =>
                new LibraryInsertPayload { Kind = LibraryInsertKind.Track, Track = track },
            LibraryTileModel { Identifier: { } identifier } tile =>
                new LibraryInsertPayload
                {
                    Kind = tile.ThumbnailKind == ThumbnailKind.Artist ? LibraryInsertKind.Artist : LibraryInsertKind.Album,
                    Identifier = identifier,
                },
            _ => null,
        };

        if (payload is null)
        {
            return null;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(QueueDragFormats.LibraryInsert, payload));
        return data;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        boundTile = DataContext as LibraryTileModel;
        UpdateArtHold();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        UpdateArtHold();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        attached = false;
        UpdateArtHold();
    }

    // Exactly one artwork hold per tile control, taken while it is bound and on screen. Tiles are
    // recycled by the virtualizing grid and torn down when scrolled out of the realized window, so
    // dropping the hold there is what keeps decoded bitmaps from piling up; the item itself only
    // frees its artwork once no control holds it any more.
    private void UpdateArtHold()
    {
        var target = attached ? boundTile : null;
        if (ReferenceEquals(artHolder, target))
        {
            return;
        }

        artHolder?.ReleaseArt();
        artHolder = target;
        if (artHolder is null)
        {
            return;
        }

        var grid = this.FindAncestorOfType<UniformTileGrid>();
        artHolder.RequestArt(grid?.RealizationWindowSize ?? 1, ArtPixelSize(grid), FastImage.GetCachePool(this));
    }

    // Library grids scale the tile through a Viewbox so that it fills the grid's uniform cell.
    private int ArtPixelSize(UniformTileGrid? grid)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (grid is { ItemSize: { Width: > 0, Height: > 0 } cell } &&
            this.FindAncestorOfType<Viewbox>() is { } viewbox &&
            ReferenceEquals(viewbox.FindAncestorOfType<UniformTileGrid>(), grid))
        {
            scale *= Math.Max(1, Math.Min(cell.Width / Width, cell.Height / Height));
        }

        return (int)Math.Ceiling(ArtHost.Width * scale);
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        // Let the hover overlay's play/append buttons run their own commands without
        // also opening the detail page.
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (DataContext is LibraryTileModel tile && tile.OpenCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
