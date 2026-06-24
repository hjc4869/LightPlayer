using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed class DefaultPageOption
{
    public DefaultPageOption(AppRouteId? routeId, string title)
    {
        RouteId = routeId;
        Title = title;
    }

    /// <summary>The page to open, or <see langword="null"/> to remember the last visited page.</summary>
    public AppRouteId? RouteId { get; }

    public string Title { get; }

    public bool IsRememberLast => RouteId is null;
}

public partial class DefaultPageSettingsSectionViewModel : SettingsSectionViewModel
{
    private readonly IAppSettingsStore settingsStore;

    [ObservableProperty]
    private DefaultPageOption selectedPage = null!;

    public DefaultPageSettingsSectionViewModel(IAppSettingsStore settingsStore)
        : base("Default Page", "Choose the route Light opens first.")
    {
        this.settingsStore = settingsStore;

        PageOptions =
        [
            new DefaultPageOption(null, "Last page"),
            new DefaultPageOption(AppRouteId.Home, "Home"),
            new DefaultPageOption(AppRouteId.Songs, "Music"),
            new DefaultPageOption(AppRouteId.Albums, "Albums"),
            new DefaultPageOption(AppRouteId.Artists, "Artists"),
            new DefaultPageOption(AppRouteId.Playlists, "Playlists"),
        ];

        selectedPage = settingsStore.RememberLastPage
            ? PageOptions[0]
            : FindOption(settingsStore.DefaultRouteId);
    }

    public IReadOnlyList<DefaultPageOption> PageOptions { get; }

    partial void OnSelectedPageChanged(DefaultPageOption value)
    {
        if (value.RouteId is { } routeId)
        {
            settingsStore.RememberLastPage = false;
            settingsStore.DefaultRouteId = routeId;
        }
        else
        {
            settingsStore.RememberLastPage = true;
        }
    }

    private DefaultPageOption FindOption(AppRouteId routeId) =>
        PageOptions.FirstOrDefault(option => option.RouteId == routeId)
            ?? PageOptions.First(option => option.RouteId == AppRouteId.Home);
}