using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LightStudio.LightPlayer.WebWorkers;

/// <summary>Waits on a native completion word without occupying the worker's JavaScript event loop.</summary>
[SupportedOSPlatform("browser")]
internal static class WorkerCompletion
{
    /// <summary>The caller owns the aligned word until all waiters and asynchronous writers have finished.</summary>
    /// <returns>False on timeout; otherwise recheck the completion predicate because notifications can be spurious.</returns>
    internal static bool WaitForChange(IntPtr word, int expected, int timeoutMilliseconds)
    {
        if (word == IntPtr.Zero || ((nuint)word & 3) != 0)
            throw new ArgumentException("A completion word must be a nonzero, four-byte-aligned native address.", nameof(word));
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMilliseconds);
        int result = Wait(word, expected, timeoutMilliseconds);
        return result switch
        {
            0 or 1 => true,
            2 => false,
            _ => throw new InvalidOperationException($"The native completion wait failed ({result})."),
        };
    }

    [DllImport("source-bridge", EntryPoint = "lightplayer_completion_wait")]
    private static extern int Wait(IntPtr word, int expected, int timeoutMilliseconds);
}