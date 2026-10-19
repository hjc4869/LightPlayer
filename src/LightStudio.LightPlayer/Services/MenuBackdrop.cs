using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace LightStudio.LightPlayer.Services;

public enum MenuBackdropMode
{
    Default,
    Native,
    Overlay,

    /// <summary>Native windows drawn like WinUI menu flyouts, over the window manager's acrylic blur.</summary>
    WinUI,
}

public static class MenuBackdrop
{
    public static readonly CornerRadius WinUICornerRadius = new(8);

    public static readonly AttachedProperty<bool> IsMenuProperty =
        AvaloniaProperty.RegisterAttached<Popup, bool>("IsMenu", typeof(MenuBackdrop));

    private static MenuBackdropMode mode;
    private static bool installed;

    static MenuBackdrop()
    {
        IsMenuProperty.Changed.AddClassHandler<Popup>((popup, change) =>
        {
            if (change.GetNewValue<bool>())
            {
                popup.Classes.Set("nativeMenuBackdrop", mode == MenuBackdropMode.Native);
                popup.Classes.Set("overlayMenuBackdrop", mode == MenuBackdropMode.Overlay);
                popup.Classes.Set("winUIMenu", mode == MenuBackdropMode.WinUI);
                popup.SetCurrentValue(Popup.ShouldUseOverlayLayerProperty, mode == MenuBackdropMode.Overlay);
                popup.Resources["MenuFlyoutPresenterBackground"] = Brushes.Transparent;
            }
        });
    }

    public static bool GetIsMenu(Popup popup) => popup.GetValue(IsMenuProperty);

    public static void SetIsMenu(Popup popup, bool value) => popup.SetValue(IsMenuProperty, value);

    /// <summary>
    /// Installs the menu styles for <paramref name="backdropMode"/>. In <see cref="MenuBackdropMode.WinUI"/>
    /// mode, <paramref name="frameWindow"/> receives each menu window's native handle and corner radius in
    /// device pixels before the window is shown, and <paramref name="linearBlending"/> tells whether the
    /// renderer blends in linear light.
    /// </summary>
    public static void Install(
        MenuBackdropMode backdropMode,
        Action<IntPtr, double>? frameWindow = null,
        bool linearBlending = false)
    {
        if (installed || backdropMode == MenuBackdropMode.Default)
            return;

        Application application = Application.Current
            ?? throw new InvalidOperationException("Menu backdrops require an initialized application.");
        mode = backdropMode;
        installed = true;

        Popup.ChildProperty.Changed.AddClassHandler<Popup>((popup, change) =>
        {
            if (change.NewValue is MenuFlyoutPresenter or ContextMenu)
                SetIsMenu(popup, true);
        });

        if (backdropMode != MenuBackdropMode.WinUI)
        {
            application.Styles.Add(new MenuBackdropStyles());
            return;
        }

        application.Styles.Add(new WinUIMenuStyles(linearBlending));
        if (frameWindow is not null)
        {
            // The template is applied after the popup has been placed on its screen and before it shows.
            TemplatedControl.TemplateAppliedEvent.AddClassHandler<PopupRoot>((root, _) =>
            {
                if (root.Parent is Popup popup && GetIsMenu(popup) && root.TryGetPlatformHandle() is { } handle)
                    frameWindow(handle.Handle, WinUICornerRadius.TopLeft * root.RenderScaling);
            });
        }
    }
}

internal sealed class MenuBackdropStyles : Styles
{
    public MenuBackdropStyles() => AvaloniaXamlLoader.Load(this);
}

internal sealed class WinUIMenuStyles : Styles
{
    private static readonly string[] FillKeys =
    [
        "WinUIMenuItemPointerOverBrush",
        "WinUIMenuItemPressedBrush",
        "WinUIMenuSeparatorBrush",
        "WinUIMenuStrokeBrush",
    ];

    public WinUIMenuStyles(bool linearBlending)
    {
        AvaloniaXamlLoader.Load(this);
        if (!linearBlending)
            return;

        foreach (var theme in Resources.ThemeDictionaries.Values.OfType<IResourceDictionary>())
        {
            var tint = ((ISolidColorBrush)theme["WinUIMenuAcrylicTintBrush"]!).Color;
            foreach (var key in FillKeys)
                theme[key] = new SolidColorBrush(ToLinearBlending(((ISolidColorBrush)theme[key]!).Color, tint));
        }
    }

    // WinUI blends these gray fills over the tint in sRGB; this is the alpha that gives the same gray in linear light.
    private static Color ToLinearBlending(Color fill, Color tint)
    {
        double tintAlpha = tint.A / 255.0;
        double fillAlpha = fill.A / 255.0;
        double tintValue = tint.G / 255.0;
        double fillValue = fill.G / 255.0;
        double blended = (fillValue * fillAlpha + tintValue * tintAlpha * (1 - fillAlpha))
            / (fillAlpha + tintAlpha * (1 - fillAlpha));

        double tintLinear = ToLinear(tintValue);
        double blendedLinear = ToLinear(blended);
        double alpha = tintAlpha * (blendedLinear - tintLinear)
            / (ToLinear(fillValue) - tintAlpha * tintLinear - blendedLinear * (1 - tintAlpha));
        return new Color((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), fill.R, fill.G, fill.B);
    }

    private static double ToLinear(double value) =>
        value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
}