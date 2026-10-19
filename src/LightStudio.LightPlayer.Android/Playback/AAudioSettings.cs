using LightMediaRenderer.Avalonia.Audio;
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Android.Content;
using Android.Media;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.Android.Playback;

internal sealed class AAudioSettings : AudioDeviceCallback, IAudioOutputSettings, ISystemSampleRateProvider
{
    internal const string ExclusiveOptionKey = "exclusive";
    private const string DeviceIdPrefix = "aaudio:";
    private static readonly AudioOutputDeviceInfo DefaultDevice =
        new(IAudioOutputSettings.DefaultDeviceId, "Default device");

    private readonly object gate = new();
    private readonly AudioManager audioManager;
    private string deviceId = IAudioOutputSettings.DefaultDeviceId;
    private bool exclusive;
    private bool disposed;

    internal AAudioSettings(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        audioManager = (AudioManager?)context.ApplicationContext?.GetSystemService(Context.AudioService)
            ?? throw new InvalidOperationException("Android AudioManager is unavailable.");
        audioManager.RegisterAudioDeviceCallback(this, null);
    }

    public string DeviceId
    {
        get
        {
            lock (gate)
            {
                return deviceId;
            }
        }
    }

    internal bool Exclusive
    {
        get
        {
            lock (gate)
            {
                return exclusive;
            }
        }
    }

    internal int SelectedNativeDeviceId
    {
        get
        {
            var selectedDeviceId = DeviceId;
            if (string.Equals(selectedDeviceId, IAudioOutputSettings.DefaultDeviceId, StringComparison.Ordinal))
            {
                return AAudioNative.Unspecified;
            }

            return GetOutputDevices()
                .FirstOrDefault(device => string.Equals(
                    FormatDeviceId(device),
                    selectedDeviceId,
                    StringComparison.Ordinal))
                ?.Id ?? AAudioNative.Unspecified;
        }
    }

    public event EventHandler<AudioOutputSettingsChangedEventArgs>? Changed;

    public IReadOnlyList<AudioOutputDeviceInfo> GetDevices()
    {
        try
        {
            return GetOutputDevices()
                .Select(static device => new AudioOutputDeviceInfo(
                    FormatDeviceId(device),
                    GetDeviceName(device)))
                .DistinctBy(static device => device.Id, StringComparer.Ordinal)
                .OrderBy(static device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .Prepend(DefaultDevice)
                .ToArray();
        }
        catch
        {
            return [DefaultDevice];
        }
    }

    public IReadOnlyDictionary<string, string> GetOptions() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExclusiveOptionKey] = Exclusive ? bool.TrueString.ToLowerInvariant() : bool.FalseString.ToLowerInvariant(),
        };

    public void SetDevice(string selectedDeviceId)
    {
        var normalized = string.IsNullOrWhiteSpace(selectedDeviceId)
            ? IAudioOutputSettings.DefaultDeviceId
            : selectedDeviceId;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (string.Equals(deviceId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            deviceId = normalized;
        }

        RaiseChanged(configurationChanged: true, devicesChanged: false);
    }

    public void SetOption(string key, string value)
    {
        if (!string.Equals(key, ExclusiveOptionKey, StringComparison.OrdinalIgnoreCase) ||
            !bool.TryParse(value, out var enabled))
        {
            return;
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (exclusive == enabled)
            {
                return;
            }

            exclusive = enabled;
        }

        RaiseChanged(configurationChanged: true, devicesChanged: false);
    }

    public int GetSystemSampleRate()
    {
        try
        {
            return AAudioNative.ProbeSampleRate(SelectedNativeDeviceId);
        }
        catch
        {
            return 0;
        }
    }

    public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices) =>
        HandleDevicesChanged(addedDevices);

    public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices) =>
        HandleDevicesChanged(removedDevices);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (gate)
            {
                if (disposed)
                {
                    base.Dispose(disposing);
                    return;
                }

                disposed = true;
            }

            audioManager.UnregisterAudioDeviceCallback(this);
        }

        base.Dispose(disposing);
    }

    private void HandleDevicesChanged(AudioDeviceInfo[]? changedDevices)
    {
        var selectedDeviceId = DeviceId;
        var followsDefault = string.Equals(
            selectedDeviceId,
            IAudioOutputSettings.DefaultDeviceId,
            StringComparison.Ordinal);
        var selectedDeviceChanged = changedDevices?.Any(device => string.Equals(
            FormatDeviceId(device),
            selectedDeviceId,
            StringComparison.Ordinal)) == true;
        RaiseChanged(
            configurationChanged: followsDefault || selectedDeviceChanged,
            devicesChanged: true);
    }

    private void RaiseChanged(bool configurationChanged, bool devicesChanged) =>
        Changed?.Invoke(this, new AudioOutputSettingsChangedEventArgs(configurationChanged, devicesChanged));

    private AudioDeviceInfo[] GetOutputDevices() =>
        audioManager.GetDevices(GetDevicesTargets.Outputs) ?? [];

    private static string FormatDeviceId(AudioDeviceInfo device)
    {
        var address = OperatingSystem.IsAndroidVersionAtLeast(28) ? device.Address : string.Empty;
        var identity = $"{(int)device.Type}\n{address}\n{GetDeviceIdentityName(device)}";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return DeviceIdPrefix + Convert.ToHexString(hash);
    }

    private static string GetDeviceName(AudioDeviceInfo device)
    {
        var productName = device.ProductNameFormatted?.ToString()?.Trim();
        var typeName = GetDeviceTypeName(device.Type);
        return string.IsNullOrWhiteSpace(productName)
            ? typeName
            : $"{productName} ({typeName})";
    }

    private static string GetDeviceIdentityName(AudioDeviceInfo device)
    {
        var productName = device.ProductNameFormatted?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(productName) ? device.Type.ToString() : productName;
    }

    private static string GetDeviceTypeName(AudioDeviceType type) => type switch
    {
        AudioDeviceType.BuiltinEarpiece => "Built-in earpiece",
        AudioDeviceType.BuiltinSpeaker => "Built-in speaker",
        AudioDeviceType.WiredHeadset => "Wired headset",
        AudioDeviceType.WiredHeadphones => "Wired headphones",
        AudioDeviceType.LineAnalog => "Analog line output",
        AudioDeviceType.LineDigital => "Digital line output",
        AudioDeviceType.BluetoothSco => "Bluetooth call audio",
        AudioDeviceType.BluetoothA2dp => "Bluetooth media audio",
        AudioDeviceType.Hdmi => "HDMI",
        AudioDeviceType.HdmiArc => "HDMI ARC",
        AudioDeviceType.UsbDevice => "USB audio device",
        AudioDeviceType.UsbAccessory => "USB audio accessory",
        AudioDeviceType.Dock => "Dock audio",
        AudioDeviceType.Fm => "FM audio",
        AudioDeviceType.BuiltinMic => "Built-in microphone",
        AudioDeviceType.FmTuner => "FM tuner",
        AudioDeviceType.TvTuner => "TV tuner",
        AudioDeviceType.Telephony => "Telephony audio",
        AudioDeviceType.AuxLine => "Auxiliary line output",
        AudioDeviceType.Ip => "Network audio",
        AudioDeviceType.Bus => "Audio bus",
        AudioDeviceType.UsbHeadset => "USB headset",
        AudioDeviceType.HearingAid => "Hearing aid",
        AudioDeviceType.BuiltinSpeakerSafe => "Safe built-in speaker",
        AudioDeviceType.RemoteSubmix => "Remote submix",
        AudioDeviceType.BleHeadset => "Bluetooth LE headset",
        AudioDeviceType.BleSpeaker => "Bluetooth LE speaker",
        AudioDeviceType.HdmiEarc => "HDMI eARC",
        AudioDeviceType.BleBroadcast => "Bluetooth LE broadcast",
        AudioDeviceType.DockAnalog => "Analog dock audio",
        AudioDeviceType.Unknown => "Unknown device type",
        _ when (int)type == 32 => "Multichannel speaker group",
        _ => $"Audio device type {(int)type}",
    };
}