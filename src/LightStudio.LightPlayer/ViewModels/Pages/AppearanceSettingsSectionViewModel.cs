using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Groups the theme, default page, and language settings under a single
/// "Appearance" entry in the settings navigation list.
/// </summary>
public sealed class AppearanceSettingsSectionViewModel : SettingsSectionViewModel
{
    public AppearanceSettingsSectionViewModel(
        IAppSettingsStore settingsStore,
        IThemeVariantService themeVariantService)
        : base("Appearance", "Theme, default page, and language.")
    {
        Theme = new ThemeSettingsSectionViewModel(settingsStore, themeVariantService);
        DefaultPage = new DefaultPageSettingsSectionViewModel(settingsStore);
        Language = new LanguageSettingsSectionViewModel(settingsStore);
    }

    public ThemeSettingsSectionViewModel Theme { get; }

    public DefaultPageSettingsSectionViewModel DefaultPage { get; }

    public LanguageSettingsSectionViewModel Language { get; }
}
