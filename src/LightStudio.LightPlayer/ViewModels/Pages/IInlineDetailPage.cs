namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Implemented by a detail page view model that can be hosted inline inside an adaptive
/// master-detail view (for example the album/artist list page). The host toggles the page's
/// own header back button so it appears only in the narrow single-pane layout, where the
/// detail fills the page and back returns to the list.
/// </summary>
public interface IInlineDetailPage
{
    /// <summary>
    /// Whether the page shows its own header back button. Left <see langword="true"/> when the
    /// page is shown on its own route; set to the host's compact state when hosted inline.
    /// </summary>
    bool ShowBackButton { get; set; }
}
