using System;

namespace LightStudio.FfmpegShim.Demuxing;

/// <summary>
/// How much of what has already played a packet source keeps, so a short backward seek needs no new
/// read from the file or the server. The byte bound is a small share of the memory the runtime can
/// see, which keeps a phone's window far smaller than a workstation's.
/// </summary>
public static class PacketHistoryBudget
{
    public const double DurationSeconds = 30;

    private const long MinimumBytes = 32L * 1024 * 1024;
    private const long MaximumBytes = 200L * 1024 * 1024;
    private const int MemoryShareDivisor = 128;

    public static long ResolveMaximumBytes() =>
        Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / MemoryShareDivisor, MinimumBytes, MaximumBytes);
}
