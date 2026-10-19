using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public sealed class SampleRateOption : ViewModelBase
{
    private readonly string title;
    private readonly string? titleKey;

    /// <summary>
    /// <paramref name="titleKey"/> is a resource key (use <c>nameof(Resources.Key)</c>) for labels
    /// that need translating; rate labels such as "44100 Hz" pass their literal text instead.
    /// </summary>
    public SampleRateOption(int sampleRate, string title, string? titleKey = null)
    {
        SampleRate = sampleRate;
        this.title = title;
        this.titleKey = titleKey;
    }

    /// <summary>Output sample rate in Hz, or 0 to follow the source.</summary>
    public int SampleRate { get; }

    public string Title => Localized(titleKey, title);
}

public partial class SampleRateSettingsSectionViewModel : SettingsSectionViewModel, IDisposable
{
    private readonly IAppSettingsStore settingsStore;
    private readonly Action<int>? sampleRateChanged;
    private readonly Action<bool>? alwaysResampleChanged;
    private readonly ISystemSampleRateProvider systemSampleRateProvider;
    private readonly IAudioOutputSettings? audioOutputSettings;
    private readonly int initialSampleRate;
    private readonly bool initialAlwaysResample;
    private int refreshVersion;
    private bool isLoaded;
    private bool disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextTrackPromptVisible))]
    private SampleRateOption selectedOption = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextTrackPromptVisible))]
    private bool alwaysResample;

    [ObservableProperty]
    private string systemSampleRate = Strings.Resources.SampleRate_Detecting;

    public SampleRateSettingsSectionViewModel(
        IAppSettingsStore settingsStore,
        Action<int>? sampleRateChanged = null,
        Action<bool>? alwaysResampleChanged = null,
        ISystemSampleRateProvider? systemSampleRateProvider = null,
        IAudioOutputSettings? audioOutputSettings = null)
        : base(nameof(Strings.Resources.SettingsSection_SampleRateListTitle), string.Empty)
    {
        this.settingsStore = settingsStore;
        this.sampleRateChanged = sampleRateChanged;
        this.alwaysResampleChanged = alwaysResampleChanged;
        this.systemSampleRateProvider = systemSampleRateProvider ?? new UnknownSystemSampleRateProvider();
        this.audioOutputSettings = audioOutputSettings;

        Options =
        [
            new SampleRateOption(0, string.Empty, nameof(Strings.Resources.Common_System)),
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
        if (audioOutputSettings is not null)
        {
            audioOutputSettings.Changed += OnAudioOutputSettingsChanged;
        }
        isLoaded = true;
        _ = RefreshSystemSampleRateAsync();
    }

    public IReadOnlyList<SampleRateOption> Options { get; }

    public IAsyncRelayCommand RefreshSystemSampleRateCommand { get; }

    /// <summary>
    /// Whether to render this section's own title/subtitle header. False when the controls are
    /// merged beneath another section's header (the Audio Output section on the Playback page).
    /// </summary>
    public bool ShowHeader { get; init; } = true;

    /// <summary>True once the user changes a value, since the change applies to the next track.</summary>
    public bool NextTrackPromptVisible =>
        SelectedOption.SampleRate != initialSampleRate || AlwaysResample != initialAlwaysResample;

    /// <summary>
    /// <see cref="SystemSampleRate"/> holds a resolved string (a rate, or a localized
    /// "unknown"/"detecting" placeholder), so it is recomputed when the language changes.
    /// </summary>
    public override void OnLocalizedTextChanged()
    {
        base.OnLocalizedTextChanged();
        if (!disposed)
        {
            _ = RefreshSystemSampleRateAsync();
        }
    }

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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Interlocked.Increment(ref refreshVersion);
        if (audioOutputSettings is not null)
        {
            audioOutputSettings.Changed -= OnAudioOutputSettingsChanged;
        }
    }

    private async Task RefreshSystemSampleRateAsync()
    {
        var version = Interlocked.Increment(ref refreshVersion);
        var rate = await Task.Run(systemSampleRateProvider.GetSystemSampleRate);
        if (disposed || version != Volatile.Read(ref refreshVersion))
        {
            return;
        }

        SystemSampleRate = rate > 0 ? $"{rate} Hz" : Strings.Resources.Common_Unknown;
    }

    private void OnAudioOutputSettingsChanged(object? sender, AudioOutputSettingsChangedEventArgs e)
    {
        if ((!e.ConfigurationChanged && !e.DevicesChanged) || disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!disposed)
            {
                _ = RefreshSystemSampleRateAsync();
            }
        });
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
