#nullable enable
using System;
using System.Runtime.InteropServices;

namespace LightStudio.LightPlayer.Android.Playback;

internal static class AAudioNative
{
    internal const int Ok = 0;
    internal const int Unspecified = 0;
    internal const int DirectionOutput = 0;
    internal const int FormatPcmI16 = 1;
    // AAUDIO_FORMAT_PCM_I24_PACKED (API 31+): packed signed 24-bit, 3 bytes per sample.
    internal const int FormatPcmI24Packed = 3;
    internal const int SharingModeExclusive = 0;
    internal const int SharingModeShared = 1;
    internal const int PerformanceModeNone = 10;
    internal const int PerformanceModePowerSaving = 11;
    internal const int PerformanceModeLowLatency = 12;
    internal const int CallbackResultContinue = 0;
    internal const int StreamStateStarting = 3;
    internal const int StreamStateStarted = 4;
    internal const int StreamStatePausing = 5;
    internal const int StreamStatePaused = 6;
    internal const int ClockMonotonic = 1;

    [DllImport("aaudio")]
    internal static extern int AAudio_createStreamBuilder(out nint builder);

    [DllImport("aaudio")]
    internal static extern nint AAudio_convertResultToText(int result);

    [DllImport("aaudio")]
    internal static extern void AAudioStreamBuilder_setDeviceId(nint builder, int deviceId);

    [DllImport("aaudio")]
    internal static extern void AAudioStreamBuilder_setDirection(nint builder, int direction);

    [DllImport("aaudio")]
    internal static extern void AAudioStreamBuilder_setSharingMode(nint builder, int sharingMode);

    [DllImport("aaudio")]
    internal static extern void AAudioStreamBuilder_setPerformanceMode(nint builder, int performanceMode);

    [DllImport("aaudio")]
    internal static extern void AAudioStreamBuilder_setSampleRate(nint builder, int sampleRate);

    [DllImport("aaudio")]
    internal static extern void AAudioStreamBuilder_setChannelCount(nint builder, int channelCount);

    [DllImport("aaudio")]
    internal static extern void AAudioStreamBuilder_setFormat(nint builder, int format);

    [DllImport("aaudio")]
    internal static extern void AAudioStreamBuilder_setBufferCapacityInFrames(nint builder, int numFrames);

    [DllImport("aaudio")]
    internal static extern void AAudioStreamBuilder_setDataCallback(
        nint builder,
        nint callback,
        nint userData);

    [DllImport("aaudio")]
    internal static extern void AAudioStreamBuilder_setErrorCallback(
        nint builder,
        nint callback,
        nint userData);

    [DllImport("aaudio")]
    internal static extern int AAudioStreamBuilder_openStream(nint builder, out nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStreamBuilder_delete(nint builder);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_close(nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_getSampleRate(nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_getHardwareSampleRate(nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_getChannelCount(nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_getFormat(nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_getDeviceId(nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_getSharingMode(nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_getBufferCapacityInFrames(nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_getFramesPerBurst(nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_setBufferSizeInFrames(nint stream, int numFrames);

    [DllImport("aaudio")]
    internal static extern long AAudioStream_getFramesRead(nint stream);

    /// <summary>
    /// Passes back the frame that reached the speaker or headset at <paramref name="timeNanoseconds"/>,
    /// which is the only AAudio position that includes the mixer, HAL and Bluetooth transport delay.
    /// </summary>
    [DllImport("aaudio")]
    internal static extern int AAudioStream_getTimestamp(
        nint stream,
        int clockId,
        out long framePosition,
        out long timeNanoseconds);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_getXRunCount(nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_requestStart(nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_requestPause(nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_getState(nint stream);

    [DllImport("aaudio")]
    internal static extern int AAudioStream_waitForStateChange(
        nint stream,
        int inputState,
        out int nextState,
        long timeoutNanoseconds);

    [DllImport("c")]
    private static extern int clock_gettime(int clockId, out Timespec time);

    internal static string GetResultText(int result)
    {
        var text = Marshal.PtrToStringUTF8(AAudio_convertResultToText(result));
        return string.IsNullOrWhiteSpace(text) ? result.ToString() : text;
    }

    /// <summary>Reads the same clock the AAudio timestamps are stamped with.</summary>
    internal static long MonotonicNanoseconds() =>
        clock_gettime(ClockMonotonic, out var time) == 0
            ? ((long)time.Seconds * 1_000_000_000L) + time.Nanoseconds
            : 0;

    internal static int ProbeSampleRate(int deviceId)
    {
        nint builder = 0;
        nint stream = 0;
        try
        {
            if (AAudio_createStreamBuilder(out builder) != Ok || builder == 0)
            {
                return 0;
            }

            AAudioStreamBuilder_setDirection(builder, DirectionOutput);
            AAudioStreamBuilder_setSharingMode(builder, SharingModeShared);
            AAudioStreamBuilder_setPerformanceMode(builder, PerformanceModeNone);
            if (deviceId > 0)
            {
                AAudioStreamBuilder_setDeviceId(builder, deviceId);
            }

            if (AAudioStreamBuilder_openStream(builder, out stream) != Ok || stream == 0)
            {
                return 0;
            }

            var streamRate = OperatingSystem.IsAndroidVersionAtLeast(34)
                ? AAudioStream_getHardwareSampleRate(stream)
                : AAudioStream_getSampleRate(stream);
            return Math.Max(0, streamRate);
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
        finally
        {
            if (stream != 0)
            {
                AAudioStream_close(stream);
            }

            if (builder != 0)
            {
                AAudioStreamBuilder_delete(builder);
            }
        }
    }

    // time_t and long are pointer-sized on every Android ABI this app targets.
    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public nint Seconds;
        public nint Nanoseconds;
    }
}