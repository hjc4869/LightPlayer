using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LightStudio.Inference;

internal sealed class OnnxAudioSeparationModel : IMdxSeparationModel
{
    private readonly Func<string, SessionOptions> createOptions;
    private readonly object sync = new();
    private readonly string inputName;
    private readonly string outputName;
    private InferenceSession session;
    private int references = 1;
    private string activeDeviceId;

    internal OnnxAudioSeparationModel(string path, string deviceId,
        Func<string, SessionOptions> createOptions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Path = path;
        DeviceId = deviceId;
        this.createOptions = createOptions;
        try
        {
            session = Open(deviceId);
            activeDeviceId = deviceId;
        }
        catch (Exception exception) when (deviceId != "cpu" && !cancellationToken.IsCancellationRequested &&
            exception is OnnxRuntimeException or InvalidOperationException)
        {
            AppLog.Warning("AudioSeparation", "The selected device failed; using CPU inference.", exception);
            session = Open("cpu");
            activeDeviceId = "cpu";
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count != 1)
                throw new InvalidDataException("The separation model must have one input and one output.");
            inputName = session.InputMetadata.Keys.Single();
            outputName = session.OutputMetadata.Keys.Single();
            var metadata = session.InputMetadata[inputName];
            var outputMetadata = session.OutputMetadata[outputName];
            if (!metadata.IsTensor || !outputMetadata.IsTensor || metadata.ElementType != typeof(float) ||
                outputMetadata.ElementType != typeof(float))
                throw new InvalidDataException("The separation model must have float32 tensor input and output.");
            var dimensions = metadata.Dimensions;
            var outputDimensions = outputMetadata.Dimensions;
            if (dimensions.Length != 4 || outputDimensions.Length != 4 || dimensions[0] == 0 || dimensions[0] > 1 ||
                dimensions[1] != 4 || dimensions[2] is < 1 or > 16385 || dimensions[3] == 0)
                throw new InvalidDataException("Unsupported UVR MDX tensor layout.");
            Profile = OnnxAudioModels.GetProfile(path, dimensions[2]);
            var frames = dimensions[3] > 0 ? dimensions[3] : outputDimensions[3] > 0 ? outputDimensions[3] : Profile.Frames;
            if (frames is < 2 or > 1024 || 1024 * (frames - 1) <= Profile.FftSize)
                throw new InvalidDataException("Unsupported UVR MDX window length.");
            int[] expectedDimensions = [1, 4, Profile.Frequencies, frames];
            for (var index = 0; index < expectedDimensions.Length; index++)
                if (outputDimensions[index] >= 0 && outputDimensions[index] != expectedDimensions[index])
                    throw new InvalidDataException("The separation model output must match its input spectrogram dimensions.");
            Transform = new ConvTdfNet(Profile.Frequencies, frames, Profile.FftSize);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal string Path { get; }
    internal string DeviceId { get; }
    public string ActiveDeviceId => Volatile.Read(ref activeDeviceId);
    public MdxProfile Profile { get; }
    public ConvTdfNet Transform { get; }

    public void AddReference() => Interlocked.Increment(ref references);

    public ValueTask<float[]> ExecuteAsync(float[] spectrum, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Execute(spectrum, cancellationToken));

    internal float[] Execute(float[] spectrum, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Run(spectrum, cancellationToken);
            }
            catch (OnnxRuntimeException exception) when (ActiveDeviceId != "cpu" && !cancellationToken.IsCancellationRequested)
            {
                AppLog.Warning("AudioSeparation", "Device inference failed; retrying on CPU.", exception);
                var replacement = Open("cpu");
                session.Dispose();
                session = replacement;
                Volatile.Write(ref activeDeviceId, "cpu");
                return Run(spectrum, cancellationToken);
            }
        }
    }

    private float[] Run(float[] spectrum, CancellationToken cancellationToken)
    {
        using var options = new RunOptions();
        using var registration = cancellationToken.Register(() => options.Terminate = true);
        using var tensor = OrtValue.CreateTensorValueFromMemory(spectrum, [1, 4, Transform.Frequencies, Transform.Frames]);
        try
        {
            using var results = session.Run(options, new Dictionary<string, OrtValue> { [inputName] = tensor }, [outputName]);
            cancellationToken.ThrowIfCancellationRequested();
            if (!results[0].GetTensorTypeAndShape().Shape.AsSpan().SequenceEqual([1, 4, Transform.Frequencies, Transform.Frames]))
                throw new InvalidDataException("The separation model returned an unexpected spectrogram shape.");
            return results[0].GetTensorDataAsSpan<float>().ToArray();
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private InferenceSession Open(string deviceId)
    {
        using var options = createOptions(deviceId);
        options.AddFreeDimensionOverrideByName("batch_size", 1);
        options.AddFreeDimensionOverrideByName("batch", 1);
        return new InferenceSession(File.ReadAllBytes(Path), options);
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref references) == 0)
            session.Dispose();
    }
}