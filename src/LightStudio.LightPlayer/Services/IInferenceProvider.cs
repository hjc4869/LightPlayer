using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.LightPlayer.Services;

/// <summary>Supplies model packages, compute devices, and inference capabilities for a platform head.</summary>
public interface IInferenceProvider
{
    string DefaultDeviceId { get; }
    Task<IReadOnlyList<InferenceDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);
    InferenceModelCatalog GetModelCatalog(string deviceId, CultureInfo culture);
    bool IsModelSupported(string modelFileName, string deviceId);
    IReadOnlyList<InferenceModelFile> GetImportFiles(string path);
    Task<InferenceModelImport> PrepareImportAsync(IReadOnlyList<InferenceModelFile> files, CancellationToken cancellationToken);
    async Task<byte[]> HashModelFileAsync(string path, CancellationToken cancellationToken)
    {
        using var source = File.OpenRead(path);
        return await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
    }
    Task<InferenceModelValidation> ValidateModelAsync(string directory, string modelFileName, CancellationToken cancellationToken);
    IEmbeddingInferenceSession CreateSession(string directory, string modelFileName, string deviceId);
    Task<IEmbeddingInferenceSession> CreateSessionAsync(string directory, string modelFileName, string deviceId,
        CancellationToken cancellationToken) => Task.FromResult(CreateSession(directory, modelFileName, deviceId));
    IEmbeddingInferenceSession? CreateIndependentTextSession(string directory, string modelFileName) => null;
    Task<IEmbeddingInferenceSession?> CreateIndependentTextSessionAsync(string directory, string modelFileName,
        CancellationToken cancellationToken) => Task.FromResult(CreateIndependentTextSession(directory, modelFileName));
    IInferenceModelDownloads CreateDownloads(string modelDirectory,
        Func<IReadOnlyList<InferenceModelFile>, string, CancellationToken, Task> install);
    IAudioSeparationInference? AudioSeparation => null;
}

/// <summary>Returns finite, unit-length vectors in the same embedding space for images and text.</summary>
public interface IEmbeddingInferenceSession : IDisposable
{
    int EmbeddingDimensions { get; }
    string ModelName { get; }
    string ActiveDeviceId { get; }
    float[] EncodeText(string query, CancellationToken cancellationToken);
    float[] EncodeImage(StillImageThumbnail image, CancellationToken cancellationToken);
    float[][] EncodeImages(IReadOnlyList<StillImageThumbnail> images, CancellationToken cancellationToken);
    ValueTask<float[]> EncodeTextAsync(string query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(EncodeText(query, cancellationToken));
    ValueTask<float[]> EncodeImageAsync(StillImageThumbnail image, CancellationToken cancellationToken) =>
        ValueTask.FromResult(EncodeImage(image, cancellationToken));
    ValueTask<float[][]> EncodeImagesAsync(IReadOnlyList<StillImageThumbnail> images, CancellationToken cancellationToken) =>
        ValueTask.FromResult(EncodeImages(images, cancellationToken));
}

public interface IInferenceModelDownloads : IDisposable
{
    event EventHandler? Changed;
    IReadOnlyList<InferenceModelDownload> Status { get; }
    Task StartAsync(string modelId, CancellationToken cancellationToken);
    void Pause(string modelId);
    Task CancelAsync(string modelId);
}

public sealed record InferenceModelImport(string ModelFileName, IReadOnlyList<InferenceModelFile> Files,
    string? CatalogId = null, string? Name = null);

public sealed record InferenceModelValidation(int EmbeddingDimensions, string Name);