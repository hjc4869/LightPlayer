using System;
using LightStudio.LightPlayer.Services;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Groups appearance preferences under a single
/// "Appearance" entry in the settings navigation list.
/// </summary>
public sealed class AppearanceSettingsSectionViewModel : SettingsSectionViewModel, IDisposable
{
    public AppearanceSettingsSectionViewModel(
        IAppSettingsStore settingsStore,
        IThemeService themeService,
        IDialogService? dialogService)
        : base(nameof(Strings.Resources.Placeholder_Appearance), string.Empty)
    {
        Preferences = new AppearancePreferencesViewModel(settingsStore, themeService);
        Theme = new ThemeSettingsSectionViewModel(themeService, dialogService, allowCustomThemes: true);
    }

    public AppearancePreferencesViewModel Preferences { get; }

    public ThemeSettingsSectionViewModel Theme { get; }

    public void Dispose() => Preferences.Dispose();
}
