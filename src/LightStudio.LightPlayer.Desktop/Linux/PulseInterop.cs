using System;
using System.Runtime.InteropServices;

namespace LightStudio.LightPlayer.Desktop.Linux;

/// <summary>
/// Minimal P/Invoke surface for the PulseAudio client library (<c>libpulse.so.0</c>). Only the
/// asynchronous threaded-mainloop entry points required to enumerate sinks and stream 16-bit PCM
/// playback are bound. The raw field offsets read from <c>pa_sink_info</c> / <c>pa_server_info</c>
/// by the callbacks assume the 64-bit LP64 ABI, which holds for every Linux desktop target the app
/// ships (<c>linux-x64</c> and <c>linux-arm64</c>).
/// </summary>
internal static class PulseNative
{
    private const string Lib = "libpulse.so.0";

    // pa_context_flags_t
    public const int PA_CONTEXT_NOFLAGS = 0;

    // pa_context_state_t
    public const int PA_CONTEXT_READY = 4;
    public const int PA_CONTEXT_FAILED = 5;
    public const int PA_CONTEXT_TERMINATED = 6;

    // pa_stream_state_t
    public const int PA_STREAM_READY = 2;
    public const int PA_STREAM_FAILED = 3;
    public const int PA_STREAM_TERMINATED = 4;

    // pa_stream_flags_t
    public const int PA_STREAM_START_CORKED = 0x0001;
    public const int PA_STREAM_INTERPOLATE_TIMING = 0x0002;
    public const int PA_STREAM_AUTO_TIMING_UPDATE = 0x0008;

    // pa_sample_format_t
    public const int PA_SAMPLE_S16LE = 3;
    public const int PA_SAMPLE_S16BE = 4;
    // Packed signed 24-bit (3 bytes per sample), matching the app's internal PCM layout.
    public const int PA_SAMPLE_S24LE = 9;
    public const int PA_SAMPLE_S24BE = 10;

    // pa_volume_t (volume.h): PA_VOLUME_NORM is unity gain (0 dB); PA_VOLUME_MUTED is silence.
    public const uint PA_VOLUME_NORM = 0x10000;
    public const uint PA_VOLUME_MUTED = 0;

    // PA_CHANNELS_MAX (sample.h): capacity of the pa_cvolume per-channel array.
    public const int PA_CHANNELS_MAX = 32;

    // PA_INVALID_INDEX ((uint32_t)-1): pa_stream_get_index result before the stream has an index.
    public const uint PA_INVALID_INDEX = 0xFFFFFFFF;

    // pa_seek_mode_t
    public const int PA_SEEK_RELATIVE = 0;

    // pa_operation_state_t
    public const int PA_OPERATION_RUNNING = 0;

    // pa_subscription_mask_t
    public const int PA_SUBSCRIPTION_MASK_SINK = 0x0001;
    public const int PA_SUBSCRIPTION_MASK_SINK_INPUT = 0x0004;
    public const int PA_SUBSCRIPTION_MASK_SERVER = 0x0200;

    // pa_subscription_event_type_t
    public const uint PA_SUBSCRIPTION_EVENT_FACILITY_MASK = 0x000F;
    public const uint PA_SUBSCRIPTION_EVENT_SINK_INPUT = 0x0002;
    public const uint PA_SUBSCRIPTION_EVENT_SERVER = 0x0006;
    public const uint PA_SUBSCRIPTION_EVENT_CHANGE = 0x0010;
    public const uint PA_SUBSCRIPTION_EVENT_TYPE_MASK = 0x0030;

    // pa_sink_info field offsets (LP64): const char* name; uint32 index; const char* description;
    // pa_sample_spec sample_spec (format int, rate uint32, channels uint8).
    public const int SinkInfoNameOffset = 0;
    public const int SinkInfoDescriptionOffset = 16;
    public const int SinkInfoRateOffset = 28;

    // pa_server_info field offsets (LP64): four const char* fields, then pa_sample_spec, then
    // const char* default_sink_name.
    public const int ServerInfoRateOffset = 36;
    public const int ServerInfoDefaultSinkNameOffset = 48;

    // pa_sink_input_info field offset (LP64): the pa_cvolume 'volume' member follows index, name,
    // owner_module, client, sink, pa_sample_spec (12 B), and pa_channel_map (132 B).
    public const int SinkInputInfoVolumeOffset = 172;

    /// <summary>The sentinel <c>(size_t)-1</c> returned by <see cref="pa_stream_writable_size"/> on error.</summary>
    public static readonly nuint InvalidSize = unchecked((nuint)(nint)(-1));

    [StructLayout(LayoutKind.Sequential)]
    public struct PaSampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    /// <summary>
    /// The fields of <c>pa_timing_info</c> the backend reads, at their LP64 offsets: a 16-byte
    /// <c>struct timeval</c>, an <c>int</c> and the three latency members come first, and each
    /// stream index follows its own validity flag.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    public struct PaTimingInfo
    {
        [FieldOffset(0)] public long TimestampSeconds;
        [FieldOffset(8)] public long TimestampMicroseconds;
        [FieldOffset(24)] public ulong SinkMicroseconds;
        [FieldOffset(48)] public int Playing;
        [FieldOffset(52)] public int WriteIndexCorrupt;
        [FieldOffset(56)] public long WriteIndex;
        [FieldOffset(64)] public int ReadIndexCorrupt;
        [FieldOffset(72)] public long ReadIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaBufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;
    }

    // pa_cvolume (volume.h): uint8 channels followed by a PA_CHANNELS_MAX array of per-channel
    // pa_volume_t values. Matches the LP64 layout (3 padding bytes before the 4-byte-aligned array).
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct PaCVolume
    {
        public byte Channels;
        public fixed uint Values[PA_CHANNELS_MAX];
    }

    // Threaded mainloop.
    [DllImport(Lib)] public static extern IntPtr pa_threaded_mainloop_new();
    [DllImport(Lib)] public static extern void pa_threaded_mainloop_free(IntPtr mainloop);
    [DllImport(Lib)] public static extern int pa_threaded_mainloop_start(IntPtr mainloop);
    [DllImport(Lib)] public static extern void pa_threaded_mainloop_stop(IntPtr mainloop);
    [DllImport(Lib)] public static extern void pa_threaded_mainloop_lock(IntPtr mainloop);
    [DllImport(Lib)] public static extern void pa_threaded_mainloop_unlock(IntPtr mainloop);
    [DllImport(Lib)] public static extern void pa_threaded_mainloop_wait(IntPtr mainloop);
    [DllImport(Lib)] public static extern void pa_threaded_mainloop_signal(IntPtr mainloop, int waitForAccept);
    [DllImport(Lib)] public static extern IntPtr pa_threaded_mainloop_get_api(IntPtr mainloop);

    // Context.
    [DllImport(Lib)] public static extern IntPtr pa_context_new(IntPtr mainloopApi, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] public static extern void pa_context_unref(IntPtr context);
    [DllImport(Lib)] public static extern int pa_context_connect(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string? server, int flags, IntPtr spawnApi);
    [DllImport(Lib)] public static extern void pa_context_disconnect(IntPtr context);
    [DllImport(Lib)] public static extern int pa_context_get_state(IntPtr context);
    [DllImport(Lib)] public static extern void pa_context_set_state_callback(IntPtr context, IntPtr callback, IntPtr userdata);
    [DllImport(Lib)] public static extern IntPtr pa_context_get_sink_info_list(IntPtr context, IntPtr callback, IntPtr userdata);
    [DllImport(Lib)] public static extern IntPtr pa_context_get_server_info(IntPtr context, IntPtr callback, IntPtr userdata);
    [DllImport(Lib)] public static extern void pa_context_set_subscribe_callback(IntPtr context, IntPtr callback, IntPtr userdata);
    [DllImport(Lib)] public static extern IntPtr pa_context_subscribe(IntPtr context, int mask, IntPtr callback, IntPtr userdata);
    [DllImport(Lib)] public static extern IntPtr pa_context_set_sink_input_volume(IntPtr context, uint idx, in PaCVolume volume, IntPtr callback, IntPtr userdata);
    [DllImport(Lib)] public static extern IntPtr pa_context_get_sink_input_info(IntPtr context, uint idx, IntPtr callback, IntPtr userdata);

    // Stream.
    [DllImport(Lib)] public static extern IntPtr pa_stream_new(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, in PaSampleSpec sampleSpec, IntPtr channelMap);
    [DllImport(Lib)] public static extern void pa_stream_unref(IntPtr stream);
    [DllImport(Lib)] public static extern int pa_stream_get_state(IntPtr stream);
    [DllImport(Lib)] public static extern void pa_stream_set_state_callback(IntPtr stream, IntPtr callback, IntPtr userdata);
    [DllImport(Lib)] public static extern void pa_stream_set_underflow_callback(IntPtr stream, IntPtr callback, IntPtr userdata);
    [DllImport(Lib)] public static extern int pa_stream_connect_playback(IntPtr stream, [MarshalAs(UnmanagedType.LPUTF8Str)] string? device, in PaBufferAttr attr, int flags, IntPtr volume, IntPtr syncStream);
    [DllImport(Lib)] public static extern int pa_stream_disconnect(IntPtr stream);
    [DllImport(Lib)] public static extern nuint pa_stream_writable_size(IntPtr stream);
    [DllImport(Lib)] public static extern int pa_stream_write(IntPtr stream, IntPtr data, nuint nbytes, IntPtr freeCallback, long offset, int seek);
    [DllImport(Lib)] public static extern IntPtr pa_stream_cork(IntPtr stream, int cork, IntPtr callback, IntPtr userdata);
    [DllImport(Lib)] public static extern IntPtr pa_stream_trigger(IntPtr stream, IntPtr callback, IntPtr userdata);
    [DllImport(Lib)] public static extern IntPtr pa_stream_flush(IntPtr stream, IntPtr callback, IntPtr userdata);
    [DllImport(Lib)] public static extern int pa_stream_get_latency(IntPtr stream, out ulong rUsec, out int negative);
    [DllImport(Lib)] public static extern IntPtr pa_stream_get_buffer_attr(IntPtr stream);
    [DllImport(Lib)] public static extern IntPtr pa_stream_get_timing_info(IntPtr stream);
    [DllImport(Lib)] public static extern uint pa_stream_get_index(IntPtr stream);

    // Operation.
    [DllImport(Lib)] public static extern int pa_operation_get_state(IntPtr operation);
    [DllImport(Lib)] public static extern void pa_operation_unref(IntPtr operation);
}
