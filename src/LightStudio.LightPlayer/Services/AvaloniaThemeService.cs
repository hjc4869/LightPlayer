using LightMediaRenderer.Avalonia.Controls;
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Default <see cref="IThemeService"/>. Applies the selected color themes by
/// installing a runtime <see cref="ResourceDictionary"/> whose theme dictionaries
/// override <c>Palette.axaml</c>, and drives the light/dark/system appearance via
/// <see cref="Application.RequestedThemeVariant"/>. All choices persist through the
/// injected <see cref="IAppSettingsStore"/>.
/// </summary>
public sealed class AvaloniaThemeService : IThemeService
{
    private readonly IAppSettingsStore settings;
    private ResourceDictionary? installedOverride;

    // The pair derived from the OS accent, rebuilt whenever the platform reports a new one. Left
    // null where no accent is worth following, which is also what keeps them out of the picker.
    private ColorTheme? systemAccentLight;
    private ColorTheme? systemAccentDark;

    // Optimistic until the window host reports back: the palette is built once before any window
    // exists, and starting opaque would flash the classic surfaces before the backdrop lands.
    private bool micaApplied = true;

    public AvaloniaThemeService(IAppSettingsStore settings)
    {
        this.settings = settings;

        RefreshSystemAccentThemes();
        if (PlatformServices.SupportsSystemAccentTheme && Application.Current?.PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += OnPlatformColorValuesChanged;
        }

        ApplyFromSettings();
    }

    public event Action? ThemesChanged;

    public AppThemePreference Mode => settings.ThemePreference;

    public bool IsMicaSupported => PlatformServices.SupportsMicaBackdrop;

    public bool IsMicaEnabled
    {
        get => settings.EnableMicaBackdrop;
        set
        {
            if (settings.EnableMicaBackdrop == value)
            {
                return;
            }

            settings.EnableMicaBackdrop = value;

            // A fresh request deserves a fresh answer: the host reports again once it has asked
            // the platform for the backdrop.
            micaApplied = true;
            ApplyPalette();
            ThemesChanged?.Invoke();
        }
    }

    public bool IsMicaActive => IsMicaSupported && IsMicaEnabled && micaApplied;

    public BackdropStyle BackdropStyle
    {
        get => settings.BackdropStyle == BackdropStyle.LiquidGlass ? BackdropStyle.LiquidGlassContentOnly : settings.BackdropStyle;
        set
        {
            if (settings.BackdropStyle == value)
                return;

            settings.BackdropStyle = value;
            ApplyBackdropChange();
        }
    }

    public bool IsAppleChromeEnabled
    {
        get => PlatformServices.SupportsAppleChrome && settings.EnableAppleChrome;
        set
        {
            if (settings.EnableAppleChrome == value)
                return;

            settings.EnableAppleChrome = value;
            ApplyBackdropChange();
        }
    }

    public event Action? BackdropStyleChanged;

    private void ApplyBackdropChange()
    {
        if (installedOverride is not null)
        {
            ApplyBackdropResources(installedOverride);
        }

        BackdropStyleChanged?.Invoke();
    }

    private void ApplyBackdropResources(ResourceDictionary resources)
    {
        var style = IsAppleChromeEnabled ? BackdropStyle.LiquidGlass : BackdropStyle;
        resources["AppBackdropStyle"] = style == BackdropStyle.LiquidGlassContentOnly ? BackdropStyle.Blur : style;
        resources["AppContentBackdropStyle"] = style == BackdropStyle.LiquidGlassContentOnly ? BackdropStyle.LiquidGlass : style;
        resources["AppHasBackdropEffects"] = style != BackdropStyle.Off;
        resources["AppIsLiquidGlass"] = style == BackdropStyle.LiquidGlass;
        resources["AppIsContentLiquidGlass"] = style is BackdropStyle.LiquidGlassContentOnly or BackdropStyle.LiquidGlass;
        resources["AppIsAppleChrome"] = IsAppleChromeEnabled;
    }

    public void ReportMicaApplied(bool applied)
    {
        if (micaApplied == applied)
        {
            return;
        }

        micaApplied = applied;
        ApplyPalette();
    }

    public string SelectedLightThemeId => GetSelectedTheme(ColorThemeVariant.Light).Id;

    public string SelectedDarkThemeId => GetSelectedTheme(ColorThemeVariant.Dark).Id;

    public void SetMode(AppThemePreference mode)
    {
        settings.ThemePreference = mode;
        ApplyVariant();
    }

    public IReadOnlyList<ColorTheme> GetThemes(ColorThemeVariant variant)
    {
        var themes = new List<ColorTheme>();

        // First in the picker because it is the default here: the desktop's own accent leads, the
        // shipped families follow, then the user's own themes.
        if (SystemAccentTheme(variant) is { } systemAccent)
        {
            themes.Add(systemAccent);
        }

        foreach (var builtIn in BuiltInColorThemes.All)
        {
            if (builtIn.Variant == variant)
            {
                themes.Add(builtIn);
            }
        }

        foreach (var custom in LoadCustomThemes())
        {
            if (custom.Variant == variant)
            {
                themes.Add(custom);
            }
        }

        return themes;
    }

    public ColorTheme GetSelectedTheme(ColorThemeVariant variant)
    {
        var id = variant == ColorThemeVariant.Dark
            ? settings.SelectedDarkColorThemeId
            : settings.SelectedLightColorThemeId;

        if (!string.IsNullOrEmpty(id) && FindTheme(id) is { } theme && theme.Variant == variant)
        {
            return theme;
        }

        return DefaultTheme(variant);
    }

    public ColorTheme? FindTheme(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (SystemAccentColorTheme.IsSystemAccentId(id))
        {
            return SystemAccentTheme(string.Equals(id, SystemAccentColorTheme.DarkId, StringComparison.Ordinal)
                ? ColorThemeVariant.Dark
                : ColorThemeVariant.Light);
        }

        if (BuiltInColorThemes.Find(id) is { } builtIn)
        {
            return builtIn;
        }

        foreach (var custom in LoadCustomThemes())
        {
            if (string.Equals(custom.Id, id, StringComparison.Ordinal))
            {
                return custom;
            }
        }

        return null;
    }

    public void SelectTheme(ColorTheme theme)
    {
        if (theme.Variant == ColorThemeVariant.Dark)
        {
            settings.SelectedDarkColorThemeId = theme.Id;
        }
        else
        {
            settings.SelectedLightColorThemeId = theme.Id;
        }

        ApplyPalette();
        ThemesChanged?.Invoke();
    }

    public void SaveCustomTheme(ColorTheme theme)
    {
        settings.SaveCustomColorTheme(theme.ToRecord());

        // Re-apply when the edited theme is the one currently in use so color edits show at once.
        var selectedId = theme.Variant == ColorThemeVariant.Dark
            ? settings.SelectedDarkColorThemeId
            : settings.SelectedLightColorThemeId;
        if (string.Equals(selectedId, theme.Id, StringComparison.Ordinal))
        {
            ApplyPalette();
        }

        ThemesChanged?.Invoke();
    }

    public void DeleteCustomTheme(string id)
    {
        var target = FindTheme(id);
        if (target is null || target.IsBuiltIn)
        {
            return;
        }

        var wasSelected = false;
        if (string.Equals(settings.SelectedLightColorThemeId, id, StringComparison.Ordinal))
        {
            settings.SelectedLightColorThemeId = DefaultTheme(ColorThemeVariant.Light).Id;
            wasSelected = true;
        }

        if (string.Equals(settings.SelectedDarkColorThemeId, id, StringComparison.Ordinal))
        {
            settings.SelectedDarkColorThemeId = DefaultTheme(ColorThemeVariant.Dark).Id;
            wasSelected = true;
        }

        settings.DeleteCustomColorTheme(id);

        if (wasSelected)
        {
            ApplyPalette();
        }

        ThemesChanged?.Invoke();
    }

    public bool CustomThemeNameExists(ColorThemeVariant variant, string name, string? excludeId = null)
    {
        var variantName = variant.ToString();
        foreach (var record in settings.CustomColorThemes)
        {
            if (string.Equals(record.Variant, variantName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(record.Id, excludeId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public void ApplyFromSettings()
    {
        ApplyPalette();
        ApplyVariant();
    }

    /// <summary>
    /// The theme a variant falls back to when nothing has been chosen: the one derived from the
    /// desktop accent where the platform publishes one, otherwise the shipped default family.
    /// </summary>
    private ColorTheme DefaultTheme(ColorThemeVariant variant) =>
        SystemAccentTheme(variant) ?? BuiltInColorThemes.Default(variant);

    private ColorTheme? SystemAccentTheme(ColorThemeVariant variant) =>
        variant == ColorThemeVariant.Dark ? systemAccentDark : systemAccentLight;

    private void RefreshSystemAccentThemes()
    {
        if (!PlatformServices.SupportsSystemAccentTheme)
        {
            return;
        }

        var accent = Application.Current?.PlatformSettings?.GetColorValues().AccentColor1
            ?? SystemAccentColorTheme.FallbackAccent;
        systemAccentLight = SystemAccentColorTheme.Build(ColorThemeVariant.Light, accent);
        systemAccentDark = SystemAccentColorTheme.Build(ColorThemeVariant.Dark, accent);
    }

    // Raised when the user picks a different desktop accent (or flips the OS appearance). Rebuild
    // the derived pair and re-apply so a running app follows along without a restart; the palette
    // is re-installed unconditionally because it costs one dictionary swap and the picker previews
    // need the new colors either way.
    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues e) =>
        Dispatcher.UIThread.Post(() =>
        {
            RefreshSystemAccentThemes();
            ApplyPalette();
            ThemesChanged?.Invoke();
        });

    private List<ColorTheme> LoadCustomThemes()
    {
        var themes = new List<ColorTheme>();
        foreach (var record in settings.CustomColorThemes)
        {
            var variant = Enum.TryParse<ColorThemeVariant>(record.Variant, ignoreCase: true, out var parsed)
                ? parsed
                : ColorThemeVariant.Light;
            themes.Add(ColorTheme.FromRecord(record, BuiltInColorThemes.Default(variant)));
        }

        return themes;
    }

    private void ApplyPalette()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var overrideDict = new ResourceDictionary();
        var mica = IsMicaActive;
        overrideDict["AppIsMicaActive"] = mica;
        ApplyBackdropResources(overrideDict);

        // Fluent keys its light resources under the "Default" theme-variant dictionary and its
        // dark resources under "Dark". Fluent's accent brushes resolve SystemAccentColor in the
        // variant of the dictionary they live in, so a light or system-follow app only picks up an
        // override registered under Default -- registering only Light left Fluent controls stuck on
        // the OS accent in light mode. Register the light palette under both Default and Light (and
        // the dark palette under Dark) so the accent follows the theme in every mode.
        overrideDict.ThemeDictionaries[ThemeVariant.Default] = BuildVariantDictionary(GetSelectedTheme(ColorThemeVariant.Light), mica);
        overrideDict.ThemeDictionaries[ThemeVariant.Light] = BuildVariantDictionary(GetSelectedTheme(ColorThemeVariant.Light), mica);
        overrideDict.ThemeDictionaries[ThemeVariant.Dark] = BuildVariantDictionary(GetSelectedTheme(ColorThemeVariant.Dark), mica);

        // Replacing the last merged dictionary raises ResourcesChanged, so every
        // DynamicResource consumer re-resolves against the new colors immediately.
        if (installedOverride is not null)
        {
            app.Resources.MergedDictionaries.Remove(installedOverride);
        }

        app.Resources.MergedDictionaries.Add(overrideDict);
        installedOverride = overrideDict;
    }

    private static ResourceDictionary BuildVariantDictionary(ColorTheme theme, bool mica)
    {
        var dictionary = new ResourceDictionary();
        foreach (var role in AppColorRoles.All)
        {
            var color = theme.Get(role.Role);
            dictionary[role.BrushKey] = new SolidColorBrush(color);
            if (role.ColorKey is { } colorKey)
            {
                dictionary[colorKey] = color;
            }
        }

        AddShellSurfaces(dictionary, theme, mica);

        var navigation = theme.Get(AppColorRole.NavBackground);
        var queue = theme.Get(AppColorRole.PageBackground);
        dictionary["AppNavBackdropColor"] = navigation;
        dictionary["AppNavBackdropTintColor"] = Color.FromArgb(0xB0, navigation.R, navigation.G, navigation.B);
        dictionary["AppQueueBackdropColor"] = queue;
        dictionary["AppQueueBackdropTintColor"] = Color.FromArgb(0xB0, queue.R, queue.G, queue.B);

        var panel = theme.Get(AppColorRole.PanelBackground);
        var selection = theme.Get(AppColorRole.Accent);
        bool dark = theme.Variant == ColorThemeVariant.Dark;
        byte foreground = dark ? (byte)0xFF : (byte)0x00;
        // Light materials keep the lighter surface where panels are grayer than the page.
        var material = !dark && queue.R + queue.G + queue.B > panel.R + panel.G + panel.B ? queue : panel;
        dictionary["AppPanelBackdropColor"] = material;
        dictionary["AppPanelBackdropTintColor"] = Color.FromArgb(0xB0, material.R, material.G, material.B);
        dictionary["AppBackdropHoverBrush"] = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x18 : (byte)0x14, foreground, foreground, foreground));
        dictionary["AppBackdropPressedBrush"] = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x28 : (byte)0x24, foreground, foreground, foreground));
        dictionary["AppBackdropSelectionBrush"] = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x50 : (byte)0x30, selection.R, selection.G, selection.B));
        dictionary["AppBackdropSelectedHoverBrush"] = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x68 : (byte)0x48, selection.R, selection.G, selection.B));
        dictionary["AppBackdropFieldBrush"] = new SolidColorBrush(Color.FromArgb(0x30, material.R, material.G, material.B));

        // Drive Fluent's accent from the selected theme so every built-in control -- slider,
        // check box, radio button, toggle switch, combo box and list selection -- follows the app
        // accent instead of the OS accent. Fluent's accent brushes bind to SystemAccentColor (and
        // its shades for hover/pressed states), so overriding these colors cascades to all of them.
        var accent = theme.Get(AppColorRole.Accent);
        dictionary["SystemAccentColor"] = accent;
        AddMacControlColors(dictionary, accent, dark);
        var (dark1, dark2, dark3, light1, light2, light3) = CalculateAccentShades(accent);
        dictionary["SystemAccentColorDark1"] = dark1;
        dictionary["SystemAccentColorDark2"] = dark2;
        dictionary["SystemAccentColorDark3"] = dark3;
        dictionary["SystemAccentColorLight1"] = light1;
        dictionary["SystemAccentColorLight2"] = light2;
        dictionary["SystemAccentColorLight3"] = light3;

        return dictionary;
    }

    /// <summary>
    /// Publishes the four shell surfaces (window base, navigation chrome, playback bar, queue),
    /// the two page layers and the chrome divider. Without a backdrop these are just the matching
    /// palette colors, so the shell looks exactly as it always has. Over a backdrop the chrome
    /// turns transparent -- rail, playback bar, queue and bottom navigation then share the one
    /// material the title bar is drawn in, with nothing dividing them -- and the page area is
    /// raised off it in two translucent steps, the way WinUI's LayerOnMicaBaseAlt brushes are:
    /// the page itself, and a second step for the panes that sit on the page (a master/detail
    /// detail pane, above all).
    /// </summary>
    private static void AddShellSurfaces(ResourceDictionary dictionary, ColorTheme theme, bool mica)
    {
        var page = theme.Get(AppColorRole.PageBackground);
        if (!mica)
        {
            dictionary["AppShellBackgroundBrush"] = new SolidColorBrush(page);
            dictionary["AppNavSurfaceBrush"] = new SolidColorBrush(theme.Get(AppColorRole.NavBackground));
            dictionary["AppPlaybackBarSurfaceBrush"] = new SolidColorBrush(theme.Get(AppColorRole.PlaybackBarBackground));
            dictionary["AppQueueSurfaceBrush"] = new SolidColorBrush(page);

            // The page already sits on the shell background, so it needs no fill of its own, and
            // the panes on it stay the flat page color they have always been.
            dictionary["AppContentSurfaceBrush"] = new SolidColorBrush(Colors.Transparent);
            dictionary["AppContentLayerBrush"] = new SolidColorBrush(page);
            dictionary["AppChromeDividerBrush"] = new SolidColorBrush(theme.Get(AppColorRole.Divider));
            dictionary["AppSubtleHoverBrush"] = new SolidColorBrush(theme.Get(AppColorRole.Hover));
            return;
        }

        var transparent = new SolidColorBrush(Colors.Transparent);
        dictionary["AppShellBackgroundBrush"] = transparent;
        dictionary["AppNavSurfaceBrush"] = transparent;
        dictionary["AppPlaybackBarSurfaceBrush"] = transparent;
        dictionary["AppQueueSurfaceBrush"] = transparent;
        dictionary["AppContentSurfaceBrush"] = new SolidColorBrush(LayerOnBackdrop(page, theme.Variant, second: false));
        dictionary["AppContentLayerBrush"] = new SolidColorBrush(LayerOnBackdrop(page, theme.Variant, second: true));
        dictionary["AppChromeDividerBrush"] = transparent;

        // A solid hover fill reads as a gray plate once what it covers is a material rather than a
        // flat page color -- on the backdrop itself, and equally on the translucent layers over it.
        // Wash what is underneath instead, the way the system's own subtle fills do: darken it in a
        // light theme, lighten it in a dark one.
        dictionary["AppSubtleHoverBrush"] = new SolidColorBrush(theme.Variant == ColorThemeVariant.Dark
            ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
    }

    /// <summary>
    /// A translucent page layer drawn over the backdrop, in two steps: the first raises the page
    /// off the surrounding material, the second raises a pane off the page. A light theme veils
    /// with its own page color, which is already near white. A dark theme cannot: its page color
    /// is darker than the backdrop, so thinning it would dig a hole rather than lift the page;
    /// the color is lightened first so the thinned layer still reads as a step up.
    /// </summary>
    private static Color LayerOnBackdrop(Color page, ColorThemeVariant variant, bool second)
    {
        if (variant != ColorThemeVariant.Dark)
        {
            return Color.FromArgb(second ? (byte)0xF0 : (byte)0xB0, page.R, page.G, page.B);
        }

        var hsl = page.ToHsl();
        var lifted = new HslColor(1d, hsl.H, hsl.S, Math.Min(1d, hsl.L + 0.12)).ToRgb();
        return Color.FromArgb(second ? (byte)0xC0 : (byte)0x70, lifted.R, lifted.G, lifted.B);
    }

    // Linear maps from the accent to AppKit's drawn control colors, fitted across every macOS accent.
    private static readonly double[] LightFocusRing = [0.9742, 0.0481, 0.1847, -17.74, 0.0111, 0.9646, -0.0261, -10.66, 0.0699, 0.1916, 1.0724, -53.18];
    private static readonly double[] DarkFocusRing = [0.7415, -0.1651, -0.0786, 104.80, -0.0015, 1.2063, 0.0252, 23.02, 0.1910, 0.1240, 1.0990, -17.72];
    private static readonly double[] LightPressedAccent = [0.9, 0, 0, 0, 0, 0.9, 0, 0, 0, 0, 0.9, 0];
    private static readonly double[] DarkPressedAccent = [0.8726, 0.0975, 0.1083, 12.17, 0.0152, 0.8978, 0.0111, 22.84, 0.1453, 0.3222, 1.0270, -49.18];
    private static readonly double[] LightDisabledAccent = [0.8726, 0.0201, 0.0877, 10.69, 0.0219, 0.9151, 0.0001, 1.76, 0.0372, 0.1036, 0.9516, -14.45];
    private static readonly double[] DarkDisabledAccent = [0.9815, 0.2171, 0.1918, -35.30, 0.0521, 1.0249, 0.0504, -11.51, 0.1266, 0.3062, 1.0625, -56.65];

    private static void AddMacControlColors(ResourceDictionary dictionary, Color accent, bool dark)
    {
        dictionary["AppMacFocusRingBrush"] = new SolidColorBrush(MapAccent(accent, dark ? DarkFocusRing : LightFocusRing, dark ? 0.48 : 0.49));
        dictionary["AppMacAccentPressedBrush"] = new SolidColorBrush(MapAccent(accent, dark ? DarkPressedAccent : LightPressedAccent, 1));
        dictionary["AppMacAccentDisabledBrush"] = new SolidColorBrush(MapAccent(accent, dark ? DarkDisabledAccent : LightDisabledAccent, 0.7));

        // AppKit lays 30% of the accent over white, or over a neutral gray in dark mode.
        var paper = dark ? 0x59 : 0xFF;
        dictionary["AppMacTextSelectionBrush"] = new SolidColorBrush(Color.FromRgb(
            (byte)Math.Round(accent.R * 0.3 + paper * 0.7),
            (byte)Math.Round(accent.G * 0.3 + paper * 0.7),
            (byte)Math.Round(accent.B * 0.3 + paper * 0.7)));
    }

    private static Color MapAccent(Color accent, double[] map, double alpha)
    {
        byte Channel(int row) => (byte)Math.Clamp(Math.Round(
            map[row * 4] * accent.R + map[row * 4 + 1] * accent.G + map[row * 4 + 2] * accent.B + map[row * 4 + 3]), 0, 255);
        return Color.FromArgb((byte)Math.Round(alpha * 255), Channel(0), Channel(1), Channel(2));
    }

    // Mirrors Avalonia's internal SystemAccentColors.CalculateAccentShades so the derived accent
    // shades match those the Fluent theme computes for a platform accent (darker for pressed,
    // lighter for pointer-over states).
    private static (Color Dark1, Color Dark2, Color Dark3, Color Light1, Color Light2, Color Light3) CalculateAccentShades(Color accent)
    {
        const double dark1Step = 28.5 / 255d;
        const double dark2Step = 49 / 255d;
        const double dark3Step = 74.5 / 255d;
        const double light1Step = 39 / 255d;
        const double light2Step = 70 / 255d;
        const double light3Step = 103 / 255d;

        var hsl = accent.ToHsl();
        return (
            new HslColor(hsl.A, hsl.H, hsl.S, hsl.L - dark1Step).ToRgb(),
            new HslColor(hsl.A, hsl.H, hsl.S, hsl.L - dark2Step).ToRgb(),
            new HslColor(hsl.A, hsl.H, hsl.S, hsl.L - dark3Step).ToRgb(),
            new HslColor(hsl.A, hsl.H, hsl.S, hsl.L + light1Step).ToRgb(),
            new HslColor(hsl.A, hsl.H, hsl.S, hsl.L + light2Step).ToRgb(),
            new HslColor(hsl.A, hsl.H, hsl.S, hsl.L + light3Step).ToRgb()
        );
    }

    private void ApplyVariant()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = settings.ThemePreference switch
        {
            AppThemePreference.Light => ThemeVariant.Light,
            AppThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
