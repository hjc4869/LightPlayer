using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed class DefaultPageOption : ViewModelBase
{
    private readonly string titleKey;

    /// <summary>Takes a resource key (use <c>nameof(Resources.Key)</c>) so the label follows the language.</summary>
    public DefaultPageOption(AppRouteId? routeId, string titleKey)
    {
        RouteId = routeId;
        this.titleKey = titleKey;
    }

    /// <summary>The page to open, or <see langword="null"/> to remember the last visited page.</summary>
    public AppRouteId? RouteId { get; }

    public string Title => Localized(titleKey);

    public bool IsRememberLast => RouteId is null;
}

public partial class DefaultPagePreferenceViewModel : ViewModelBase
{
    private readonly IAppSettingsStore settingsStore;

    [ObservableProperty]
    private DefaultPageOption selectedPage = null!;

    public DefaultPagePreferenceViewModel(IAppSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore;

        PageOptions =
        [
            new DefaultPageOption(null, nameof(Strings.Resources.DefaultPage_LastPage)),
            new DefaultPageOption(AppRouteId.Home, nameof(Strings.Resources.Nav_Home)),
            new DefaultPageOption(AppRouteId.Songs, nameof(Strings.Resources.Nav_Music)),
            new DefaultPageOption(AppRouteId.Albums, nameof(Strings.Resources.Nav_Albums)),
            new DefaultPageOption(AppRouteId.Artists, nameof(Strings.Resources.Nav_Artists)),
            new DefaultPageOption(AppRouteId.Videos, nameof(Strings.Resources.Nav_Video)),
            new DefaultPageOption(AppRouteId.Playlists, nameof(Strings.Resources.Nav_Playlists)),
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