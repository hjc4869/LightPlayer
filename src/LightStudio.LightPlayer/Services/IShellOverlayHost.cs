using System.Threading.Tasks;
using Avalonia.Controls;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// A host that can present modal content inside the application shell rather than in a separate
/// OS window. Single-view platforms (Android) cannot open <see cref="Window"/> instances, so
/// dialogs are rendered as an in-shell overlay instead. Implemented by the shell view model.
/// </summary>
public interface IShellOverlayHost
{
    /// <summary>True while an overlay is currently presented.</summary>
    bool IsOverlayOpen { get; }

    /// <summary>
    /// Presents <paramref name="content"/> as a modal overlay centered over the shell. The returned
    /// task completes when the overlay is dismissed (by a button, the scrim, or the back gesture).
    /// Any overlay already open is replaced.
    /// </summary>
    Task ShowOverlayAsync(Control content);

    /// <summary>Dismisses the current overlay, if any. Returns true when one was open.</summary>
    bool CloseOverlay();
}
