using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace LightStudio.LightPlayer.Controls;

/// <summary>Layout contracts between the shell chrome and the pages and views it frames.</summary>
public static class ShellChrome
{
    /// <summary>
    /// Space the window's own controls take at the top-left corner of the shell. Inherited, so views
    /// that cover the whole shell can keep their controls clear of them.
    /// </summary>
    public static readonly AttachedProperty<Thickness> WindowControlsInsetProperty =
        AvaloniaProperty.RegisterAttached<Control, Thickness>("WindowControlsInset", typeof(ShellChrome), inherits: true);

    /// <summary>
    /// How far shell chrome overlaps a page host from beyond its top and bottom edges. Vertical
    /// scrollers that reach those edges keep drawing their content under the chrome.
    /// </summary>
    public static readonly AttachedProperty<Thickness> ScrollUnderInsetsProperty =
        AvaloniaProperty.RegisterAttached<Control, Thickness>("ScrollUnderInsets", typeof(ShellChrome));

    private static readonly ConditionalWeakTable<Control, ScrollUnderHost> Hosts = new();

    static ShellChrome()
    {
        ScrollUnderInsetsProperty.Changed.AddClassHandler<Control>(static (host, _) =>
            Hosts.GetValue(host, static control => new ScrollUnderHost(control)).Update());
    }

    public static Thickness GetWindowControlsInset(Control control) => control.GetValue(WindowControlsInsetProperty);

    public static void SetWindowControlsInset(Control control, Thickness value) =>
        control.SetValue(WindowControlsInsetProperty, value);

    public static Thickness GetScrollUnderInsets(Control control) => control.GetValue(ScrollUnderInsetsProperty);

    public static void SetScrollUnderInsets(Control control, Thickness value) =>
        control.SetValue(ScrollUnderInsetsProperty, value);

    /// <summary>
    /// Widens the render clips between the host and the scrollers that touch its top or bottom edge,
    /// vertically only. Their content then stays visible beyond the edge, and as nothing on the way
    /// clips to bounds any more, virtualized items are realized there too. Layout, scroll range, and
    /// scroll bars are unchanged.
    /// </summary>
    private sealed class ScrollUnderHost
    {
        private const double EdgeTolerance = 0.5;

        private readonly Control host;
        private readonly Dictionary<Visual, Extension> extended = [];
        private readonly Dictionary<Visual, Reach> required = [];
        private readonly List<Visual> chain = [];
        private readonly List<Visual> released = [];
        private bool subscribed;

        public ScrollUnderHost(Control host)
        {
            this.host = host;
            host.AttachedToVisualTree += (_, _) => Update();
            host.DetachedFromVisualTree += (_, _) => Update();
        }

        public void Update()
        {
            var insets = GetScrollUnderInsets(host);
            bool active = host.IsAttachedToVisualTree() && (insets.Top > 0 || insets.Bottom > 0);
            if (active != subscribed)
            {
                subscribed = active;
                if (active)
                    host.LayoutUpdated += OnLayoutUpdated;
                else
                    host.LayoutUpdated -= OnLayoutUpdated;
            }

            Apply();
        }

        private void OnLayoutUpdated(object? sender, EventArgs args) => Apply();

        private void Apply()
        {
            required.Clear();
            if (subscribed)
            {
                chain.Add(host);
                Visit(host, 0, GetScrollUnderInsets(host));
                chain.Clear();
            }

            bool changed = false;
            foreach (var (visual, reach) in required)
            {
                changed |= Extend(visual, reach);
            }

            foreach (var visual in extended.Keys)
            {
                if (!required.ContainsKey(visual))
                    released.Add(visual);
            }

            foreach (var visual in released)
            {
                extended.Remove(visual, out var extension);
                extension!.Dispose();
                changed = true;
            }

            released.Clear();

            // Clipping does not invalidate layout, but the effective viewports it bounds must follow.
            if (changed)
                host.InvalidateArrange();
        }

        // Layout offsets rather than transformed positions, so page transitions do not toggle clips.
        private void Visit(Visual visual, double top, Thickness insets)
        {
            if (visual.GetVisualChildren() is not IReadOnlyList<Visual> children)
                return;

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (!child.IsVisible)
                    continue;

                double childTop = top + child.Bounds.Top;
                chain.Add(child);
                if (child is not ScrollViewer viewer)
                {
                    Visit(child, childTop, insets);
                }
                // Scrollers that hide their bar (lyrics, carousels) and nested scrollers keep their clip.
                else if (viewer.Presenter is ScrollContentPresenter presenter &&
                    viewer.VerticalScrollBarVisibility is ScrollBarVisibility.Auto or ScrollBarVisibility.Visible)
                {
                    var reach = new Reach(
                        Math.Abs(childTop) <= EdgeTolerance ? insets.Top : 0,
                        Math.Abs(childTop + viewer.Bounds.Height - host.Bounds.Height) <= EdgeTolerance ? insets.Bottom : 0);
                    if (reach.Above > 0 || reach.Below > 0)
                    {
                        foreach (var ancestor in chain)
                            Require(ancestor, reach);
                        Require(presenter, reach);
                    }
                }

                chain.RemoveAt(chain.Count - 1);
            }
        }

        private void Require(Visual visual, Reach reach)
        {
            if (!visual.ClipToBounds && !extended.ContainsKey(visual))
                return;

            required[visual] = required.TryGetValue(visual, out var current)
                ? new Reach(Math.Max(current.Above, reach.Above), Math.Max(current.Below, reach.Below))
                : reach;
        }

        private bool Extend(Visual visual, Reach reach)
        {
            var clip = new Rect(0, -reach.Above, visual.Bounds.Width, visual.Bounds.Height + reach.Above + reach.Below);
            if (extended.TryGetValue(visual, out var extension))
            {
                if (extension.Clip == clip)
                    return false;
                extension.Dispose();
            }

            // Above local values, and undone without disturbing them.
            extended[visual] = new Extension(
                clip,
                visual.SetValue(Visual.ClipToBoundsProperty, false, BindingPriority.Animation),
                visual.SetValue(Visual.ClipProperty, new RectangleGeometry(clip), BindingPriority.Animation));
            return true;
        }

        private readonly record struct Reach(double Above, double Below);

        private sealed record Extension(Rect Clip, IDisposable? ClipToBounds, IDisposable? Geometry) : IDisposable
        {
            public void Dispose()
            {
                ClipToBounds?.Dispose();
                Geometry?.Dispose();
            }
        }
    }
}
