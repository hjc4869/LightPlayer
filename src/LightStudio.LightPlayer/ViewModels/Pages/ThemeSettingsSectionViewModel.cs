using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed class ThemeOption
{
    public ThemeOption(string title, AppThemePreference preference)
    {
        Title = title;
        Preference = preference;
    }

    public string Title { get; }

    public AppThemePreference Preference { get; }
}

public partial class ThemeSettingsSectionViewModel : SettingsSectionViewModel
{
    private readonly IAppSettingsStore settingsStore;
    private readonly IThemeVariantService themeVariantService;

    [ObservableProperty]
    private ThemeOption selectedTheme = null!;

    public ThemeSettingsSectionViewModel(IAppSettingsStore settingsStore, IThemeVariantService themeVariantService)
        : base("Theme", "Choose the app theme.")
    {
        this.settingsStore = settingsStore;
        this.themeVariantService = themeVariantService;

        ThemeOptions =
        [
            new ThemeOption("System", AppThemePreference.Default),
            new ThemeOption("Light", AppThemePreference.Light),
            new ThemeOption("Dark", AppThemePreference.Dark),
        ];

        selectedTheme = FindOption(settingsStore.ThemePreference);
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        settingsStore.ThemePreference = value.Preference;
        themeVariantService.Apply(value.Preference);
    }

    private ThemeOption FindOption(AppThemePreference preference)
    {
        foreach (var option in ThemeOptions)
        {
            if (option.Preference == preference)
            {
                return option;
            }
        }

        return ThemeOptions[0];
    }
}