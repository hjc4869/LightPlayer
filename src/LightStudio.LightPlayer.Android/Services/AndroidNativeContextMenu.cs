#nullable enable
using System;
using System.Collections.Generic;
using Android.App;
using Android.Views;
using Android.Widget;
using LightStudio.LightPlayer.Services;
using AndroidResource = Android.Resource;

namespace LightStudio.LightPlayer.Android.Services;

/// <summary>
/// Presents context menus with <c>android.widget.PopupMenu</c>, the platform's own menu widget.
/// Unlike a menu drawn by Avalonia this follows the device's theme, OEM skin, and OS version, and
/// gets Android's standard 48dp touch targets for free.
/// </summary>
/// <remarks>
/// <para>
/// A <c>PopupMenu</c> anchors to an Android <c>View</c>, but the whole Avalonia UI is a single view,
/// so an invisible 1x1 placeholder is parked in the activity's content frame and moved to the bounds
/// of the Avalonia control that raised the request. The popup then drops below that rectangle (or
/// flips above it near the bottom of the screen) exactly as it would for a native list row.
/// </para>
/// <para>
/// Positions arrive in physical pixels relative to the top-level client area. The activity is
/// edge-to-edge (see the shell's inset handling), so the content frame spans the whole window and
/// those coordinates map directly onto it.
/// </para>
/// </remarks>
internal sealed class AndroidNativeContextMenu(Activity activity) : INativeContextMenuPresenter
{
    private readonly Activity activity = activity;
    private View? anchor;
    private PopupMenu? current;

    public bool TryShow(
        Avalonia.Controls.TopLevel topLevel, IReadOnlyList<NativeContextMenuEntry> entries, NativeContextMenuAnchor rect) =>
        TryShow(entries, rect);

    private bool TryShow(IReadOnlyList<NativeContextMenuEntry> entries, NativeContextMenuAnchor rect)
    {
        if (activity.FindViewById<FrameLayout>(AndroidResource.Id.Content) is not { } content)
        {
            return false;
        }

        current?.Dismiss();
        current = null;

        var anchorView = EnsureAnchor(content);
        if (anchorView.LayoutParameters is not FrameLayout.LayoutParams layout)
        {
            return false;
        }

        layout.LeftMargin = (int)Math.Round(rect.X);
        layout.TopMargin = (int)Math.Round(rect.Y);
        layout.Width = Math.Max(1, (int)Math.Round(rect.Width));
        layout.Height = Math.Max(1, (int)Math.Round(rect.Height));
        anchorView.LayoutParameters = layout;

        var popup = new PopupMenu(activity, anchorView, GravityFlags.Start);
        var menu = popup.Menu;
        if (menu is null)
        {
            popup.Dispose();
            return false;
        }

        // Draws native dividers wherever the Avalonia menu had a <Separator/>.
        menu.SetGroupDividerEnabled(true);

        var actions = new List<Action>();
        AddEntries(menu, entries, actions, rect);

        popup.MenuItemClick += (_, e) =>
        {
            // Submenu headers keep id 0 and fall through unhandled, so the popup opens them.
            var index = (e.Item?.ItemId ?? 0) - 1;
            if (index >= 0 && index < actions.Count)
            {
                // The popup's callbacks run on the Android main thread, which is also Avalonia's
                // UI thread, so the menu item's command can be raised straight away.
                actions[index]();
                e.Handled = true;
            }
        };

        popup.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(current, popup))
            {
                current = null;
            }
        };

        current = popup;

        // The layout parameters set above only take effect on the next traversal, and PopupMenu
        // reads the anchor's on-screen position when it is shown. Posting the show defers it past
        // that traversal (the sync barrier installed by requestLayout holds queued messages until
        // the frame is laid out), so the popup lands on the row instead of its previous position.
        anchorView.RequestLayout();
        anchorView.Post(() =>
        {
            if (ReferenceEquals(current, popup))
            {
                popup.Show();
            }
        });

        return true;
    }

    /// <summary>
    /// Fills <paramref name="menu"/> from <paramref name="entries"/>, appending every invokable row
    /// to <paramref name="actions"/>. An item's id is its position in that list plus one, so a click
    /// in a submenu resolves exactly like one in the root menu. ID 0, assigned to submenu headers,
    /// can never match a row.
    /// </summary>
    private void AddEntries(IMenu menu, IReadOnlyList<NativeContextMenuEntry> entries, List<Action> actions,
        NativeContextMenuAnchor rect, bool isSubmenu = false)
    {
        foreach (var entry in entries)
        {
            // Android sorts each menu by this order value, so it has to keep rising as rows are
            // added anywhere in the tree for every menu to keep its declared order.
            var order = actions.Count;

            if (entry.Items.Count > 0 && !isSubmenu)
            {
                if (menu.AddSubMenu(entry.Section, IMenu.None, order, entry.Text) is { } submenu)
                {
                    submenu.Item?.SetEnabled(entry.IsEnabled);
                    submenu.SetGroupDividerEnabled(true);
                    AddEntries(submenu, entry.Items, actions, rect, isSubmenu: true);
                }

                continue;
            }

            var menuItem = menu.Add(entry.Section, actions.Count + 1, order, entry.Text);
            if (menuItem is null)
            {
                continue;
            }

            menuItem.SetEnabled(entry.IsEnabled);
            if (entry.IsCheckable)
            {
                menuItem.SetCheckable(true);
                menuItem.SetChecked(entry.IsChecked);
            }

            actions.Add(entry.Items.Count > 0 ? () => TryShow(entry.Items, rect) : entry.Invoke);
        }
    }

    private View EnsureAnchor(FrameLayout content)
    {
        if (anchor is { } existing && ReferenceEquals(existing.Parent, content))
        {
            return existing;
        }

        // Invisible rather than gone: it must keep taking part in layout to have a position, but an
        // invisible view is not drawn and never receives touch input.
        var view = new View(activity)
        {
            LayoutParameters = new FrameLayout.LayoutParams(1, 1),
            Visibility = ViewStates.Invisible,
            Clickable = false,
            Focusable = false,
        };

        content.AddView(view);
        anchor = view;
        return view;
    }
}
