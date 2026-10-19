using System;
using System.Runtime.InteropServices;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>
/// The few COM calls needed to hand the renderer's Direct3D device to a decoder.
/// </summary>
/// <remarks>
/// Uses COM vtables so the shared library does not require a Windows-only interop package.
/// </remarks>
internal static unsafe class Direct3DInterop
{
    // IUnknown, whose first three slots every COM interface begins with.
    private const int QueryInterfaceSlot = 0;
    private const int AddRefSlot = 1;
    private const int ReleaseSlot = 2;

    private const int GetCreationFlagsSlot = 38;
    private const int GetDeviceRemovedReasonSlot = 39;
    private const int SetMultithreadProtectedSlot = 5;
    private const int GetMultithreadProtectedSlot = 6;

    private static readonly Guid ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");

    /// <summary>Takes a reference the caller is expected to hand to something that will release it.</summary>
    public static void AddRef(IntPtr instance)
    {
        if (instance == IntPtr.Zero)
            return;

        var addRef = (delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, AddRefSlot);
        addRef(instance);
    }

    public static void Release(IntPtr instance)
    {
        if (instance == IntPtr.Zero)
            return;

        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, ReleaseSlot);
        release(instance);
    }

    /// <summary>Enables and verifies protection for individual immediate-context calls.</summary>
    public static bool TryEnableMultithreadProtection(IntPtr device)
    {
        if (device == IntPtr.Zero)
            return false;

        var getFlags = (delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(device, GetCreationFlagsSlot);
        if ((getFlags(device) & 1) != 0 || GetDeviceRemovedReason(device) < 0)
            return false;

        var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(device, QueryInterfaceSlot);

        Guid iid = ID3D10Multithread;
        IntPtr multithread = IntPtr.Zero;
        if (queryInterface(device, &iid, &multithread) < 0 || multithread == IntPtr.Zero)
            return false;

        try
        {
            var setProtected = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(multithread, SetMultithreadProtectedSlot);
            var getProtected = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(multithread, GetMultithreadProtectedSlot);
            setProtected(multithread, 1);
            return getProtected(multithread) != 0;
        }
        finally { Release(multithread); }
    }

    public static int GetDeviceRemovedReason(IntPtr device) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(device, GetDeviceRemovedReasonSlot))(device);

    private static IntPtr Slot(IntPtr instance, int index)
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
    }
}
