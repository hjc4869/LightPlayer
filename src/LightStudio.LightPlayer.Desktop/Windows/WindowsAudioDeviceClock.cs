using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LightStudio.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LightStudio.LightPlayer.Desktop.Windows;

/// <summary>
/// Reads WASAPI's stream-relative device position and correlated performance counter through
/// <c>IAudioClock</c>. Position units are converted with the clock's own frequency. The native clock
/// is retained from NAudio; when it is unavailable, the caller uses NAudio's position fallback.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsAudioDeviceClock : IDisposable
{
    private static readonly Guid AudioClockId = new("cd63314f-3fba-4a1b-812c-ef96358728e7");

    // Clamps the extrapolation from the reported counter value, so a device that stops updating its
    // position cannot run the estimate away from the audio that was actually written.
    private const ulong MaxExtrapolation100Nanoseconds = 10_000_000;

    private IntPtr clock;
    private readonly ulong frequency;

    private WindowsAudioDeviceClock(IntPtr clock, ulong frequency)
    {
        this.clock = clock;
        this.frequency = frequency;
    }

    // The two fields are private to NAudio, so publishing trimmed or AOT has to be told to keep them.
    [DynamicDependency("audioClient", typeof(WasapiPlayer))]
    [DynamicDependency("audioClockClientInterface", typeof(AudioClockClient))]
    public static WindowsAudioDeviceClock? TryCreate(WasapiPlayer player)
    {
        try
        {
            var clientField = typeof(WasapiPlayer).GetField(
                "audioClient",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (clientField?.GetValue(player) is not AudioClient client)
            {
                return null;
            }

            var clockField = typeof(AudioClockClient).GetField(
                "audioClockClientInterface",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (clockField?.GetValue(client.AudioClockClient) is not { } clockObject ||
                !ComWrappers.TryGetComInstance(clockObject, out var unknown))
            {
                return null;
            }

            try
            {
                if (Marshal.QueryInterface(unknown, in AudioClockId, out var nativeClock) != 0)
                    return null;

                var getFrequency = (delegate* unmanaged[Stdcall]<IntPtr, ulong*, int>)(*(IntPtr**)nativeClock)[3];
                ulong frequency;
                if (getFrequency(nativeClock, &frequency) < 0 || frequency == 0)
                {
                    Marshal.Release(nativeClock);
                    return null;
                }

                return new WindowsAudioDeviceClock(nativeClock, frequency);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }
        catch (Exception exception)
        {
            AppLog.Warning("Audio.Windows", "Could not acquire the WASAPI presentation clock.", exception);
            return null;
        }
    }

    /// <summary>
    /// Stream seconds played, extrapolated to now from the performance counter value the endpoint
    /// reported alongside the position.
    /// </summary>
    public bool TryGetPlayedSeconds(out double seconds)
    {
        seconds = 0;
        if (clock == IntPtr.Zero)
        {
            return false;
        }

        var getPosition =
            (delegate* unmanaged[Stdcall]<IntPtr, ulong*, ulong*, int>)(*(IntPtr**)clock)[4];
        ulong position;
        ulong counterPosition;
        if (getPosition(clock, &position, &counterPosition) < 0)
        {
            return false;
        }

        seconds = position / (double)frequency;
        if (position == 0 || !Stopwatch.IsHighResolution)
        {
            return true;
        }

        var now = (ulong)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
        if (now > counterPosition)
        {
            var elapsed = Math.Min(now - counterPosition, MaxExtrapolation100Nanoseconds);
            seconds += elapsed / 10_000_000.0;
        }

        return true;
    }

    public void Dispose()
    {
        var released = clock;
        clock = IntPtr.Zero;
        if (released != IntPtr.Zero)
        {
            Marshal.Release(released);
        }
    }
}
