using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;

namespace LightStudio.Inference;

internal sealed record OnnxEmbeddingModel(string RepositoryId, string Name, string ImageProcessorType, string SourceModelId,
    string DescriptionKey, string ModelPath = "onnx/model_fp16.onnx")
{
    internal string Id => ModelPath == "onnx/model_fp16.onnx" ? RepositoryId : RepositoryId + ":" + Path.GetFileName(ModelPath);
    internal InferenceModelDefinition Describe(CultureInfo culture) => new(Id, Name,
        Resources.ResourceManager.GetString(DescriptionKey, culture) ?? Name,
        new Uri($"https://huggingface.co/{RepositoryId}/tree/main"));
}

internal static class OnnxEmbeddingModels
{
    internal static IReadOnlyList<OnnxEmbeddingModel> All { get; } =
    [
        new("onnx-community/siglip2-base-patch16-224-ONNX", "SigLIP2 Base", "SiglipImageProcessor", "google/siglip2-base-patch16-224", "Inference_SiglipBaseDescription"),
        new("onnx-community/siglip2-large-patch16-256-ONNX", "SigLIP2 Large", "SiglipImageProcessor", "google/siglip2-large-patch16-256", "Inference_SiglipLargeDescription"),
        new("onnx-community/siglip2-so400m-patch14-384-ONNX", "SigLIP2 SO400M", "SiglipImageProcessor", "google/siglip2-so400m-patch14-384", "Inference_SiglipSo400mDescription"),
    ];

    internal static IReadOnlyList<string> CompanionNames(string? imageProcessorType) => imageProcessorType switch
    {
        "CLIPImageProcessor" or "CLIPFeatureExtractor" => ["tokenizer.json", "vocab.json", "merges.txt", "preprocessor_config.json"],
        "SiglipImageProcessor" => ["tokenizer.model", "tokenizer_config.json", "preprocessor_config.json"],
        _ => throw new InvalidDataException("Select a CLIP or fixed-resolution SigLIP2 image processor."),
    };

    internal static bool IsExternalDataFile(string modelName, string name) =>
        name.StartsWith(modelName + "_data", StringComparison.Ordinal) || name == modelName + ".data";

    internal static IReadOnlyList<InferenceModelFile> GetImportFiles(string path)
    {
        path = Path.GetFullPath(path);
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        var paths = Directory.EnumerateFiles(directory).ToList();
        if (Directory.Exists(path) && Directory.Exists(Path.Combine(directory, "onnx")))
            paths.AddRange(Directory.EnumerateFiles(Path.Combine(directory, "onnx")));
        if (!Directory.Exists(path))
        {
            paths.RemoveAll(candidate => candidate.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) && candidate != path);
            var parent = Path.GetDirectoryName(directory);
            if (!File.Exists(Path.Combine(directory, "preprocessor_config.json")) && parent is not null)
                paths.AddRange(Directory.EnumerateFiles(parent).Where(candidate => !candidate.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)));
        }
        return paths.Select(file => new InferenceModelFile(Path.GetFileName(file),
            _ => Task.FromResult<Stream>(File.OpenRead(file)))).ToArray();
    }

    internal static async Task<InferenceModelImport> PrepareImportAsync(IReadOnlyList<InferenceModelFile> files, CancellationToken cancellationToken)
    {
        var model = new[] { "model_fp16.onnx", "model.onnx", "model_fp32.onnx", "model_q4.onnx", "model_int8.onnx", "model_quantized.onnx",
            "model_uint8.onnx", "model_q4f16.onnx", "model_bnb4.onnx" }
            .Select(name => files.SingleOrDefault(file => file.Name == name)).FirstOrDefault(file => file is not null)
            ?? throw new InvalidDataException("Select a joint ONNX image/text embedding model.");
        var processor = files.SingleOrDefault(file => file.Name == "preprocessor_config.json")
            ?? throw new ArgumentException("The model requires preprocessor_config.json.", nameof(files));
        IReadOnlyList<string> companions;
        using (var source = await processor.OpenReadAsync(cancellationToken).ConfigureAwait(false))
        using (var configuration = await JsonDocument.ParseAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false))
            companions = CompanionNames(configuration.RootElement.GetProperty("image_processor_type").GetString());
        var selected = new List<InferenceModelFile> { model };
        foreach (var name in companions)
        {
            var companion = files.SingleOrDefault(file => file.Name == name)
                ?? throw new ArgumentException($"The model requires {name}.", nameof(files));
            selected.Add(companion with { MaximumBytes = 64L * 1024 * 1024 });
        }
        selected.AddRange(files.Where(file => IsExternalDataFile(model.Name, file.Name)));
        OnnxEmbeddingModel? definition = null;
        if (files.SingleOrDefault(file => file.Name == "config.json") is { } metadata)
        {
            using var source = await metadata.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            using var configuration = await JsonDocument.ParseAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (configuration.RootElement.TryGetProperty("_name_or_path", out var sourceModel))
                definition = All.FirstOrDefault(candidate => candidate.SourceModelId == sourceModel.GetString() ||
                    candidate.RepositoryId == sourceModel.GetString());
        }
        var precision = model.Name switch
        {
            "model_fp16.onnx" => "FP16",
            "model_q4.onnx" => "Q4",
            "model_int8.onnx" => "INT8",
            "model_quantized.onnx" => "Q8",
            "model_uint8.onnx" => "UINT8",
            "model_q4f16.onnx" => "Q4F16",
            "model_bnb4.onnx" => "BNB4",
            _ => "FP32",
        };
        return new InferenceModelImport(model.Name, selected,
            definition is not null && Path.GetFileName(definition.ModelPath) == model.Name ? definition.Id : null,
            definition is null ? null : $"{definition.Name} ({precision})");
    }
}