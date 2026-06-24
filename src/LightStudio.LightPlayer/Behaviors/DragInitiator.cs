using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;

namespace LightStudio.LightPlayer.Behaviors;

/// <summary>
/// Helper that turns a pointer press-and-drag gesture into an Avalonia
/// <see cref="DragDrop.DoDragDropAsync"/> call. The press args are cached and the
/// drag only begins once the pointer moves past a small threshold, so taps,
/// double-taps and button clicks on the same control are preserved.
/// </summary>
/// <remarks>
/// <see cref="DragDrop.DoDragDropAsync"/> requires the original
/// <see cref="PointerPressedEventArgs"/>; the cached instance stays valid for the
/// duration of the active pointer gesture, which is all the platform drag sources need.
/// </remarks>
internal sealed class DragInitiator
{
    private const double DragThreshold = 4;

    private PointerPressedEventArgs? pressed;
    private Point origin;

    /// <summary>Records a potential drag start. Call from a pointer-pressed handler.</summary>
    public void Arm(PointerPressedEventArgs e, Visual reference)
    {
        // Internal drags are mouse/pen only. On a touch screen a press-and-move is a tap or a
        // scroll, so starting a drag there fights with those gestures and cripples usability;
        // ignore touch entirely. External file drops don't come through here, so they still work.
        if (e.Pointer.Type == PointerType.Touch)
        {
            Disarm();
            return;
        }

        if (!e.GetCurrentPoint(reference).Properties.IsLeftButtonPressed)
        {
            Disarm();
            return;
        }

        pressed = e;
        origin = e.GetPosition(reference);
    }

    /// <summary>Cancels a pending drag (pointer released / capture lost).</summary>
    public void Disarm()
    {
        pressed = null;
        origin = default;
    }

    /// <summary>
    /// Starts the drag once the pointer has moved past the threshold. The
    /// <paramref name="dataFactory"/> builds the drag payload; returning null
    /// aborts (e.g. nothing draggable under the pointer). Call from a
    /// pointer-moved handler.
    /// </summary>
    public async void Update(
        PointerEventArgs e,
        Visual reference,
        Func<IDataTransfer?> dataFactory,
        DragDropEffects effects)
    {
        if (pressed is not { } trigger)
        {
            return;
        }

        var current = e.GetPosition(reference);
        if (Math.Abs(current.X - origin.X) < DragThreshold && Math.Abs(current.Y - origin.Y) < DragThreshold)
        {
            return;
        }

        // Clear before awaiting so a cancelled/failed drag doesn't leave us armed.
        Disarm();

        var data = dataFactory();
        if (data is null)
        {
            return;
        }

        try
        {
            await DragDrop.DoDragDropAsync(trigger, data, effects);
        }
        catch
        {
            // The drag may be cancelled or the platform source unavailable; ignore.
        }
    }
}
