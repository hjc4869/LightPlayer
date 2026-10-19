#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LightStudio.FfmpegShim.Core;

/// <summary>
/// Monotonic high-resolution time source and precise short-delay helpers shared by every
/// pipeline component. Playback timing is expressed in seconds as <see cref="double"/> because
/// that is the unit FFmpeg presentation timestamps rescale into most naturally; only the public
/// player surface converts to <see cref="TimeSpan"/>.
/// </summary>
public static class MediaTime
{
    private const uint OneMillisecond = 1;
    private const uint TimerNoError = 0;
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint Synchronize = 0x00100000;
    private const uint TimerModifyState = 0x00000002;
    private const uint Infinite = 0xffffffff;
    private const uint WaitObject0 = 0;

    /// <summary>Sentinel for "this item carries no usable presentation timestamp".</summary>
    public const double NoTimestamp = double.NaN;

    private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

    // Thread.Sleep is still quantized to the default 15.625 ms timer period in an otherwise idle
    // Windows process. A 20 ms presentation wait therefore commonly lasts 31 ms. Keep one
    // high-resolution timer per timing thread; the video scheduler additionally requests the
    // one-millisecond system timer period for the duration of active playback.
    [ThreadStatic]
    private static SafeWaitHandle? windowsHighResolutionTimer;

    [ThreadStatic]
    private static bool windowsHighResolutionTimerUnavailable;

    /// <summary>Seconds since an arbitrary fixed origin, immune to wall-clock adjustments.</summary>
    public static double Now => Stopwatch.GetTimestamp() * TicksToSeconds;

    public static bool IsValid(double timestamp) => !double.IsNaN(timestamp);

    public static TimeSpan ToTimeSpan(double seconds) =>
        IsValid(seconds) ? TimeSpan.FromSeconds(Math.Max(0, seconds)) : TimeSpan.Zero;

    /// <summary>
    /// Requests Windows' one-millisecond multimedia timer period until the returned lease is
    /// disposed. This improves the precision of scheduler and dispatcher waits outside
    /// <see cref="PreciseDelay"/>, which already uses a high-resolution waitable timer.
    /// </summary>
    /// <remarks>
    /// Calls are balanced individually as required by <c>timeBeginPeriod</c>. Windows versions
    /// before 10 2004 applied the request globally; current Windows normally scopes its effect to
    /// the requesting process. Other platforms and unsupported Windows systems return a no-op
    /// lease.
    /// </remarks>
    public static IDisposable RequestHighResolutionTimerPeriod()
    {
        if (!OperatingSystem.IsWindows())
            return NoopTimerPeriodRequest.Instance;

        try
        {
            return TimeBeginPeriod(OneMillisecond) == TimerNoError
                ? new WindowsTimerPeriodRequest(OneMillisecond)
                : NoopTimerPeriodRequest.Instance;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return NoopTimerPeriodRequest.Instance;
        }
    }

    /// <summary>
    /// Waits for <paramref name="seconds"/> with sub-millisecond accuracy: a yielding kernel wait
    /// for the bulk of the interval (a high-resolution waitable timer on Windows, or
    /// <see cref="Thread.Sleep"/> elsewhere) and a spin for the final fraction the yielding wait
    /// cannot resolve. <paramref name="abort"/> lets a pending seek or stop cut the wait short.
    /// </summary>
    public static void PreciseDelay(double seconds, CancellationToken abort = default)
    {
        if (seconds <= 0)
            return;

        // Leave a spin margin: Sleep() routinely overshoots by up to a scheduler quantum, and
        // overshooting is what produces visible judder when a frame misses its vsync.
        const double SpinMargin = 0.0015;

        double deadline = Now + seconds;
        while (true)
        {
            double remaining = deadline - Now;
            if (remaining <= 0 || abort.IsCancellationRequested)
                return;

            if (remaining > SpinMargin)
            {
                double coarseDelay = remaining - SpinMargin;
                if (!TryWindowsHighResolutionWait(coarseDelay))
                    Thread.Sleep((int)(coarseDelay * 1000));
            }
            else
                Thread.SpinWait(40);
        }
    }

    private static bool TryWindowsHighResolutionWait(double seconds)
    {
        if (!OperatingSystem.IsWindows() || windowsHighResolutionTimerUnavailable)
            return false;

        try
        {
            SafeWaitHandle timer = windowsHighResolutionTimer ??= CreateWaitableTimerEx(
                IntPtr.Zero,
                null,
                CreateWaitableTimerHighResolution,
                Synchronize | TimerModifyState);
            if (timer.IsInvalid)
            {
                DisableWindowsHighResolutionTimer();
                return false;
            }

            // Negative values are relative deadlines in 100 ns units. Round down rather than past
            // the requested point; PreciseDelay's final spin closes the sub-tick remainder.
            long dueTime = -Math.Max(1, (long)(seconds * 10_000_000));
            if (!SetWaitableTimer(timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false) ||
                WaitForSingleObject(timer, Infinite) != WaitObject0)
            {
                DisableWindowsHighResolutionTimer();
                return false;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            DisableWindowsHighResolutionTimer();
            return false;
        }
    }

    private static void DisableWindowsHighResolutionTimer()
    {
        windowsHighResolutionTimerUnavailable = true;
        windowsHighResolutionTimer?.Dispose();
        windowsHighResolutionTimer = null;
    }

    private sealed class WindowsTimerPeriodRequest(uint period) : IDisposable
    {
        private int active = 1;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref active, 0) != 0)
                TimeEndPeriod(period);
        }
    }

    private sealed class NoopTimerPeriodRequest : IDisposable
    {
        public static NoopTimerPeriodRequest Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(
        IntPtr timerAttributes,
        string? timerName,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        SafeWaitHandle timer,
        ref long dueTime,
        int period,
        IntPtr completionRoutine,
        IntPtr completionArgument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
