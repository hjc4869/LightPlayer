namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// Implemented by a page view model that can consume a back gesture internally before the
/// shell pops its navigation stack. Master-detail pages use this to collapse their detail
/// pane back to the list on narrow layouts, so the first back returns to the page's own menu
/// and only a second back leaves the page.
/// </summary>
public interface IInPageBackHandler
{
    /// <summary>
    /// Whether the page could currently consume a back request (for example a master-detail page
    /// with an open detail on the narrow layout). Lets the shell decide whether to offer its global
    /// back button without performing the navigation. Implementers must raise
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> for this property
    /// when the answer changes so the shell can refresh the button.
    /// </summary>
    bool CanHandleBack { get; }

    bool CanPreviewBack => false;

    /// <summary>
    /// Attempts to handle a back request within the page. Returns <see langword="true"/> when
    /// the page consumed the gesture, so the shell should not navigate back.
    /// </summary>
    bool TryHandleBack();
}
