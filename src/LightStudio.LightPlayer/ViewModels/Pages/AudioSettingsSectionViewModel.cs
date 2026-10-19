using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public partial class AudioSettingsSectionViewModel : SettingsSectionViewModel, IDisposable
{
    private const string ExclusiveOptionKey = "exclusive";

    private readonly IAppSettingsStore settingsStore;
    private readonly IAudioOutputSettings audioSettings;
    private bool isLoaded;
    private bool disposed;

    [ObservableProperty]
    private AudioOutputDeviceInfo? selectedDevice;

    [ObservableProperty]
    private bool exclusive;

    public AudioSettingsSectionViewModel(
        IAppSettingsStore settingsStore,
        IAudioOutputSettings audioSettings)
        : base(nameof(Strings.Resources.AudioSettings_Title), nameof(Strings.Resources.AudioSettings_Subtitle))
    {
        this.settingsStore = settingsStore;
        this.audioSettings = audioSettings;
        Devices = [];

        var options = audioSettings.GetOptions();
        SupportsExclusiveMode = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsAndroid();
        exclusive = SupportsExclusiveMode &&
            options.TryGetValue(ExclusiveOptionKey, out var exclusiveValue) &&
            bool.TryParse(exclusiveValue, out var enabled) &&
            enabled;
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync);
        audioSettings.Changed += OnAudioSettingsChanged;
        isLoaded = true;
        _ = RefreshDevicesAsync();
    }

    public ObservableCollection<AudioOutputDeviceInfo> Devices { get; }

    public bool SupportsExclusiveMode { get; }

    public IAsyncRelayCommand RefreshDevicesCommand { get; }

    /// <summary>
    /// The device list carries localized names for the default and unavailable entries, so it is
    /// rebuilt (not just re-notified) when the interface language changes.
    /// </summary>
    public override void OnLocalizedTextChanged()
    {
        base.OnLocalizedTextChanged();
        if (!disposed)
        {
            _ = RefreshDevicesAsync();
        }
    }

    partial void OnSelectedDeviceChanged(AudioOutputDeviceInfo? value)
    {
        if (!isLoaded || value is null)
        {
            return;
        }

        settingsStore.AudioOutputDeviceId = value.Id;
        audioSettings.SetDevice(value.Id);
    }

    partial void OnExclusiveChanged(bool value)
    {
        if (!isLoaded || !SupportsExclusiveMode)
        {
            return;
        }

        var serialized = value ? bool.TrueString.ToLowerInvariant() : bool.FalseString.ToLowerInvariant();
        settingsStore.SetAudioOutputOption(ExclusiveOptionKey, serialized);
        audioSettings.SetOption(ExclusiveOptionKey, serialized);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        audioSettings.Changed -= OnAudioSettingsChanged;
    }

    private async Task RefreshDevicesAsync()
    {
        IReadOnlyList<AudioOutputDeviceInfo> devices;
        try
        {
            devices = await Task.Run(audioSettings.GetDevices);
        }
        catch
        {
            devices = [];
        }

        if (disposed)
        {
            return;
        }

        var localized = devices
            .Select(device => string.Equals(device.Id, IAudioOutputSettings.DefaultDeviceId, StringComparison.Ordinal)
                ? device with { Name = Strings.Resources.AudioSettings_DefaultDevice }
                : device)
            .ToList();
        var selectedId = audioSettings.DeviceId;
        if (localized.All(device => !string.Equals(device.Id, selectedId, StringComparison.Ordinal)))
        {
            localized.Add(new AudioOutputDeviceInfo(
                selectedId,
                Strings.Resources.AudioSettings_UnavailableDevice));
        }

        isLoaded = false;
        Devices.Clear();
        foreach (var device in localized)
        {
            Devices.Add(device);
        }
        SelectedDevice = Devices.FirstOrDefault(device =>
            string.Equals(device.Id, selectedId, StringComparison.Ordinal))
            ?? Devices.FirstOrDefault();
        isLoaded = true;
    }

    private void OnAudioSettingsChanged(object? sender, AudioOutputSettingsChangedEventArgs e)
    {
        if (!e.DevicesChanged || disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!disposed && RefreshDevicesCommand.CanExecute(null))
            {
                RefreshDevicesCommand.Execute(null);
            }
        });
    }
}
