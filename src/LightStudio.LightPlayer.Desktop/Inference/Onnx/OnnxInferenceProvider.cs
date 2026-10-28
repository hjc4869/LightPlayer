using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LightStudio.Inference;

public sealed partial class OnnxInferenceProvider : OnnxInferenceProviderBase
{
    [LibraryImport("webgpu_dawn", EntryPoint = "_ZN4dawn6native8GetProcsEv")]
    private static partial IntPtr GetDawnProcTable();

    public override Task<IReadOnlyList<InferenceDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<InferenceDevice>>(() =>
        {
            var environment = OrtEnv.Instance();
            environment.DisableTelemetryEvents();
            try
            {
                return Devices().Select(device => device.Device).ToArray();
            }
            catch (OnnxRuntimeException exception)
            {
                AppLog.Warning("Inference", "Device enumeration failed; CPU inference is available.", exception);
                return [new InferenceDevice("cpu", "CPU")];
            }
#if ANDROID
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                AppLog.Warning("Inference", "Vulkan adapter discovery failed; CPU inference is available.", exception);
                return [new InferenceDevice("cpu", "CPU")];
            }
#endif
        }, cancellationToken);

    protected override SessionOptions CreateOptions(string deviceId, int width, int height, int sequenceLength)
    {
        var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
        try
        {
            if ((OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()) && deviceId == "coreml")
            {
                options.AppendExecutionProvider("CoreML", new Dictionary<string, string>
                {
                    ["ModelFormat"] = "MLProgram",
                    ["MLComputeUnits"] = "ALL",
                });
            }
#if ANDROID
            else if (deviceId == "gpu")
            {
                options.AppendExecutionProvider("WebGPU", new Dictionary<string, string>
                {
                    ["dawnProcTable"] = GetDawnProcTable().ToInt64().ToString(CultureInfo.InvariantCulture),
                    ["dawnBackendType"] = "Vulkan",
                    ["preferredLayout"] = "NHWC",
                    ["validationMode"] = "basic",
                });
            }
#endif
            else if (deviceId != "cpu")
            {
                var selected = Devices().FirstOrDefault(device => device.Device.Id == deviceId).Native
                    ?? throw new InvalidOperationException("The selected inference device is unavailable.");
                options.AppendExecutionProvider(OrtEnv.Instance(), [selected], new Dictionary<string, string>
                {
                    ["dawnProcTable"] = GetDawnProcTable().ToInt64().ToString(CultureInfo.InvariantCulture),
                    ["preferredLayout"] = "NHWC",
                    ["validationMode"] = "basic",
                });
            }
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<(InferenceDevice Device, OrtEpDevice? Native)> Devices()
    {
        var result = new List<(InferenceDevice, OrtEpDevice?)> { (new("cpu", "CPU"), null) };
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
        {
            if (OrtEnv.Instance().GetAvailableProviders().Contains("CoreMLExecutionProvider"))
                result.Add((new InferenceDevice("coreml", "Core ML"), null));
            return result;
        }
#if ANDROID
        if (OrtEnv.Instance().GetAvailableProviders().Contains("WebGpuExecutionProvider"))
        {
            ProbeAndroidWebGpuAdapter();
            result.Add((new InferenceDevice("gpu", "GPU"), null));
        }
#else
        foreach (var device in OrtEnv.Instance().GetEpDevices().Where(device => device.EpName == "WebGpuExecutionProvider"))
        {
            var hardware = device.HardwareDevice;
            var metadata = hardware.Metadata.Entries;
            var name = metadata.GetValueOrDefault("description") ?? metadata.GetValueOrDefault("name")
                ?? $"{hardware.Vendor} GPU {hardware.VendorId:X4}:{hardware.DeviceId:X4}";
            var identity = metadata.GetValueOrDefault("luid") ?? metadata.GetValueOrDefault("uuid")
                ?? metadata.GetValueOrDefault("pci_bus_id") ?? metadata.GetValueOrDefault("device_path")
                ?? result.Count.ToString(CultureInfo.InvariantCulture);
            result.Add((new InferenceDevice($"webgpu:{hardware.VendorId:X4}:{hardware.DeviceId:X4}:{identity}", name.Trim()), device));
        }
#endif
        return result;
    }

#if ANDROID
    private static unsafe void ProbeAndroidWebGpuAdapter()
    {
        var instance = WebGpuCreateInstance(IntPtr.Zero);
        if (instance == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create a WebGPU instance for Vulkan adapter discovery.");
        var request = new AndroidAdapterRequest();
        var requestHandle = GCHandle.Alloc(request);
        try
        {
            var adapterOptions = new WebGpuAdapterOptions { BackendType = 6 };
            var callbackInfo = new WebGpuAdapterCallbackInfo
            {
                Mode = 1,
                Callback = &AndroidAdapterReceived,
                Userdata1 = GCHandle.ToIntPtr(requestHandle),
            };
            var waitInfo = new WebGpuFutureWaitInfo
            {
                Future = WebGpuRequestAdapter(instance, in adapterOptions, callbackInfo),
            };
            var waitStatus = WebGpuInstanceWaitAny(instance, 1, ref waitInfo, 0);
            if (waitStatus != 1 || waitInfo.Completed == 0)
                throw new InvalidOperationException($"Vulkan adapter discovery did not complete (wait status {waitStatus}).");
            if (request.Exception is not null)
                throw new InvalidOperationException("Failed to read the Vulkan adapter discovery result.", request.Exception);
            if (request.Status != 1 || request.Adapter == IntPtr.Zero)
                throw new InvalidOperationException($"Vulkan adapter discovery failed (status {request.Status}): {request.Message}");
        }
        finally
        {
            WebGpuInstanceRelease(instance);
            if (request.Adapter != IntPtr.Zero)
                WebGpuAdapterRelease(request.Adapter);
            requestHandle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void AndroidAdapterReceived(uint status, IntPtr adapter, WebGpuStringView message, IntPtr userdata1, IntPtr userdata2)
    {
        var request = (AndroidAdapterRequest)GCHandle.FromIntPtr(userdata1).Target!;
        request.Status = status;
        request.Adapter = adapter;
        try
        {
            if (message.Data != IntPtr.Zero)
                request.Message = message.Length == nuint.MaxValue
                    ? Marshal.PtrToStringUTF8(message.Data)
                    : Marshal.PtrToStringUTF8(message.Data, checked((int)message.Length));
        }
        catch (Exception exception)
        {
            request.Exception = exception;
        }
    }

    private sealed class AndroidAdapterRequest
    {
        public uint Status;
        public IntPtr Adapter;
        public string? Message;
        public Exception? Exception;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebGpuStringView
    {
        public IntPtr Data;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebGpuAdapterOptions
    {
        public IntPtr NextInChain;
        public uint FeatureLevel;
        public uint PowerPreference;
        public uint ForceFallbackAdapter;
        public uint BackendType;
        public IntPtr CompatibleSurface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WebGpuAdapterCallbackInfo
    {
        public IntPtr NextInChain;
        public uint Mode;
        public delegate* unmanaged[Cdecl]<uint, IntPtr, WebGpuStringView, IntPtr, IntPtr, void> Callback;
        public IntPtr Userdata1;
        public IntPtr Userdata2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebGpuFuture
    {
        public ulong Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WebGpuFutureWaitInfo
    {
        public WebGpuFuture Future;
        public uint Completed;
    }

    [LibraryImport("webgpu_dawn", EntryPoint = "wgpuCreateInstance")]
    private static partial IntPtr WebGpuCreateInstance(IntPtr descriptor);

    [LibraryImport("webgpu_dawn", EntryPoint = "wgpuInstanceRequestAdapter")]
    private static partial WebGpuFuture WebGpuRequestAdapter(IntPtr instance, in WebGpuAdapterOptions options, WebGpuAdapterCallbackInfo callbackInfo);

    [LibraryImport("webgpu_dawn", EntryPoint = "wgpuInstanceWaitAny")]
    private static partial uint WebGpuInstanceWaitAny(IntPtr instance, nuint futureCount, ref WebGpuFutureWaitInfo futures, ulong timeoutNanoseconds);

    [LibraryImport("webgpu_dawn", EntryPoint = "wgpuInstanceRelease")]
    private static partial void WebGpuInstanceRelease(IntPtr instance);

    [LibraryImport("webgpu_dawn", EntryPoint = "wgpuAdapterRelease")]
    private static partial void WebGpuAdapterRelease(IntPtr adapter);
#endif
}