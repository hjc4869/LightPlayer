using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class PlaylistsPageView : UserControl
{
    private const double CompactThreshold = 650;

    private readonly DragInitiator drag = new();
    private PlaylistModel? pressedPlaylist;

    public PlaylistsPageView()
    {
        InitializeComponent();

        PlaylistsList.SelectionChanged += OnSelectionChanged;

        // Drag a playlist row onto the now-playing queue to insert its tracks. The rows
        // are selectable list items, so listen for handled events too.
        PlaylistsList.AddHandler(PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        PlaylistsList.AddHandler(PointerMovedEvent, OnRowPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        PlaylistsList.AddHandler(PointerReleasedEvent, OnRowPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        PlaylistsList.AddHandler(PointerCaptureLostEvent, OnRowPointerCaptureLost, handledEventsToo: true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            ApplyAdaptiveLayout(Bounds.Width);
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ApplyAdaptiveLayout(Bounds.Width);

    // Master-detail: the list and the selected playlist sit side by side while there is
    // room, then collapse to a single pane with a back button on narrow screens. A null
    // selection means "show the list".
    private void ApplyAdaptiveLayout(double width)
    {
        if (RootGrid is null || MasterPane is null || DetailPane is null ||
            PlaylistsList is null || width <= 0)
        {
            return;
        }

        var isCompact = width < CompactThreshold;
        var hasSelection = PlaylistsList.SelectedItem is not null;

        // The inline playlist detail now owns the back affordance in its header. Tell the
        // page view model whether we are in the narrow single-pane layout so it shows the
        // detail's back button only then (and hides it in the wide two-pane layout).
        if (DataContext is PlaylistsPageViewModel vm)
        {
            vm.IsCompact = isCompact;
        }

        if (!isCompact)
        {
            RootGrid.ColumnDefinitions[0].Width = new GridLength(300);
            RootGrid.ColumnDefinitions[1].Width = GridLength.Star;
            MasterPane.IsVisible = true;
            DetailPane.IsVisible = true;
            DetailPane.Margin = new Thickness(10, 0, 0, 0);
            return;
        }

        DetailPane.Margin = new Thickness(0);
        if (hasSelection)
        {
            RootGrid.ColumnDefinitions[0].Width = new GridLength(0);
            RootGrid.ColumnDefinitions[1].Width = GridLength.Star;
            MasterPane.IsVisible = false;
            DetailPane.IsVisible = true;
        }
        else
        {
            RootGrid.ColumnDefinitions[0].Width = GridLength.Star;
            RootGrid.ColumnDefinitions[1].Width = new GridLength(0);
            MasterPane.IsVisible = true;
            DetailPane.IsVisible = false;
        }
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        pressedPlaylist = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as PlaylistModel;
        if (pressedPlaylist is not null)
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
