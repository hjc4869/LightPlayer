using System;
using System.Runtime.InteropServices;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>
/// Minimal P/Invoke surface for the macOS audio stack used by the CoreAudio playback backend.
/// Three frameworks are bound:
/// <list type="bullet">
///   <item><b>CoreAudio</b>: the hardware abstraction layer (<c>AudioObject*</c>) used to
///     enumerate output devices, resolve the default device, read the mixer sample rate, acquire
///     hog (exclusive) mode, and observe device hot-plug / default-device changes.</item>
///   <item><b>AudioToolbox</b>: the Audio Unit host API (<c>AudioComponent*</c>, <c>AudioUnit*</c>,
///     <c>AudioOutputUnit*</c>) used to render PCM through an AUHAL output unit.</item>
///   <item><b>CoreFoundation</b>: <c>CFString</c> helpers for reading the device name and UID
///     returned by the HAL.</item>
/// </list>
/// Only blittable types cross the boundary (no string marshalling, no delegate marshalling) so the
/// bindings stay NativeAOT-clean; callbacks are declared with <see cref="UnmanagedCallersOnlyAttribute"/>
/// and passed as raw function pointers. The struct field offsets assume the 64-bit ABI, which holds
/// for every macOS target the app ships (<c>osx-arm64</c> and <c>osx-x64</c>).
/// </summary>
internal static unsafe class CoreAudioNative
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>The well-known object id of the global system audio object.</summary>
    public const uint SystemObject = 1;

    public const int NoErr = 0;

    // AudioObjectPropertyScope / element.
    public const uint ScopeGlobal = 0x676C6F62; // 'glob'
    public const uint ScopeOutput = 0x6F757470; // 'outp'
    public const uint ElementMain = 0;          // kAudioObjectPropertyElementMain

    // System-object selectors.
    public const uint HardwareDevices = 0x64657623;             // 'dev#'
    public const uint HardwareDefaultOutputDevice = 0x644F7574; // 'dOut'
    public const uint HardwareRunLoop = 0x726E6C70;             // 'rnlp'

    // Device selectors.
    public const uint DeviceUID = 0x75696420;             // 'uid '
    public const uint ObjectName = 0x6C6E616D;            // 'lnam' (kAudioObjectPropertyName)
    public const uint DeviceStreams = 0x73746D23;         // 'stm#'
    public const uint DeviceNominalSampleRate = 0x6E737274; // 'nsrt'
    public const uint DeviceHogMode = 0x6F696E6B;         // 'oink'
    public const uint DeviceBufferFrameSize = 0x6673697A; // 'fsiz'
    public const uint DeviceBufferFrameSizeRange = 0x66737A23; // 'fsz#'
    public const uint DeviceSafetyOffset = 0x73616674;    // 'saft'
    public const uint DeviceLatency = 0x6C746E63;         // 'ltnc' (also kAudioStreamPropertyLatency)

    // Audio Unit component identity (AUHAL output unit).
    public const uint AudioUnitType_Output = 0x61756F75;     // 'auou'
    public const uint AudioUnitSubType_HALOutput = 0x6168616C; // 'ahal'
    public const uint AudioUnitManufacturer_Apple = 0x6170706C; // 'appl'

    // Audio Unit scopes.
    public const uint AudioUnitScope_Global = 0;
    public const uint AudioUnitScope_Input = 1;
    public const uint AudioUnitScope_Output = 2;

    // Audio Unit property ids.
    public const uint AudioUnitProperty_StreamFormat = 8;
    public const uint AudioUnitProperty_MaximumFramesPerSlice = 14;
    public const uint AudioUnitProperty_SetRenderCallback = 23;
    public const uint AudioOutputUnitProperty_CurrentDevice = 2000;

    // AudioStreamBasicDescription format ids / flags.
    public const uint FormatLinearPCM = 0x6C70636D; // 'lpcm'
    public const uint FormatFlagIsSignedInteger = 1u << 2;
    public const uint FormatFlagIsPacked = 1u << 3;

    // AudioUnitRenderActionFlags.
    public const uint RenderActionOutputIsSilence = 1u << 4;

    public const uint AudioTimeStampHostTimeValid = 1u << 1;

    // CFStringEncoding.
    public const uint CFStringEncodingUTF8 = 0x08000100;

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioObjectPropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;

        public AudioObjectPropertyAddress(uint selector, uint scope, uint element)
        {
            Selector = selector;
            Scope = scope;
            Element = element;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioComponentDescription
    {
        public uint Type;
        public uint SubType;
        public uint Manufacturer;
        public uint Flags;
        public uint FlagsMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioValueRange
    {
        public double Minimum;
        public double Maximum;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBuffer
    {
        public uint NumberChannels;
        public uint DataByteSize;
        public IntPtr Data;
    }

    // A single-buffer AudioBufferList. Interleaved PCM always renders through one buffer, so the
    // trailing variable-length array of the C struct collapses to one element here; the pointer
    // member forces 8-byte alignment, placing Buffers at offset 8 exactly as the C ABI requires.
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBufferList
    {
        public uint NumberBuffers;
        public AudioBuffer Buffers;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AURenderCallbackStruct
    {
        public IntPtr InputProc;
        public IntPtr InputProcRefCon;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct AudioTimeStamp
    {
        [FieldOffset(8)]
        public ulong HostTime;

        [FieldOffset(56)]
        public uint Flags;
    }

    // CoreAudio HAL.
    [DllImport(CoreAudio)]
    public static extern ulong AudioGetCurrentHostTime();

    [DllImport(CoreAudio)]
    public static extern double AudioGetHostClockFrequency();

    [DllImport(CoreAudio)]
    public static extern int AudioObjectGetPropertyDataSize(
        uint objectId,
        AudioObjectPropertyAddress* address,
        uint qualifierDataSize,
        void* qualifierData,
        uint* dataSize);

    [DllImport(CoreAudio)]
    public static extern int AudioObjectGetPropertyData(
        uint objectId,
        AudioObjectPropertyAddress* address,
        uint qualifierDataSize,
        void* qualifierData,
        uint* dataSize,
        void* data);

    [DllImport(CoreAudio)]
    public static extern int AudioObjectSetPropertyData(
        uint objectId,
        AudioObjectPropertyAddress* address,
        uint qualifierDataSize,
        void* qualifierData,
        uint dataSize,
        void* data);

    [DllImport(CoreAudio)]
    public static extern byte AudioObjectHasProperty(uint objectId, AudioObjectPropertyAddress* address);

    [DllImport(CoreAudio)]
    public static extern int AudioObjectAddPropertyListener(
        uint objectId,
        AudioObjectPropertyAddress* address,
        IntPtr listener,
        IntPtr clientData);

    [DllImport(CoreAudio)]
    public static extern int AudioObjectRemovePropertyListener(
        uint objectId,
        AudioObjectPropertyAddress* address,
        IntPtr listener,
        IntPtr clientData);

    // AudioToolbox (Audio Unit host).
    [DllImport(AudioToolbox)]
    public static extern IntPtr AudioComponentFindNext(IntPtr component, AudioComponentDescription* description);

    [DllImport(AudioToolbox)]
    public static extern int AudioComponentInstanceNew(IntPtr component, IntPtr* instance);

    [DllImport(AudioToolbox)]
    public static extern int AudioComponentInstanceDispose(IntPtr instance);

    [DllImport(AudioToolbox)]
    public static extern int AudioUnitInitialize(IntPtr unit);

    [DllImport(AudioToolbox)]
    public static extern int AudioUnitUninitialize(IntPtr unit);

    [DllImport(AudioToolbox)]
    public static extern int AudioUnitReset(IntPtr unit, uint scope, uint element);

    [DllImport(AudioToolbox)]
    public static extern int AudioUnitSetProperty(
        IntPtr unit,
        uint propertyId,
        uint scope,
        uint element,
        void* data,
        uint dataSize);

    [DllImport(AudioToolbox)]
    public static extern int AudioOutputUnitStart(IntPtr unit);

    [DllImport(AudioToolbox)]
    public static extern int AudioOutputUnitStop(IntPtr unit);

    // CoreFoundation.
    [DllImport(CoreFoundation)]
    public static extern nint CFStringGetLength(IntPtr theString);

    [DllImport(CoreFoundation)]
    public static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

    [DllImport(CoreFoundation)]
    public static extern byte CFStringGetCString(IntPtr theString, byte* buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    public static extern void CFRelease(IntPtr cf);

    public static AudioObjectPropertyAddress Address(uint selector, uint scope = ScopeGlobal) =>
        new(selector, scope, ElementMain);

    /// <summary>Reads a scalar <see cref="uint"/> HAL property (device ids, counts, etc.).</summary>
    public static bool TryGetUInt32(uint objectId, uint selector, uint scope, out uint value)
    {
        var address = Address(selector, scope);
        var size = (uint)sizeof(uint);
        uint result;
        if (AudioObjectGetPropertyData(objectId, &address, 0, null, &size, &result) != NoErr || size < sizeof(uint))
        {
            value = 0;
            return false;
        }

        value = result;
        return true;
    }

    /// <summary>Reads a scalar <see cref="double"/> HAL property (nominal sample rate, etc.).</summary>
    public static bool TryGetFloat64(uint objectId, uint selector, uint scope, out double value)
    {
        var address = Address(selector, scope);
        var size = (uint)sizeof(double);
        double result;
        if (AudioObjectGetPropertyData(objectId, &address, 0, null, &size, &result) != NoErr || size < sizeof(double))
        {
            value = 0;
            return false;
        }

        value = result;
        return true;
    }

    /// <summary>Reads a HAL array of <see cref="uint"/> values (e.g. the device id list).</summary>
    public static uint[] GetUInt32Array(uint objectId, uint selector, uint scope)
    {
        var address = Address(selector, scope);
        uint byteSize;
        if (AudioObjectGetPropertyDataSize(objectId, &address, 0, null, &byteSize) != NoErr || byteSize == 0)
        {
            return [];
        }

        var count = (int)(byteSize / sizeof(uint));
        var result = new uint[count];
        fixed (uint* pointer = result)
        {
            if (AudioObjectGetPropertyData(objectId, &address, 0, null, &byteSize, pointer) != NoErr)
            {
                return [];
            }
        }

        return result;
    }

    /// <summary>Reads a HAL <c>CFStringRef</c> property (device name / UID) as a managed string.</summary>
    public static string? GetStringProperty(uint objectId, uint selector, uint scope)
    {
        var address = Address(selector, scope);
        var size = (uint)IntPtr.Size;
        IntPtr cfString;
        if (AudioObjectGetPropertyData(objectId, &address, 0, null, &size, &cfString) != NoErr ||
            cfString == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return CFStringToString(cfString);
        }
        finally
        {
            CFRelease(cfString);
        }
    }

    /// <summary>Returns whether the device exposes at least one output stream.</summary>
    public static bool DeviceHasOutputStreams(uint deviceId)
    {
        var address = Address(DeviceStreams, ScopeOutput);
        uint byteSize;
        return AudioObjectGetPropertyDataSize(deviceId, &address, 0, null, &byteSize) == NoErr && byteSize > 0;
    }

    /// <summary>
    /// Returns the device's first output stream. A stream reports its own latency, which adds to the
    /// device's, so both are needed to know how far ahead of the speaker the render callback runs.
    /// </summary>
    public static bool TryGetFirstOutputStream(uint deviceId, out uint streamId)
    {
        streamId = 0;
        var address = Address(DeviceStreams, ScopeOutput);
        uint byteSize;
        if (AudioObjectGetPropertyDataSize(deviceId, &address, 0, null, &byteSize) != NoErr ||
            byteSize < sizeof(uint))
        {
            return false;
        }

        var count = Math.Min(byteSize / sizeof(uint), 64u);
        var streams = stackalloc uint[(int)count];
        var size = count * sizeof(uint);
        if (AudioObjectGetPropertyData(deviceId, &address, 0, null, &size, streams) != NoErr ||
            size < sizeof(uint))
        {
            return false;
        }

        streamId = streams[0];
        return streamId != 0;
    }

    private static string? CFStringToString(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var length = CFStringGetLength(handle);
        if (length == 0)
        {
            return string.Empty;
        }

        var maxSize = CFStringGetMaximumSizeForEncoding(length, CFStringEncodingUTF8);
        if (maxSize <= 0)
        {
            return null;
        }

        var buffer = new byte[(int)maxSize + 1];
        fixed (byte* pointer = buffer)
        {
            if (CFStringGetCString(handle, pointer, buffer.Length, CFStringEncodingUTF8) == 0)
            {
                return null;
            }

            return Marshal.PtrToStringUTF8((IntPtr)pointer);
        }
    }
}
