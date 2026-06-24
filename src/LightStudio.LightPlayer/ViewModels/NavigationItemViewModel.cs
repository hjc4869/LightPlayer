using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// A single entry in the navigation rail.
/// </summary>
public partial class NavigationItemViewModel : ObservableObject
{
    private readonly Action<NavigationItemViewModel> onSelected;

    [ObservableProperty]
    private bool isSelected;

    public NavigationItemViewModel(AppRoute route, Action<NavigationItemViewModel> onSelected)
    {
        Route = route;
        this.onSelected = onSelected;
        SelectCommand = new RelayCommand(() => this.onSelected(this));
    }

    public AppRoute Route { get; }

    public AppRouteId Id => Route.Id;

    public string Title => Route.Title;

    public string IconKey => Route.IconKey;

    public IRelayCommand SelectCommand { get; }
}
