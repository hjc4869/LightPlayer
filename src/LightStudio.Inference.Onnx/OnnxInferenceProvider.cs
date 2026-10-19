using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LightStudio.Inference;

public sealed class OnnxInferenceProvider : OnnxInferenceProviderBase
{
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
            else if (deviceId != "cpu")
            {
                var selected = Devices().FirstOrDefault(device => device.Device.Id == deviceId).Native
                    ?? throw new InvalidOperationException("The selected inference device is unavailable.");
                options.AppendExecutionProvider(OrtEnv.Instance(), [selected], new Dictionary<string, string>
                {
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
        return result;
    }
}