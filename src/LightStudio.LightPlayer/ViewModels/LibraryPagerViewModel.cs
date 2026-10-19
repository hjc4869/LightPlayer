using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// One page position in the narrow layout's library pager. The page is created lazily (the slot
/// exists from the start so the pager's geometry is fixed) and the slot is the binding target the
/// pager's presenter follows, so materializing a page swaps it in without rebuilding the track.
/// </summary>
public sealed partial class LibraryPagerSlotViewModel : ObservableObject
{
    public LibraryPagerSlotViewModel(AppRouteId id) => Id = id;

    /// <summary>The library route this position shows.</summary>
    public AppRouteId Id { get; }

    /// <summary>The live page, or <see langword="null"/> until this position is materialized.</summary>
    [ObservableProperty]
    private PageViewModelBase? page;
}

/// <summary>
/// Backs the narrow (mobile) layout's library pager: the music / albums / artists / playlists
/// sections laid out side by side so the user swipes horizontally between them instead of
/// navigating tab by tab.
///
/// The pager owns the pages it shows. Only the selected position and its immediate neighbors are
/// materialized (the neighbors on a background dispatcher pass, so entering the section never
/// waits on them), and materialized pages are then kept for as long as the narrow layout lasts, so
/// swiping back and forth reuses their loaded items and scroll position. <see cref="Release"/>
/// hands the selected page back to the shell and tears down the rest when the layout goes wide.
/// </summary>
public sealed partial class LibraryPagerViewModel : ObservableObject
{
    private readonly Func<AppRouteId, PageViewModelBase> pageFactory;

    // True while the pager owns pages, i.e. between the first Adopt and the next Release. Guards the
    // deferred neighbor pass so a release that lands first cannot be followed by a fresh page.
    private bool isActive;

    // Set while the shell drives the selection (a navigation it already knows about), so the change
    // is not echoed straight back to it as a user page change.
    private bool isSyncing;

    private bool neighborsQueued;

    /// <summary>
    /// The visible position. Bound two-way by the pager control, so a swipe writes here and a
    /// programmatic change animates the track.
    /// </summary>
    [ObservableProperty]
    private int selectedIndex;

    /// <summary>
    /// Whether a horizontal swipe changes page. Cleared by the shell while the visible page has
    /// drilled into its own detail on a single-pane (compact) master-detail layout: that detail owns
    /// the width, and the sections a swipe would move between are not what the user is looking at.
    /// </summary>
    [ObservableProperty]
    private bool canSwipe = true;

    public LibraryPagerViewModel(IReadOnlyList<AppRouteId> routes, Func<AppRouteId, PageViewModelBase> pageFactory)
    {
        this.pageFactory = pageFactory;
        Slots = routes.Select(id => new LibraryPagerSlotViewModel(id)).ToArray();
    }

    /// <summary>
    /// Raised when the user changes page (a swipe, or a tab tapped while the pager is showing), so
    /// the shell can adopt the new route without running a navigation.
    /// </summary>
    public event Action<AppRouteId>? PageSelected;

    /// <summary>The pager positions, in tab-strip order.</summary>
    public IReadOnlyList<LibraryPagerSlotViewModel> Slots { get; }

    /// <summary>Whether <paramref name="routeId"/> is one of the paged library sections.</summary>
    public bool Contains(AppRouteId routeId) => IndexOf(routeId) >= 0;

    /// <summary>The live page for a route, or null when that position has not been materialized.</summary>
    public PageViewModelBase? GetPage(AppRouteId routeId)
    {
        var index = IndexOf(routeId);
        return index < 0 ? null : Slots[index].Page;
    }

    /// <summary>
    /// True when the pager is the owner of <paramref name="page"/>. The shell consults this before
    /// disposing the page it navigated away from, and before retaining one on the back stack: paged
    /// library pages outlive individual navigations and are released by the pager alone.
    /// </summary>
    public bool Owns(PageViewModelBase? page) =>
        page is not null && Slots.Any(slot => ReferenceEquals(slot.Page, page));

    /// <summary>
    /// Takes ownership of <paramref name="page"/> for <paramref name="routeId"/> and makes it the
    /// visible position without announcing the change (the shell is the one performing it). Used
    /// both when the shell navigates to a library route and when the layout switches to narrow with
    /// a library page already showing, so the very same page instance carries across the switch.
    /// </summary>
    public void Adopt(AppRouteId routeId, PageViewModelBase page)
    {
        var index = IndexOf(routeId);
        if (index < 0)
        {
            return;
        }

        isActive = true;

        var slot = Slots[index];
        if (!ReferenceEquals(slot.Page, page))
        {
            var previous = slot.Page;
            slot.Page = page;
            (previous as IDisposable)?.Dispose();
        }

        isSyncing = true;
        try
        {
            SelectedIndex = index;
        }
        finally
        {
            isSyncing = false;
        }

        QueueNeighbors();
    }

    /// <summary>
    /// Moves to a route from outside a swipe (the tab strip, or the rail while narrow). Announces
    /// the change like a swipe does, so the shell adopts the page rather than navigating to it.
    /// </summary>
    public void SelectRoute(AppRouteId routeId)
    {
        var index = IndexOf(routeId);
        if (index >= 0)
        {
            SelectedIndex = index;
        }
    }

    /// <summary>
    /// Drops a position so the next navigation to it rebuilds the page, and returns the page that
    /// was there (the caller owns it now). Used when a view setting that is baked into the page at
    /// construction changes, such as the album/artist grid-versus-list layout.
    /// </summary>
    public PageViewModelBase? Remove(AppRouteId routeId)
    {
        var index = IndexOf(routeId);
        if (index < 0)
        {
            return null;
        }

        var page = Slots[index].Page;
        Slots[index].Page = null;
        return page;
    }

    /// <summary>
    /// Gives up ownership of every page, disposing all but the one for <paramref name="keep"/>,
    /// which the shell keeps showing as its routed page. Called when the layout leaves narrow, where
    /// the sections are separate destinations again.
    /// </summary>
    public void Release(AppRouteId keep)
    {
        isActive = false;

        foreach (var slot in Slots)
        {
            var page = slot.Page;
            slot.Page = null;

            if (page is not null && slot.Id != keep)
            {
                (page as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// Every live page the pager holds. Lets the shell refresh them together after a library scan,
    /// since only the selected one is the shell's current page.
    /// </summary>
    public IEnumerable<PageViewModelBase> LivePages =>
        Slots.Select(slot => slot.Page).OfType<PageViewModelBase>();

    partial void OnSelectedIndexChanged(int value)
    {
        // The position being shown must have a page even if the deferred neighbor pass has not run
        // (a very fast swipe, or a route selected before the pager settled).
        Materialize(value);
        QueueNeighbors();

        if (!isSyncing && value >= 0 && value < Slots.Count)
        {
            PageSelected?.Invoke(Slots[value].Id);
        }
    }

    private int IndexOf(AppRouteId routeId)
    {
        for (var i = 0; i < Slots.Count; i++)
        {
            if (Slots[i].Id == routeId)
            {
                return i;
            }
        }

        return -1;
    }

    // Builds the pages on either side of the selection so a swipe reveals real content instead of an
    // empty page. Deferred to a background pass so entering the section (and settling a swipe) never
    // waits on the extra library queries.
    private void QueueNeighbors()
    {
        if (neighborsQueued)
        {
            return;
        }

        neighborsQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                neighborsQueued = false;
                Materialize(SelectedIndex - 1);
                Materialize(SelectedIndex + 1);
            },
            DispatcherPriority.Background);
    }

    private void Materialize(int index)
    {
        if (!isActive || index < 0 || index >= Slots.Count)
        {
            return;
        }

        var slot = Slots[index];
        slot.Page ??= pageFactory(slot.Id);
    }
}
