using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Shared building blocks for the in-shell modal overlays used on single-view (mobile) platforms.
/// Centralizes the card chrome (title, scrollable body, button row) and the "show and await a
/// result" pattern so the dialog and playlist overlay services stay consistent and DRY.
/// </summary>
internal static class OverlayDialog
{
    /// <summary>A right-aligned horizontal row for a card's action buttons.</summary>
    public static StackPanel ButtonsRow() => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 16, 0, 0),
    };

    /// <summary>
    /// Composes a card body: a title docked on top, the button row docked at the bottom, and the
    /// scrollable content filling the middle. A <see cref="DockPanel"/> keeps the card compact for
    /// short content while letting tall content scroll when the shell caps the card height.
    /// </summary>
    public static Control BuildContent(string title, Control body, Control buttons) =>
        BuildContent(title, body, buttons, out _);

    /// <summary>Composes a card body and exposes its title for content loaded after presentation.</summary>
    public static Control BuildContent(string title, Control body, Control buttons, out TextBlock header)
    {
        var root = new DockPanel { LastChildFill = true };

        header = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);

        root.Children.Add(header);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        return root;
    }

    /// <summary>Presents <paramref name="content"/> and returns a value once the overlay closes.</summary>
    public static async Task<T> ShowForResultAsync<T>(IShellOverlayHost host, Control content, Func<T> resultSelector)
    {
        await host.ShowOverlayAsync(content);
        return resultSelector();
    }

    /// <summary>A standard confirm/cancel card. Returns true only when the confirm button was used.</summary>
    public static Task<bool> ConfirmAsync(IShellOverlayHost host, string title, string message, string confirmLabel)
    {
        var confirmed = false;
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };

        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button { Content = Strings.Resources.Common_Cancel };
        cancel.Click += (_, _) => { confirmed = false; host.CloseOverlay(); };
        var confirm = new Button { Content = confirmLabel, IsDefault = true };
        confirm.Click += (_, _) => { confirmed = true; host.CloseOverlay(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        return ShowForResultAsync(host, BuildContent(title, body, buttons), () => confirmed);
    }
}
