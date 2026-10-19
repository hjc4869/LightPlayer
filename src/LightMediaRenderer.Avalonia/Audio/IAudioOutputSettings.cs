using System;
using System.Collections.Generic;

namespace LightMediaRenderer.Avalonia.Audio;

/// <summary>Describes an audio endpoint exposed by a playback backend.</summary>
/// <param name="Id">Stable backend-defined identifier used for persistence.</param>
/// <param name="Name">Display name presented to the user.</param>
public sealed record AudioOutputDeviceInfo(string Id, string Name);

/// <summary>Describes what changed in an audio backend's live configuration.</summary>
public sealed class AudioOutputSettingsChangedEventArgs(
    bool configurationChanged,
    bool devicesChanged) : EventArgs
{
    /// <summary>Whether active output devices must reopen their endpoint.</summary>
    public bool ConfigurationChanged { get; } = configurationChanged;

    /// <summary>Whether callers should refresh the endpoint list.</summary>
    public bool DevicesChanged { get; } = devicesChanged;
}

/// <summary>
/// Cross-platform audio-backend configuration. Platform capabilities are represented by
/// backend-defined string options rather than platform-specific methods.
/// </summary>
public interface IAudioOutputSettings
{
    /// <summary>A virtual endpoint that follows the operating system's current default device.</summary>
    public const string DefaultDeviceId = "default";

    /// <summary>The currently selected endpoint identifier.</summary>
    string DeviceId { get; }

    /// <summary>Raised when device availability or live backend configuration changes.</summary>
    event EventHandler<AudioOutputSettingsChangedEventArgs>? Changed;

    /// <summary>Returns usable endpoints, including <see cref="DefaultDeviceId"/>.</summary>
    IReadOnlyList<AudioOutputDeviceInfo> GetDevices();

    /// <summary>Returns the backend's supported options and their current string values.</summary>
    IReadOnlyDictionary<string, string> GetOptions();

    /// <summary>Selects an endpoint. The change applies to active playback immediately.</summary>
    void SetDevice(string deviceId);

    /// <summary>Sets a backend-defined option. Unsupported keys are ignored.</summary>
    void SetOption(string key, string value);
}

/// <summary>Default configuration used by backends that expose only the system endpoint.</summary>
public sealed class DefaultAudioOutputSettings : IAudioOutputSettings
{
    private static readonly IReadOnlyList<AudioOutputDeviceInfo> Devices =
    [
        new(IAudioOutputSettings.DefaultDeviceId, "Default device"),
    ];

    private DefaultAudioOutputSettings()
    {
    }

    public static DefaultAudioOutputSettings Instance { get; } = new();

    public string DeviceId => IAudioOutputSettings.DefaultDeviceId;

    public event EventHandler<AudioOutputSettingsChangedEventArgs>? Changed
    {
        add { }
        remove { }
    }

    public IReadOnlyList<AudioOutputDeviceInfo> GetDevices() => Devices;

    public IReadOnlyDictionary<string, string> GetOptions() =>
        System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    public void SetDevice(string deviceId)
    {
    }

    public void SetOption(string key, string value)
    {
    }
}
