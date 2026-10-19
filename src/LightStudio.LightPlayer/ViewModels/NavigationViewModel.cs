using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
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

    [ObservableProperty]
    private SplitViewDisplayMode displayMode = SplitViewDisplayMode.CompactOverlay;

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

        // The narrow layout's top tab strip reuses the primary destinations minus Home, Video and
        // Playlists, which the bottom navigation surfaces separately.
        LibraryTabs = PrimaryItems
            .Where(item => item.Id is not (AppRouteId.Home or AppRouteId.Videos or AppRouteId.Playlists))
            .ToArray();

        // Everything after Home is grouped under the Apple layout's library heading.
        if (PrimaryItems.FirstOrDefault(item => item.Id != AppRouteId.Home) is { } firstLibraryItem)
        {
            firstLibraryItem.StartsLibrarySection = true;
        }

        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>Raised when the user picks a destination from the rail.</summary>
    public event Action<AppRouteId>? NavigationRequested;

    public IReadOnlyList<NavigationItemViewModel> PrimaryItems { get; }

    public IReadOnlyList<NavigationItemViewModel> FooterItems { get; }

    /// <summary>Library sub-tabs (music / albums / artists) for the narrow top tab strip.</summary>
    public IReadOnlyList<NavigationItemViewModel> LibraryTabs { get; }

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

    public void CollapseOverlay()
    {
        if (DisplayMode == SplitViewDisplayMode.CompactOverlay)
        {
            IsExpanded = false;
        }
    }

    private void OnItemSelected(NavigationItemViewModel item)
    {
        CollapseOverlay();

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
