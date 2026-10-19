using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>
/// CoreAudio backend configuration. Enumerates output devices, resolves the mixer sample rate, and
/// observes device hot-plug / default-device changes through the CoreAudio HAL. Device identifiers
/// are the stable device UID strings prefixed with <c>coreaudio:</c> (the numeric
/// <c>AudioDeviceID</c> is not stable across reboots or re-plug); the virtual
/// <see cref="IAudioOutputSettings.DefaultDeviceId"/> follows the system's current default output
/// device. The "exclusive" option maps to CoreAudio hog mode, the closest equivalent to WASAPI
/// exclusive-mode playback.
/// </summary>
internal sealed unsafe class CoreAudioSettings : IAudioOutputSettings, ISystemSampleRateProvider, IDisposable
{
    internal const string ExclusiveOptionKey = "exclusive";
    private const string DeviceIdPrefix = "coreaudio:";
    private static readonly IReadOnlyList<AudioOutputDeviceInfo> DefaultDevices =
    [
        new(IAudioOutputSettings.DefaultDeviceId, "Default device"),
    ];

    private readonly object gate = new();
    private readonly IntPtr listenerProc;
    private GCHandle selfHandle;
    private string deviceId = IAudioOutputSettings.DefaultDeviceId;
    private bool exclusive;
    private bool listenersRegistered;
    private bool disposed;

    public CoreAudioSettings()
    {
        listenerProc = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, IntPtr, IntPtr, int>)&OnPropertyChanged;
        selfHandle = GCHandle.Alloc(this);
        TrySetInternalNotificationThread();
        RegisterListeners();
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
        try
        {
            var devices = new List<AudioOutputDeviceInfo>();
            foreach (var device in CoreAudioNative.GetUInt32Array(
                CoreAudioNative.SystemObject, CoreAudioNative.HardwareDevices, CoreAudioNative.ScopeGlobal))
            {
                if (!CoreAudioNative.DeviceHasOutputStreams(device))
                {
                    continue;
                }

                var uid = CoreAudioNative.GetStringProperty(
                    device, CoreAudioNative.DeviceUID, CoreAudioNative.ScopeGlobal);
                if (string.IsNullOrEmpty(uid))
                {
                    continue;
                }

                var name = CoreAudioNative.GetStringProperty(
                    device, CoreAudioNative.ObjectName, CoreAudioNative.ScopeGlobal);
                devices.Add(new AudioOutputDeviceInfo(
                    DeviceIdPrefix + uid,
                    string.IsNullOrWhiteSpace(name) ? uid : name));
            }

            return devices
                .OrderBy(static device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .Prepend(DefaultDevices[0])
                .ToArray();
        }
        catch
        {
            return DefaultDevices;
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
            var device = ResolveDeviceId();
            if (device == 0)
            {
                return 0;
            }

            return CoreAudioNative.TryGetFloat64(
                device, CoreAudioNative.DeviceNominalSampleRate, CoreAudioNative.ScopeGlobal, out var rate) && rate > 0
                ? (int)Math.Round(rate)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Resolves the selected endpoint to a live <c>AudioDeviceID</c>, or 0 when unavailable.</summary>
    internal uint ResolveDeviceId()
    {
        var selectedUid = SelectedDeviceUid;
        if (selectedUid is null)
        {
            return CoreAudioNative.TryGetUInt32(
                CoreAudioNative.SystemObject,
                CoreAudioNative.HardwareDefaultOutputDevice,
                CoreAudioNative.ScopeGlobal,
                out var defaultDevice)
                ? defaultDevice
                : 0;
        }

        foreach (var device in CoreAudioNative.GetUInt32Array(
            CoreAudioNative.SystemObject, CoreAudioNative.HardwareDevices, CoreAudioNative.ScopeGlobal))
        {
            if (!CoreAudioNative.DeviceHasOutputStreams(device))
            {
                continue;
            }

            var uid = CoreAudioNative.GetStringProperty(
                device, CoreAudioNative.DeviceUID, CoreAudioNative.ScopeGlobal);
            if (string.Equals(uid, selectedUid, StringComparison.Ordinal))
            {
                return device;
            }
        }

        return 0;
    }

    /// <summary>The selected device UID, or <see langword="null"/> to follow the system default.</summary>
    internal string? SelectedDeviceUid
    {
        get
        {
            var id = DeviceId;
            if (string.Equals(id, IAudioOutputSettings.DefaultDeviceId, StringComparison.Ordinal))
            {
                return null;
            }

            return id.StartsWith(DeviceIdPrefix, StringComparison.Ordinal)
                ? id[DeviceIdPrefix.Length..]
                : id;
        }
    }

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

        UnregisterListeners();
        if (selfHandle.IsAllocated)
        {
            selfHandle.Free();
        }
    }

    private void TrySetInternalNotificationThread()
    {
        // Ask the HAL to service property notifications on its own internal thread. Without this the
        // HAL would deliver notifications on a CFRunLoop that the app never runs, so device changes
        // would go unnoticed. A null CFRunLoopRef selects the internal-thread behavior.
        var runLoop = IntPtr.Zero;
        var address = CoreAudioNative.Address(CoreAudioNative.HardwareRunLoop, CoreAudioNative.ScopeGlobal);
        try
        {
            _ = CoreAudioNative.AudioObjectSetPropertyData(
                CoreAudioNative.SystemObject, &address, 0, null, (uint)IntPtr.Size, &runLoop);
        }
        catch
        {
            // Best effort: notifications may still arrive via the HAL's default dispatch.
        }
    }

    private void RegisterListeners()
    {
        try
        {
            var clientData = GCHandle.ToIntPtr(selfHandle);
            var devices = CoreAudioNative.Address(CoreAudioNative.HardwareDevices, CoreAudioNative.ScopeGlobal);
            var defaultDevice = CoreAudioNative.Address(
                CoreAudioNative.HardwareDefaultOutputDevice, CoreAudioNative.ScopeGlobal);
            var addedDevices = CoreAudioNative.AudioObjectAddPropertyListener(
                CoreAudioNative.SystemObject, &devices, listenerProc, clientData) == CoreAudioNative.NoErr;
            var addedDefault = CoreAudioNative.AudioObjectAddPropertyListener(
                CoreAudioNative.SystemObject, &defaultDevice, listenerProc, clientData) == CoreAudioNative.NoErr;
            listenersRegistered = addedDevices || addedDefault;
        }
        catch
        {
            // Device-change notifications are unavailable; the device list can still be refreshed manually.
        }
    }

    private void UnregisterListeners()
    {
        if (!listenersRegistered)
        {
            return;
        }

        var clientData = GCHandle.ToIntPtr(selfHandle);
        var devices = CoreAudioNative.Address(CoreAudioNative.HardwareDevices, CoreAudioNative.ScopeGlobal);
        var defaultDevice = CoreAudioNative.Address(
            CoreAudioNative.HardwareDefaultOutputDevice, CoreAudioNative.ScopeGlobal);
        _ = CoreAudioNative.AudioObjectRemovePropertyListener(
            CoreAudioNative.SystemObject, &devices, listenerProc, clientData);
        _ = CoreAudioNative.AudioObjectRemovePropertyListener(
            CoreAudioNative.SystemObject, &defaultDevice, listenerProc, clientData);
        listenersRegistered = false;
    }

    private void HandlePropertyChanged(bool defaultChanged, bool devicesChanged)
    {
        var followsDefault = string.Equals(
            DeviceId, IAudioOutputSettings.DefaultDeviceId, StringComparison.Ordinal);

        // The default-device change forces active output following the default to reopen. A device
        // list change forces a reopen only when a specific endpoint is selected, because that
        // endpoint may have just been unplugged; following the default relies on the dedicated
        // default-device notification instead.
        var configurationChanged = (defaultChanged && followsDefault) || (devicesChanged && !followsDefault);
        RaiseChanged(configurationChanged, devicesChanged: devicesChanged || defaultChanged);
    }

    private void RaiseChanged(bool configurationChanged, bool devicesChanged) =>
        Changed?.Invoke(this, new AudioOutputSettingsChangedEventArgs(configurationChanged, devicesChanged));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPropertyChanged(
        uint objectId,
        uint numberAddresses,
        IntPtr addresses,
        IntPtr clientData)
    {
        if (FromHandle(clientData) is not { } self)
        {
            return CoreAudioNative.NoErr;
        }

        var defaultChanged = false;
        var devicesChanged = false;
        var addressList = (CoreAudioNative.AudioObjectPropertyAddress*)addresses;
        for (var index = 0u; index < numberAddresses; index++)
        {
            switch (addressList[index].Selector)
            {
                case CoreAudioNative.HardwareDefaultOutputDevice:
                    defaultChanged = true;
                    break;
                case CoreAudioNative.HardwareDevices:
                    devicesChanged = true;
                    break;
            }
        }

        // Never raise Changed on the HAL notification thread: a handler may call back into the HAL
        // (GetDevices / reopen), so hand the work to the thread pool to avoid reentrancy stalls.
        ThreadPool.QueueUserWorkItem(
            static state => state.Self.HandlePropertyChanged(state.DefaultChanged, state.DevicesChanged),
            (Self: self, DefaultChanged: defaultChanged, DevicesChanged: devicesChanged),
            preferLocal: false);
        return CoreAudioNative.NoErr;
    }

    private static CoreAudioSettings? FromHandle(IntPtr clientData)
    {
        if (clientData == IntPtr.Zero)
        {
            return null;
        }

        var handle = GCHandle.FromIntPtr(clientData);
        return handle.IsAllocated ? handle.Target as CoreAudioSettings : null;
    }
}