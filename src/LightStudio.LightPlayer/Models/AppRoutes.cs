using System;
using System.Collections.Generic;

namespace LightStudio.LightPlayer.Models;

public static class AppRoutes
{
    public static readonly AppRoute Home = new(AppRouteId.Home, "Home", AppRouteKind.TopLevel, "IconHome", AppNavigationSlot.Primary);

    public static IReadOnlyList<AppRoute> All { get; } = Array.AsReadOnly(
    [
        Home,
        new AppRoute(AppRouteId.Songs, "Music", AppRouteKind.TopLevel, "IconMusicNote", AppNavigationSlot.Primary),
        new AppRoute(AppRouteId.Albums, "Albums", AppRouteKind.TopLevel, "IconAlbum", AppNavigationSlot.Primary),
        new AppRoute(AppRouteId.Artists, "Artists", AppRouteKind.TopLevel, "IconArtists", AppNavigationSlot.Primary),
        new AppRoute(AppRouteId.Playlists, "Playlists", AppRouteKind.TopLevel, "IconPlaylist", AppNavigationSlot.Primary),
        new AppRoute(AppRouteId.Search, "Search", AppRouteKind.TopLevel, "IconSearch"),
        new AppRoute(AppRouteId.Settings, "Settings", AppRouteKind.TopLevel, "IconSettings", AppNavigationSlot.Footer),
        new AppRoute(AppRouteId.NowPlaying, "Now Playing", AppRouteKind.TopLevel, "IconQueue"),
        new AppRoute(AppRouteId.PlaylistDetail, "Playlist Detail", AppRouteKind.Detail, "IconPlaylist"),
        new AppRoute(AppRouteId.AlbumDetail, "Album Detail", AppRouteKind.Detail, "IconAlbum"),
        new AppRoute(AppRouteId.ArtistDetail, "Artist Detail", AppRouteKind.Detail, "IconArtists"),
    ]);
}