using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using LightMediaRenderer.Contracts;
using SkiaSharp;

namespace LightMediaRenderer.Shaders;

/// <summary>
/// A file-loaded graph of SkSL passes. Pass inputs are exposed to each complete SkSL program as
/// <c>input0</c>, <c>input1</c>, and so on, in manifest order.
/// </summary>
/// <remarks>
/// Feature maps use signed RGB half floats and keep alpha at one. Skia applies alpha semantics to
/// render targets, so alpha cannot safely carry a fourth arbitrary tensor component.
/// </remarks>
public sealed class FileVideoScaleStage : IVideoScaleStage, IDisposable
{
    private const int CurrentVersion = 1;
    private const int MaximumPasses = 256;
    private const int MaximumInputs = 64;
    private const int MaximumDimension = 32768;
    private const long MaximumPixels = 134_217_728;
    private const long MaximumShaderBytes = 4 * 1024 * 1024;
    private const string SceneName = "MAIN";
    private const string SignalRgbDomain = "signal-rgb";

    private readonly CompiledPass[] passes;
    private readonly string output;
    private readonly double minimumScale;
    private readonly double maximumScale;
    private int disposed;
    private int loggedFailure;
    private int loggedSuccess;

    private FileVideoScaleStage(
        string name,
        string output,
        double minimumScale,
        double maximumScale,
        CompiledPass[] passes)
    {
        Name = name;
        this.output = output;
        this.minimumScale = minimumScale;
        this.maximumScale = maximumScale;
        this.passes = passes;
    }

    public string Name { get; }

    public int PassCount => passes.Length;

    public bool Supports(in VideoScaleStageContext context) =>
        context.Transfer is not (VideoColorTransfer.Pq or VideoColorTransfer.Hlg or VideoColorTransfer.Linear);

    public bool ShouldRun(in VideoScaleStageContext context) =>
        Supports(context) &&
        context.Scaling.ScaleX > minimumScale &&
        context.Scaling.ScaleY > minimumScale &&
        context.Scaling.ScaleX < maximumScale &&
        context.Scaling.ScaleY < maximumScale;

    public static bool TryLoad(
        string manifestPath,
        out FileVideoScaleStage? stage,
        out string error)
    {
        stage = null;
        try
        {
            stage = Load(manifestPath);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            RendererLog.Warning("Video.Shader", $"Could not load pre-scale stage {manifestPath}.", exception);
            error = exception.Message;
            return false;
        }
    }

    public SKImage? Render(
        SKImage source,
        GRContext? grContext,
        in VideoScaleStageContext context,
        out string declined)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Volatile.Read(ref disposed) != 0)
        {
            declined = $"the {Name} pre-scale stage has been disposed";
            return null;
        }

        try
        {
            SKImage result = RenderCore(source, grContext, context);
            if (Interlocked.Exchange(ref loggedSuccess, 1) == 0)
            {
                RendererLog.Debug(
                    "Video.Shader",
                    $"Pre-scale stage {Name} rendered {source.Width}x{source.Height} to {result.Width}x{result.Height}.");
            }
            declined = string.Empty;
            return result;
        }
        catch (Exception exception)
        {
            declined = $"the {Name} pre-scale stage failed: {exception.Message}";
            if (Interlocked.Exchange(ref loggedFailure, 1) == 0)
                RendererLog.Error("Video.Shader", declined, exception);
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        foreach (CompiledPass pass in passes)
            pass.Dispose();
    }

    private static FileVideoScaleStage Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string fullManifestPath = Path.GetFullPath(manifestPath);
        string manifestDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidDataException("The stage manifest has no parent directory.");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullManifestPath));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The stage manifest root must be an object.");
        EnsureProperties(
            root,
            "stage manifest",
            "version",
            "name",
            "source",
            "sourceSha256",
            "inputDomain",
            "minimumScale",
            "maximumScale",
            "output",
            "passes");

        int version = RequiredInt(root, "version");
        if (version != CurrentVersion)
            throw new InvalidDataException($"Stage manifest version {version} is not supported.");

        string inputDomain = RequiredString(root, "inputDomain");
        if (!string.Equals(inputDomain, SignalRgbDomain, StringComparison.Ordinal))
            throw new InvalidDataException($"The inputDomain property must be {SignalRgbDomain}.");

        string name = root.TryGetProperty("name", out _)
            ? RequiredString(root, "name")
            : Path.GetFileNameWithoutExtension(fullManifestPath);
        string output = RequiredString(root, "output");
        if (!string.Equals(output, SceneName, StringComparison.Ordinal))
            throw new InvalidDataException($"The output property must be {SceneName}.");
        double minimumScale = OptionalDouble(root, "minimumScale", 0);
        if (!double.IsFinite(minimumScale) || minimumScale < 0)
            throw new InvalidDataException("The minimumScale property must be a finite non-negative number.");
        double maximumScale = OptionalDouble(root, "maximumScale", double.PositiveInfinity);
        if (double.IsNaN(maximumScale) || maximumScale <= minimumScale)
            throw new InvalidDataException("The maximumScale property must be greater than minimumScale.");
        if (!root.TryGetProperty("passes", out JsonElement passArray) ||
            passArray.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The stage manifest must contain a passes array.");
        }

        int passCount = passArray.GetArrayLength();
        if (passCount is < 1 or > MaximumPasses)
            throw new InvalidDataException($"A stage must contain between 1 and {MaximumPasses} passes.");

        var available = new HashSet<string>(StringComparer.Ordinal) { SceneName, "NATIVE" };
        var produced = new HashSet<string>(StringComparer.Ordinal);
        var compiled = new List<CompiledPass>(passCount);
        try
        {
            int index = 0;
            foreach (JsonElement passElement in passArray.EnumerateArray())
            {
                if (passElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"Pass {index} must be an object.");
                EnsureProperties(
                    passElement,
                    $"pass {index}",
                    "name",
                    "shader",
                    "inputs",
                    "output",
                    "width",
                    "height",
                    "widthScale",
                    "heightScale",
                    "sizeUniforms");

                string passName = OptionalString(passElement, "name") ?? $"pass {index}";
                string shaderFile = RequiredString(passElement, "shader");
                string passOutput = RequiredString(passElement, "output");
                PassDimension width = ReadDimension(passElement, "width", "widthScale");
                PassDimension height = ReadDimension(passElement, "height", "heightScale");
                bool sizeUniforms = OptionalBool(passElement, "sizeUniforms", false);

                string[] inputs = ReadInputs(passElement, passName);
                foreach (string input in inputs)
                {
                    if (!available.Contains(input))
                        throw new InvalidDataException($"{passName} reads {input} before it is produced.");
                }

                string shaderPath = Path.GetFullPath(Path.Combine(manifestDirectory, shaderFile));
                string relativeShaderPath = Path.GetRelativePath(manifestDirectory, shaderPath);
                if (relativeShaderPath == ".." ||
                    relativeShaderPath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    Path.IsPathRooted(relativeShaderPath))
                {
                    throw new InvalidDataException($"{passName} refers to a shader outside the manifest directory.");
                }
                if (new FileInfo(shaderPath).Length > MaximumShaderBytes)
                    throw new InvalidDataException($"{passName} exceeds the shader source size limit.");
                string shaderSource = File.ReadAllText(shaderPath);
                SKRuntimeEffect effect = SKRuntimeEffect.CreateShader(shaderSource, out string errors)
                    ?? throw new InvalidDataException($"{passName} did not compile: {errors}");

                compiled.Add(new CompiledPass(
                    passName,
                    passOutput,
                    inputs,
                    width,
                    height,
                    sizeUniforms,
                    [],
                    KeepOutput: true,
                    effect));
                available.Add(passOutput);
                produced.Add(passOutput);
                index++;
            }

            if (!produced.Contains(output))
                throw new InvalidDataException($"The final output {output} is not produced by any pass.");

            CompiledPass[] plannedPasses = PlanLifetimes(compiled, output);
            return new FileVideoScaleStage(
                name,
                output,
                minimumScale,
                maximumScale,
                plannedPasses);
        }
        catch
        {
            foreach (CompiledPass pass in compiled)
                pass.Dispose();
            throw;
        }
    }

    private SKImage RenderCore(
        SKImage source,
        GRContext? grContext,
        in VideoScaleStageContext context)
    {
        var images = new Dictionary<string, SKImage>(StringComparer.Ordinal)
        {
            [SceneName] = source,
            ["NATIVE"] = source,
        };
        var ownedImages = new HashSet<SKImage>();
        try
        {
            foreach (CompiledPass pass in passes)
            {
                int width = pass.Width.Resolve(source.Width, context.Scaling.DestinationWidth);
                int height = pass.Height.Resolve(source.Height, context.Scaling.DestinationHeight);
                if (width > MaximumDimension || height > MaximumDimension || (long)width * height > MaximumPixels)
                {
                    throw new InvalidOperationException(
                        $"{pass.Name} requested an unsupported {width}x{height} intermediate");
                }
                SKImage rendered = RenderPass(pass, images, grContext, width, height);

                foreach (string inputName in pass.ReleaseInputs)
                {
                    if (images.Remove(inputName, out SKImage? released) && ownedImages.Remove(released))
                        released.Dispose();
                }
                if (images.Remove(pass.Output, out SKImage? replaced) && ownedImages.Remove(replaced))
                    replaced.Dispose();
                images[pass.Output] = rendered;
                ownedImages.Add(rendered);
                if (!pass.KeepOutput)
                {
                    images.Remove(pass.Output);
                    ownedImages.Remove(rendered);
                    rendered.Dispose();
                }
            }

            SKImage result = images[output];
            if (!ownedImages.Remove(result))
                throw new InvalidOperationException($"The final output {output} is not owned by the stage.");
            return result;
        }
        finally
        {
            foreach (SKImage image in ownedImages)
                image.Dispose();
        }
    }

    private static CompiledPass[] PlanLifetimes(
        IReadOnlyList<CompiledPass> passes,
        string finalOutput)
    {
        var planned = new CompiledPass[passes.Count];
        for (int index = 0; index < passes.Count; index++)
        {
            CompiledPass pass = passes[index];
            var releases = new HashSet<string>(StringComparer.Ordinal);
            foreach (string input in pass.Inputs)
            {
                if (input == pass.Output || !CurrentValueIsNeeded(passes, index + 1, input, finalOutput))
                    releases.Add(input);
            }
            planned[index] = pass with
            {
                ReleaseInputs = [.. releases],
                KeepOutput = CurrentValueIsNeeded(passes, index + 1, pass.Output, finalOutput),
            };
        }
        return planned;
    }

    private static bool CurrentValueIsNeeded(
        IReadOnlyList<CompiledPass> passes,
        int startIndex,
        string name,
        string finalOutput)
    {
        for (int index = startIndex; index < passes.Count; index++)
        {
            CompiledPass pass = passes[index];
            if (Array.IndexOf(pass.Inputs, name) >= 0)
                return true;
            if (pass.Output == name)
                return false;
        }
        return name == finalOutput;
    }

    private static SKImage RenderPass(
        CompiledPass pass,
        IReadOnlyDictionary<string, SKImage> images,
        GRContext? grContext,
        int width,
        int height)
    {
        var inputShaders = new SKShader?[pass.Inputs.Length];
        try
        {
            var children = new SKRuntimeEffectChildren(pass.Effect);
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            for (int index = 0; index < pass.Inputs.Length; index++)
            {
                SKImage input = images[pass.Inputs[index]];
                SKShader inputShader = input.ToRawShader(
                    SKShaderTileMode.Clamp,
                    SKShaderTileMode.Clamp,
                    sampling,
                    SKMatrix.Identity)
                    ?? throw new InvalidOperationException($"{pass.Name} could not sample {pass.Inputs[index]}.");
                inputShaders[index] = inputShader;
                children[$"input{index}"] = inputShader;
            }

            var uniforms = new SKRuntimeEffectUniforms(pass.Effect);
            if (pass.SizeUniforms)
            {
                uniforms["outputSize"] = new[] { (float)width, (float)height };
                for (int index = 0; index < pass.Inputs.Length; index++)
                {
                    SKImage input = images[pass.Inputs[index]];
                    uniforms[$"input{index}Size"] = new[] { (float)input.Width, (float)input.Height };
                }
            }
            using SKShader shader = pass.Effect.ToShader(uniforms, children)
                ?? throw new InvalidOperationException($"{pass.Name} could not bind its inputs.");
            var info = new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Opaque);
            using SKSurface surface = grContext is null
                ? SKSurface.Create(info)
                    ?? throw new InvalidOperationException($"the raster backend refused {width}x{height} half floats")
                : SKSurface.Create(grContext, true, info)
                    ?? throw new InvalidOperationException($"the graphics backend refused {width}x{height} half floats");
            using (var paint = new SKPaint
            {
                IsAntialias = false,
                BlendMode = SKBlendMode.Src,
                Shader = shader,
            })
            {
                surface.Canvas.DrawRect(SKRect.Create(0, 0, width, height), paint);
            }

            return surface.Snapshot();
        }
        finally
        {
            foreach (SKShader? inputShader in inputShaders)
                inputShader?.Dispose();
        }
    }

    private static string[] ReadInputs(JsonElement pass, string passName)
    {
        if (!pass.TryGetProperty("inputs", out JsonElement inputArray) ||
            inputArray.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{passName} must contain an inputs array.");
        }

        int inputCount = inputArray.GetArrayLength();
        if (inputCount is < 1 or > MaximumInputs)
            throw new InvalidDataException($"{passName} must have between 1 and {MaximumInputs} inputs.");

        var inputs = new string[inputCount];
        int index = 0;
        foreach (JsonElement inputElement in inputArray.EnumerateArray())
        {
            string? input = inputElement.GetString();
            if (string.IsNullOrWhiteSpace(input))
                throw new InvalidDataException($"{passName} contains an empty input name.");
            inputs[index++] = input;
        }
        return inputs;
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        OptionalString(element, propertyName)
        ?? throw new InvalidDataException($"The {propertyName} property must be a non-empty string.");

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"The {propertyName} property must be a string.");
        string? value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int RequiredInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || !property.TryGetInt32(out int value))
            throw new InvalidDataException($"The {propertyName} property must be an integer.");
        return value;
    }

    private static int OptionalInt(JsonElement element, string propertyName, int fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            return fallback;
        if (!property.TryGetInt32(out int value))
            throw new InvalidDataException($"The {propertyName} property must be an integer.");
        return value;
    }

    private static double OptionalDouble(JsonElement element, string propertyName, double fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            return fallback;
        if (!property.TryGetDouble(out double value))
            throw new InvalidDataException($"The {propertyName} property must be a number.");
        return value;
    }

    private static bool OptionalBool(JsonElement element, string propertyName, bool fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            return fallback;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"The {propertyName} property must be a boolean.");
        return property.GetBoolean();
    }

    private static PassDimension ReadDimension(
        JsonElement pass,
        string propertyName,
        string legacyScaleProperty)
    {
        if (!pass.TryGetProperty(propertyName, out JsonElement property))
            return new PassDimension(DimensionReference.Source, OptionalInt(pass, legacyScaleProperty, 1), 1);
        if (property.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"The {propertyName} property must be an object.");
        EnsureProperties(property, propertyName, "reference", "numerator", "denominator");

        string referenceName = RequiredString(property, "reference");
        DimensionReference reference = referenceName switch
        {
            "source" => DimensionReference.Source,
            "destination" => DimensionReference.Destination,
            _ => throw new InvalidDataException(
                $"The {propertyName}.reference property must be source or destination."),
        };
        int numerator = RequiredInt(property, "numerator");
        int denominator = RequiredInt(property, "denominator");
        if (numerator <= 0 || denominator <= 0)
            throw new InvalidDataException($"The {propertyName} ratio must be positive.");
        return new PassDimension(reference, numerator, denominator);
    }

    private static void EnsureProperties(
        JsonElement element,
        string scope,
        params string[] allowedNames)
    {
        var allowed = new HashSet<string>(allowedNames, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException($"The {scope} contains unknown property {property.Name}.");
        }
    }

    private enum DimensionReference
    {
        Source,
        Destination,
    }

    private readonly record struct PassDimension(
        DimensionReference Reference,
        int Numerator,
        int Denominator)
    {
        public int Resolve(int source, double destination)
        {
            double reference = Reference == DimensionReference.Source ? source : destination;
            double value = reference * Numerator / Denominator;
            if (!double.IsFinite(value) || value < 1 || value > int.MaxValue)
                throw new InvalidOperationException($"invalid intermediate dimension {value}");
            return checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
        }
    }

    private sealed record CompiledPass(
        string Name,
        string Output,
        string[] Inputs,
        PassDimension Width,
        PassDimension Height,
        bool SizeUniforms,
        string[] ReleaseInputs,
        bool KeepOutput,
        SKRuntimeEffect Effect) : IDisposable
    {
        public void Dispose() => Effect.Dispose();
    }
}

