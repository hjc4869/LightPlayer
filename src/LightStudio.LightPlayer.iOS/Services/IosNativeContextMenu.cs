using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;
using CoreGraphics;
using LightStudio.LightPlayer.Services;
using ObjCRuntime;
using UIKit;

namespace LightStudio.LightPlayer.iOS.Services;

/// <summary>
/// Presents context menus, button menus, and pop-up selections as UIKit menus, with the system menu
/// material, touch metrics, and gestures.
/// </summary>
/// <remarks>
/// UIKit opens a menu only from a control or a context menu interaction, so a transparent button over
/// the anchor carries the menu and opens it with <c>performPrimaryAction</c> (iOS 17.4). Earlier
/// versions keep the Avalonia flyouts. Chosen actions run as soon as UIKit calls back, without waiting
/// for the dismissal animation, which UIKit reports finished about a second later.
/// </remarks>
internal sealed class IosNativeContextMenu : INativeContextMenuPresenter
{
    private AnchorButton? active;

    public bool TryShow(TopLevel topLevel, IReadOnlyList<NativeContextMenuEntry> entries, NativeContextMenuAnchor anchor)
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(17, 4) || GetView(topLevel) is not { } view)
        {
            return false;
        }

        if (active is not null)
        {
            // UIKit shows one menu at a time; claiming the request keeps a flyout from stacking on it.
            return true;
        }

        return Present(view, topLevel.RenderScaling, anchor, UIMenu.Create(BuildElements(entries)), null);
    }

    public bool TryShowSelection(TopLevel topLevel, IReadOnlyList<string> items, int selectedIndex,
        NativeContextMenuAnchor anchor, Action<int> completed)
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(17, 4) || active is not null || items.Count == 0 ||
            GetView(topLevel) is not { } view)
        {
            return false;
        }

        // Reported once: by the chosen action, or as a dismissal once the menu has closed.
        var chosen = -1;
        var reported = false;
        void Report()
        {
            if (!reported)
            {
                reported = true;
                completed(chosen);
            }
        }

        var actions = new UIMenuElement[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            var item = index;
            // Applied in the callback: UIKit hands over the touches it held while the menu was open right
            // after it, and a tap reopening the box must see the new selection.
            var action = UIAction.Create(items[index], null, null, _ =>
            {
                chosen = item;
                Report();
            });
            action.State = index == selectedIndex ? UIMenuElementState.On : UIMenuElementState.Off;
            actions[index] = action;
        }

        return Present(view, topLevel.RenderScaling, anchor, UIMenu.Create(actions), Report);
    }

    private static UIView? GetView(TopLevel topLevel) =>
        topLevel.TryGetPlatformHandle() is { HandleDescriptor: "UIView" } handle
            ? Runtime.GetNSObject<UIView>(handle.Handle)
            : null;

    [SupportedOSPlatform("ios17.4")]
    private bool Present(UIView view, double scaling, NativeContextMenuAnchor anchor, UIMenu menu, Action? ended)
    {
        var frame = new CGRect(anchor.X / scaling, anchor.Y / scaling,
            Math.Max(1, anchor.Width / scaling), Math.Max(1, anchor.Height / scaling));
        var button = new AnchorButton(frame, Release, ended)
        {
            Menu = menu,
            ShowsMenuAsPrimaryAction = true,
            PreferredMenuElementOrder = UIContextMenuConfigurationElementOrder.Fixed,
        };
        active = button;
        view.AddSubview(button);
        button.PerformPrimaryAction();
        if (button.IsDisplayed)
        {
            return true;
        }

        // UIKit declined to open the menu, so the caller falls back to the Avalonia flyout.
        active = null;
        button.RemoveFromSuperview();
        return false;
    }

    // A closing menu no longer blocks the next one, though its dismissal animation is still running.
    private void Release(AnchorButton button)
    {
        if (ReferenceEquals(active, button))
        {
            active = null;
        }
    }

    // Groups the rows between separators into inline sections, as UIKit draws dividers between them.
    private static UIMenuElement[] BuildElements(IReadOnlyList<NativeContextMenuEntry> entries)
    {
        var sections = new List<UIMenuElement>();
        var rows = new List<UIMenuElement>();
        int? section = null;
        foreach (var entry in entries)
        {
            if (section is { } current && entry.Section != current && rows.Count > 0)
            {
                sections.Add(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, rows.ToArray()));
                rows.Clear();
            }

            section = entry.Section;
            rows.Add(BuildElement(entry));
        }

        if (rows.Count > 0)
        {
            sections.Add(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, rows.ToArray()));
        }

        return sections.Count == 1 && sections[0] is UIMenu only ? only.Children : sections.ToArray();
    }

    private static UIMenuElement BuildElement(NativeContextMenuEntry entry)
    {
        if (entry.Items.Count > 0)
        {
            return UIMenu.Create(entry.Text, null, UIMenuIdentifier.None, (UIMenuOptions)0, BuildElements(entry.Items));
        }

        var invoke = entry.Invoke;
        var action = UIAction.Create(entry.Text, null, null, _ => Dispatcher.UIThread.Post(invoke));
        if (entry.IsCheckable)
        {
            action.State = entry.IsChecked ? UIMenuElementState.On : UIMenuElementState.Off;
        }

        if (!entry.IsEnabled)
        {
            action.Attributes = UIMenuElementAttributes.Disabled;
        }

        return action;
    }

    // Carries a menu over its anchor and leaves once the menu has closed.
    private sealed class AnchorButton : UIButton
    {
        private readonly Action<AnchorButton> closing;
        private readonly Action? closed;

        public AnchorButton(CGRect frame, Action<AnchorButton> closing, Action? closed) : base(frame)
        {
            this.closing = closing;
            this.closed = closed;
            BackgroundColor = UIColor.Clear;
        }

        /// <summary>Set while <c>performPrimaryAction</c> runs, when UIKit opens the menu.</summary>
        public bool IsDisplayed { get; private set; }

        public override void WillDisplayMenu(UIContextMenuInteraction interaction, UIContextMenuConfiguration configuration,
            IUIContextMenuInteractionAnimating? animator)
        {
            base.WillDisplayMenu(interaction, configuration, animator);
            IsDisplayed = true;
        }

        public override void WillEnd(UIContextMenuInteraction interaction, UIContextMenuConfiguration configuration,
            IUIContextMenuInteractionAnimating? animator)
        {
            base.WillEnd(interaction, configuration, animator);

            // Until the dismissal animation ends the button still covers its anchor; touches pass to the
            // control beneath so that a quick tap opens a fresh menu rather than reopening this one.
            UserInteractionEnabled = false;
            closing(this);
            if (animator is null)
            {
                Finish();
            }
            else
            {
                animator.AddCompletion(Finish);
            }
        }

        private void Finish()
        {
            RemoveFromSuperview();
            if (closed is not null)
            {
                Dispatcher.UIThread.Post(closed);
            }
        }
    }
}
