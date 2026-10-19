using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>
/// Presents context menus with AppKit's <c>NSMenu</c>, which brings the system menu material,
/// typography, keyboard navigation, type-select, and submenu behavior.
/// </summary>
/// <remarks>
/// Menu tracking is scheduled on the main run loop outside Avalonia's dispatcher callback, so its
/// observer can process redraws during AppKit's nested menu loop. Item actions only record the chosen
/// row; the row's managed action runs after tracking has returned, outside AppKit's menu loop.
/// </remarks>
internal sealed unsafe class AppleNativeContextMenu : INativeContextMenuPresenter
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const nint NSControlStateValueOn = 1;

    // "- (void)itemSelected:(id)sender": void self _cmd id.
    private const string ActionTypeEncoding = "v@:@";

    private static readonly IntPtr SelAlloc = ObjC.Sel("alloc");
    private static readonly IntPtr SelInit = ObjC.Sel("init");
    private static readonly IntPtr SelRelease = ObjC.Sel("release");
    private static readonly IntPtr SelIsFlipped = ObjC.Sel("isFlipped");
    private static readonly IntPtr SelSetAutoenablesItems = ObjC.Sel("setAutoenablesItems:");
    private static readonly IntPtr SelAddItem = ObjC.Sel("addItem:");
    private static readonly IntPtr SelAddItemWithTitle = ObjC.Sel("addItemWithTitle:action:keyEquivalent:");
    private static readonly IntPtr SelSeparatorItem = ObjC.Sel("separatorItem");
    private static readonly IntPtr SelSetEnabled = ObjC.Sel("setEnabled:");
    private static readonly IntPtr SelSetState = ObjC.Sel("setState:");
    private static readonly IntPtr SelSetTag = ObjC.Sel("setTag:");
    private static readonly IntPtr SelTag = ObjC.Sel("tag");
    private static readonly IntPtr SelSetTarget = ObjC.Sel("setTarget:");
    private static readonly IntPtr SelSetSubmenu = ObjC.Sel("setSubmenu:");
    private static readonly IntPtr SelPopUp = ObjC.Sel("popUpMenuPositioningItem:atLocation:inView:");
    private static readonly IntPtr SelItemSelected = ObjC.Sel("itemSelected:");
    private static readonly IntPtr SelInitPopUpCell = ObjC.Sel("initTextCell:pullsDown:");
    private static readonly IntPtr SelSetMenu = ObjC.Sel("setMenu:");
    private static readonly IntPtr SelSelectItemAtIndex = ObjC.Sel("selectItemAtIndex:");
    private static readonly IntPtr SelPerformClick = ObjC.Sel("performClickWithFrame:inView:");
    private static readonly IntPtr DefaultRunLoopMode =
        ObjC.ReadStringConstant(ObjC.LoadLibrary(CoreFoundation), "kCFRunLoopDefaultMode");
    private static readonly IntPtr ShowMenuBlock =
        ObjC.CreateGlobalBlock((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&RunPendingMenu);

    private static IntPtr target;
    private static IntPtr emptyString;
    private static nint selectedTag;
    private static Action? pendingMenu;
    private static bool isActive;

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFRunLoopGetMain();

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopPerformBlock(IntPtr runLoop, IntPtr mode, IntPtr block);

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopWakeUp(IntPtr runLoop);

    private static void PostMenu(Action action)
    {
        pendingMenu = action;
        var runLoop = CFRunLoopGetMain();
        CFRunLoopPerformBlock(runLoop, DefaultRunLoopMode, ShowMenuBlock);
        CFRunLoopWakeUp(runLoop);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunPendingMenu(IntPtr block)
    {
        var action = pendingMenu;
        pendingMenu = null;
        try
        {
            using var context = AvaloniaSynchronizationContext.Ensure(Dispatcher.UIThread, DispatcherPriority.Normal);
            action!();
        }
        catch (Exception exception)
        {
            // Propagate through Avalonia rather than unwinding through the native block callback.
            Dispatcher.UIThread.Post(ExceptionDispatchInfo.Capture(exception).Throw);
        }
    }

    public bool TryShow(TopLevel topLevel, IReadOnlyList<NativeContextMenuEntry> entries, NativeContextMenuAnchor anchor)
    {
        if (isActive)
        {
            // AppKit tracks one menu at a time; claiming the request keeps a flyout from stacking on it.
            return true;
        }

        if (GetView(topLevel) == IntPtr.Zero)
        {
            return false;
        }

        isActive = true;
        PostMenu(() =>
        {
            try
            {
                Show(topLevel, entries, anchor);
            }
            finally
            {
                isActive = false;
            }
        });

        return true;
    }

    private static IntPtr GetView(TopLevel topLevel) =>
        topLevel.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle handle ? handle.NSView : IntPtr.Zero;

    public bool TryShowSelection(TopLevel topLevel, IReadOnlyList<string> items, int selectedIndex,
        NativeContextMenuAnchor anchor, Action<int> completed)
    {
        if (isActive || items.Count == 0 || GetView(topLevel) == IntPtr.Zero)
        {
            return false;
        }

        isActive = true;
        PostMenu(() =>
        {
            var chosen = -1;
            try
            {
                chosen = ShowSelection(topLevel, items, selectedIndex, anchor);
            }
            finally
            {
                isActive = false;
                completed(chosen);
            }
        });

        return true;
    }

    /// <summary>
    /// Tracks the menu with an off-screen <c>NSPopUpButtonCell</c>, which positions it exactly as a
    /// pop-up button does: the current item over the control's title, checked.
    /// </summary>
    private static int ShowSelection(TopLevel topLevel, IReadOnlyList<string> items, int selectedIndex,
        NativeContextMenuAnchor anchor)
    {
        var view = GetView(topLevel);
        if (view == IntPtr.Zero)
        {
            return -1;
        }

        var menu = ObjC.Send(ObjC.Send(ObjC.GetClass("NSMenu"), SelAlloc), SelInit);
        ObjC.Send(menu, SelSetAutoenablesItems, ObjC.No);
        for (var index = 0; index < items.Count; index++)
        {
            var title = ObjC.CreateString(items[index]);
            var item = ObjC.Send(menu, SelAddItemWithTitle, title, SelItemSelected, EmptyString);
            ObjC.CFRelease(title);
            ObjC.Send(item, SelSetTag, (IntPtr)(index + 1));
            ObjC.Send(item, SelSetTarget, Target);
        }

        selectedTag = 0;
        try
        {
            TrackSelection(topLevel, view, menu, selectedIndex < items.Count ? selectedIndex : -1, anchor);
        }
        finally
        {
            ObjC.Send(menu, SelRelease);
        }

        var chosen = (int)selectedTag - 1;
        selectedTag = 0;
        return chosen;
    }

    private static void TrackSelection(TopLevel topLevel, IntPtr view, IntPtr menu, int selectedIndex,
        NativeContextMenuAnchor anchor)
    {
        var scaling = topLevel.RenderScaling;
        var width = anchor.Width / scaling;
        var height = anchor.Height / scaling;
        var y = anchor.Y / scaling;
        if (ObjC.SendBool(view, SelIsFlipped) == ObjC.No)
        {
            y = topLevel.ClientSize.Height - y - height;
        }

        var cell = ObjC.Send(ObjC.Send(ObjC.GetClass("NSPopUpButtonCell"), SelAlloc), SelInitPopUpCell, EmptyString, ObjC.No);
        try
        {
            ObjC.Send(cell, SelSetMenu, menu);
            ObjC.Send(cell, SelSelectItemAtIndex, (IntPtr)selectedIndex);
            ObjC.Send(cell, SelPerformClick, new ObjC.CGRect(anchor.X / scaling, y, width, height), view);
        }
        finally
        {
            ObjC.Send(cell, SelRelease);
        }
    }

    private static void Show(TopLevel topLevel, IReadOnlyList<NativeContextMenuEntry> entries, NativeContextMenuAnchor anchor)
    {
        var view = GetView(topLevel);
        if (view == IntPtr.Zero)
        {
            return;
        }

        // The anchor is in physical pixels; AppKit view coordinates are points, which match DIPs here.
        var scaling = topLevel.RenderScaling;
        var x = anchor.X / scaling;
        var y = (anchor.Y + anchor.Height) / scaling;
        if (ObjC.SendBool(view, SelIsFlipped) == ObjC.No)
        {
            y = topLevel.ClientSize.Height - y;
        }

        var actions = new List<Action>();
        var menu = CreateMenu(entries, actions);
        selectedTag = 0;

        try
        {
            if (anchor.PositioningItemIndex is { } positioningItemIndex)
            {
                TrackSelection(topLevel, view, menu, positioningItemIndex, anchor with { Width = 0 });
            }
            else
            {
                // A nil positioning item places the menu's top-left corner at the location.
                ObjC.SendBool(menu, SelPopUp, IntPtr.Zero, x, y, view);
            }
        }
        finally
        {
            ObjC.Send(menu, SelRelease);
        }

        var index = (int)selectedTag - 1;
        selectedTag = 0;
        if (index >= 0 && index < actions.Count)
        {
            actions[index]();
        }
    }

    /// <summary>
    /// Builds an owned (+1) <c>NSMenu</c>. Every invokable row is appended to
    /// <paramref name="actions"/> and tagged with its position there plus one, so tag 0 never matches.
    /// </summary>
    private static IntPtr CreateMenu(IReadOnlyList<NativeContextMenuEntry> entries, List<Action> actions)
    {
        var menu = ObjC.Send(ObjC.Send(ObjC.GetClass("NSMenu"), SelAlloc), SelInit);
        ObjC.Send(menu, SelSetAutoenablesItems, ObjC.No);

        var section = entries.Count > 0 ? entries[0].Section : 0;
        foreach (var entry in entries)
        {
            if (entry.Section != section)
            {
                ObjC.Send(menu, SelAddItem, ObjC.Send(ObjC.GetClass("NSMenuItem"), SelSeparatorItem));
                section = entry.Section;
            }

            var isSubmenu = entry.Items.Count > 0;
            var title = ObjC.CreateString(entry.Text);
            var item = ObjC.Send(menu, SelAddItemWithTitle, title, isSubmenu ? IntPtr.Zero : SelItemSelected, EmptyString);
            ObjC.CFRelease(title);
            ObjC.Send(item, SelSetEnabled, entry.IsEnabled ? ObjC.Yes : ObjC.No);

            if (isSubmenu)
            {
                var submenu = CreateMenu(entry.Items, actions);
                ObjC.Send(item, SelSetSubmenu, submenu);
                ObjC.Send(submenu, SelRelease);
                continue;
            }

            actions.Add(entry.Invoke);
            ObjC.Send(item, SelSetTag, (IntPtr)actions.Count);
            ObjC.Send(item, SelSetTarget, Target);
            if (entry.IsCheckable && entry.IsChecked)
            {
                ObjC.Send(item, SelSetState, NSControlStateValueOn);
            }
        }

        return menu;
    }

    private static IntPtr EmptyString =>
        emptyString != IntPtr.Zero ? emptyString : emptyString = ObjC.CreateString(string.Empty);

    /// <summary>Process-lifetime action target; <c>NSMenuItem.target</c> does not retain it.</summary>
    private static IntPtr Target
    {
        get
        {
            if (target != IntPtr.Zero)
            {
                return target;
            }

            var cls = ObjC.AllocateClassPair(ObjC.GetClass("NSObject"), "LPContextMenuTarget");
            ObjC.AddMethod(
                cls,
                SelItemSelected,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnItemSelected,
                ActionTypeEncoding);
            ObjC.RegisterClassPair(cls);

            target = ObjC.Send(ObjC.Send(cls, SelAlloc), SelInit);
            return target;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnItemSelected(IntPtr self, IntPtr cmd, IntPtr sender) =>
        selectedTag = ObjC.Send(sender, SelTag);
}
