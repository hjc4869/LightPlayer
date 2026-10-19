using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using LightStudio.NativeBindings.Types;
using LightStudio.FfmpegShim.Interop;
using LightStudio.Logging;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Decoding;

internal static unsafe partial class D3D11HardwareContext
{
#if WINDOWS
    private static readonly delegate* unmanaged[Cdecl]<void*, void> LockFunction = &Lock;
    private static readonly Dictionary<IntPtr, DeviceLock> deviceLocks = [];

    [ThreadStatic]
    private static int lockDepth;

    public static AVBufferRef* Adopt(IntPtr device, uint bindFlags, uint miscFlags = 0)
    {
        if (!Direct3DInterop.TryEnableMultithreadProtection(device))
        {
            AppLog.Warning("FFmpeg.Decode", "D3D11 decoding requires a live device with multithread protection.");
            return null;
        }

        AVBufferRef* reference = av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (reference == null)
            return null;
        var context = (AVHWDeviceContext*)reference->data;
        var hardware = (AVD3D11VADeviceContext*)context->hwctx;
        Direct3DInterop.AddRef(device);
        hardware->device = (ID3D11Device*)device;
        hardware->BindFlags = bindFlags;
        hardware->MiscFlags = miscFlags;
        try
        {
            context->user_opaque = (void*)AcquireLock(device);
            context->free = &Free;
            // FFmpeg transactions share a recursive gate; D3D11 protects individual context calls.
            hardware->@lock = LockFunction;
            hardware->unlock = &Unlock;
            hardware->lock_ctx = context->user_opaque;
            int result = av_hwdevice_ctx_init(reference);
            if (result < 0)
            {
                AppLog.Warning("FFmpeg.Decode", $"Could not initialize a shared D3D11 context: {FfmpegError.Describe(result)}.");
                return null;
            }
            AVBufferRef* initialized = reference;
            reference = null;
            return initialized;
        }
        finally
        {
            if (reference != null)
                av_buffer_unref(&reference);
        }
    }

    public static int CreateDevice(AVBufferRef** device, AVHWDeviceType type, string name, AVDictionary* options, int flags)
    {
        int result = av_hwdevice_ctx_create(device, type, name, options, flags);
        if (result < 0 || type != AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)
            return result;

        AVBufferRef* original = *device;
        *device = null;
        try
        {
            var hardware = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)original->data)->hwctx;
            *device = Adopt((IntPtr)hardware->device, hardware->BindFlags, hardware->MiscFlags);
            return *device == null ? AVERROR_EXTERNAL : 0;
        }
        finally { av_buffer_unref(&original); }
    }

    public static int TransferFrame(AVFrame* destination, AVFrame* source)
    {
        if (source->format != (int)AVPixelFormat.AV_PIX_FMT_D3D11 || source->hw_frames_ctx == null)
            return av_hwframe_transfer_data(destination, source, 0);

        var frames = (AVHWFramesContext*)source->hw_frames_ctx->data;
        var hardware = (AVD3D11VADeviceContext*)frames->device_ctx->hwctx;
        int removedReason = Direct3DInterop.GetDeviceRemovedReason((IntPtr)hardware->device);
        int depth = lockDepth;
        int result;
        try
        {
            result = removedReason < 0 ? AVERROR_EXTERNAL : av_hwframe_transfer_data(destination, source, 0);
        }
        finally
        {
            // FFmpeg's staging-allocation failure path can leave its recursive device lock held.
            if ((nint)hardware->@lock == (nint)LockFunction)
                while (lockDepth > depth)
                    UnlockCore(hardware->lock_ctx);
        }
        if (result < 0)
        {
            removedReason = Direct3DInterop.GetDeviceRemovedReason((IntPtr)hardware->device);
            AppLog.Warning("FFmpeg.Decode", $"D3D11 frame download failed: {FfmpegError.Describe(result)}; " +
                $"GetDeviceRemovedReason=0x{removedReason:X8}.");
        }
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Lock(void* context)
    {
        Monitor.Enter(GCHandle.FromIntPtr((IntPtr)context).Target);
        lockDepth++;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Unlock(void* context) => UnlockCore(context);

    private static void UnlockCore(void* context)
    {
        lockDepth--;
        Monitor.Exit(GCHandle.FromIntPtr((IntPtr)context).Target);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Free(AVHWDeviceContext* context)
    {
        GCHandle owner = GCHandle.FromIntPtr((IntPtr)context->user_opaque);
        var gate = (DeviceLock)owner.Target;
        owner.Free();
        lock (deviceLocks)
            if (--gate.References == 0)
                deviceLocks.Remove(gate.Device);
    }

    private static IntPtr AcquireLock(IntPtr device)
    {
        lock (deviceLocks)
        {
            if (!deviceLocks.TryGetValue(device, out DeviceLock gate))
                gate = new DeviceLock(device);
            GCHandle owner = GCHandle.Alloc(gate);
            try
            {
                deviceLocks[device] = gate;
                gate.References++;
                return GCHandle.ToIntPtr(owner);
            }
            catch
            {
                owner.Free();
                throw;
            }
        }
    }

    private sealed class DeviceLock(IntPtr device)
    {
        public IntPtr Device { get; } = device;
        public int References;
    }
#else
    public static AVBufferRef* Adopt(IntPtr device, uint bindFlags, uint miscFlags = 0) =>
        throw new PlatformNotSupportedException("D3D11 requires a Windows native binding profile.");

    public static int CreateDevice(AVBufferRef** device, AVHWDeviceType type, string name, AVDictionary* options, int flags) =>
        av_hwdevice_ctx_create(device, type, name, options, flags);

    public static int TransferFrame(AVFrame* destination, AVFrame* source) =>
        av_hwframe_transfer_data(destination, source, 0);
#endif
}