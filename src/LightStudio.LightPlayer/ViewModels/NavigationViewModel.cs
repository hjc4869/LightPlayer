using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// Drives the navigation rail: primary and footer destinations, the expand
/// toggle, and the active-route highlight.
/// </summary>
public partial class NavigationViewModel : ObservableObject
{
    [ObservableProperty]
    private bool isExpanded;

    public NavigationViewModel(IEnumerable<AppRoute> routes)
    {
        var all = routes.ToList();
        PrimaryItems = all
            .Where(route => route.NavigationSlot == AppNavigationSlot.Primary)
            .Select(route => new NavigationItemViewModel(route, OnItemSelected))
            .ToArray();
        FooterItems = all
            .Where(route => route.NavigationSlot == AppNavigationSlot.Footer)
            .Select(route => new NavigationItemViewModel(route, OnItemSelected))
            .ToArray();

        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>Raised when the user picks a destination from the rail.</summary>
    public event Action<AppRouteId>? NavigationRequested;

    public IReadOnlyList<NavigationItemViewModel> PrimaryItems { get; }

    public IReadOnlyList<NavigationItemViewModel> FooterItems { get; }

    public IRelayCommand ToggleExpandCommand { get; }

    /// <summary>
    /// Updates the active highlight without raising <see cref="NavigationRequested"/>.
    /// Used when navigation originates outside the rail (e.g. search).
    /// </summary>
    public void SetActive(AppRouteId routeId)
    {
        foreach (var item in PrimaryItems)
        {
            item.IsSelected = item.Id == routeId;
        }

        foreach (var item in FooterItems)
        {
            item.IsSelected = item.Id == routeId;
        }
    }

    private void OnItemSelected(NavigationItemViewModel item)
    {
        // Picking a destination collapses the expanded (overlay) drawer back to the compact rail
        // so the chosen page is immediately visible, matching standard navigation-drawer behaviour.
        // Collapse on any tap — even re-selecting the active item below is a no-op — so the drawer
        // is always dismissed once the user interacts with it.
        IsExpanded = false;

        // Re-selecting the destination that is already active is a no-op: navigating would
        // needlessly rebuild the page and push a duplicate entry onto the back stack. When the
        // shell sits on a hierarchical (detail) page no rail item is selected, so clicking one
        // still navigates back to that root as expected.
        if (item.IsSelected)
        {
            return;
        }

        SetActive(item.Id);
        NavigationRequested?.Invoke(item.Id);
    }
}
