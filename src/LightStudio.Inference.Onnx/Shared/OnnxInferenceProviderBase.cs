using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;
using LightStudio.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LightStudio.Inference;

public abstract class OnnxInferenceProviderBase : IInferenceProvider
{
    public string DefaultDeviceId => "cpu";

    public abstract Task<IReadOnlyList<InferenceDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);
    protected abstract SessionOptions CreateOptions(string deviceId, int width, int height, int sequenceLength);

    public virtual InferenceModelCatalog GetModelCatalog(string deviceId, CultureInfo culture) =>
        new(OnnxEmbeddingModels.All.Select(model => model.Describe(culture)).ToArray());

    public virtual bool IsModelSupported(string modelFileName, string deviceId) => true;

    public IReadOnlyList<InferenceModelFile> GetImportFiles(string path) => OnnxEmbeddingModels.GetImportFiles(path);

    public Task<InferenceModelImport> PrepareImportAsync(IReadOnlyList<InferenceModelFile> files, CancellationToken cancellationToken) =>
        OnnxEmbeddingModels.PrepareImportAsync(files, cancellationToken);

    public Task<InferenceModelValidation> ValidateModelAsync(string directory, string modelFileName, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var validation = new OnnxEmbeddingEncoder(directory, modelFileName, "cpu", CreateOptions);
            _ = validation.EncodeText("a photo", cancellationToken);
            _ = validation.EncodeImage(new StillImageThumbnail { Width = 224, Height = 224, Pixels = new byte[224 * 224 * 4] }, cancellationToken);
            return new InferenceModelValidation(validation.EmbeddingDimensions, $"{validation.ModelName} ({modelFileName})");
        }, cancellationToken);

    public IEmbeddingInferenceSession CreateSession(string directory, string modelFileName, string deviceId)
    {
        if (!IsModelSupported(modelFileName, deviceId))
            throw new NotSupportedException(GetModelCatalog(deviceId, CultureInfo.CurrentUICulture).Description);
        return new FallbackSession(directory, modelFileName, deviceId, CreateOptions);
    }

    public IEmbeddingInferenceSession CreateIndependentTextSession(string directory, string modelFileName) =>
        new OnnxEmbeddingEncoder(directory, modelFileName, "cpu", CreateOptions);

    public virtual IInferenceModelDownloads CreateDownloads(string modelDirectory,
        Func<IReadOnlyList<InferenceModelFile>, string, CancellationToken, Task> install) => new OnnxModelDownloads(modelDirectory, install);

    public IAudioSeparationInference AudioSeparation => new OnnxAudioSeparationInference(GetDevicesAsync,
        deviceId => CreateOptions(deviceId, 0, 0, 0), GetAudioDescription);

    protected virtual string? GetAudioDescription(string deviceId, CultureInfo culture) => null;

    protected static string Text(string key, CultureInfo culture) => Resources.ResourceManager.GetString(key, culture) ?? string.Empty;

    private sealed class FallbackSession : IEmbeddingInferenceSession
    {
        private readonly string directory;
        private readonly string modelFileName;
        private readonly Func<string, int, int, int, SessionOptions> createOptions;
        private OnnxEmbeddingEncoder encoder;

        internal FallbackSession(string directory, string modelFileName, string deviceId,
            Func<string, int, int, int, SessionOptions> createOptions)
        {
            this.directory = directory;
            this.modelFileName = modelFileName;
            this.createOptions = createOptions;
            try
            {
                encoder = new OnnxEmbeddingEncoder(directory, modelFileName, deviceId, createOptions);
            }
            catch (Exception exception) when (deviceId != "cpu" && exception is OnnxRuntimeException or InvalidOperationException)
            {
                AppLog.Warning("Inference", "The selected device failed; using CPU inference.", exception);
                encoder = new OnnxEmbeddingEncoder(directory, modelFileName, "cpu", createOptions);
            }
        }

        public int EmbeddingDimensions => encoder.EmbeddingDimensions;
        public string ModelName => encoder.ModelName;
        public string ActiveDeviceId => encoder.ActiveDeviceId;
        public float[] EncodeText(string query, CancellationToken cancellationToken) => Run(current => current.EncodeText(query, cancellationToken), cancellationToken);
        public float[] EncodeImage(StillImageThumbnail image, CancellationToken cancellationToken) => Run(current => current.EncodeImage(image, cancellationToken), cancellationToken);
        public float[][] EncodeImages(IReadOnlyList<StillImageThumbnail> images, CancellationToken cancellationToken) => Run(current => current.EncodeImages(images, cancellationToken), cancellationToken);

        private T Run<T>(Func<OnnxEmbeddingEncoder, T> action, CancellationToken cancellationToken)
        {
            try
            {
                return action(encoder);
            }
            catch (OnnxRuntimeException exception) when (encoder.ActiveDeviceId != "cpu" && !cancellationToken.IsCancellationRequested)
            {
                AppLog.Warning("Inference", "Device inference failed; retrying on CPU.", exception);
                encoder.Dispose();
                encoder = new OnnxEmbeddingEncoder(directory, modelFileName, "cpu", createOptions);
                return action(encoder);
            }
        }

        public void Dispose() => encoder.Dispose();
    }
}