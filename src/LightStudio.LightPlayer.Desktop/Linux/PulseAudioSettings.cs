using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.Desktop.Linux;

/// <summary>
/// PulseAudio backend configuration. Maintains a persistent client connection used to enumerate
/// sinks, resolve the mixer sample rate, and observe device hot-plug / default-sink changes. Device
/// identifiers are the raw PulseAudio sink names prefixed with <c>pulse:</c>; the virtual
/// <see cref="IAudioOutputSettings.DefaultDeviceId"/> follows the server's current default sink.
/// </summary>
internal sealed unsafe class PulseAudioSettings : IAudioOutputSettings, ISystemSampleRateProvider
{
    private const string DeviceIdPrefix = "pulse:";
    private static readonly IReadOnlyList<AudioOutputDeviceInfo> DefaultDevices =
    [
        new(IAudioOutputSettings.DefaultDeviceId, "Default device"),
    ];

    private readonly object gate = new();
    private readonly object connectGate = new();
    private string deviceId = IAudioOutputSettings.DefaultDeviceId;
    private IntPtr mainloop;
    private IntPtr api;
    private IntPtr context;
    private GCHandle selfHandle;
    private int connectionState; // 0 = unknown, 1 = connected, -1 = unavailable.

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

    public event EventHandler<AudioOutputSettingsChangedEventArgs>? Changed;

    public IReadOnlyList<AudioOutputDeviceInfo> GetDevices()
    {
        if (!EnsureConnected())
        {
            return DefaultDevices;
        }

        try
        {
            return QuerySinks()
                .Where(static sink => !string.IsNullOrEmpty(sink.Name))
                .OrderBy(static sink => sink.Description, StringComparer.CurrentCultureIgnoreCase)
                .Select(static sink => new AudioOutputDeviceInfo(DeviceIdPrefix + sink.Name, sink.Description))
                .Prepend(DefaultDevices[0])
                .ToArray();
        }
        catch
        {
            return DefaultDevices;
        }
    }

    public IReadOnlyDictionary<string, string> GetOptions() =>
        System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    public void SetDevice(string selectedDeviceId)
    {
        var normalized = string.IsNullOrWhiteSpace(selectedDeviceId)
            ? IAudioOutputSettings.DefaultDeviceId
            : selectedDeviceId;
        if (!string.Equals(normalized, IAudioOutputSettings.DefaultDeviceId, StringComparison.Ordinal) &&
            !EnsureConnected())
        {
            normalized = IAudioOutputSettings.DefaultDeviceId;
        }

        lock (gate)
        {
            if (string.Equals(deviceId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            deviceId = normalized;
        }

        Changed?.Invoke(this, new AudioOutputSettingsChangedEventArgs(
            configurationChanged: true,
            devicesChanged: false));
    }

    public void SetOption(string key, string value)
    {
    }

    public int GetSystemSampleRate()
    {
        if (!EnsureConnected())
        {
            return 0;
        }

        try
        {
            var server = QueryServer();
            var targetSink = SelectedSinkName ?? server.DefaultSinkName;
            if (!string.IsNullOrEmpty(targetSink))
            {
                foreach (var sink in QuerySinks())
                {
                    if (string.Equals(sink.Name, targetSink, StringComparison.Ordinal) && sink.Rate > 0)
                    {
                        return (int)sink.Rate;
                    }
                }
            }

            return (int)server.Rate;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The selected sink name, or <see langword="null"/> to follow the server default.</summary>
    internal string? SelectedSinkName
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

    private bool EnsureConnected()
    {
        var cached = Volatile.Read(ref connectionState);
        if (cached != 0)
        {
            return cached == 1;
        }

        lock (connectGate)
        {
            if (connectionState != 0)
            {
                return connectionState == 1;
            }

            var connected = false;
            try
            {
                connected = ConnectCore();
            }
            catch
            {
                connected = false;
            }

            if (!connected)
            {
                TeardownCore();
            }

            Volatile.Write(ref connectionState, connected ? 1 : -1);
            return connected;
        }
    }

    private bool ConnectCore()
    {
        mainloop = PulseNative.pa_threaded_mainloop_new();
        if (mainloop == IntPtr.Zero)
        {
            return false;
        }

        api = PulseNative.pa_threaded_mainloop_get_api(mainloop);
        if (PulseNative.pa_threaded_mainloop_start(mainloop) < 0)
        {
            return false;
        }

        selfHandle = GCHandle.Alloc(this);
        var userdata = GCHandle.ToIntPtr(selfHandle);

        PulseNative.pa_threaded_mainloop_lock(mainloop);
        try
        {
            context = PulseNative.pa_context_new(api, "LightPlayer");
            if (context == IntPtr.Zero)
            {
                return false;
            }

            PulseNative.pa_context_set_state_callback(
                context,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnContextState,
                userdata);
            if (PulseNative.pa_context_connect(context, null, PulseNative.PA_CONTEXT_NOFLAGS, IntPtr.Zero) < 0)
            {
                return false;
            }

            while (true)
            {
                var state = PulseNative.pa_context_get_state(context);
                if (state == PulseNative.PA_CONTEXT_READY)
                {
                    break;
                }

                if (state is PulseNative.PA_CONTEXT_FAILED or PulseNative.PA_CONTEXT_TERMINATED)
                {
                    return false;
                }

                PulseNative.pa_threaded_mainloop_wait(mainloop);
            }

            PulseNative.pa_context_set_subscribe_callback(
                context,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, uint, IntPtr, void>)&OnSubscribe,
                userdata);
            var subscribe = PulseNative.pa_context_subscribe(
                context,
                PulseNative.PA_SUBSCRIPTION_MASK_SINK | PulseNative.PA_SUBSCRIPTION_MASK_SERVER,
                IntPtr.Zero,
                IntPtr.Zero);
            if (subscribe != IntPtr.Zero)
            {
                PulseNative.pa_operation_unref(subscribe);
            }

            return true;
        }
        finally
        {
            PulseNative.pa_threaded_mainloop_unlock(mainloop);
        }
    }

    private void TeardownCore()
    {
        if (mainloop != IntPtr.Zero)
        {
            PulseNative.pa_threaded_mainloop_stop(mainloop);
        }

        if (context != IntPtr.Zero)
        {
            PulseNative.pa_context_disconnect(context);
            PulseNative.pa_context_unref(context);
            context = IntPtr.Zero;
        }

        if (mainloop != IntPtr.Zero)
        {
            PulseNative.pa_threaded_mainloop_free(mainloop);
            mainloop = IntPtr.Zero;
        }

        if (selfHandle.IsAllocated)
        {
            selfHandle.Free();
        }
    }

    private List<PulseSink> QuerySinks()
    {
        var query = new SinkQuery { Mainloop = mainloop };
        var handle = GCHandle.Alloc(query);
        PulseNative.pa_threaded_mainloop_lock(mainloop);
        try
        {
            var operation = PulseNative.pa_context_get_sink_info_list(
                context,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, void>)&OnSinkInfo,
                GCHandle.ToIntPtr(handle));
            WaitForOperation(operation);
        }
        finally
        {
            PulseNative.pa_threaded_mainloop_unlock(mainloop);
            handle.Free();
        }

        return query.Sinks;
    }

    private ServerQuery QueryServer()
    {
        var query = new ServerQuery { Mainloop = mainloop };
        var handle = GCHandle.Alloc(query);
        PulseNative.pa_threaded_mainloop_lock(mainloop);
        try
        {
            var operation = PulseNative.pa_context_get_server_info(
                context,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnServerInfo,
                GCHandle.ToIntPtr(handle));
            WaitForOperation(operation);
        }
        finally
        {
            PulseNative.pa_threaded_mainloop_unlock(mainloop);
            handle.Free();
        }

        return query;
    }

    private void WaitForOperation(IntPtr operation)
    {
        if (operation == IntPtr.Zero)
        {
            return;
        }

        while (PulseNative.pa_operation_get_state(operation) == PulseNative.PA_OPERATION_RUNNING)
        {
            PulseNative.pa_threaded_mainloop_wait(mainloop);
        }

        PulseNative.pa_operation_unref(operation);
    }

    private void RaiseChanged(bool serverChanged)
    {
        var followsDefault = string.Equals(
            DeviceId,
            IAudioOutputSettings.DefaultDeviceId,
            StringComparison.Ordinal);
        Changed?.Invoke(this, new AudioOutputSettingsChangedEventArgs(
            configurationChanged: serverChanged && followsDefault,
            devicesChanged: true));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnContextState(IntPtr context, IntPtr userdata)
    {
        if (FromHandle(userdata) is { } self)
        {
            PulseNative.pa_threaded_mainloop_signal(self.mainloop, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSinkInfo(IntPtr context, IntPtr info, int endOfList, IntPtr userdata)
    {
        if (GCHandle.FromIntPtr(userdata).Target is not SinkQuery query)
        {
            return;
        }

        if (endOfList != 0 || info == IntPtr.Zero)
        {
            PulseNative.pa_threaded_mainloop_signal(query.Mainloop, 0);
            return;
        }

        var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(info, PulseNative.SinkInfoNameOffset));
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var description = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(info, PulseNative.SinkInfoDescriptionOffset));
        var rate = (uint)Marshal.ReadInt32(info, PulseNative.SinkInfoRateOffset);
        query.Sinks.Add(new PulseSink(name, string.IsNullOrWhiteSpace(description) ? name : description, rate));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnServerInfo(IntPtr context, IntPtr info, IntPtr userdata)
    {
        if (GCHandle.FromIntPtr(userdata).Target is not ServerQuery query)
        {
            return;
        }

        if (info != IntPtr.Zero)
        {
            query.DefaultSinkName = Marshal.PtrToStringUTF8(
                Marshal.ReadIntPtr(info, PulseNative.ServerInfoDefaultSinkNameOffset));
            query.Rate = (uint)Marshal.ReadInt32(info, PulseNative.ServerInfoRateOffset);
        }

        PulseNative.pa_threaded_mainloop_signal(query.Mainloop, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSubscribe(IntPtr context, uint eventType, uint index, IntPtr userdata)
    {
        if (FromHandle(userdata) is not { } self)
        {
            return;
        }

        var serverChanged = (eventType & PulseNative.PA_SUBSCRIPTION_EVENT_FACILITY_MASK)
            == PulseNative.PA_SUBSCRIPTION_EVENT_SERVER;

        // Never raise Changed on the mainloop thread: a handler that calls back into this settings
        // object (e.g. GetDevices) would deadlock re-acquiring the mainloop lock we hold here.
        ThreadPool.QueueUserWorkItem(
            static state => state.Self.RaiseChanged(state.ServerChanged),
            (Self: self, ServerChanged: serverChanged),
            preferLocal: false);
    }

    private static PulseAudioSettings? FromHandle(IntPtr userdata)
    {
        if (userdata == IntPtr.Zero)
        {
            return null;
        }

        var handle = GCHandle.FromIntPtr(userdata);
        return handle.IsAllocated ? handle.Target as PulseAudioSettings : null;
    }

    private readonly record struct PulseSink(string Name, string Description, uint Rate);

    private sealed class SinkQuery
    {
        public IntPtr Mainloop;
        public List<PulseSink> Sinks { get; } = new();
    }

    private sealed class ServerQuery
    {
        public IntPtr Mainloop;
        public string? DefaultSinkName;
        public uint Rate;
    }
}
