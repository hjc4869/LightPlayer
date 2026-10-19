#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Anchor rectangle for a native context menu, in physical pixels relative to the top-level's
/// client area (Avalonia device-independent coordinates already multiplied by render scaling).
/// </summary>
public readonly record struct NativeContextMenuAnchor(double X, double Y, double Width, double Height)
{
    public int? PositioningItemIndex { get; init; }
}

/// <summary>A single row mirrored from an Avalonia <see cref="MenuFlyout"/> into a native menu.</summary>
public sealed class NativeContextMenuEntry
{
    /// <summary>Display text, taken from the source <see cref="MenuItem.Header"/>.</summary>
    public string Text { get; init; } = string.Empty;

    public bool IsEnabled { get; init; } = true;

    public bool IsCheckable { get; init; }

    public bool IsChecked { get; init; }

    /// <summary>
    /// Section index. Incremented after every <see cref="Separator"/> so a platform that supports
    /// grouped menus (Android's <c>setGroupDividerEnabled</c>) can draw dividers in the same places
    /// the Avalonia menu would.
    /// </summary>
    public int Section { get; init; }

    /// <summary>
    /// Child rows when this entry is a submenu, empty for a normal row.
    /// </summary>
    public IReadOnlyList<NativeContextMenuEntry> Items { get; init; } = Array.Empty<NativeContextMenuEntry>();

    /// <summary>Runs the source menu item's action. Must be invoked on the UI thread.</summary>
    public Action Invoke { get; init; } = static () => { };
}

/// <summary>
/// Shows a context menu using the host platform's own menu widget instead of Avalonia's
/// <see cref="MenuFlyout"/> popup.
/// </summary>
public interface INativeContextMenuPresenter
{
    /// <summary>
    /// Shows <paramref name="entries"/> anchored to <paramref name="anchor"/> within
    /// <paramref name="topLevel"/>.
    /// Returns <see langword="false"/> when the platform cannot present right now, in which case
    /// the caller falls back to the Avalonia flyout.
    /// </summary>
    bool TryShow(TopLevel topLevel, IReadOnlyList<NativeContextMenuEntry> entries, NativeContextMenuAnchor anchor);

    /// <summary>
    /// Shows a selection menu the way the platform presents a pop-up button, with the item at
    /// <paramref name="selectedIndex"/> over <paramref name="anchor"/>. Reports the chosen index,
    /// or -1 when dismissed, to <paramref name="completed"/>.
    /// </summary>
    bool TryShowSelection(TopLevel topLevel, IReadOnlyList<string> items, int selectedIndex,
        NativeContextMenuAnchor anchor, Action<int> completed) => false;
}

/// <summary>
/// Redirects <see cref="Control.ContextFlyout"/> menus, and the button drop-downs attached through
/// <see cref="MenuProperty"/>, to a platform-native menu widget.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia opens a <c>ContextFlyout</c> from a private instance handler that
/// <c>PopupFlyoutBase</c> attaches to each target control when <c>ContextFlyoutProperty</c> changes
/// (<c>c.ContextRequested += OnControlContextRequested</c>), which lands on the <b>Bubble</b> route
/// with <c>handledEventsToo: false</c>. A <b>class</b> handler on the same route runs first for a
/// given element -- <c>Interactive.BuildEventRoute</c> adds the class-handler marker before that
/// element's instance handlers, and <c>EventRoute.RaiseEventImpl</c> dispatches
/// <c>RoutedEvent.InvokeRaised</c> when it first reaches each target. Marking the event handled
/// here therefore suppresses Avalonia's own flyout without unsubscribing anything.
/// </para>
/// <para>
/// Bubble (not Tunnel) is deliberate: it preserves Avalonia's innermost-target-wins behavior for
/// nested context flyouts, whereas the tunnel pass would open the outermost one.
/// </para>
/// </remarks>
public static class NativeContextMenu
{
    /// <summary>
    /// Drop-down menu for a button, in place of <see cref="Button.Flyout"/>: the menu is offered to
    /// the platform first and only falls back to the Avalonia fly-out.
    /// </summary>
    /// <remarks>
    /// <c>Button.Flyout</c> cannot be redirected, because <c>Button.OnClick</c> opens it before it
    /// raises <c>Click</c>, so there is no point at which a handler could step in.
    /// </remarks>
    public static readonly AttachedProperty<MenuFlyout?> MenuProperty =
        AvaloniaProperty.RegisterAttached<Button, MenuFlyout?>("Menu", typeof(NativeContextMenu));

    /// <summary>
    /// Presents the combo box's choices as the platform's pop-up button menu where available,
    /// falling back to the Avalonia drop-down.
    /// </summary>
    public static readonly AttachedProperty<bool> IsNativeSelectionProperty =
        AvaloniaProperty.RegisterAttached<ComboBox, bool>("IsNativeSelection", typeof(NativeContextMenu));

    private static INativeContextMenuPresenter? presenter;
    private static bool installed;

    static NativeContextMenu()
    {
        MenuProperty.Changed.AddClassHandler<Button>(OnMenuChanged);
        IsNativeSelectionProperty.Changed.AddClassHandler<ComboBox>(OnNativeSelectionChanged);
    }

    public static MenuFlyout? GetMenu(Button button) => button.GetValue(MenuProperty);

    public static void SetMenu(Button button, MenuFlyout? value) => button.SetValue(MenuProperty, value);

    public static bool GetIsNativeSelection(ComboBox comboBox) => comboBox.GetValue(IsNativeSelectionProperty);

    public static void SetIsNativeSelection(ComboBox comboBox, bool value) => comboBox.SetValue(IsNativeSelectionProperty, value);

    /// <summary>
    /// Registers the platform presenter and, on first call, installs the global interception
    /// handler. Heads that never call this keep Avalonia's stock flyout behavior untouched.
    /// </summary>
    public static void Install(INativeContextMenuPresenter platformPresenter)
    {
        presenter = platformPresenter ?? throw new ArgumentNullException(nameof(platformPresenter));

        if (installed)
        {
            return;
        }

        installed = true;
        InputElement.ContextRequestedEvent.AddClassHandler<Control>(
            OnContextRequested,
            RoutingStrategies.Bubble);
    }

    private static void OnMenuChanged(Button button, AvaloniaPropertyChangedEventArgs e)
    {
        button.RemoveHandler(Button.ClickEvent, OnMenuButtonClick);

        if (e.NewValue is MenuFlyout)
        {
            button.AddHandler(Button.ClickEvent, OnMenuButtonClick);
        }
    }

    private static void OnMenuButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && GetMenu(button) is { } flyout && !TryShow(flyout, button))
        {
            flyout.ShowAt(button);
        }
    }

    private static void OnNativeSelectionChanged(ComboBox comboBox, AvaloniaPropertyChangedEventArgs e)
    {
        comboBox.RemoveHandler(InputElement.PointerPressedEvent, OnSelectionPointerPressed);
        comboBox.RemoveHandler(InputElement.KeyDownEvent, OnSelectionKeyDown);

        if (e.NewValue is true)
        {
            comboBox.AddHandler(InputElement.PointerPressedEvent, OnSelectionPointerPressed, RoutingStrategies.Tunnel);
            comboBox.AddHandler(InputElement.KeyDownEvent, OnSelectionKeyDown, RoutingStrategies.Tunnel);
        }
    }

    // Pop-up buttons open on mouse down so the choice can be made by dragging to it and releasing.
    private static void OnSelectionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is ComboBox comboBox && !e.Handled && !comboBox.IsDropDownOpen &&
            e.GetCurrentPoint(comboBox).Properties.IsLeftButtonPressed &&
            TryShowSelection(comboBox, e.Pointer))
        {
            e.Handled = true;
        }
    }

    private static void OnSelectionKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ComboBox comboBox || e.Handled || comboBox.IsDropDownOpen)
        {
            return;
        }

        var opens = e.KeyModifiers == KeyModifiers.None && e.Key is Key.Space or Key.Enter or Key.F4 ||
                    e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Up or Key.Down;
        if (opens && TryShowSelection(comboBox, null))
        {
            e.Handled = true;
        }
    }

    private static bool TryShowSelection(ComboBox comboBox, IPointer? pointer)
    {
        if (presenter is null || comboBox.IsEditable || TopLevel.GetTopLevel(comboBox) is not { } topLevel ||
            comboBox.TranslatePoint(default, topLevel) is not { } origin ||
            ReadSelectionItems(comboBox) is not { } titles)
        {
            return false;
        }

        var items = comboBox.Items.Cast<object?>().ToArray();
        var scaling = topLevel.RenderScaling;
        var anchor = new NativeContextMenuAnchor(
            origin.X * scaling,
            origin.Y * scaling,
            comboBox.Bounds.Width * scaling,
            comboBox.Bounds.Height * scaling);

        return presenter.TryShowSelection(topLevel, titles, comboBox.SelectedIndex, anchor, index =>
        {
            // Menu tracking consumes the mouse up that would end the press's implicit capture.
            if (pointer?.Captured is not null)
            {
                pointer.Capture(null);
            }

            if (index >= 0 && index < items.Length && comboBox.Items.Contains(items[index]))
            {
                comboBox.SelectedItem = items[index];
            }
        });
    }

    /// <summary>
    /// Reads the text each item shows, or returns <see langword="null"/> when an item's
    /// presentation is not plain text the native menu can carry.
    /// </summary>
    private static List<string>? ReadSelectionItems(ComboBox comboBox)
    {
        var titles = new List<string>(comboBox.ItemCount);
        foreach (var item in comboBox.Items)
        {
            var title = item switch
            {
                ContentControl container => container.Content as string,
                _ when comboBox.DisplayMemberBinding is { } binding => ReadBindingText(binding, item),
                _ when comboBox.ItemTemplate is { } template => ReadTemplateText(template, item),
                string or Enum or IFormattable => item.ToString(),
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            titles.Add(title);
        }

        return titles.Count > 0 ? titles : null;
    }

    private static string? ReadBindingText(Avalonia.Data.BindingBase binding, object? item)
    {
        var text = new TextBlock { DataContext = item };
        text.Bind(TextBlock.TextProperty, binding);
        return text.Text;
    }

    private static string? ReadTemplateText(IDataTemplate template, object? item)
    {
        if (template.Build(item) is not { } content)
        {
            return null;
        }

        content.DataContext = item;
        var texts = content.GetSelfAndLogicalDescendants()
            .OfType<TextBlock>()
            .Where(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text))
            .Select(text => text.Text!)
            .ToList();
        return texts.Count == 1 ? texts[0] : null;
    }

    /// <summary>
    /// Shows <paramref name="flyout"/> natively, anchored to <paramref name="target"/>. Returns
    /// <see langword="false"/> when no platform presenter is installed or it declined, leaving the
    /// caller to open the Avalonia flyout instead. Menus that are opened deliberately (a button's
    /// drop-down) rather than by a context gesture go through here.
    /// </summary>
    public static bool TryShow(MenuFlyout flyout, Control target) => TryShow(flyout, target, null);

    public static bool TryShow(MenuFlyout flyout, Control target, int positioningItemIndex) =>
        TryShow(flyout, target, null, positioningItemIndex);

    private static bool TryShow(MenuFlyout flyout, Control target, Point? position, int? positioningItemIndex = null)
    {
        if (presenter is null)
        {
            return false;
        }

        if (TopLevel.GetTopLevel(target) is not { } topLevel)
        {
            return false;
        }

        var entries = BuildEntries(flyout, target);
        if (entries.Count == 0)
        {
            return false;
        }

        if (target.TranslatePoint(position ?? default, topLevel) is not { } origin)
        {
            return false;
        }

        var scaling = topLevel.RenderScaling;
        var anchor = new NativeContextMenuAnchor(
            origin.X * scaling,
            origin.Y * scaling,
            (position.HasValue ? 1 : target.Bounds.Width) * scaling,
            (position.HasValue ? 1 : target.Bounds.Height) * scaling)
        {
            PositioningItemIndex = positioningItemIndex,
        };

        return presenter.TryShow(topLevel, entries, anchor);
    }

    private static void OnContextRequested(Control control, ContextRequestedEventArgs e)
    {
        if (e.Handled || presenter is null)
        {
            return;
        }

        // Matches PopupFlyoutBase: a ContextMenu wins over a ContextFlyout.
        if (control.ContextMenu is not null || control.ContextFlyout is not MenuFlyout flyout)
        {
            return;
        }

        Point? position = e.TryGetPosition(control, out var point) ? point : null;
        if (TryShow(flyout, control, position))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Reads the flyout's items while they are temporarily parented to <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// A <c>MenuFlyout</c> is an <c>AvaloniaObject</c>, not a control, and its items only join a
    /// logical tree once Avalonia builds the <c>MenuFlyoutPresenter</c> and <c>Popup.SetPopupParent</c>
    /// parents it to the target. Until then they are rootless, so <i>every</i> binding on them is
    /// dead: plain <c>{Binding Foo}</c> has no inherited DataContext, and tree-walking forms such as
    /// <c>{Binding $parent[UserControl].DataContext.PlayCommand}</c> have no ancestors to find.
    /// Because the native path never opens that popup, the items are lent the target's logical
    /// parent for the duration of the read, which makes both forms resolve, then handed back.
    /// </remarks>
    private static List<NativeContextMenuEntry> BuildEntries(MenuFlyout flyout, Control target)
    {
        var borrowed = Borrow(flyout.Items, target);

        try
        {
            return ReadEntries(flyout.Items, target);
        }
        finally
        {
            Release(borrowed);
        }
    }

    /// <summary>
    /// Gives every rootless item in <paramref name="items"/> the logical parent
    /// <paramref name="target"/>, and returns those that were actually borrowed.
    /// </summary>
    /// <remarks>
    /// <c>StyledElement.SetParent</c> assigns the inheritance parent, resolves the logical root and
    /// raises <c>ParentProperty</c>, so DataContext, <c>$parent</c> lookups, styles and dynamic
    /// resources all become live synchronously. It throws when the control already has a parent,
    /// hence the <c>Parent: null</c> filter -- anything Avalonia's own presenter owns is left alone,
    /// since it is correctly rooted already.
    /// </remarks>
    private static List<Control> Borrow(IEnumerable items, Control target)
    {
        var borrowed = new List<Control>();

        foreach (var raw in items)
        {
            if (raw is Control { Parent: null } item)
            {
                ((ISetLogicalParent)item).SetParent(target);
                borrowed.Add(item);
            }
        }

        return borrowed;
    }

    private static void Release(List<Control> borrowed)
    {
        foreach (var item in borrowed)
        {
            ((ISetLogicalParent)item).SetParent(null);
        }
    }

    private static List<NativeContextMenuEntry> ReadEntries(IEnumerable items, Control target)
    {
        var entries = new List<NativeContextMenuEntry>();
        var section = 0;
        var separatorPending = false;

        foreach (var raw in items)
        {
            if (raw is Separator separator)
            {
                separatorPending |= separator.IsVisible && entries.Count > 0;
                continue;
            }

            if (raw is not MenuItem item || !item.IsVisible)
            {
                continue;
            }

            var text = item.Header?.ToString();
            if (string.IsNullOrEmpty(text) || text == "-")
            {
                continue;
            }

            List<NativeContextMenuEntry>? children = null;
            if (item.ItemCount > 0)
            {
                children = ReadSubmenu(item, target);
                if (children.Count == 0)
                {
                    continue;
                }
            }

            if (separatorPending)
            {
                section++;
                separatorPending = false;
            }

            // Captured now, while the bindings are live; they go dead again once the item is
            // handed back out of the logical tree.
            var command = item.Command;
            var parameter = item.CommandParameter;
            var source = item;

            entries.Add(new NativeContextMenuEntry
            {
                Text = text,
                IsEnabled = item.IsEnabled && (command is null || command.CanExecute(parameter)),
                IsCheckable = item.ToggleType != MenuItemToggleType.None,
                IsChecked = item.IsChecked,
                Section = section,
                Items = children ?? (IReadOnlyList<NativeContextMenuEntry>)Array.Empty<NativeContextMenuEntry>(),
                Invoke = () => InvokeEntry(source, target, command, parameter),
            });
        }

        return entries;
    }

    /// <summary>
    /// Reads a submenu's rows. Its children are lent the same logical parent as the top-level
    /// items: an unopened <c>MenuItem</c> never realizes its containers, so they are rootless too
    /// and their bindings (headers, <c>IsVisible</c>, <c>IsChecked</c>) are dead until borrowed.
    /// </summary>
    private static List<NativeContextMenuEntry> ReadSubmenu(MenuItem item, Control target)
    {
        var borrowed = Borrow(item.Items, target);

        try
        {
            return ReadEntries(item.Items, target);
        }
        finally
        {
            Release(borrowed);
        }
    }

    private static void InvokeEntry(MenuItem item, Control target, ICommand? command, object? parameter)
    {
        // The item has to be rooted again for the click itself, not just for the read: code-behind
        // Click handlers routinely identify their subject from the sender's DataContext (every
        // QueuePane item does), and that is only live while the item has a logical parent.
        var borrowed = Borrow(new[] { item }, target);

        try
        {
            // Click drives any XAML Click="..." handler. MenuItem's own ClickEvent class handler
            // also runs the Command and marks the event handled. The event does not bubble past the
            // item, because Interactive walks the visual tree and only the logical parent was lent.
            var args = new RoutedEventArgs(MenuItem.ClickEvent);
            item.RaiseEvent(args);

            // Safety net for the case where the item could not be re-rooted and its Command binding
            // is therefore still dead: run the command captured during the read instead. Handled
            // means MenuItem already ran it, so this cannot double-execute.
            if (!args.Handled && command is not null && command.CanExecute(parameter))
            {
                command.Execute(parameter);
            }
        }
        finally
        {
            Release(borrowed);
        }
    }
}
