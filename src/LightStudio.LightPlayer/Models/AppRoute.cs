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
    Videos,
    VideoPlayer,
    PhotoViewer,
    PhotoSearch,
    AlbumSearch,
    ArtistSearch,
    SongSearch,
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
/// Describes a navigable destination in the Avalonia shell. <paramref name="TitleKey"/> is a
/// resource key (use <c>nameof(Resources.Key)</c>) so the label follows the interface language.
/// </summary>
public sealed record AppRoute(
    AppRouteId Id,
    string TitleKey,
    AppRouteKind Kind,
    string IconKey = "IconHome",
    AppNavigationSlot NavigationSlot = AppNavigationSlot.None)
{
    /// <summary>The route label in the current interface language.</summary>
    public string Title => LightPlayer.Strings.LocalizedStrings.Instance[TitleKey];
}