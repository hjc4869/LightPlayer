#if WINDOWS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.Desktop.Windows;

/// <summary>
/// What Direct3D 11 video decoding can be opened with: which adapter decodes, and the arguments
/// libavutil's D3D11VA device creation reads.
/// </summary>
/// <remarks>
/// Choosing an adapter here also gives up the zero-copy path, because only textures made by the
/// device the renderer draws with can be sampled where they lie. Left automatic, the decoder adopts
/// that device and nothing is copied.
/// </remarks>
internal sealed class Direct3D11HardwareDecoderProfile : IHardwareDecoderProfile
{
    private static readonly IReadOnlyList<HardwareOptionDescriptor> Options =
    [
        new(
            HardwareDecoderApis.DeviceCreate,
            HardwareDecoderParameters.Options,
            "debug",
            "Direct3D debug layer",
            "Creates the decoding device with the Direct3D debug layer, which reports API misuse " +
            "to the debugger. Needs the Windows graphics tools installed, and costs performance.",
            Kind: HardwareSettingKind.Choice,
            Choices: [new HardwareSettingChoice("1", "On")]),
    ];

    public string DeviceType => "d3d11va";

    /// <summary>
    /// The graphics adapters, identified by the index FFmpeg's device string takes. Software
    /// adapters are left out: decoding on one is slower than decoding on the CPU directly.
    /// </summary>
    public IReadOnlyList<HardwareDeviceInfo> GetDevices() => DxgiAdapters.Enumerate();

    public IReadOnlyList<HardwareOptionDescriptor> GetOptions() => Options;

    /// <summary>D3D11VA device creation reads no flags; the argument is there for other types.</summary>
    public IReadOnlyList<HardwareFlagDescriptor> GetFlags() => [];
}

/// <summary>Enumerates the graphics adapters through DXGI, against the vtables as elsewhere.</summary>
internal static unsafe partial class DxgiAdapters
{
    private const int ReleaseSlot = 2;
    private const int EnumAdapters1Slot = 12;
    private const int GetDesc1Slot = 10;

    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint AdapterFlagSoftware = 2;

    private static readonly Guid IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [LibraryImport("dxgi", EntryPoint = "CreateDXGIFactory1")]
    private static partial int CreateDXGIFactory1(Guid* riid, IntPtr* factory);

    public static IReadOnlyList<HardwareDeviceInfo> Enumerate()
    {
        Guid iid = IDXGIFactory1;
        IntPtr factory;
        try
        {
            if (CreateDXGIFactory1(&iid, &factory) < 0 || factory == IntPtr.Zero)
            {
                return [];
            }
        }
        catch (DllNotFoundException)
        {
            return [];
        }

        var adapters = new List<HardwareDeviceInfo>();
        try
        {
            var enumAdapters =
                (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(factory, EnumAdapters1Slot);

            for (uint index = 0; ; index++)
            {
                IntPtr adapter;
                int found = enumAdapters(factory, index, &adapter);
                if (found == DxgiErrorNotFound || found < 0 || adapter == IntPtr.Zero)
                {
                    break;
                }

                try
                {
                    var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDescription*, int>)
                        Slot(adapter, GetDesc1Slot);

                    AdapterDescription description;
                    if (getDesc(adapter, &description) >= 0 &&
                        (description.Flags & AdapterFlagSoftware) == 0)
                    {
                        adapters.Add(new HardwareDeviceInfo(
                            index.ToString(CultureInfo.InvariantCulture),
                            new string(description.Name)));
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return adapters;
    }

    private static void Release(IntPtr instance)
    {
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, ReleaseSlot);
        release(instance);
    }

    private static IntPtr Slot(IntPtr instance, int index)
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
    }

    /// <summary>DXGI_ADAPTER_DESC1, of which only the name and the software flag are read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription
    {
        public fixed char Name[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}
#endif
