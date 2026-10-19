using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightStudio.LightPlayer.Controls;

namespace LightStudio.LightPlayer.Behaviors;

/// <summary>
/// Animates the current page's active scroll region back to the top. Wired to the narrow-layout top
/// bar so tapping the title area scrolls the visible list up, matching the common mobile "tap the
/// status bar to jump to the top" gesture.
///
/// For pages that host their own child navigation via <see cref="MasterDetailView"/>, only the pane
/// the user is looking at is scrolled: the open sub-page (detail) when one is showing, otherwise the
/// master list. The inactive master keeps its position, so returning to it lands where the user left
/// off rather than at the top.
/// </summary>
internal static class PageScrollToTop
{
    // Short, snappy glide: long enough to read as motion without making the user wait.
    private const double DurationMs = 260d;

    // Timer cadence, ~60 fps; the eased position is computed from wall-clock time so jitter in the
    // tick interval does not affect the animation's duration.
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(15);

    // Offsets this close to the top are treated as already there, so a tap does nothing.
    private const double AtTopEpsilon = 0.5d;

    // One glide per scroll viewer at a time; a fresh tap cancels the one in flight so rapid taps do
    // not stack competing timers onto the same target.
    private static readonly ConditionalWeakTable<ScrollViewer, DispatcherTimer> Running = new();

    /// <summary>
    /// Finds the active scroll region beneath <paramref name="pageRoot"/> and glides it to the top.
    /// Does nothing when the page has no scrollable content.
    /// </summary>
    public static void Invoke(Visual? pageRoot)
    {
        if (pageRoot is not null && FindActiveScrollViewer(pageRoot) is { } scrollViewer)
        {
            AnimateToTop(scrollViewer);
        }
    }

    // Depth-first search for the scroll viewer the user is actually looking at. Hidden subtrees are
    // skipped so adaptive list/detail panes toggled via IsVisible are handled, and a MasterDetailView
    // defers to whichever of its panes is active so the master that stays visible
    // behind a narrow detail overlay is never chosen while a sub-page is open.
    private static ScrollViewer? FindActiveScrollViewer(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (!child.IsVisible)
            {
                continue;
            }

            if (child is MasterDetailView masterDetail)
            {
                if (FindActiveScrollViewer(masterDetail.ActiveContentRoot) is { } pane)
                {
                    return pane;
                }

                continue;
            }

            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            if (FindActiveScrollViewer(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void AnimateToTop(ScrollViewer scrollViewer)
    {
        // Cancel any glide already running for this target so rapid taps do not stack.
        if (Running.TryGetValue(scrollViewer, out var existing))
        {
            existing.Stop();
            Running.Remove(scrollViewer);
        }

        var x = scrollViewer.Offset.X;
        var startY = scrollViewer.Offset.Y;
        if (startY <= AtTopEpsilon)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = FrameInterval };
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / DurationMs, 0d, 1d);

            // Cubic ease-out: quick start, gentle settle.
            var eased = 1d - Math.Pow(1d - progress, 3d);
            var y = startY * (1d - eased);
            scrollViewer.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(x, y));

            if (progress >= 1d)
            {
                scrollViewer.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(x, 0d));
                timer.Stop();
                Running.Remove(scrollViewer);
            }
        };

        Running.Add(scrollViewer, timer);
        timer.Start();
    }
}
