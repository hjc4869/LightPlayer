using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;

namespace LightStudio.Inference;

public sealed class WindowsInferenceProvider : OnnxInferenceProviderBase
{
    private static readonly Lazy<Task> registration = new(() => Task.Run(RegisterProvidersAsync));

    public override async Task<IReadOnlyList<InferenceDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        await registration.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run<IReadOnlyList<InferenceDevice>>(() => Devices().Select(device => device.Device).ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public override InferenceModelCatalog GetModelCatalog(string deviceId, CultureInfo culture) =>
        base.GetModelCatalog(deviceId, culture) with
    {
        Description = Text(deviceId == DefaultDeviceId || deviceId.StartsWith("winml:DmlExecutionProvider:", StringComparison.Ordinal)
            ? "Inference_WindowsInferenceDescription" : "Inference_WindowsProviderWarning", culture),
    };

    private static async Task RegisterProvidersAsync()
    {
        OrtEnv.Instance().DisableTelemetryEvents();
        try
        {
            foreach (var provider in ExecutionProviderCatalog.GetDefault().FindAllProviders())
            {
                try
                {
                    var result = await provider.EnsureReadyAsync();
                    if (result.Status != ExecutionProviderReadyResultState.Success)
                        AppLog.Warning("Inference", $"Windows ML could not prepare {provider.Name}: {result.Status}; {result.DiagnosticText}.", result.ExtendedError);
                    else if (!provider.TryRegister())
                        AppLog.Warning("Inference", $"Windows ML could not register {provider.Name}.");
                }
                catch (Exception exception)
                {
                    AppLog.Warning("Inference", $"Windows ML could not initialize {provider.Name}.", exception);
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Warning("Inference", "Windows ML device discovery could not acquire additional accelerators; built-in devices remain available.", exception);
        }
    }

    protected override string? GetAudioDescription(string deviceId, CultureInfo culture) =>
        deviceId == DefaultDeviceId || deviceId.StartsWith("winml:DmlExecutionProvider:", StringComparison.Ordinal)
            ? null : Text("Inference_WindowsProviderWarning", culture);

    private static IReadOnlyList<(InferenceDevice Device, OrtEpDevice Native)> Devices()
    {
        var result = new List<(InferenceDevice, OrtEpDevice)>();
        foreach (var device in OrtEnv.Instance().GetEpDevices())
        {
            var hardware = device.HardwareDevice;
            var metadata = new Dictionary<string, string>(hardware.Metadata.Entries, StringComparer.OrdinalIgnoreCase);
            var name = metadata.GetValueOrDefault("description") ?? metadata.GetValueOrDefault("name") ?? hardware.Vendor;
            var identity = metadata.GetValueOrDefault("luid") ?? metadata.GetValueOrDefault("uuid")
                ?? metadata.GetValueOrDefault("device_path") ?? metadata.GetValueOrDefault("pci_bus_id")
                ?? result.Count.ToString(CultureInfo.InvariantCulture);
            var id = device.EpName == "CPUExecutionProvider" ? "cpu"
                : $"winml:{device.EpName}:{hardware.VendorId:X4}:{hardware.DeviceId:X4}:{identity}";
            var providerName = device.EpName.Replace("ExecutionProvider", string.Empty, StringComparison.Ordinal);
            result.Add((new InferenceDevice(id, $"{hardware.Type} {providerName}: {name.Trim()}"), device));
        }
        return result;
    }

    protected override SessionOptions CreateOptions(string deviceId, int width, int height, int sequenceLength)
    {
        var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
        try
        {
            if (deviceId == "cpu")
            {
                // ORT 1.27.1's FP16 layer-normalization fusion rejects shared constants in joint models.
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
                return options;
            }
            var selected = Devices().FirstOrDefault(device => device.Device.Id == deviceId).Native
                ?? throw new InvalidOperationException("The selected Windows ML device is unavailable.");
            if (selected.EpName == "DmlExecutionProvider")
            {
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            }
            options.AddFreeDimensionOverrideByName("batch_size", 1);
            if (width > 0)
            {
                options.AddFreeDimensionOverrideByName("text_batch_size", 1);
                options.AddFreeDimensionOverrideByName("image_batch_size", 1);
                options.AddFreeDimensionOverrideByName("sequence_length", sequenceLength);
                options.AddFreeDimensionOverrideByName("num_channels", 3);
                options.AddFreeDimensionOverrideByName("height", height);
                options.AddFreeDimensionOverrideByName("width", width);
            }
            options.AppendExecutionProvider(OrtEnv.Instance(), [selected], new Dictionary<string, string>());
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }
}