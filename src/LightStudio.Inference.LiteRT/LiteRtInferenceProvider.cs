using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.AI.Edge.LiteRT;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;
using LiteRtEnvironment = Google.AI.Edge.LiteRT.Environment;

namespace LightStudio.Inference;

public sealed class LiteRtInferenceProvider : IInferenceProvider
{
    public string DefaultDeviceId => "cpu";

    public Task<IReadOnlyList<InferenceDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<InferenceDevice>>(() =>
        {
            using var environment = LiteRtEnvironment.Create();
            try
            {
                var devices = new List<InferenceDevice> { new("cpu", "CPU") };
                if (environment.AvailableAccelerators.Any(accelerator => accelerator.Equals(Accelerator.Gpu)))
                    devices.Add(new InferenceDevice("gpu", "GPU"));
                cancellationToken.ThrowIfCancellationRequested();
                return devices;
            }
            finally
            {
                environment.Close();
            }
        }, cancellationToken);

    public InferenceModelCatalog GetModelCatalog(string deviceId, CultureInfo culture) => new(
    [
        new InferenceModelDefinition(LiteRtEmbeddingModels.Id, LiteRtEmbeddingModels.Name,
            Resources.ResourceManager.GetString(nameof(Resources.Inference_SiglipBaseDescription), culture) ?? LiteRtEmbeddingModels.Name,
            new Uri($"https://huggingface.co/{LiteRtEmbeddingModels.Id}")),
    ]);

    public bool IsModelSupported(string modelFileName, string deviceId) =>
        modelFileName == LiteRtEmbeddingModels.ImageFileName && deviceId is "cpu" or "gpu";

    public IReadOnlyList<InferenceModelFile> GetImportFiles(string path)
    {
        path = Path.GetFullPath(path);
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        return LiteRtEmbeddingModels.Files.Where(file => File.Exists(Path.Combine(directory, file.Name)))
            .Select(file => new InferenceModelFile(file.Name, _ => Task.FromResult<Stream>(File.OpenRead(Path.Combine(directory, file.Name))), file.Size))
            .ToArray();
    }

    public Task<InferenceModelImport> PrepareImportAsync(IReadOnlyList<InferenceModelFile> files, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = LiteRtEmbeddingModels.Files.Select(required =>
        {
            var file = files.SingleOrDefault(candidate => candidate.Name == required.Name)
                ?? throw new ArgumentException($"The SigLIP2 LiteRT model package requires {required.Name}.", nameof(files));
            return file with { MaximumBytes = Math.Min(file.MaximumBytes, required.Size) };
        }).ToArray();
        return Task.FromResult(new InferenceModelImport(LiteRtEmbeddingModels.ImageFileName, selected, LiteRtEmbeddingModels.Id, LiteRtEmbeddingModels.Name));
    }

    public Task<InferenceModelValidation> ValidateModelAsync(string directory, string modelFileName, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            if (!IsModelSupported(modelFileName, DefaultDeviceId))
                throw new InvalidDataException("Select the SigLIP2 LiteRT model package.");
            foreach (var file in LiteRtEmbeddingModels.Files)
            {
                if (!await file.VerifyAsync(Path.Combine(directory, file.Name), cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException($"The SigLIP2 model integrity check failed for {file.Name}.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var session = new LiteRtEmbeddingEncoder(directory, DefaultDeviceId);
            _ = session.EncodeText("a photo", cancellationToken);
            var size = LiteRtEmbeddingModels.ImageSize;
            _ = session.EncodeImage(new StillImageThumbnail { Width = size, Height = size, Pixels = new byte[size * size * 4] }, cancellationToken);
            return new InferenceModelValidation(session.EmbeddingDimensions, session.ModelName);
        }, cancellationToken);

    public IEmbeddingInferenceSession CreateSession(string directory, string modelFileName, string deviceId)
    {
        if (!IsModelSupported(modelFileName, deviceId))
            throw new NotSupportedException("Select the SigLIP2 LiteRT model package and a supported device.");
        return new LiteRtEmbeddingEncoder(directory, deviceId);
    }

    public IEmbeddingInferenceSession CreateIndependentTextSession(string directory, string modelFileName) =>
        CreateSession(directory, modelFileName, DefaultDeviceId);

    public IInferenceModelDownloads CreateDownloads(string modelDirectory,
        Func<IReadOnlyList<InferenceModelFile>, string, CancellationToken, Task> install) => new LiteRtModelDownloads(modelDirectory, install);
}