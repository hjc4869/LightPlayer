using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.AI.Edge.LiteRT;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;
using Microsoft.ML.Tokenizers;
using SkiaSharp;
using LiteRtEnvironment = Google.AI.Edge.LiteRT.Environment;

namespace LightStudio.Inference;

internal sealed class LiteRtEmbeddingEncoder : IEmbeddingInferenceSession
{
    private readonly string directory;
    private readonly object sync = new();
    private readonly BlockingCollection<Action> work = new();
    private readonly Thread thread;
    private LiteRtEnvironment? environment;
    private LiteRtModel? imageModel;
    private LiteRtModel? textModel;
    private SentencePieceTokenizer tokenizer = null!;
    private volatile string activeDeviceId;
    private bool lowercase;
    private bool disposed;

    internal LiteRtEmbeddingEncoder(string directory, string deviceId)
    {
        if (deviceId is not ("cpu" or "gpu"))
            throw new ArgumentException("Select a LiteRT CPU or GPU device.", nameof(deviceId));
        this.directory = directory;
        activeDeviceId = deviceId;
        thread = new Thread(() =>
        {
            foreach (var action in work.GetConsumingEnumerable())
                action();
        }) { IsBackground = true, Name = "Inference.LiteRT" };
        thread.Start();
        try
        {
            Invoke(() =>
            {
                Initialize();
                return 0;
            }, CancellationToken.None);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int EmbeddingDimensions => LiteRtEmbeddingModels.EmbeddingDimensions;
    public string ModelName => LiteRtEmbeddingModels.Name;
    public string ActiveDeviceId => activeDeviceId;

    private void Initialize()
    {
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "tokenizer_config.json")));
        var text = configuration.RootElement;
        if (text.GetProperty("tokenizer_class").GetString() != "GemmaTokenizer" ||
            text.GetProperty("add_bos_token").GetBoolean() || !text.GetProperty("add_eos_token").GetBoolean() ||
            text.GetProperty("padding_side").GetString() != "right")
            throw new InvalidDataException("Select the SigLIP2 Gemma tokenizer with right padding and an end-of-sentence token.");
        lowercase = text.TryGetProperty("do_lower_case", out var lower) && lower.GetBoolean();
        using var source = File.OpenRead(Path.Combine(directory, "tokenizer.model"));
        tokenizer = SentencePieceTokenizer.Create(source, addBeginningOfSentence: false, addEndOfSentence: false,
            specialTokens: text.GetProperty("added_tokens_decoder").EnumerateObject().ToDictionary(
                token => token.Value.GetProperty("content").GetString()!, token => int.Parse(token.Name, CultureInfo.InvariantCulture)));
        if (tokenizer.BeginningOfSentenceId != 2 || tokenizer.EndOfSentenceId != 1 || tokenizer.UnknownId != 3)
            throw new InvalidDataException("The tokenizer is not compatible with SigLIP2.");
        environment = LiteRtEnvironment.Create();
    }

    private LiteRtModel CreateImageModel() => new(Path.Combine(directory, LiteRtEmbeddingModels.ImageFileName), environment!, activeDeviceId == "gpu", text: false);

    public float[] EncodeText(string query, CancellationToken cancellationToken) => Invoke(() =>
    {
        var tokens = new int[LiteRtEmbeddingModels.SequenceLength];
        var encoded = tokenizer.EncodeToIds(lowercase ? query.ToLowerInvariant() : query).Take(tokens.Length - 1).ToArray();
        encoded.CopyTo(tokens, 0);
        tokens[encoded.Length] = 1;
        cancellationToken.ThrowIfCancellationRequested();
        textModel ??= new LiteRtModel(Path.Combine(directory, LiteRtEmbeddingModels.TextFileName), environment!, gpu: false, text: true);
        cancellationToken.ThrowIfCancellationRequested();
        return Normalize(textModel.Run(tokens));
    }, cancellationToken);

    public float[] EncodeImage(StillImageThumbnail image, CancellationToken cancellationToken) =>
        Invoke(() => EncodeImageCore(image, cancellationToken), cancellationToken);

    public float[][] EncodeImages(IReadOnlyList<StillImageThumbnail> images, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(images.Count, 1);
        return Invoke(() =>
        {
            var result = new float[images.Count][];
            for (var index = 0; index < images.Count; index++)
                result[index] = EncodeImageCore(images[index], cancellationToken);
            return result;
        }, cancellationToken);
    }

    private float[] EncodeImageCore(StillImageThumbnail image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pixels = PrepareImage(image);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            imageModel ??= CreateImageModel();
            return Normalize(imageModel.Run(pixels));
        }
        catch (Exception exception) when (activeDeviceId == "gpu" && IsDeviceFailure(exception) && !cancellationToken.IsCancellationRequested)
        {
            AppLog.Warning("Inference", "LiteRT device inference failed; retrying on CPU.", exception);
            imageModel?.Dispose();
            imageModel = null;
            activeDeviceId = "cpu";
            imageModel = CreateImageModel();
            return Normalize(imageModel.Run(pixels));
        }
    }

    private static bool IsDeviceFailure(Exception exception) => exception is LiteRtException or Java.Lang.RuntimeException;

    private static float[] PrepareImage(StillImageThumbnail thumbnail)
    {
        if (thumbnail.Width < 1 || thumbnail.Height < 1 || thumbnail.Pixels.Length != checked(thumbnail.Width * thumbnail.Height * 4))
            throw new ArgumentException("The thumbnail must contain packed BGRA pixels.", nameof(thumbnail));
        using var source = new SKBitmap(thumbnail.Width, thumbnail.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        Marshal.Copy(thumbnail.Pixels, 0, source.GetPixels(), thumbnail.Pixels.Length);
        var size = LiteRtEmbeddingModels.ImageSize;
        using var resized = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(resized);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, SKRect.Create(thumbnail.Width, thumbnail.Height), SKRect.Create(size, size), new SKSamplingOptions(SKFilterMode.Linear));
        var colors = resized.Pixels;
        var area = size * size;
        var pixels = new float[3 * area];
        for (var index = 0; index < area; index++)
        {
            pixels[index] = colors[index].Red / 127.5f - 1;
            pixels[area + index] = colors[index].Green / 127.5f - 1;
            pixels[2 * area + index] = colors[index].Blue / 127.5f - 1;
        }
        return pixels;
    }

    private static float[] Normalize(float[] embedding)
    {
        if (embedding.Length != LiteRtEmbeddingModels.EmbeddingDimensions)
            throw new InvalidDataException("The model returned an embedding with invalid dimensions.");
        double squared = 0;
        foreach (var value in embedding)
            squared += (double)value * value;
        var norm = Math.Sqrt(squared);
        if (!double.IsFinite(norm) || norm < 1e-12)
            throw new InvalidDataException("The model returned an invalid embedding.");
        for (var index = 0; index < embedding.Length; index++)
            embedding[index] /= (float)norm;
        return embedding;
    }

    private TResult Invoke<TResult>(Func<TResult> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            work.Add(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = action();
                    cancellationToken.ThrowIfCancellationRequested();
                    completion.SetResult(result);
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            });
            return completion.Task.GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            try
            {
                Invoke(() =>
                {
                    try { imageModel?.Dispose(); }
                    finally
                    {
                        try { textModel?.Dispose(); }
                        finally
                        {
                            try { environment?.Close(); }
                            finally { environment?.Dispose(); }
                        }
                    }
                    return 0;
                }, CancellationToken.None);
            }
            finally
            {
                disposed = true;
                work.CompleteAdding();
                thread.Join();
                work.Dispose();
            }
        }
    }
}