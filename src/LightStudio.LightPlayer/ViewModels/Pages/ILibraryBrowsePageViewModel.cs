using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Implemented by the music, albums, and artists pages so the shell can push
/// persisted sort/grouping settings and trigger an asynchronous load.
/// </summary>
public interface ILibraryBrowsePageViewModel
{
    LibraryItemKind Kind { get; }

    /// <summary>
    /// Last vertical scroll offset of the page's grid/list. Persisted on the view model so that
    /// returning to this page (for example via Back from a detail page) restores the position the
    /// user left off at instead of jumping back to the top.
    /// </summary>
    double ScrollOffset { get; set; }

    void ApplyViewSettings(LibraryViewSettings settings);

    Task LoadAsync();
}
