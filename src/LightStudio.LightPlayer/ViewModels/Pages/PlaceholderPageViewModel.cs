using System.Collections.Generic;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public enum PageContentKind
{
    Placeholder,
    Tiles,
    Tracks,
    Settings,
    Empty,
}

/// <summary>
/// View model for a routed content page. Until the dedicated pages land in later
/// migration tasks, this renders a small set of content shapes (tiles, tracks,
/// settings rows, empty state, or a labelled placeholder) so the shell layout
/// matches the target UI.
/// </summary>
public class PlaceholderPageViewModel : PageViewModelBase
{
    public PlaceholderPageViewModel()
        : this(AppRoutes.Home)
    {
    }

    public PlaceholderPageViewModel(AppRoute route)
    {
        Route = route;
    }

    public AppRoute Route { get; }

    public string RouteId => Route.Id.ToString();

    public string Kind => Route.Kind == AppRouteKind.TopLevel ? "Top-level route" : "Detail route";

    public PageContentKind ContentKind { get; init; } = PageContentKind.Placeholder;

    public IReadOnlyList<LibraryTileModel> Tiles { get; init; } = [];

    public IReadOnlyList<TrackRowModel> Tracks { get; init; } = [];

    public IReadOnlyList<LibraryFolderModel> Folders { get; init; } = [];

    public string EmptyHeader { get; init; } = "It's lonely here.";

    public string EmptyDescription { get; init; } = "Add a library folder to get started.";

    public bool IsTiles => ContentKind == PageContentKind.Tiles;

    public bool IsTracks => ContentKind == PageContentKind.Tracks;

    public bool IsSettings => ContentKind == PageContentKind.Settings;

    public bool IsEmpty => ContentKind == PageContentKind.Empty;

    public bool IsPlaceholder => ContentKind == PageContentKind.Placeholder;

    public static PlaceholderPageViewModel Placeholder(AppRoute route) =>
        new(route) { Title = route.Title, ContentKind = PageContentKind.Placeholder };

    public static PlaceholderPageViewModel ForSettings(AppRoute route, IReadOnlyList<LibraryFolderModel> folders) =>
        new(route) { Title = route.Title, ContentKind = PageContentKind.Settings, Folders = folders };

    public static PlaceholderPageViewModel ForEmpty(AppRoute route, string header, string description) =>
        new(route) { Title = route.Title, ContentKind = PageContentKind.Empty, EmptyHeader = header, EmptyDescription = description };
}