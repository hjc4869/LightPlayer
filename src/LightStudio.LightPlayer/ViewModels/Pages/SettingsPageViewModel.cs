using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public partial class SettingsPageViewModel : PageViewModelBase
{
    // Null means "no section chosen" so the adaptive view can start on the list in
    // narrow mode. The view auto-selects the first section when it is wide.
    [ObservableProperty]
    private SettingsSectionViewModel? selectedSection;

    public SettingsPageViewModel(IAppSettingsStore settingsStore, IThemeVariantService themeVariantService)
        : this(settingsStore, themeVariantService, new AsyncRelayCommand(() => Task.CompletedTask))
    {
    }

    public SettingsPageViewModel(
        IAppSettingsStore settingsStore,
        IThemeVariantService themeVariantService,
        IAsyncRelayCommand scanLibraryCommand,
        ILyricSourceService? lyricSourceService = null,
        Action<int>? sampleRateChanged = null,
        Action<bool>? alwaysResampleChanged = null,
        ISystemSampleRateProvider? systemSampleRateProvider = null,
        Func<Task>? clearPlaybackHistory = null)
    {
        Title = "Settings";

        Library = new LibrarySettingsSectionViewModel(settingsStore, scanLibraryCommand)
        {
            ClearPlaybackHistoryAsync = clearPlaybackHistory,
        };
        Appearance = new AppearanceSettingsSectionViewModel(settingsStore, themeVariantService);
        Playback = new PlaybackSettingsSectionViewModel(
            settingsStore,
            lyricSourceService ?? new JsonLyricSourceService(),
            sampleRateChanged,
            alwaysResampleChanged,
            systemSampleRateProvider);

        Sections =
        [
            Library,
            Appearance,
            Playback,
        ];
    }

    public ObservableCollection<SettingsSectionViewModel> Sections { get; }

    public LibrarySettingsSectionViewModel Library { get; }

    public AppearanceSettingsSectionViewModel Appearance { get; }

    public PlaybackSettingsSectionViewModel Playback { get; }
}