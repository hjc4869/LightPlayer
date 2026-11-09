using System;
using System.Runtime.InteropServices;

namespace LightStudio.LocalServices;

/// <summary>Lets glibc return native memory freed by decoding and inference bursts to the system.</summary>
public static unsafe class NativeHeap
{
    private const int TrimThreshold = -1;
    private const int MmapThreshold = -3;

    private static readonly delegate* unmanaged[Cdecl]<int, int, int> mallopt;
    private static readonly delegate* unmanaged[Cdecl]<nuint, int> mallocTrim;

    static NativeHeap()
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libc.so.6", out IntPtr library))
            return;
        if (NativeLibrary.TryGetExport(library, "mallopt", out IntPtr malloptAddress) &&
            NativeLibrary.TryGetExport(library, "malloc_trim", out IntPtr trimAddress))
        {
            mallopt = (delegate* unmanaged[Cdecl]<int, int, int>)malloptAddress;
            mallocTrim = (delegate* unmanaged[Cdecl]<nuint, int>)trimAddress;
        }
    }

    /// <summary>
    /// Fixes the thresholds glibc otherwise raises after each large free, which leaves freed space resident in
    /// per-thread heaps. Buffers of 1 MiB or more are then mapped individually, and free heap tops over 2 MiB are released.
    /// </summary>
    public static void Configure()
    {
        if (mallopt == null)
            return;
        mallopt(MmapThreshold, 1 << 20);
        mallopt(TrimThreshold, 2 << 20);
    }

    /// <summary>Releases free pages inside glibc heaps.</summary>
    public static void Trim()
    {
        if (mallocTrim != null)
            mallocTrim(0);
    }
}
