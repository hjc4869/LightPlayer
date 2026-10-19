using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LightStudio.FfmpegShim.Media;
using Microsoft.ML.Tokenizers;
using SkiaSharp;

namespace LightStudio.Inference;

internal sealed class EmbeddingPreprocessor
{
    private readonly Tokenizer tokenizer;
    private readonly float[] means;
    private readonly float[] deviations;
    private readonly float rescale;
    private readonly bool siglip2;
    private readonly bool lowercase;
    private readonly int resizeEdge;
    private readonly SKSamplingOptions sampling;

    internal int Width { get; }
    internal int Height { get; }
    internal int DefaultSequenceLength => siglip2 ? 64 : 77;
    internal long PaddingToken { get; }
    internal long[] ImageTokens { get; }
    internal string ModelName => $"{(siglip2 ? "SigLIP2" : "CLIP")} {Width}x{Height}";

    internal EmbeddingPreprocessor(string directory)
    {
        using var processor = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "preprocessor_config.json")));
        var config = processor.RootElement;
        siglip2 = config.GetProperty("image_processor_type").GetString() == "SiglipImageProcessor";
        var size = config.GetProperty(siglip2 ? "size" : "crop_size");
        Width = size.GetProperty("width").GetInt32();
        Height = size.GetProperty("height").GetInt32();
        resizeEdge = siglip2 ? 0 : config.GetProperty("size").GetProperty("shortest_edge").GetInt32();
        if (Width is < 1 or > 1024 || Height is < 1 or > 1024 ||
            !siglip2 && (resizeEdge < Math.Max(Width, Height) || resizeEdge > 1024))
            throw new InvalidDataException("Invalid embedding model image size.");
        sampling = config.GetProperty("resample").GetInt32() switch
        {
            2 => new SKSamplingOptions(SKFilterMode.Linear),
            3 => new SKSamplingOptions(SKCubicResampler.CatmullRom),
            _ => throw new InvalidDataException("The embedding model must use bilinear or bicubic image resizing."),
        };
        means = config.GetProperty("image_mean").EnumerateArray().Select(value => value.GetSingle()).ToArray();
        deviations = config.GetProperty("image_std").EnumerateArray().Select(value => value.GetSingle()).ToArray();
        rescale = config.GetProperty("rescale_factor").GetSingle();
        if (means.Length != 3 || deviations.Length != 3 || means.Any(value => !float.IsFinite(value)) ||
            deviations.Any(value => !float.IsFinite(value) || value <= 0) || !float.IsFinite(rescale) || rescale <= 0)
            throw new InvalidDataException("Invalid embedding model image normalization.");

        if (siglip2)
        {
            using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "tokenizer_config.json")));
            var text = configuration.RootElement;
            if (text.GetProperty("tokenizer_class").GetString() != "GemmaTokenizer" ||
                text.GetProperty("add_bos_token").GetBoolean() || !text.GetProperty("add_eos_token").GetBoolean() ||
                text.GetProperty("padding_side").GetString() != "right")
                throw new InvalidDataException("Select a SigLIP2 Gemma tokenizer with right padding and an end-of-sentence token.");
            lowercase = text.TryGetProperty("do_lower_case", out var lower) && lower.GetBoolean();
            using var model = File.OpenRead(Path.Combine(directory, "tokenizer.model"));
            var sentencePiece = SentencePieceTokenizer.Create(model, addBeginningOfSentence: false, addEndOfSentence: false,
                specialTokens: text.GetProperty("added_tokens_decoder").EnumerateObject().ToDictionary(
                    token => token.Value.GetProperty("content").GetString()!,
                    token => int.Parse(token.Name, CultureInfo.InvariantCulture)));
            if (sentencePiece.BeginningOfSentenceId != 2 || sentencePiece.EndOfSentenceId != 1 || sentencePiece.UnknownId != 3)
                throw new InvalidDataException("The tokenizer is not compatible with SigLIP2.");
            tokenizer = sentencePiece;
            PaddingToken = 0;
            ImageTokens = [1];
        }
        else
        {
            using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "tokenizer.json")));
            var special = configuration.RootElement.GetProperty("added_tokens").EnumerateArray()
                .ToDictionary(token => token.GetProperty("content").GetString()!, token => token.GetProperty("id").GetInt32());
            if (special.GetValueOrDefault("<|startoftext|>") != 49406 || special.GetValueOrDefault("<|endoftext|>") != 49407)
                throw new InvalidDataException("The tokenizer is not compatible with CLIP.");
            var pattern = configuration.RootElement.GetProperty("pre_tokenizer").GetProperty("pretokenizers")[0]
                .GetProperty("pattern").GetProperty("Regex").GetString()!;
            using var vocabulary = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "vocab.json")));
            var merges = File.ReadLines(Path.Combine(directory, "merges.txt"))
                .Where(line => line.Length > 0 && !line.StartsWith("#version:", StringComparison.Ordinal))
                .ToArray();
            tokenizer = BpeTokenizer.Create(new BpeOptions(vocabulary.RootElement.EnumerateObject()
                .Select(property => new KeyValuePair<string, int>(property.Name, property.Value.GetInt32())).ToArray())
            {
                Merges = merges,
                ByteLevel = true,
                EndOfWordSuffix = "</w>",
                PreTokenizer = new RegexPreTokenizer(new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), special),
                SpecialTokens = special,
                UnknownToken = "<|endoftext|>",
                BeginningOfSentenceToken = "<|startoftext|>",
                EndOfSentenceToken = "<|endoftext|>",
            });
            PaddingToken = 49407;
            ImageTokens = [49406, 49407];
        }
    }

    internal long[] Tokenize(string query, int sequenceLength)
    {
        if (siglip2)
            return tokenizer.EncodeToIds(lowercase ? query.ToLowerInvariant() : query)
                .Take(sequenceLength - 1).Select(token => (long)token).Append(1L).ToArray();
        var normalized = Regex.Replace(query.Normalize(NormalizationForm.FormC), @"\s+", " ",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim().ToLowerInvariant();
        var tokens = tokenizer.EncodeToIds(normalized).Take(sequenceLength).Select(token => (long)token).ToArray();
        if (tokens.Length < 2 || tokens[0] != 49406)
            throw new InvalidDataException("The CLIP tokenizer returned invalid tokens.");
        tokens[^1] = 49407;
        return tokens;
    }

    internal float[] PrepareImage(StillImageThumbnail thumbnail)
    {
        using var source = new SKBitmap(thumbnail.Width, thumbnail.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        Marshal.Copy(thumbnail.Pixels, 0, source.GetPixels(), thumbnail.Pixels.Length);
        var region = SKRect.Create(thumbnail.Width, thumbnail.Height);
        if (!siglip2)
        {
            var width = thumbnail.Width <= thumbnail.Height ? resizeEdge : (int)((long)thumbnail.Width * resizeEdge / thumbnail.Height);
            var height = thumbnail.Height <= thumbnail.Width ? resizeEdge : (int)((long)thumbnail.Height * resizeEdge / thumbnail.Width);
            float scaleX = (float)thumbnail.Width / width;
            float scaleY = (float)thumbnail.Height / height;
            region = SKRect.Create((width - Width) / 2 * scaleX, (height - Height) / 2 * scaleY,
                Width * scaleX, Height * scaleY);
        }
        using var crop = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(crop);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, region, SKRect.Create(Width, Height), sampling);
        var colors = crop.Pixels;
        var area = Width * Height;
        var values = new float[3 * area];
        for (var index = 0; index < area; index++)
        {
            values[index] = (colors[index].Red * rescale - means[0]) / deviations[0];
            values[area + index] = (colors[index].Green * rescale - means[1]) / deviations[1];
            values[2 * area + index] = (colors[index].Blue * rescale - means[2]) / deviations[2];
        }
        return values;
    }

    internal static void Normalize(float[] embedding, int count, int dimensions)
    {
        if (embedding.Length != count * dimensions)
            throw new InvalidDataException("The model returned an embedding with invalid dimensions.");
        for (var row = 0; row < count; row++)
        {
            var vector = embedding.AsSpan(row * dimensions, dimensions);
            double squared = 0;
            foreach (var value in vector)
                squared += (double)value * value;
            var norm = Math.Sqrt(squared);
            if (!double.IsFinite(norm) || norm < 1e-12)
                throw new InvalidDataException("The model returned an invalid embedding.");
            for (var index = 0; index < vector.Length; index++)
                vector[index] /= (float)norm;
        }
    }
}