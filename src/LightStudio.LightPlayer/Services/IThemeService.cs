using LightMediaRenderer.Avalonia.Controls;
using System;
using System.Collections.Generic;
using LightStudio.LightPlayer.Models;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Owns the application appearance: the light/dark/system mode and the color
/// themes applied for each appearance. Applies the chosen palette to the live
/// Avalonia resources and persists all choices through <see cref="IAppSettingsStore"/>.
/// </summary>
public interface IThemeService
{
    /// <summary>The current light/dark/system mode.</summary>
    AppThemePreference Mode { get; }

    /// <summary>Sets and persists the mode, then applies the matching Avalonia theme variant.</summary>
    void SetMode(AppThemePreference mode);

    /// <summary>Id of the color theme applied for the light appearance.</summary>
    string SelectedLightThemeId { get; }

    /// <summary>Id of the color theme applied for the dark appearance.</summary>
    string SelectedDarkThemeId { get; }

    /// <summary>The selectable themes for a variant: built-ins first, then custom themes.</summary>
    IReadOnlyList<ColorTheme> GetThemes(ColorThemeVariant variant);

    /// <summary>The theme currently applied for a variant (falls back to the default built-in).</summary>
    ColorTheme GetSelectedTheme(ColorThemeVariant variant);

    /// <summary>Finds any theme (built-in or custom) by id, or null when none matches.</summary>
    ColorTheme? FindTheme(string id);

    /// <summary>Selects and applies a theme for its own variant, persisting the choice.</summary>
    void SelectTheme(ColorTheme theme);

    /// <summary>Adds or overwrites a custom theme (matched by id) and re-applies if it is in use.</summary>
    void SaveCustomTheme(ColorTheme theme);

    /// <summary>Deletes a custom theme; if it was selected, reverts that variant to the default.</summary>
    void DeleteCustomTheme(string id);

    /// <summary>True when a custom theme of the variant already uses the name (case-insensitive).</summary>
    bool CustomThemeNameExists(ColorThemeVariant variant, string name, string? excludeId = null);

    /// <summary>
    /// Whether this head can paint the shell over a system backdrop (Mica on Windows 11). False
    /// everywhere else, where <see cref="IsMicaActive"/> stays false whatever the preference is.
    /// </summary>
    bool IsMicaSupported { get; }

    /// <summary>
    /// The persisted preference. Only has an effect where <see cref="IsMicaSupported"/> is true;
    /// setting it re-applies the palette so the shell surfaces switch immediately.
    /// </summary>
    bool IsMicaEnabled { get; set; }

    /// <summary>
    /// The backdrop preference, which applies while <see cref="IsAppleChromeEnabled"/> is false and
    /// limits liquid glass to content.
    /// </summary>
    BackdropStyle BackdropStyle { get; set; }

    /// <summary>
    /// Whether the shell uses its native Apple UI style: the Apple layout, with liquid glass throughout
    /// in place of <see cref="BackdropStyle"/>. Setting it persists the preference, which only takes
    /// effect where <see cref="PlatformServices.SupportsAppleChrome"/>.
    /// </summary>
    bool IsAppleChromeEnabled { get; set; }

    /// <summary>
    /// How frosted liquid glass appears, from 0 for clear to 1 for frosted. Setting it applies at once
    /// and persists the preference.
    /// </summary>
    double LiquidGlassFrost { get; set; }

    /// <summary>
    /// Raised after <see cref="BackdropStyle"/> or <see cref="IsAppleChromeEnabled"/> changes and the
    /// resources are applied.
    /// </summary>
    event Action? BackdropStyleChanged;

    /// <summary>
    /// Whether the shell is currently painted for a backdrop: chrome turns transparent so the
    /// window, its title bar and the rail share one material, and the page area gets a lighter
    /// layer over it. False keeps the classic opaque palette.
    /// </summary>
    bool IsMicaActive { get; }

    /// <summary>
    /// Reports what the window host actually achieved. The window asks for the backdrop and calls
    /// this with the level the platform granted, so a refusal (transparency effects switched off,
    /// an unexpected build) falls back to the opaque palette instead of leaving the shell see-through.
    /// </summary>
    void ReportMicaApplied(bool applied);

    /// <summary>Applies the persisted mode and color themes to the running application.</summary>
    void ApplyFromSettings();

    /// <summary>Raised when the set of themes or the current selection changes.</summary>
    event Action? ThemesChanged;
}
