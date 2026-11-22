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
    private EmbeddingMatchCalibration? calibration;
    private bool calibrated;

    public int EmbeddingDimensions { get; }
    public int ImageSize => Math.Max(preprocessing.Width, preprocessing.Height);
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

    public EmbeddingMatchCalibration? GetMatchCalibration(CancellationToken cancellationToken)
    {
        if (calibrated)
            return calibration;
        if (preprocessing.HasMatchProbability && session.OutputMetadata.ContainsKey("logits_per_text"))
        {
            // The logit is linear in the cosine similarity, so two texts scored against one image recover its scale and bias.
            var pixels = new float[3 * preprocessing.Width * preprocessing.Height];
            var first = Probe("a plain gray background");
            var second = Probe("a crowded city street at night with neon signs");
            var scale = (first.Logit - second.Logit) / (first.Similarity - second.Similarity);
            var bias = first.Logit - scale * first.Similarity;
            if (Math.Abs(first.Similarity - second.Similarity) < 0.001 || !double.IsFinite(scale) || !double.IsFinite(bias) || scale <= 0)
                throw new InvalidDataException("The model returned inconsistent match logits.");
            calibration = new EmbeddingMatchCalibration(scale, bias);

            (double Similarity, double Logit) Probe(string text)
            {
                var outputs = Run(pixels, Tokenize(text), ["text_embeds", "image_embeds", "logits_per_text"], cancellationToken, 1);
                EmbeddingPreprocessor.Normalize(outputs[0], 1, EmbeddingDimensions);
                EmbeddingPreprocessor.Normalize(outputs[1], 1, EmbeddingDimensions);
                double similarity = 0;
                for (var index = 0; index < EmbeddingDimensions; index++)
                    similarity += (double)outputs[0][index] * outputs[1][index];
                if (outputs[2].Length != 1)
                    throw new InvalidDataException("The model returned match logits with invalid dimensions.");
                return (similarity, outputs[2][0]);
            }
        }
        calibrated = true;
        return calibration;
    }

    private float[] Execute(float[] pixels, long[] tokens, string output, CancellationToken cancellationToken, int imageCount = 1)
    {
        var embedding = Run(pixels, tokens, [output], cancellationToken, imageCount)[0];
        EmbeddingPreprocessor.Normalize(embedding, output == "image_embeds" ? imageCount : 1, EmbeddingDimensions);
        cancellationToken.ThrowIfCancellationRequested();
        return embedding;
    }

    private float[][] Run(float[] pixels, long[] tokens, string[] outputs, CancellationToken cancellationToken, int imageCount)
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
            long[] imageShape = [imageCount, 3, preprocessing.Height, preprocessing.Width];
            if (session.InputMetadata["pixel_values"].ElementType == typeof(Float16))
            {
                var halves = new Float16[pixels.Length];
                for (var index = 0; index < pixels.Length; index++)
                    halves[index] = (Float16)pixels[index];
                inputs["pixel_values"] = OrtValue.CreateTensorValueFromMemory(halves, imageShape);
            }
            else
                inputs["pixel_values"] = OrtValue.CreateTensorValueFromMemory(pixels, imageShape);
            using var results = session.Run(options, inputs, outputs);
            return results.Select(result =>
                result.GetTensorTypeAndShape().ElementDataType == Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Float16
                    ? result.GetTensorDataAsSpan<Float16>().ToArray().Select(value => (float)value).ToArray()
                    : result.GetTensorDataAsSpan<float>().ToArray()).ToArray();
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