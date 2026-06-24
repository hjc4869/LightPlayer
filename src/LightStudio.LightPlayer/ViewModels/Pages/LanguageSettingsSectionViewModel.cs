using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed class LanguageOption
{
    public LanguageOption(string languageTag, string title)
    {
        LanguageTag = languageTag;
        Title = title;
    }

    /// <summary>BCP-47 language tag, or empty to follow the operating system.</summary>
    public string LanguageTag { get; }

    public string Title { get; }
}

public partial class LanguageSettingsSectionViewModel : SettingsSectionViewModel
{
    private readonly IAppSettingsStore settingsStore;
    private readonly string initialLanguage;
    private bool isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartPromptVisible))]
    private LanguageOption selectedLanguage = null!;

    public LanguageSettingsSectionViewModel(IAppSettingsStore settingsStore)
        : base("Language", "Choose the interface language.")
    {
        this.settingsStore = settingsStore;

        LanguageOptions =
        [
            new LanguageOption(string.Empty, "System"),
            new LanguageOption("en-US", "English (United States)"),
            new LanguageOption("en-GB", "English (United Kingdom)"),
            new LanguageOption("zh-CN", "简体中文 (中华人民共和国)"),
        ];

        initialLanguage = settingsStore.InterfaceLanguage;
        selectedLanguage = FindOption(initialLanguage);
        isLoaded = true;
    }

    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    /// <summary>True once the language differs from the value the app started with.</summary>
    public bool RestartPromptVisible => SelectedLanguage.LanguageTag != initialLanguage;

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (!isLoaded)
        {
            return;
        }

        settingsStore.InterfaceLanguage = value.LanguageTag;
    }

    private LanguageOption FindOption(string languageTag)
    {
        foreach (var option in LanguageOptions)
        {
            if (option.LanguageTag == languageTag)
            {
                return option;
            }
        }

        return LanguageOptions[0];
    }
}
