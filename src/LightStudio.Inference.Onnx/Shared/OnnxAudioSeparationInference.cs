using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using Microsoft.ML.OnnxRuntime;

namespace LightStudio.Inference;

internal sealed class OnnxAudioSeparationInference(
    Func<CancellationToken, Task<IReadOnlyList<InferenceDevice>>> getDevices,
    Func<string, SessionOptions> createOptions,
    Func<string, CultureInfo, string?> getDescription) : IAudioSeparationInference
{
    private readonly object sync = new();
    private OnnxAudioSeparationModel? model;

    public Task<IReadOnlyList<InferenceDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) => getDevices(cancellationToken);

    public InferenceModelCatalog GetModelCatalog(string deviceId, CultureInfo culture) => new(
        OnnxAudioModels.All.Select(model => new InferenceModelDefinition(model.Id, Path.GetFileNameWithoutExtension(model.ModelPath),
            model.ModelPath, new Uri($"https://huggingface.co/{model.RepositoryId}/blob/main/{model.ModelPath}"))).ToArray(),
        getDescription(deviceId, culture));

    public IInferenceModelDownloads CreateDownloads(string modelDirectory,
        Func<IReadOnlyList<InferenceModelFile>, string, CancellationToken, Task> install) =>
        new OnnxModelDownloads(modelDirectory, install, OnnxAudioModels.All);

    public Task ValidateModelAsync(string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var validation = new OnnxAudioSeparationModel(path, "cpu", createOptions, cancellationToken);
        using var session = new OnnxAudioSeparationSession(validation, false);
        session.Append(new float[8820]);
        session.CompleteInput();
        if (session.Read(new float[2048], cancellationToken) == 0)
            throw new InvalidDataException("The separation model did not return audio.");
    }, cancellationToken);

    public IAudioSeparationSession CreateSession(string path, string deviceId, bool residual, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (model is not null && (model.Path != path || model.DeviceId != deviceId))
            {
                model.Dispose();
                model = null;
            }
            model ??= new OnnxAudioSeparationModel(path, deviceId, createOptions, cancellationToken);
            return new OnnxAudioSeparationSession(model, residual);
        }
    }

    public void ReleaseCachedSession()
    {
        lock (sync)
        {
            model?.Dispose();
            model = null;
        }
    }
}