using System;
using System.Collections.Generic;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Shared base for library pages that present their items as a grid of tiles
/// (albums and artists). Closes <see cref="LibraryBrowsePageViewModelBase{TItem}"/> over the tile
/// model so a single view (<c>GridItemsPageView</c>) can render either page.
/// </summary>
public abstract class GridItemsPageViewModelBase : LibraryBrowsePageViewModelBase<LibraryTileModel>
{
    protected GridItemsPageViewModelBase(LibraryItemKind kind)
        : base(kind)
    {
    }

    /// <summary>Message shown beneath the progress indicator while the page loads.</summary>
    public string LoadingText => Localized(LoadingTextKey, "Loading...");

    /// <summary>Resource key for <see cref="LoadingText"/> (use <c>nameof(Resources.Key)</c>).</summary>
    protected string? LoadingTextKey { get; init; }

    protected override void ReleaseItems(IReadOnlyList<LibraryTileModel> discarded)
    {
        foreach (var tile in discarded)
        {
            tile.ReleaseArt();
        }
    }
}
