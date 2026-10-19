using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;
using NAudio.CoreAudioApi;

namespace LightStudio.LightPlayer.Desktop.Windows;

/// <summary>Registers the Windows Core Audio event-mode playback backend.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsAudioPlaybackBackend
{
    private static WindowsAudioSettings? settings;

    public static void Register()
    {
        settings ??= new WindowsAudioSettings();
        AudioOutputDeviceFactory.Register(
            settings.CreateOutputDevice,
            settings,
            settings,
            settings.CreateOutputDeviceAsync);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsAudioSettings :
    IAudioOutputSettings,
    ISystemSampleRateProvider,
    IDisposable
{
    internal const string ExclusiveOptionKey = "exclusive";

    private readonly object gate = new();
    private readonly MMDeviceEnumerator notificationEnumerator = new();
    private readonly MMDeviceNotificationClient notificationClient;
    private string deviceId = IAudioOutputSettings.DefaultDeviceId;
    private bool exclusive;
    private bool disposed;

    public WindowsAudioSettings()
    {
        notificationClient = notificationEnumerator.CreateNotificationClient(useSynchronizationContext: false);
        notificationClient.DeviceStateChanged += OnDeviceStateChanged;
        notificationClient.DeviceAdded += OnDeviceAdded;
        notificationClient.DeviceRemoved += OnDeviceRemoved;
        notificationClient.DefaultDeviceChanged += OnDefaultDeviceChanged;
        notificationClient.PropertyValueChanged += OnPropertyValueChanged;
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

    public event EventHandler<AudioOutputSettingsChangedEventArgs>? Changed;

    public IReadOnlyList<AudioOutputDeviceInfo> GetDevices()
    {
        var devices = new List<AudioOutputDeviceInfo>
        {
            new(IAudioOutputSettings.DefaultDeviceId, "Default device"),
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var endpoint in endpoints)
            {
                using (endpoint)
                {
                    devices.Add(new AudioOutputDeviceInfo(endpoint.ID, endpoint.FriendlyName));
                }
            }
        }
        catch (COMException)
        {
            // A device can disappear while Core Audio is enumerating it. Keep the virtual default
            // endpoint available and let a later notification or manual refresh repopulate the list.
        }

        return devices
            .Skip(1)
            .OrderBy(static device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .Prepend(devices[0])
            .ToArray();
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
            using var endpoint = OpenSelectedDevice();
            try
            {
                if (endpoint.Properties[PropertyKeys.PKEY_AudioEngine_DeviceFormat].Value is byte[] deviceFormat &&
                    deviceFormat.Length >= 8)
                {
                    var configuredRate = BinaryPrimitives.ReadInt32LittleEndian(deviceFormat.AsSpan(4, 4));
                    if (configuredRate > 0)
                    {
                        return configuredRate;
                    }
                }
            }
            catch
            {
                // Some drivers do not expose the persisted engine-format property. Fall back to
                // IAudioClient.GetMixFormat, which is required for active render endpoints.
            }

            using var audioClient = endpoint.CreateAudioClient();
            return Math.Max(0, audioClient.MixFormat.SampleRate);
        }
        catch
        {
            return 0;
        }
    }

    internal IAudioOutputDevice CreateOutputDevice(AudioFormat format, TimeSpan bufferTarget) =>
        new WindowsAudioOutputDevice(format, bufferTarget, this);

    internal ValueTask<IAudioOutputDevice> CreateOutputDeviceAsync(
        AudioFormat format, TimeSpan bufferTarget, CancellationToken cancellationToken) =>
        WindowsAudioOutputDevice.CreateAsync(format, bufferTarget, this, cancellationToken);

    internal MMDevice OpenSelectedDevice()
    {
        var selectedDeviceId = DeviceId;
        using var enumerator = new MMDeviceEnumerator();
        return string.Equals(selectedDeviceId, IAudioOutputSettings.DefaultDeviceId, StringComparison.Ordinal)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)
            : enumerator.GetDevice(selectedDeviceId);
    }

    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        var affectsSelection = IsSelected(e.DeviceId);
        RaiseChanged(configurationChanged: affectsSelection, devicesChanged: true);
    }

    private void OnDeviceAdded(object? sender, DeviceNotificationEventArgs e)
    {
        var affectsSelection = IsSelected(e.DeviceId);
        RaiseChanged(configurationChanged: affectsSelection, devicesChanged: true);
    }

    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs e)
    {
        var affectsSelection = IsSelected(e.DeviceId);
        RaiseChanged(configurationChanged: affectsSelection, devicesChanged: true);
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow is DataFlow.Render or DataFlow.All &&
            e.Role == Role.Console &&
            IsDefaultSelected())
        {
            RaiseChanged(configurationChanged: true, devicesChanged: true);
        }
    }

    private void OnPropertyValueChanged(object? sender, DevicePropertyChangedEventArgs e) =>
        RaiseChanged(configurationChanged: false, devicesChanged: true);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        notificationClient.Dispose();
        notificationEnumerator.Dispose();
    }

    private bool IsDefaultSelected() =>
        string.Equals(DeviceId, IAudioOutputSettings.DefaultDeviceId, StringComparison.Ordinal);

    private bool IsSelected(string changedDeviceId) =>
        string.Equals(DeviceId, changedDeviceId, StringComparison.Ordinal);

    private void RaiseChanged(bool configurationChanged, bool devicesChanged) =>
        Changed?.Invoke(this, new AudioOutputSettingsChangedEventArgs(configurationChanged, devicesChanged));
}