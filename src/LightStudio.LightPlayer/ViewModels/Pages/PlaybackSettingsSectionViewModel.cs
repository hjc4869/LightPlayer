using LightMediaRenderer.Avalonia.Audio;
using System;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using LightMediaRenderer.Avalonia.Shaders;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

/// <summary>
/// Owns the playback settings and presents the audio playback page.
/// </summary>
public sealed class PlaybackSettingsSectionViewModel : SettingsSectionViewModel, IDisposable
{
    public PlaybackSettingsSectionViewModel(
        IAppSettingsStore settingsStore,
        Action<int>? sampleRateChanged = null,
        Action<bool>? alwaysResampleChanged = null,
        ISystemSampleRateProvider? systemSampleRateProvider = null,
        IAudioOutputSettings? audioOutputSettings = null,
        VideoShaderPackageCatalog? shaderPackages = null,
        IDialogService? dialogService = null)
        : base(nameof(Strings.Resources.SettingsSection_PlaybackTitle), string.Empty)
    {
        var outputSettings = audioOutputSettings ?? DefaultAudioOutputSettings.Instance;
        AudioSeparation = PlatformServices.AudioSeparation is { } separation ? new AudioSeparationSettingsViewModel(separation) : null;
        Video = new VideoSettingsSectionViewModel(settingsStore, shaderPackages, dialogService);
        SampleRate = new SampleRateSettingsSectionViewModel(
            settingsStore,
            sampleRateChanged,
            alwaysResampleChanged,
            systemSampleRateProvider,
            outputSettings)
        {
            // The sample-rate controls sit beneath the Audio Output header, so hide their own.
            ShowHeader = !PlatformServices.SupportsAudioOutputSettings,
        };
        if (PlatformServices.SupportsAudioOutputSettings)
        {
            AudioSettings = new AudioSettingsSectionViewModel(
                settingsStore,
                outputSettings);
        }

        if (PlatformServices.RemoteLibraryManager is not null)
        {
            RemoteTranscode = new RemoteTranscodeSettingsSectionViewModel(
                settingsStore,
                PlatformServices.LibrarySources);
        }
    }

    public VideoSettingsSectionViewModel Video { get; }

    public SampleRateSettingsSectionViewModel SampleRate { get; }

    public AudioSettingsSectionViewModel? AudioSettings { get; }

    public AudioSeparationSettingsViewModel? AudioSeparation { get; }

    public bool HasAudioSeparation => AudioSeparation is not null;

    public RemoteTranscodeSettingsSectionViewModel? RemoteTranscode { get; }

    public void Dispose()
    {
        Video.DisposeShaderPackages();
        SampleRate.Dispose();
        AudioSettings?.Dispose();
        AudioSeparation?.Dispose();
    }
}
