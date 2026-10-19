using System;
using Avalonia;
using Avalonia.Controls;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Behaviors;

/// <summary>
/// Remembers the vertical scroll offset of a browse or search page on its
/// <see cref="IScrollPositionPageViewModel"/> and restores it when the view is shown again. Because the
/// shell keeps browse view models alive on the back stack, navigating back from a detail page returns
/// to the same grid scrolled to where the user left off instead of jumping back to the top.
/// </summary>
internal sealed class BrowseScrollMemory
{
    internal static readonly AttachedProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.RegisterAttached<BrowseScrollMemory, Control, bool>("IsReadOnly", inherits: true);

    // The grid measures its content over a few layout passes after attaching (and may still be
    // loading); cap how long we wait for it to settle before giving up on restoring the offset.
    private const int MaxRestorePasses = 30;

    private readonly Control view;
    private readonly ScrollViewer[] scrollViewers;
    private bool readOnly;
    private bool restoring;
    private int restorePasses;

    private BrowseScrollMemory(Control view, ScrollViewer[] scrollViewers)
    {
        this.view = view;
        this.scrollViewers = scrollViewers;
        readOnly = view.GetValue(IsReadOnlyProperty);

        view.AttachedToVisualTree += OnAttached;
        view.DetachedFromVisualTree += OnDetached;
        foreach (var scrollViewer in scrollViewers)
        {
            scrollViewer.ScrollChanged += OnScrollChanged;
        }
    }

    /// <summary>
    /// Wires offset tracking for a browse page view. If a page has multiple adaptive scroll viewers,
    /// only the one that is currently visible participates at any time.
    /// </summary>
    public static void Track(Control view, params ScrollViewer[] scrollViewers) =>
        _ = new BrowseScrollMemory(view, scrollViewers);

    private IScrollPositionPageViewModel? ViewModel => view.DataContext as IScrollPositionPageViewModel;

    private ScrollViewer? VisibleScrollViewer => Array.Find(scrollViewers, s => s.IsEffectivelyVisible);

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Ignore the programmatic offset changes made while restoring so they do not overwrite the
        // saved position with a transient (often zero) value mid-layout.
        if (restoring || readOnly || view.GetValue(IsReadOnlyProperty) ||
            sender is not ScrollViewer scrollViewer || !scrollViewer.IsEffectivelyVisible)
        {
            return;
        }

        if (ViewModel is { } viewModel)
        {
            viewModel.ScrollOffset = scrollViewer.Offset.Y;
        }
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        readOnly = view.GetValue(IsReadOnlyProperty);
        // Watch the layout passes that follow attachment and apply the saved offset once the grid
        // has measured enough content to honor it.
        restoring = true;
        restorePasses = 0;
        view.LayoutUpdated += OnLayoutUpdatedDuringRestore;
    }

    private void OnLayoutUpdatedDuringRestore(object? sender, EventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is not null && viewModel.ScrollOffset <= 0)
        {
            // Nothing to restore (the user left the page at the top).
            EndRestore();
            return;
        }

        if (viewModel is not null && VisibleScrollViewer is { Extent.Height: > 0 } scrollViewer)
        {
            var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            var target = Math.Min(viewModel.ScrollOffset, maxOffset);
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, target);

            // Stop once the offset sticks (reached the target or clamped to the bottom).
            if (Math.Abs(scrollViewer.Offset.Y - target) < 0.5)
            {
                EndRestore();
            }

            return;
        }

        // The view model or its content is not ready yet (data context not applied, list still
        // loading, or content not measured). Allow a few passes to settle, then give up.
        if (++restorePasses > MaxRestorePasses)
        {
            EndRestore();
        }
    }

    private void EndRestore()
    {
        view.LayoutUpdated -= OnLayoutUpdatedDuringRestore;
        restoring = false;
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (restoring)
        {
            EndRestore();
            return;
        }

        // Capture the final position when leaving, in case the last scroll change was coalesced
        // away by the navigation transition tearing the view down.
        if (!readOnly && !view.GetValue(IsReadOnlyProperty) &&
            VisibleScrollViewer is { } scrollViewer && ViewModel is { } viewModel)
        {
            viewModel.ScrollOffset = scrollViewer.Offset.Y;
        }
    }
}
