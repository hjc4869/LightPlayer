using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Services;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed class LanguageOption : ViewModelBase
{
    private readonly string title;
    private readonly string? titleKey;

    /// <summary>
    /// <paramref name="titleKey"/> is a resource key (use <c>nameof(Resources.Key)</c>) for the
    /// "System" entry; languages are named in their own language and pass literal text.
    /// </summary>
    public LanguageOption(string languageTag, string title, string? titleKey = null)
    {
        LanguageTag = languageTag;
        this.title = title;
        this.titleKey = titleKey;
    }

    /// <summary>BCP-47 language tag, or empty to follow the operating system.</summary>
    public string LanguageTag { get; }

    public string Title => Localized(titleKey, title);
}

public partial class LanguagePreferenceViewModel : ViewModelBase
{
    private readonly IAppSettingsStore settingsStore;
    private bool isLoaded;

    [ObservableProperty]
    private LanguageOption selectedLanguage = null!;

    public LanguagePreferenceViewModel(IAppSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore;

        LanguageOptions =
        [
            new LanguageOption(string.Empty, string.Empty, nameof(Strings.Resources.Common_System)),
            new LanguageOption("en-US", "English (US)"),
            new LanguageOption("en-GB", "English (UK)"),
            new LanguageOption("de-DE", "Deutsch"),
            new LanguageOption("es-ES", "Español"),
            new LanguageOption("fr-FR", "Français"),
            new LanguageOption("ja-JP", "日本語"),
            new LanguageOption("zh-CN", "中文 (中国)"),
            new LanguageOption("zh-TW", "中文 (台灣)"),
        ];

        selectedLanguage = FindOption(settingsStore.InterfaceLanguage);
        isLoaded = true;
    }

    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (!isLoaded)
        {
            return;
        }

        settingsStore.InterfaceLanguage = value.LanguageTag;

        // Switch the culture straight away; every XAML binding goes through
        // LocalizedStrings, so the UI re-reads its text without a restart.
        Strings.LocalizedStrings.ApplyLanguage(value.LanguageTag);
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
