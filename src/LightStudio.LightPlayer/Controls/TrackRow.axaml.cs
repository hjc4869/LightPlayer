using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Behaviors;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Controls;

public partial class TrackRow : UserControl
{
    private readonly DragInitiator drag = new();

    public TrackRow()
    {
        InitializeComponent();

        // Drag a track onto the now-playing queue to insert it. Listen even for handled
        // pointer events so a press anywhere on the row arms the drag.
        AddHandler(PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnRowPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnRowPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnRowPointerCaptureLost, handledEventsToo: true);
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Let the inline play/append buttons handle their own presses.
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            drag.Disarm();
            return;
        }

        drag.Arm(e, this);
    }

    private void OnRowPointerMoved(object? sender, PointerEventArgs e) =>
        drag.Update(e, this, BuildDragData, DragDropEffects.Copy);

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e) => drag.Disarm();

    private void OnRowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => drag.Disarm();

    private IDataTransfer? BuildDragData()
    {
        if (DataContext is not TrackRowModel track)
        {
            return null;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(
            QueueDragFormats.LibraryInsert,
            new LibraryInsertPayload { Kind = LibraryInsertKind.Track, Track = track }));
        return data;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            ApplyAdaptiveLayout(Bounds.Width);
        }
    }

    // Collapse to a stacked two-line layout below 650px, then progressively
    // reveal Album (>=750), Year (>=850) and Genre (>=1000) as the row gains width.
    private void ApplyAdaptiveLayout(double width)
    {
        if (WideRoot is null || NarrowRoot is null || WideRoot.ColumnDefinitions.Count < 6)
        {
            return;
        }

        var useWide = width >= 650;
        WideRoot.IsVisible = useWide;
        NarrowRoot.IsVisible = !useWide;
        if (!useWide)
        {
            return;
        }

        var showAlbum = width >= 750;
        var showYear = width >= 850;
        var showGenre = width >= 1000;

        WideRoot.ColumnDefinitions[2].Width = showAlbum ? GridLength.Star : new GridLength(0);
        AlbumText.IsVisible = showAlbum;
        WideRoot.ColumnDefinitions[3].Width = showYear ? new GridLength(70) : new GridLength(0);
        YearText.IsVisible = showYear;
        WideRoot.ColumnDefinitions[4].Width = showGenre ? new GridLength(90) : new GridLength(0);
        GenreText.IsVisible = showGenre;
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        // Touch users expect a single tap to play; mouse/pen keep the double-tap
        // behavior handled by OnDoubleTapped.
        if (e.Pointer.Type == PointerType.Touch)
        {
            TryPlay(e);
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Touch already played on the single tap (see OnTapped); don't replay.
        if (e.Pointer.Type != PointerType.Touch)
        {
            TryPlay(e);
        }
    }

    private void TryPlay(TappedEventArgs e)
    {
        // The inline play/append buttons run their own commands; don't also
        // trigger playback from the row.
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (DataContext is TrackRowModel track && track.PlayCommand.CanExecute(null))
        {
            track.PlayCommand.Execute(null);
            e.Handled = true;
        }
    }
}
