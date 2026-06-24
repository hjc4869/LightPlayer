using System;
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

    public LibraryTile()
    {
        InitializeComponent();

        // Drag an album/artist tile onto the now-playing queue to insert its tracks.
        AddHandler(PointerPressedEvent, OnTilePointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnTilePointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnTilePointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnTilePointerCaptureLost, handledEventsToo: true);
    }

    private void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Let the hover overlay's play/append buttons handle their own presses.
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            drag.Disarm();
            return;
        }

        drag.Arm(e, this);
    }

    private void OnTilePointerMoved(object? sender, PointerEventArgs e) =>
        drag.Update(e, this, BuildDragData, DragDropEffects.Copy);

    private void OnTilePointerReleased(object? sender, PointerReleasedEventArgs e) => drag.Disarm();

    private void OnTilePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => drag.Disarm();

    private IDataTransfer? BuildDragData()
    {
        // Only real library tiles (with a backing identity) can be resolved to tracks.
        if (DataContext is not LibraryTileModel { Identifier: { } } tile)
        {
            return null;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(
            QueueDragFormats.LibraryInsert,
            new LibraryInsertPayload
            {
                Kind = tile.ThumbnailKind == ThumbnailKind.Artist ? LibraryInsertKind.Artist : LibraryInsertKind.Album,
                Identifier = tile.Identifier,
            }));
        return data;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Tiles are recycled by the virtualizing grid; ask the freshly bound item
        // to load its artwork so only on-screen albums extract covers.
        (DataContext as LibraryTileModel)?.RequestArt();
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
