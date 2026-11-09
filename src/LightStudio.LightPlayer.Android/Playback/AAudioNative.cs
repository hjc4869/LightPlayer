#nullable enable
using System;
using System.Diagnostics;
using System.Reflection;
using LightStudio.NativeBindings;
using LightStudio.NativeBindings.Types;

namespace LightStudio.LightPlayer.Android.Playback;

[Include("aaudio/AAudio.h", Access = BindingFlags.Public)]
[Include("time.h", Access = BindingFlags.Public, Macros = ["CLOCK_MONOTONIC"])]
internal static unsafe partial class AAudioNative
{
    internal static string GetResultText(int result)
    {
        var text = AAudio_convertResultToText(result);
        return string.IsNullOrWhiteSpace(text) ? result.ToString() : text;
    }

    /// <summary>
    /// Reads the same clock the AAudio timestamps are stamped with: the runtime's timestamp clock is
    /// CLOCK_MONOTONIC on Android.
    /// </summary>
    internal static long MonotonicNanoseconds() =>
        Stopwatch.GetTimestamp() * (1_000_000_000L / Stopwatch.Frequency);

    internal static int ProbeSampleRate(int deviceId)
    {
        AAudioStreamBuilderStruct* builder = null;
        AAudioStreamStruct* stream = null;
        try
        {
            if (AAudio_createStreamBuilder(&builder) != AAUDIO_OK || builder == null)
            {
                return 0;
            }

            AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
            AAudioStreamBuilder_setSharingMode(builder, AAUDIO_SHARING_MODE_SHARED);
            AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_NONE);
            if (deviceId > 0)
            {
                AAudioStreamBuilder_setDeviceId(builder, deviceId);
            }

            if (AAudioStreamBuilder_openStream(builder, &stream) != AAUDIO_OK || stream == null)
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
            if (stream != null)
            {
                AAudioStream_close(stream);
            }

            if (builder != null)
            {
                AAudioStreamBuilder_delete(builder);
            }
        }
    }
}
