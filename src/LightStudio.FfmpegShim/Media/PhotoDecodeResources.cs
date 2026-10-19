using System;
using System.Threading;

namespace LightStudio.FfmpegShim.Media;

/// <summary>Bounds retained native photo samples across current and read-ahead images.</summary>
public static class PhotoDecodeResources
{
    private static long reservedBytes;
    private static volatile bool preferThroughput;

    public static bool PreferThroughput
    {
        get => preferThroughput;
        set => preferThroughput = value;
    }

    public static bool TryReserve(long bytes, out IDisposable reservation)
    {
        reservation = null;
        if (bytes <= 0 || !PreferThroughput)
            return false;
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        long budget = Math.Min(4L * 1024 * 1024 * 1024, memory.TotalAvailableMemoryBytes / 4);
        if (bytes > budget || memory.HighMemoryLoadThresholdBytes > 0 &&
            memory.MemoryLoadBytes >= memory.HighMemoryLoadThresholdBytes * 9 / 10)
            return false;
        long current = Volatile.Read(ref reservedBytes);
        while (current <= budget - bytes)
        {
            long observed = Interlocked.CompareExchange(ref reservedBytes, current + bytes, current);
            if (observed == current)
            {
                reservation = new Reservation(bytes);
                return true;
            }
            current = observed;
        }
        return false;
    }

    private sealed class Reservation(long bytes) : IDisposable
    {
        private long retained = bytes;

        public void Dispose() => Interlocked.Add(ref reservedBytes, -Interlocked.Exchange(ref retained, 0));
    }
}