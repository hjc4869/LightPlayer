using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace LightStudio.LightPlayer.Controls;

/// <summary>
/// A <see cref="ListBox"/> that commits selection on pointer <em>release</em> instead of press,
/// the same way <see cref="ComboBox"/> does. <see cref="QueuePane"/> reorders rows with a
/// press-and-drag gesture; selecting on release means the initial press no longer mutates the
/// selection, so a drag can begin -- and carry a whole multi-selection -- without the press first
/// collapsing it. Everything else about selection stays native: range selection (Shift), toggling
/// (Ctrl), Ctrl+A and the selection anchor are all still driven by the framework. That is exactly
/// what breaks when selection is instead intercepted on press from code-behind (handling the press
/// makes <c>SelectingItemsControl.UpdateSelectionFromEvent</c> bail out and never focuses the row).
/// </summary>
public class ReleaseSelectingListBox : ListBox
{
    // Behave, and be themed, exactly like a ListBox; only the moment selection is applied changes.
    protected override Type StyleKeyOverride => typeof(ListBox);

    /// <summary>
    /// Triggers selection on a left-button release that ends over the row, rather than on press.
    /// A press-and-drag therefore never selects (the release is consumed by the drag-and-drop loop
    /// or lands outside the row's bounds), while a plain click still selects on release. Right
    /// clicks are intentionally excluded so opening a row's context menu doesn't disturb a
    /// multi-selection.
    /// </summary>
    protected override bool ShouldTriggerSelection(Visual selectable, PointerEventArgs eventArgs)
    {
        if (eventArgs.RoutedEvent != PointerReleasedEvent)
        {
            return false;
        }

        var point = eventArgs.GetCurrentPoint(selectable);
        return point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased
            && new Rect(selectable.Bounds.Size).Contains(point.Position);
    }
}
