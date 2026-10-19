using System;
using Avalonia.Animation;
using LightStudio.LightPlayer.Animations;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.ViewModels;

public sealed partial class ShellViewModel
{
    private BackNavigation? backPresentation;

    internal bool CanPreviewPageBack => navigationHistory is null && CanGoBack &&
        !IsOverlayOpen && !IsSearchActive && !IsVideoSheetAddressed && !NowPlayingSheet.IsExpanded &&
        CurrentPage is not IInPageBackHandler { CanHandleBack: true };

    internal BackNavigation? PrepareBackNavigation()
    {
        if (!CanPreviewPageBack || !backStack.TryPeek(out var entry))
            return null;

        PageViewModelBase? retained = IsNarrow && entry.Parameter is not MusicFolderReference
            ? LibraryPager.GetPage(entry.Id) : null;
        retained ??= entry.Page;
        PageViewModelBase page = retained ?? CreatePage(routesById[entry.Id], entry.Parameter);
        ApplyLayout(page);
        bool hierarchical = IsHierarchicalRoute(currentRouteId, currentParameter) || IsHierarchicalRoute(entry.Id, entry.Parameter);
        IPageTransition transition = !IsNarrow && CurrentPage is SongsPageViewModel && page is SongsPageViewModel && hierarchical
            ? narrowRootTransition : SelectPageTransition(hierarchical);
        return new BackNavigation(this, entry, backStack.Count, CurrentPage, page, retained is null, transition);
    }

    internal sealed class BackNavigation : IDisposable
    {
        private readonly ShellViewModel shell;
        private readonly (AppRouteId Id, object? Parameter, PageViewModelBase? Page) entry;
        private readonly int depth;
        private readonly bool ownsDestination;
        private readonly bool narrow;
        private readonly double? scrollOffset;
        private LibraryPagerSlotViewModel? previewSlot;
        private bool disposed;
        private bool committed;

        internal BackNavigation(ShellViewModel shell,
            (AppRouteId Id, object? Parameter, PageViewModelBase? Page) entry, int depth,
            PageViewModelBase from, PageViewModelBase to, bool ownsDestination, IPageTransition transition)
        {
            this.shell = shell;
            this.entry = entry;
            this.depth = depth;
            this.ownsDestination = ownsDestination;
            narrow = shell.IsNarrow;
            scrollOffset = (to as IScrollPositionPageViewModel)?.ScrollOffset;
            From = from;
            To = to;
            Transition = transition;
        }

        internal PageViewModelBase From { get; }
        internal PageViewModelBase To { get; }
        internal IPageTransition Transition { get; }
        internal bool UsesLibraryPager => narrow && entry.Parameter is not MusicFolderReference && shell.LibraryPager.Contains(entry.Id);
        internal bool ShowsLibraryTabs => UsesLibraryPager && To is not IInPageBackHandler { CanHandleBack: true };
        internal bool IsCurrent => !disposed && IsTargetCurrent;
        internal bool IsTargetCurrent => !committed && shell.CanPreviewPageBack && shell.IsNarrow == narrow &&
            ReferenceEquals(shell.CurrentPage, From) && shell.backStack.Count == depth &&
            shell.backStack.TryPeek(out var current) && current.Equals(entry);

        internal void Present()
        {
            if (UsesLibraryPager && !shell.LibraryPager.Owns(To))
            {
                foreach (LibraryPagerSlotViewModel slot in shell.LibraryPager.Slots)
                {
                    if (slot.Id == entry.Id && slot.Page is null)
                    {
                        previewSlot = slot;
                        slot.Page = To;
                        break;
                    }
                }
            }
            shell.backPresentation = this;
            shell.AddressBar.SetPage(To);
            shell.NotifyChromeVisibilityChanged();
            shell.OnPropertyChanged(nameof(ShowLibraryTabs));
            shell.OnPropertyChanged(nameof(WindowPageTitle));
        }

        internal bool Commit()
        {
            if (!IsCurrent)
                return false;
            PageViewModelBase? retained = IsSearchPage(From) ? From : null;
            shell.forwardStack.Push((shell.currentRouteId, shell.currentParameter, retained));
            shell.backStack.Pop();
            committed = true;
            if (entry.Page is not null && !ReferenceEquals(entry.Page, To) && !shell.LibraryPager.Owns(entry.Page))
                (entry.Page as IDisposable)?.Dispose();
            shell.ShowPage(entry.Id, entry.Parameter, To, isBack: true, retained, shell.narrowRootTransition);
            return true;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            if (ReferenceEquals(shell.backPresentation, this))
            {
                shell.backPresentation = null;
                shell.AddressBar.SetPage(shell.CurrentPage);
                shell.NotifyChromeVisibilityChanged();
                shell.OnPropertyChanged(nameof(ShowLibraryTabs));
                shell.OnPropertyChanged(nameof(WindowPageTitle));
            }
            if (!committed)
            {
                if (previewSlot is not null && ReferenceEquals(previewSlot.Page, To))
                    previewSlot.Page = null;
                if (scrollOffset is { } offset && To is IScrollPositionPageViewModel scrollPage)
                    scrollPage.ScrollOffset = offset;
                if (ownsDestination)
                    (To as IDisposable)?.Dispose();
            }
        }
    }
}