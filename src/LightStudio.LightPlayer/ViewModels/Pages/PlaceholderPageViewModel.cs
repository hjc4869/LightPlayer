using System.Collections.Generic;
using LightStudio.LightPlayer.Models;

using Strings = LightStudio.LightPlayer.Strings;

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
/// View model for routed pages that display tiles, tracks, settings, an empty state,
/// or a labeled placeholder.
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

    public string EmptyHeader => Localized(EmptyHeaderKey ?? nameof(Strings.Resources.Home_EmptyHeader));

    public string EmptyDescription => Localized(EmptyDescriptionKey ?? nameof(Strings.Resources.Placeholder_EmptyDescription));

    /// <summary>Resource key for <see cref="EmptyHeader"/> (use <c>nameof(Resources.Key)</c>).</summary>
    public string? EmptyHeaderKey { get; init; }

    /// <summary>Resource key for <see cref="EmptyDescription"/> (use <c>nameof(Resources.Key)</c>).</summary>
    public string? EmptyDescriptionKey { get; init; }

    public bool IsTiles => ContentKind == PageContentKind.Tiles;

    public bool IsTracks => ContentKind == PageContentKind.Tracks;

    public bool IsSettings => ContentKind == PageContentKind.Settings;

    public bool IsEmpty => ContentKind == PageContentKind.Empty;

    public bool IsPlaceholder => ContentKind == PageContentKind.Placeholder;

    public static PlaceholderPageViewModel Placeholder(AppRoute route) =>
        new(route) { TitleKey = route.TitleKey, ContentKind = PageContentKind.Placeholder };

    public static PlaceholderPageViewModel ForSettings(AppRoute route, IReadOnlyList<LibraryFolderModel> folders) =>
        new(route) { TitleKey = route.TitleKey, ContentKind = PageContentKind.Settings, Folders = folders };

    public static PlaceholderPageViewModel ForEmpty(AppRoute route, string headerKey, string descriptionKey) =>
        new(route) { TitleKey = route.TitleKey, ContentKind = PageContentKind.Empty, EmptyHeaderKey = headerKey, EmptyDescriptionKey = descriptionKey };
}