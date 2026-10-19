using System;
using System.Collections.Generic;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Models;

public static class AppRoutes
{
    public static readonly AppRoute Home = new(AppRouteId.Home, nameof(Strings.Resources.Nav_Home), AppRouteKind.TopLevel, "IconHome", AppNavigationSlot.Primary);

    public static IReadOnlyList<AppRoute> All { get; } = Array.AsReadOnly(
    [
        Home,
        new AppRoute(AppRouteId.Songs, nameof(Strings.Resources.Nav_Music), AppRouteKind.TopLevel, "IconMusicNote", AppNavigationSlot.Primary),
        new AppRoute(AppRouteId.Albums, nameof(Strings.Resources.Nav_Albums), AppRouteKind.TopLevel, "IconAlbum", AppNavigationSlot.Primary),
        new AppRoute(AppRouteId.Artists, nameof(Strings.Resources.Nav_Artists), AppRouteKind.TopLevel, "IconArtists", AppNavigationSlot.Primary),
        new AppRoute(AppRouteId.Videos, nameof(Strings.Resources.Nav_Video), AppRouteKind.TopLevel, "IconPicturesVideos", AppNavigationSlot.Primary),
        new AppRoute(AppRouteId.Playlists, nameof(Strings.Resources.Nav_Playlists), AppRouteKind.TopLevel, "IconPlaylist", AppNavigationSlot.Primary),
        new AppRoute(AppRouteId.Search, nameof(Strings.Resources.Common_Search), AppRouteKind.TopLevel, "IconSearch"),
        new AppRoute(AppRouteId.Settings, nameof(Strings.Resources.Nav_Settings), AppRouteKind.TopLevel, "IconSettings", AppNavigationSlot.Footer),
        new AppRoute(AppRouteId.NowPlaying, nameof(Strings.Resources.Nav_NowPlaying), AppRouteKind.TopLevel, "IconQueue"),
        new AppRoute(AppRouteId.VideoPlayer, nameof(Strings.Resources.Nav_VideoPlayer), AppRouteKind.Detail, "IconVideo"),
        new AppRoute(AppRouteId.PhotoViewer, nameof(Strings.Resources.Nav_PhotoViewer), AppRouteKind.Detail, "IconVideo"),
        new AppRoute(AppRouteId.PhotoSearch, nameof(Strings.Resources.Search_PhotosHeader), AppRouteKind.Detail, "IconSearch"),
        new AppRoute(AppRouteId.AlbumSearch, nameof(Strings.Resources.Nav_Albums), AppRouteKind.Detail, "IconAlbum"),
        new AppRoute(AppRouteId.ArtistSearch, nameof(Strings.Resources.Nav_Artists), AppRouteKind.Detail, "IconArtists"),
        new AppRoute(AppRouteId.SongSearch, nameof(Strings.Resources.Nav_Music), AppRouteKind.Detail, "IconMusicNote"),
        new AppRoute(AppRouteId.PlaylistDetail, nameof(Strings.Resources.Nav_PlaylistDetail), AppRouteKind.Detail, "IconPlaylist"),
        new AppRoute(AppRouteId.AlbumDetail, nameof(Strings.Resources.Nav_AlbumDetail), AppRouteKind.Detail, "IconAlbum"),
        new AppRoute(AppRouteId.ArtistDetail, nameof(Strings.Resources.Nav_ArtistDetail), AppRouteKind.Detail, "IconArtists"),
    ]);
}