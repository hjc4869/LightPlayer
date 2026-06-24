using System;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Groups the audio sample rate and extension (third-party lyric source)
/// settings under a single "Playback" entry in the settings navigation list.
/// </summary>
public sealed class PlaybackSettingsSectionViewModel : SettingsSectionViewModel
{
    public PlaybackSettingsSectionViewModel(
        IAppSettingsStore settingsStore,
        ILyricSourceService lyricSourceService,
        Action<int>? sampleRateChanged = null,
        Action<bool>? alwaysResampleChanged = null,
        ISystemSampleRateProvider? systemSampleRateProvider = null)
        : base("Playback", "Sample rate and extensions.")
    {
        SampleRate = new SampleRateSettingsSectionViewModel(
            settingsStore,
            sampleRateChanged,
            alwaysResampleChanged,
            systemSampleRateProvider);
        Extensions = new ExtensionSettingsSectionViewModel(lyricSourceService);
    }

    public SampleRateSettingsSectionViewModel SampleRate { get; }

    public ExtensionSettingsSectionViewModel Extensions { get; }
}
