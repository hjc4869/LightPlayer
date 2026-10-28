using LightMediaRenderer.Avalonia.Controls;
using System;
using System.Collections.Generic;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed class BackdropStyleOption : ViewModelBase
{
    private readonly string titleKey;

    public BackdropStyleOption(BackdropStyle style, string titleKey)
    {
        Style = style;
        this.titleKey = titleKey;
    }

    public BackdropStyle Style { get; }

    public string Title => Localized(titleKey);
}

public sealed class AppearancePreferencesViewModel : ViewModelBase, IDisposable
{
    private readonly BackdropStyleOption[] backdropStyles =
    [
        new(BackdropStyle.Blur, nameof(Strings.Resources.ThemeColor_BackdropBlur)),
        new(BackdropStyle.LiquidGlassContentOnly, nameof(Strings.Resources.ThemeColor_BackdropGlassContentOnly)),
        new(BackdropStyle.Off, nameof(Strings.Resources.ThemeColor_BackdropOff)),
    ];

    private readonly IThemeService themeService;

    public AppearancePreferencesViewModel(IAppSettingsStore settingsStore, IThemeService themeService)
    {
        this.themeService = themeService;
        DefaultPage = new DefaultPagePreferenceViewModel(settingsStore);
        Language = new LanguagePreferenceViewModel(settingsStore);
        Wayland = new WaylandPreferenceViewModel(settingsStore);
        themeService.BackdropStyleChanged += OnBackdropStyleChanged;
    }

    public DefaultPagePreferenceViewModel DefaultPage { get; }

    public LanguagePreferenceViewModel Language { get; }

    public WaylandPreferenceViewModel Wayland { get; }

    public IReadOnlyList<BackdropStyleOption> BackdropStyleOptions => backdropStyles;

    /// <summary>False while the native Apple UI style applies its own materials.</summary>
    public bool ShowBackdropStyle => !themeService.IsAppleChromeEnabled;

    public int BackdropStyleIndex
    {
        get => Array.FindIndex(backdropStyles, option => option.Style == themeService.BackdropStyle);
        set
        {
            if (value < 0 || value >= backdropStyles.Length || BackdropStyleIndex == value)
                return;

            themeService.BackdropStyle = backdropStyles[value].Style;
            OnPropertyChanged();
        }
    }

    /// <summary>True while liquid glass applies anywhere in the app.</summary>
    public bool ShowLiquidGlassFrost =>
        themeService.IsAppleChromeEnabled || themeService.BackdropStyle == BackdropStyle.LiquidGlassContentOnly;

    public double LiquidGlassFrost
    {
        get => themeService.LiquidGlassFrost;
        set
        {
            if (themeService.LiquidGlassFrost == value)
                return;

            themeService.LiquidGlassFrost = value;
            OnPropertyChanged();
        }
    }

    private void OnBackdropStyleChanged()
    {
        OnPropertyChanged(nameof(ShowBackdropStyle));
        OnPropertyChanged(nameof(ShowLiquidGlassFrost));
    }

    public void Dispose() => themeService.BackdropStyleChanged -= OnBackdropStyleChanged;
}