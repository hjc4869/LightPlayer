using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Services;
using Microsoft.ML.OnnxRuntime;

namespace LightStudio.Inference;

internal sealed class OnnxEmbeddingEncoder : IEmbeddingInferenceSession
{
    private readonly InferenceSession session;
    private readonly EmbeddingPreprocessor preprocessing;
    private readonly int sequenceLength;

    public int EmbeddingDimensions { get; }
    public string ModelName => preprocessing.ModelName;
    public string ActiveDeviceId { get; }

    internal OnnxEmbeddingEncoder(string directory, string modelFileName, string deviceId,
        Func<string, int, int, int, SessionOptions> createOptions)
    {
        ActiveDeviceId = deviceId;
        preprocessing = new EmbeddingPreprocessor(directory);
        using var options = createOptions(deviceId, preprocessing.Width, preprocessing.Height, preprocessing.DefaultSequenceLength);
        session = new InferenceSession(Path.Combine(directory, modelFileName), options);
        try
        {
            if (!session.InputMetadata.TryGetValue("pixel_values", out var image) ||
                image.ElementType != typeof(float) && image.ElementType != typeof(Float16) ||
                !session.InputMetadata.TryGetValue("input_ids", out var text) || text.ElementType != typeof(long) ||
                !session.OutputMetadata.ContainsKey("image_embeds") || !session.OutputMetadata.ContainsKey("text_embeds") ||
                session.InputMetadata.Keys.Any(key => key is not ("pixel_values" or "input_ids" or "attention_mask")))
                throw new InvalidDataException("Select a joint CLIP or SigLIP2 ONNX model with image_embeds and text_embeds outputs.");
            if (image.Dimensions.Length != 4 || image.Dimensions[1] > 0 && image.Dimensions[1] != 3 ||
                image.Dimensions[2] > 0 && image.Dimensions[2] != preprocessing.Height ||
                image.Dimensions[3] > 0 && image.Dimensions[3] != preprocessing.Width || text.Dimensions.Length != 2)
                throw new InvalidDataException("The embedding model inputs do not match its preprocessing configuration.");
            sequenceLength = text.Dimensions[1] > 0 ? text.Dimensions[1] : preprocessing.DefaultSequenceLength;
            if (sequenceLength is < 2 or > 512)
                throw new InvalidDataException("Invalid embedding model text sequence length.");
            var imageShape = session.OutputMetadata["image_embeds"].Dimensions;
            var textShape = session.OutputMetadata["text_embeds"].Dimensions;
            if (imageShape.Length != 2 || textShape.Length != 2 || imageShape[1] <= 0 || imageShape[1] != textShape[1])
                throw new InvalidDataException("The model must return matching image and text embedding dimensions.");
            EmbeddingDimensions = imageShape[1];
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal long[] Tokenize(string query) => preprocessing.Tokenize(query, sequenceLength);

    public float[] EncodeText(string query, CancellationToken cancellationToken) =>
        Execute(new float[3 * preprocessing.Width * preprocessing.Height], Tokenize(query), "text_embeds", cancellationToken);

    public float[] EncodeImage(StillImageThumbnail image, CancellationToken cancellationToken) =>
        Execute(preprocessing.PrepareImage(image), preprocessing.ImageTokens, "image_embeds", cancellationToken);

    private bool SupportsImageBatching => session.InputMetadata["pixel_values"].Dimensions[0] < 0;

    public float[][] EncodeImages(IReadOnlyList<StillImageThumbnail> images, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(images.Count, 1);
        if (images.Count == 1)
            return [EncodeImage(images[0], cancellationToken)];
        if (!SupportsImageBatching)
            return images.Select(image => EncodeImage(image, cancellationToken)).ToArray();
        var imageLength = 3 * preprocessing.Width * preprocessing.Height;
        var pixels = new float[checked(images.Count * imageLength)];
        for (var index = 0; index < images.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            preprocessing.PrepareImage(images[index]).CopyTo(pixels, index * imageLength);
        }
        var embeddings = Execute(pixels, preprocessing.ImageTokens, "image_embeds", cancellationToken, images.Count);
        return Enumerable.Range(0, images.Count).Select(index => embeddings.AsSpan(index * EmbeddingDimensions, EmbeddingDimensions).ToArray()).ToArray();
    }

    private float[] Execute(float[] pixels, long[] tokens, string output, CancellationToken cancellationToken, int imageCount = 1)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = new Dictionary<string, OrtValue>();
        using var options = new RunOptions();
        using var registration = cancellationToken.Register(() => options.Terminate = true);
        try
        {
            var ids = Enumerable.Repeat(preprocessing.PaddingToken, sequenceLength).ToArray();
            tokens.CopyTo(ids, 0);
            inputs["input_ids"] = OrtValue.CreateTensorValueFromMemory(ids, [1, sequenceLength]);
            if (session.InputMetadata.ContainsKey("attention_mask"))
                inputs["attention_mask"] = OrtValue.CreateTensorValueFromMemory(
                    Enumerable.Range(0, sequenceLength).Select(index => index < tokens.Length ? 1L : 0L).ToArray(), [1, sequenceLength]);
            inputs["pixel_values"] = session.InputMetadata["pixel_values"].ElementType == typeof(Float16)
                ? OrtValue.CreateTensorValueFromMemory(pixels.Select(value => (Float16)value).ToArray(), [imageCount, 3, preprocessing.Height, preprocessing.Width])
                : OrtValue.CreateTensorValueFromMemory(pixels, [imageCount, 3, preprocessing.Height, preprocessing.Width]);
            using var results = session.Run(options, inputs, [output]);
            var result = results[0];
            var embedding = result.GetTensorTypeAndShape().ElementDataType == Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Float16
                ? result.GetTensorDataAsSpan<Float16>().ToArray().Select(value => (float)value).ToArray()
                : result.GetTensorDataAsSpan<float>().ToArray();
            var count = output == "image_embeds" ? imageCount : 1;
            EmbeddingPreprocessor.Normalize(embedding, count, EmbeddingDimensions);
            cancellationToken.ThrowIfCancellationRequested();
            return embedding;
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            foreach (var input in inputs.Values)
                input.Dispose();
        }
    }

    public void Dispose() => session.Dispose();
}