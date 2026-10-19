using System;
using System.Collections.Generic;
using Avalonia.Media;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// A built-in Windows or macOS palette derived from the system accent and selected by default
/// on supported desktops. The OS owns its colors; custom themes can use it as a starting point.
/// </summary>
public static class SystemAccentColorTheme
{
    /// <summary>Stable id of the light system-accent theme.</summary>
    public const string LightId = "system.accent.light";

    /// <summary>Stable id of the dark system-accent theme.</summary>
    public const string DarkId = "system.accent.dark";

    /// <summary>The platform's stock accent, used until it reports the current one.</summary>
    public static readonly Color FallbackAccent = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()
        ? Color.FromRgb(0x00, 0x7A, 0xFF)
        : Color.FromRgb(0x00, 0x78, 0xD4);

    /// <summary>The id of the system-accent theme for a variant.</summary>
    public static string IdFor(ColorThemeVariant variant) =>
        variant == ColorThemeVariant.Dark ? DarkId : LightId;

    /// <summary>True when the id names one of the two system-accent themes.</summary>
    public static bool IsSystemAccentId(string? id) =>
        string.Equals(id, LightId, StringComparison.Ordinal) ||
        string.Equals(id, DarkId, StringComparison.Ordinal);

    /// <summary>
    /// Derives a palette from the system accent over neutral surfaces. macOS retains the
    /// AppKit accent; Windows adjusts its lightness for contrast in each appearance.
    /// </summary>
    public static ColorTheme Build(ColorThemeVariant variant, Color accent)
    {
        var isDark = variant == ColorThemeVariant.Dark;
        var isApple = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();
        var hsl = accent.ToHsl();

        var lightness = isDark
            ? Math.Clamp(hsl.L, 0.62, 0.80)
            : Math.Clamp(hsl.L, 0.24, 0.42);
        var themeAccent = isApple ? accent : new HslColor(1d, hsl.H, hsl.S, lightness).ToRgb();

        // Matches the tint the shipped families use for their selection fill: the accent hue at a
        // capped saturation, very light on a light theme and very dark on a dark one.
        var selection = isDark
            ? new HslColor(1d, hsl.H, Math.Min(hsl.S, 0.44), 0.24).ToRgb()
            : new HslColor(1d, hsl.H, Math.Min(hsl.S, 0.50), 0.88).ToRgb();

        var colors = new Dictionary<AppColorRole, Color>(BuiltInColorThemes.BuildPalette(
            themeAccent,
            selection,
            BuiltInColorThemes.Favorite(variant),
            variant));

        if (isApple && !isDark)
        {
            colors[AppColorRole.PageBackground] = Colors.White;
            colors[AppColorRole.PanelBackground] = Color.FromRgb(0xF6, 0xF6, 0xF6);
        }

        return new ColorTheme(
            IdFor(variant),
            string.Empty,
            variant,
            isBuiltIn: true,
            colors,
            nameKey: isApple
                ? nameof(Strings.Resources.ThemeColor_Apple)
                : nameof(Strings.Resources.ThemeColor_WindowsDefault));
    }
}
