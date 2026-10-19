#if LINUX
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LightStudio.LightPlayer.Desktop.Linux;

/// <summary>
/// The two libva entry points needed to hand a decoded surface to a graphics API.
/// </summary>
/// <remarks>
/// Resolved through <see cref="NativeLibrary"/> rather than declared as imports so that a build for
/// a platform without libva neither links against it nor has to exclude this file. By the time any
/// of this runs the decoder has already opened the library, so loading it here only takes a
/// reference to what is in the process.
/// </remarks>
internal static unsafe class VaapiLibrary
{
    public const uint MemoryTypeDrmPrime2 = 0x4000_0000;
    public const uint ExportReadOnly = 0x0001;
    public const uint ExportSeparateLayers = 0x0004;

    private const int Success = 0;

    private static readonly delegate* unmanaged[Cdecl]<IntPtr, uint, int> syncSurface;
    private static readonly delegate* unmanaged[Cdecl]<IntPtr, uint, uint, uint, void*, int> exportSurfaceHandle;
    private static readonly delegate* unmanaged[Cdecl]<int, int> closeDescriptor;

    static VaapiLibrary()
    {
        if (!OperatingSystem.IsLinux())
            return;

        if (!TryLoad(["libva.so.2", "libva.so.1", "libva.so"], out IntPtr va))
            return;

        if (!TryGet(va, "vaSyncSurface", out IntPtr sync) ||
            !TryGet(va, "vaExportSurfaceHandle", out IntPtr export))
        {
            return;
        }

        if (!TryGetClose(out IntPtr close))
            return;

        syncSurface = (delegate* unmanaged[Cdecl]<IntPtr, uint, int>)sync;
        exportSurfaceHandle = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint, uint, void*, int>)export;
        closeDescriptor = (delegate* unmanaged[Cdecl]<int, int>)close;
        IsAvailable = true;
    }

    /// <summary>Whether a surface can be exported at all on this machine.</summary>
    public static bool IsAvailable { get; }

    /// <summary>Waits for the decode into a surface to finish before anything else reads it.</summary>
    public static bool SyncSurface(IntPtr display, uint surface) =>
        IsAvailable && syncSurface(display, surface) == Success;

    /// <summary>
    /// Describes a surface as one DRM PRIME layer per plane, which is the shape the renderer's
    /// per-plane color conversion wants. The caller owns the returned file descriptors.
    /// </summary>
    public static bool TryExportPlanes(IntPtr display, uint surface, out DrmPrimeSurface descriptor)
    {
        descriptor = default;
        if (!IsAvailable)
            return false;

        DrmPrimeSurface exported = default;
        int status = exportSurfaceHandle(
            display,
            surface,
            MemoryTypeDrmPrime2,
            ExportSeparateLayers | ExportReadOnly,
            &exported);

        if (status != Success)
            return false;

        descriptor = exported;
        return true;
    }

    public static void CloseDescriptor(int fileDescriptor)
    {
        if (IsAvailable && fileDescriptor >= 0)
            closeDescriptor(fileDescriptor);
    }

    private static bool TryLoad(string[] names, out IntPtr handle)
    {
        foreach (string name in names)
        {
            if (NativeLibrary.TryLoad(name, out handle))
                return true;
        }

        handle = IntPtr.Zero;
        return false;
    }

    private static bool TryGet(IntPtr library, string name, out IntPtr address) =>
        NativeLibrary.TryGetExport(library, name, out address);

    // close(2) is in whatever libc the process already has; nothing has to be loaded for it.
    private static bool TryGetClose(out IntPtr address)
    {
        if (NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), "close", out address))
            return true;

        return TryLoad(["libc.so.6", "libc.so"], out IntPtr libc) &&
               NativeLibrary.TryGetExport(libc, "close", out address);
    }
}

/// <summary>One dma-buf object underlying an exported surface.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DrmPrimeObject
{
    public int FileDescriptor;
    public uint Size;
    public ulong Modifier;
}

/// <summary>One plane of an exported surface, as a DRM format over one or more objects.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DrmPrimeLayer
{
    public uint DrmFormat;
    public uint PlaneCount;
    public uint ObjectIndex0, ObjectIndex1, ObjectIndex2, ObjectIndex3;
    public uint Offset0, Offset1, Offset2, Offset3;
    public uint Pitch0, Pitch1, Pitch2, Pitch3;

    [UnscopedRef]
    public ref uint ObjectIndex(int plane) => ref Unsafe.Add(ref ObjectIndex0, plane);

    [UnscopedRef]
    public ref uint Offset(int plane) => ref Unsafe.Add(ref Offset0, plane);

    [UnscopedRef]
    public ref uint Pitch(int plane) => ref Unsafe.Add(ref Pitch0, plane);
}

/// <summary>Mirrors <c>VADRMPRIMESurfaceDescriptor</c>, which is four objects and four layers wide.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DrmPrimeSurface
{
    public uint Fourcc;
    public uint Width;
    public uint Height;
    public uint ObjectCount;
    public DrmPrimeObject Object0, Object1, Object2, Object3;
    public uint LayerCount;
    public DrmPrimeLayer Layer0, Layer1, Layer2, Layer3;

    [UnscopedRef]
    public ref DrmPrimeObject Object(int index) => ref Unsafe.Add(ref Object0, index);

    [UnscopedRef]
    public ref DrmPrimeLayer Layer(int index) => ref Unsafe.Add(ref Layer0, index);

    public void CloseDescriptors()
    {
        for (int i = 0; i < (int)Math.Min(ObjectCount, 4); i++)
        {
            ref DrmPrimeObject entry = ref Object(i);
            VaapiLibrary.CloseDescriptor(entry.FileDescriptor);
            entry.FileDescriptor = -1;
        }
    }
}
#endif
