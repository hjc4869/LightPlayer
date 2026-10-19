using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class PlaylistsPageView : UserControl
{
    private readonly DragInitiator drag = new();
    private PlaylistModel? pressedPlaylist;

    public PlaylistsPageView()
    {
        InitializeComponent();

        // Drag a playlist row onto the now-playing queue to insert its tracks. The rows
        // are selectable list items, so listen for handled events too.
        PlaylistsList.AddHandler(PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        PlaylistsList.AddHandler(PointerMovedEvent, OnRowPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        PlaylistsList.AddHandler(PointerReleasedEvent, OnRowPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        PlaylistsList.AddHandler(PointerCaptureLostEvent, OnRowPointerCaptureLost, handledEventsToo: true);
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only music playlists can be dropped on the queue, so video rows are never draggable.
        pressedPlaylist = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as PlaylistModel;
        if (pressedPlaylist is { IsMusic: true })
        {
            drag.Arm(e, this);
        }
        else
        {
            pressedPlaylist = null;
            drag.Disarm();
        }
    }

    private void OnRowPointerMoved(object? sender, PointerEventArgs e) =>
        drag.Update(e, this, BuildDragData, DragDropEffects.Copy);

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        drag.Disarm();
        pressedPlaylist = null;
    }

    private void OnRowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        drag.Disarm();
        pressedPlaylist = null;
    }

    private IDataTransfer? BuildDragData()
    {
        if (pressedPlaylist is not { } playlist)
        {
            return null;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(
            QueueDragFormats.LibraryInsert,
            new LibraryInsertPayload { Kind = LibraryInsertKind.Playlist, Playlist = playlist }));
        return data;
    }
}
