using System.Threading.Tasks;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public interface IScrollPositionPageViewModel
{
    double ScrollOffset { get; set; }
}

/// <summary>
/// Implemented by the music, albums, and artists pages so the shell can push
/// persisted sorting settings and trigger an asynchronous load.
/// </summary>
public interface ILibraryBrowsePageViewModel : IScrollPositionPageViewModel
{
    LibraryItemKind Kind { get; }

    void ApplyViewSettings(LibraryViewSettings settings);

    Task LoadAsync();
}

/// <summary>Implemented by browse pages that can fetch another bounded result page.</summary>
public interface IIncrementalLoadingViewModel
{
    bool CanLoadMore { get; }

    int LoadedItemCount { get; }

    int TotalItemCount { get; }

    Task LoadMoreAsync();
}
