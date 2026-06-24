namespace LightStudio.LightPlayer.Models;

public enum AppRouteId
{
    Home,
    Songs,
    Albums,
    Artists,
    Playlists,
    PlaylistDetail,
    AlbumDetail,
    ArtistDetail,
    Search,
    Settings,
    NowPlaying,
}

public enum AppRouteKind
{
    TopLevel,
    Detail,
}

/// <summary>
/// Where a route appears in the navigation rail.
/// </summary>
public enum AppNavigationSlot
{
    /// <summary>Not shown in the navigation rail (handled elsewhere, e.g. search or detail pages).</summary>
    None,

    /// <summary>Shown in the primary navigation list.</summary>
    Primary,

    /// <summary>Pinned to the footer of the navigation rail.</summary>
    Footer,
}

/// <summary>
/// Describes a navigable destination in the Avalonia shell.
/// </summary>
public sealed record AppRoute(
    AppRouteId Id,
    string Title,
    AppRouteKind Kind,
    string IconKey = "IconHome",
    AppNavigationSlot NavigationSlot = AppNavigationSlot.None);