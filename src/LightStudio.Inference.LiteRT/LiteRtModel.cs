using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.AI.Edge.LiteRT;
using LiteRtEnvironment = Google.AI.Edge.LiteRT.Environment;

namespace LightStudio.Inference;

internal sealed class LiteRtModel : IDisposable
{
    private const string Signature = "serving_default";
    private const string InputName = "args_0";
    private readonly CompiledModel model;
    private readonly Dictionary<string, TensorBuffer> inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TensorBuffer> outputs = new(StringComparer.Ordinal);
    private readonly string outputName;
    private readonly bool longTokens;
    private bool disposed;

    internal LiteRtModel(string path, LiteRtEnvironment environment, bool gpu, bool text)
    {
        using var threads = Java.Lang.Integer.ValueOf(Math.Clamp(System.Environment.ProcessorCount, 1, 4));
        using var cpuOptions = new CompiledModel.CpuOptions(threads, null, null);
        using var options = new CompiledModel.Options(gpu ? Accelerator.Gpu! : Accelerator.Cpu!) { CpuOptions = cpuOptions };
        model = CompiledModel.Create(path, options, environment);
        outputName = text ? "pooler_output" : "output_0";
        try
        {
            using var inputType = model.GetInputTensorType(InputName, Signature);
            var elementType = inputType.GetElementType();
            longTokens = text && elementType.Equals(TensorType.ElementType.Int64);
            if (!(text ? longTokens || elementType.Equals(TensorType.ElementType.Int) : elementType.Equals(TensorType.ElementType.Float)))
                throw new InvalidDataException("The LiteRT model has an incompatible input element type.");
            RequireShape(inputType, text ? [1, LiteRtEmbeddingModels.SequenceLength] : [1, 3, LiteRtEmbeddingModels.ImageSize, LiteRtEmbeddingModels.ImageSize]);
            using var outputType = model.GetOutputTensorType(outputName, Signature);
            RequireFloatShape(outputType, [1, LiteRtEmbeddingModels.EmbeddingDimensions]);
            inputs.Add(InputName, model.CreateInputBuffer(InputName, Signature));
            outputs.Add(outputName, model.CreateOutputBuffer(outputName, Signature));
            if (text)
            {
                using var hiddenType = model.GetOutputTensorType("last_hidden_state", Signature);
                RequireFloatShape(hiddenType, [1, LiteRtEmbeddingModels.SequenceLength, LiteRtEmbeddingModels.EmbeddingDimensions]);
                outputs.Add("last_hidden_state", model.CreateOutputBuffer("last_hidden_state", Signature));
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal float[] Run(float[] pixels)
    {
        inputs[InputName].WriteFloat(pixels);
        model.Run(inputs, outputs, Signature);
        return outputs[outputName].ReadFloat();
    }

    internal float[] Run(int[] tokens)
    {
        if (longTokens)
            inputs[InputName].WriteLong(tokens.Select(token => (long)token).ToArray());
        else
            inputs[InputName].WriteInt(tokens);
        model.Run(inputs, outputs, Signature);
        return outputs[outputName].ReadFloat();
    }

    private static void RequireFloatShape(TensorType type, int[] dimensions)
    {
        if (!type.GetElementType().Equals(TensorType.ElementType.Float))
            throw new InvalidDataException("The LiteRT model must return float embeddings.");
        RequireShape(type, dimensions);
    }

    private static void RequireShape(TensorType type, int[] dimensions)
    {
        using var layout = type.GetLayout();
        if (layout is null || !layout.Dimensions.Select(value => value.IntValue()).SequenceEqual(dimensions))
            throw new InvalidDataException("The LiteRT tensor dimensions do not match the SigLIP2 model package.");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            foreach (var buffer in inputs.Values.Concat(outputs.Values))
            {
                try { buffer.Close(); }
                finally { buffer.Dispose(); }
            }
        }
        finally
        {
            try { model.Close(); }
            finally { model.Dispose(); }
        }
    }
}