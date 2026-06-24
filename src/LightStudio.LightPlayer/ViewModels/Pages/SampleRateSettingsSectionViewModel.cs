using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed class SampleRateOption
{
    public SampleRateOption(int sampleRate, string title)
    {
        SampleRate = sampleRate;
        Title = title;
    }

    /// <summary>Output sample rate in Hz, or 0 to follow the source.</summary>
    public int SampleRate { get; }

    public string Title { get; }
}

public partial class SampleRateSettingsSectionViewModel : SettingsSectionViewModel
{
    private readonly IAppSettingsStore settingsStore;
    private readonly Action<int>? sampleRateChanged;
    private readonly Action<bool>? alwaysResampleChanged;
    private readonly ISystemSampleRateProvider systemSampleRateProvider;
    private readonly int initialSampleRate;
    private readonly bool initialAlwaysResample;
    private bool isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextTrackPromptVisible))]
    private SampleRateOption selectedOption = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextTrackPromptVisible))]
    private bool alwaysResample;

    [ObservableProperty]
    private string systemSampleRate = "Detecting…";

    public SampleRateSettingsSectionViewModel(
        IAppSettingsStore settingsStore,
        Action<int>? sampleRateChanged = null,
        Action<bool>? alwaysResampleChanged = null,
        ISystemSampleRateProvider? systemSampleRateProvider = null)
        : base("Sample Rate", "Choose the audio output sample rate.")
    {
        this.settingsStore = settingsStore;
        this.sampleRateChanged = sampleRateChanged;
        this.alwaysResampleChanged = alwaysResampleChanged;
        this.systemSampleRateProvider = systemSampleRateProvider ?? new UnknownSystemSampleRateProvider();

        Options =
        [
            new SampleRateOption(0, "System"),
            new SampleRateOption(44100, "44100 Hz"),
            new SampleRateOption(48000, "48000 Hz"),
            new SampleRateOption(88200, "88200 Hz"),
            new SampleRateOption(96000, "96000 Hz"),
            new SampleRateOption(192000, "192000 Hz"),
        ];

        initialSampleRate = settingsStore.PreferredSampleRate;
        initialAlwaysResample = settingsStore.AlwaysResample;
        alwaysResample = initialAlwaysResample;
        selectedOption = FindOption(initialSampleRate);
        RefreshSystemSampleRateCommand = new AsyncRelayCommand(RefreshSystemSampleRateAsync);
        isLoaded = true;
        _ = RefreshSystemSampleRateAsync();
    }

    public IReadOnlyList<SampleRateOption> Options { get; }

    public IAsyncRelayCommand RefreshSystemSampleRateCommand { get; }

    /// <summary>True once the user changes a value, since the change applies to the next track.</summary>
    public bool NextTrackPromptVisible =>
        SelectedOption.SampleRate != initialSampleRate || AlwaysResample != initialAlwaysResample;

    partial void OnSelectedOptionChanged(SampleRateOption value)
    {
        if (!isLoaded)
        {
            return;
        }

        settingsStore.PreferredSampleRate = value.SampleRate;
        sampleRateChanged?.Invoke(value.SampleRate);
    }

    partial void OnAlwaysResampleChanged(bool value)
    {
        if (!isLoaded)
        {
            return;
        }

        settingsStore.AlwaysResample = value;
        alwaysResampleChanged?.Invoke(value);
    }

    private async Task RefreshSystemSampleRateAsync()
    {
        var rate = await Task.Run(systemSampleRateProvider.GetSystemSampleRate);
        SystemSampleRate = rate > 0 ? $"{rate} Hz" : "Unknown";
    }

    private SampleRateOption FindOption(int sampleRate)
    {
        foreach (var option in Options)
        {
            if (option.SampleRate == sampleRate)
            {
                return option;
            }
        }

        return Options[0];
    }
}
