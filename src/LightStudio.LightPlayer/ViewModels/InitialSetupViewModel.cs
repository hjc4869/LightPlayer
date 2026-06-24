using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.ViewModels;

/// <summary>
/// First-run setup. Reuses the existing settings sections so the user can pick
/// library folders, language, sample rate, and theme before the main window
/// opens, then provisions third-party lyric sources and continues.
/// </summary>
public partial class InitialSetupViewModel : ViewModelBase
{
    private readonly IAppSettingsStore settingsStore;
    private readonly ILyricSourceService? lyricSourceService;

    [ObservableProperty]
    private bool enableThirdPartyLyrics = true;

    public InitialSetupViewModel(
        IAppSettingsStore settingsStore,
        IThemeVariantService themeVariantService,
        ILyricSourceService? lyricSourceService)
    {
        this.settingsStore = settingsStore;
        this.lyricSourceService = lyricSourceService;

        var noScan = new AsyncRelayCommand(() => Task.CompletedTask);
        Library = new LibrarySettingsSectionViewModel(settingsStore, noScan) { ShowScanButton = false };
        Library.EnsureDefaultMusicFolder();
        Appearance = new ThemeSettingsSectionViewModel(settingsStore, themeVariantService);
        Language = new LanguageSettingsSectionViewModel(settingsStore);
        SampleRate = new SampleRateSettingsSectionViewModel(
            settingsStore,
            systemSampleRateProvider: new OpenAlSystemSampleRateProvider());

        ContinueCommand = new RelayCommand(Continue);
    }

    public LibrarySettingsSectionViewModel Library { get; }

    public ThemeSettingsSectionViewModel Appearance { get; }

    public LanguageSettingsSectionViewModel Language { get; }

    public SampleRateSettingsSectionViewModel SampleRate { get; }

    public IRelayCommand ContinueCommand { get; }

    /// <summary>Raised when the user finishes setup; the host opens the main window.</summary>
    public event Action? SetupCompleted;

    private void Continue()
    {
        settingsStore.EnableThirdPartyLyrics = EnableThirdPartyLyrics;
        if (EnableThirdPartyLyrics)
        {
            lyricSourceService?.ProvisionBundledSources();
        }

        settingsStore.InitialSetupCompleted = true;
        SetupCompleted?.Invoke();
    }
}
