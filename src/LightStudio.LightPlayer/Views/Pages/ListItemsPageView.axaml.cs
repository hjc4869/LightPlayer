using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.Controls;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class ListItemsPageView : UserControl
{
    private readonly DragInitiator drag = new();
    private LibraryTileModel? pressedItem;
    private bool scrollTracked;

    public ListItemsPageView()
    {
        InitializeComponent();

        // Drag an album/artist row onto the now-playing queue to insert its tracks. The rows are
        // selectable list items, so listen for handled events too (ListBox marks pointer events
        // handled for selection).
        MasterList.AddHandler(PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        MasterList.AddHandler(PointerMovedEvent, OnRowPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        MasterList.AddHandler(PointerReleasedEvent, OnRowPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        MasterList.AddHandler(PointerCaptureLostEvent, OnRowPointerCaptureLost, handledEventsToo: true);

        GroupedView.ItemsControl.AddHandler(PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        GroupedView.ItemsControl.AddHandler(PointerMovedEvent, OnRowPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        GroupedView.ItemsControl.AddHandler(PointerReleasedEvent, OnRowPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        GroupedView.ItemsControl.AddHandler(PointerCaptureLostEvent, OnRowPointerCaptureLost, handledEventsToo: true);
        BrowseScrollMemory.Track(this, GroupedView.ScrollViewer);

        // The list virtualizes, so its scroll viewer only exists once the template is realized;
        // wire scroll-offset memory after the first load.
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (scrollTracked)
        {
            return;
        }

        if (MasterList.FindDescendantOfType<ScrollViewer>() is { } scrollViewer)
        {
            scrollTracked = true;
            BrowseScrollMemory.Track(this, scrollViewer);
            IncrementalLoadingScroll.Track(this, scrollViewer);
        }
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        pressedItem = (e.Source as Visual)?.FindAncestorOfType<SelectionItem>(includeSelf: true)?.DataContext as LibraryTileModel
            ?? (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as LibraryTileModel;
        if (pressedItem is { IsSelectionMode: false })
        {
            drag.Arm(e, this);
        }
        else
        {
            drag.Disarm();
        }
    }

    private void OnRowPointerMoved(object? sender, PointerEventArgs e) =>
        drag.Update(e, this, BuildDragData, DragDropEffects.Copy);

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        drag.Disarm();
        pressedItem = null;
    }

    private void OnRowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        drag.Disarm();
        pressedItem = null;
    }

    private IDataTransfer? BuildDragData()
    {
        // An album/artist row drags by identity; the shell resolves it to the album's/artist's
        // tracks (in disc/track order) when it lands on the queue.
        if (pressedItem is not { Identifier: { } identifier } tile)
        {
            return null;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(
            QueueDragFormats.LibraryInsert,
            new LibraryInsertPayload
            {
                Kind = tile.ThumbnailKind == ThumbnailKind.Artist ? LibraryInsertKind.Artist : LibraryInsertKind.Album,
                Identifier = identifier,
            }));
        return data;
    }
}
