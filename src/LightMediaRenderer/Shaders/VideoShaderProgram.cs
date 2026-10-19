using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using LightMediaRenderer.Contracts;
using SkiaSharp;

namespace LightMediaRenderer.Shaders;

/// <summary>Names of the uniforms the stages share, so a program declares each of them once.</summary>
internal static class VideoShaderUniforms
{
    public const string MatrixR = "matrixR";
    public const string MatrixG = "matrixG";
    public const string MatrixB = "matrixB";
    public const string LumaBounds = "lumaBounds";
    public const string ChromaBounds = "chromaBounds";
    public const string SourceLuma = "sourceLuma";
    public const string DeinterlaceParams = "deinterlaceParams";
    public const string DeinterlaceSize = "deinterlaceSize";
    public const string TransferParams = "transferParams";
    public const string SurfaceParams = "surfaceParams";
    public const string ToneParams = "toneParams";
    public const string ToneCurve0 = "toneCurve0";
    public const string ToneCurve1 = "toneCurve1";
    public const string Hdr10PlusParams = "hdr10PlusParams";
    public const string Hdr10PlusCurve = "hdr10PlusCurve";
    public const string SceneParams = "sceneParams";
    public const string GamutR = "gamutR";
    public const string GamutG = "gamutG";
    public const string GamutB = "gamutB";
    public const string OutputParams = "outputParams";
    public const string DitherParams = "ditherParams";
    public const string DitherX = "ditherX";
    public const string DitherY = "ditherY";
    public const string DitherW = "ditherW";
    public const string DebandParams = "debandParams";
    public const string ScalerParams = "scalerParams";
    public const string DoviParams = "doviParams";
    public const string DoviPivotCounts = "doviPivotCounts";
    public const string DoviNonlinearOffset = "doviNonlinearOffset";
    public const string DoviNonlinearMatrix = "doviNonlinearMatrix";
    public const string DoviLinearMatrix = "doviLinearMatrix";
    public const string DoviNlqOffset = "doviNlqOffset";
    public const string DoviNlqSlope = "doviNlqSlope";
    public const string DoviNlqThreshold = "doviNlqThreshold";

    internal static readonly string[] DoviPivots = ["doviPivots0", "doviPivots1", "doviPivots2"];
    internal static readonly string[] DoviPolynomial = ["doviPolynomial0", "doviPolynomial1", "doviPolynomial2"];
    internal static readonly string[] DoviMmrConstants = ["doviMmrConstants0", "doviMmrConstants1", "doviMmrConstants2"];
    internal static readonly string[] DoviMmrCoefficients =
        ["doviMmrCoefficients0", "doviMmrCoefficients1", "doviMmrCoefficients2"];

    private static readonly Dictionary<string, string> declarations = new()
    {
        [MatrixR] = "uniform float4 matrixR;",
        [MatrixG] = "uniform float4 matrixG;",
        [MatrixB] = "uniform float4 matrixB;",
        [LumaBounds] = "uniform float4 lumaBounds;",
        [ChromaBounds] = "uniform float4 chromaBounds;",
        [SourceLuma] = "uniform float4 sourceLuma;",

        // The parity of the rows the first field left behind in .x, the motion the woven rows are
        // judged against in .yz, and what a diagonal has to beat the vertical tap by in .w.
        [DeinterlaceParams] = "uniform float4 deinterlaceParams;",
        [DeinterlaceSize] = "uniform float4 deinterlaceSize;",
        // HLG peak in .x, or BT.1886 scale, offset, exponent, and reciprocal exponent.
        [TransferParams] = "uniform float4 transferParams;",

        // The exponents of the two power laws in play: the one the signal is read with in .xy and
        // the one the surface is shown through in .zw, each as the exponent and its reciprocal.
        [SurfaceParams] = "uniform float4 surfaceParams;",
        [ToneParams] = "uniform float4 toneParams;",
        [ToneCurve0] = "uniform float4 toneCurve0;",
        [ToneCurve1] = "uniform float4 toneCurve1;",
        [Hdr10PlusParams] = "uniform float4 hdr10PlusParams;",
        [Hdr10PlusCurve] = "uniform float hdr10PlusCurve[17];",
        [SceneParams] = "uniform float4 sceneParams;",
        [GamutR] = "uniform float4 gamutR;",
        [GamutG] = "uniform float4 gamutG;",
        [GamutB] = "uniform float4 gamutB;",
        [OutputParams] = "uniform float4 outputParams;",
        [DitherParams] = "uniform float4 ditherParams;",
        [DitherX] = "uniform float3 ditherX;",
        [DitherY] = "uniform float3 ditherY;",
        [DitherW] = "uniform float3 ditherW;",
        [DebandParams] = "uniform float4 debandParams;",
        [ScalerParams] = "uniform float4 scalerParams;",
        [DoviParams] = "uniform float4 doviParams;",
        [DoviPivotCounts] = "uniform float3 doviPivotCounts;",
        [DoviNonlinearOffset] = "uniform float3 doviNonlinearOffset;",
        [DoviNonlinearMatrix] = "uniform float4 doviNonlinearMatrix[3];",
        [DoviLinearMatrix] = "uniform float3 doviLinearMatrix[3];",
        [DoviNlqOffset] = "uniform float3 doviNlqOffset;",
        [DoviNlqSlope] = "uniform float3 doviNlqSlope;",
        [DoviNlqThreshold] = "uniform float3 doviNlqThreshold;",
    };

    /// <summary>
    /// The Dolby Vision uniforms one frame's curves read, each sized to what the emitted program
    /// indexes rather than to the largest curve the format allows.
    /// </summary>
    internal static IReadOnlyList<VideoShaderUniform> DolbyVision(
        DolbyVisionMetadata metadata,
        bool composeEnhancementLayer)
    {
        List<VideoShaderUniform> uniforms =
        [
            new(DoviParams, declarations[DoviParams]),
            new(DoviNonlinearMatrix, declarations[DoviNonlinearMatrix]),
            new(DoviLinearMatrix, declarations[DoviLinearMatrix]),
        ];
        if (composeEnhancementLayer)
        {
            uniforms.Add(new(DoviNlqOffset, declarations[DoviNlqOffset]));
            uniforms.Add(new(DoviNlqSlope, declarations[DoviNlqSlope]));
            uniforms.Add(new(DoviNlqThreshold, declarations[DoviNlqThreshold]));
        }
        DolbyVisionCurveShape[] shapes = VideoColorStages.DescribeDolbyVision(metadata);
        for (int component = 0; component < shapes.Length; component++)
        {
            DolbyVisionCurveShape shape = shapes[component];
            if (shape.Pivots)
            {
                uniforms.Add(new(
                    DoviPivots[component],
                    $"uniform float {DoviPivots[component]}[{shape.PieceCount + 1}];"));
            }

            if (shape.Polynomial)
            {
                uniforms.Add(new(
                    DoviPolynomial[component],
                    $"uniform float3 {DoviPolynomial[component]}[{shape.PieceCount}];"));
            }

            if (shape.MmrOrder > 0)
            {
                uniforms.Add(new(
                    DoviMmrConstants[component],
                    $"uniform float {DoviMmrConstants[component]}[{shape.PieceCount}];"));
                uniforms.Add(new(
                    DoviMmrCoefficients[component],
                    $"uniform float4 {DoviMmrCoefficients[component]}[{shape.PieceCount * shape.MmrOrder * 2}];"));
            }
        }

        return uniforms;
    }

    internal static string Declaration(string name) =>
        declarations.TryGetValue(name, out string? line)
            ? line
            : throw new ArgumentOutOfRangeException(nameof(name), name, "No such video shader uniform.");
}

/// <summary>A uniform a stage reads, together with the line that declares it.</summary>
internal readonly record struct VideoShaderUniform(string Name, string Declaration);

/// <summary>Names of the shader children the source stages read.</summary>
internal static class VideoShaderChildren
{
    public const string Luma = "luma";
    public const string Cb = "cb";
    public const string Cr = "cr";
    public const string Chroma = "chroma";
    public const string Image = "image";

    /// <summary>The scene buffer a filter reads when the picture is drawn in two passes.</summary>
    public const string Source = "source";
    public const string PreviousSamples = "previousSamples";
}

/// <summary>
/// A compiled fragment program together with what it expects to be given. Stages contribute their
/// own declarations, so a program never carries a uniform nothing reads and the caller never has
/// to guess which ones to set.
/// </summary>
internal sealed class VideoShaderProgram
{
    private VideoShaderProgram(SKRuntimeEffect effect, IReadOnlyList<string> uniforms, IReadOnlyList<string> children)
    {
        Effect = effect;
        Uniforms = uniforms;
        Children = children;
    }

    public SKRuntimeEffect Effect { get; }

    public IReadOnlyList<string> Uniforms { get; }

    public IReadOnlyList<string> Children { get; }

    public static VideoShaderProgram? Compile(
        string source,
        IReadOnlyList<string> uniforms,
        IReadOnlyList<string> children)
    {
        SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(source, out string errors);
        if (effect is null)
        {
            RendererLog.Error("Video.Shader", $"A video shader did not compile: {errors}");
            RendererLog.Debug("Video.Shader", source);
        }

        return effect is null ? null : new VideoShaderProgram(effect, uniforms, children);
    }

    /// <summary>Builds the shader, taking only the values this program actually declared.</summary>
    public SKShader? ToShader(
        IReadOnlyDictionary<string, float[]> values,
        IReadOnlyDictionary<string, SKShader> childShaders) =>
        ToShader(values, childShaders, out _);

    public SKShader? ToShader(
        IReadOnlyDictionary<string, float[]> values,
        IReadOnlyDictionary<string, SKShader> childShaders,
        out string failure)
    {
        var uniforms = new SKRuntimeEffectUniforms(Effect);
        foreach (string name in Uniforms)
        {
            if (!values.TryGetValue(name, out float[]? value))
            {
                failure = $"uniform '{name}' had no value";
                return null;
            }
            uniforms[name] = value;
        }

        var effectChildren = new SKRuntimeEffectChildren(Effect);
        foreach (string name in Children)
        {
            if (!childShaders.TryGetValue(name, out SKShader? child))
            {
                failure = $"child shader '{name}' was missing";
                return null;
            }
            effectChildren[name] = child;
        }

        SKShader? shader = Effect.ToShader(uniforms, effectChildren);
        failure = shader is null ? "the graphics backend rejected the runtime effect" : string.Empty;
        return shader;
    }
}

/// <summary>Assembles a program out of stage fragments, in the order they call each other.</summary>
internal sealed class VideoShaderBuilder
{
    private static readonly object cacheSync = new();
    private static readonly Dictionary<ProgramKey, VideoShaderProgram?> cache = new();

    private readonly List<string> stages = [];
    private readonly List<string> uniforms = [];
    private readonly List<string> declarations = [];
    private readonly List<string> children = [];
    private readonly HashSet<string> declaredUniforms = new(StringComparer.Ordinal);
    private readonly HashSet<string> declaredChildren = new(StringComparer.Ordinal);

    public VideoShaderBuilder AddChild(string name)
    {
        if (declaredChildren.Add(name))
            children.Add(name);
        return this;
    }

    public VideoShaderBuilder AddUniform(string name) =>
        AddUniform(name, VideoShaderUniforms.Declaration(name));

    public VideoShaderBuilder AddUniform(VideoShaderUniform uniform) =>
        AddUniform(uniform.Name, uniform.Declaration);

    public VideoShaderBuilder AddUniforms(IEnumerable<string> names)
    {
        foreach (string name in names)
            AddUniform(name);
        return this;
    }

    public VideoShaderBuilder AddUniforms(IEnumerable<VideoShaderUniform> values)
    {
        foreach (VideoShaderUniform value in values)
            AddUniform(value);
        return this;
    }

    private VideoShaderBuilder AddUniform(string name, string declaration)
    {
        if (declaredUniforms.Add(name))
        {
            uniforms.Add(name);
            declarations.Add(declaration);
        }

        return this;
    }

    /// <summary>Appends one stage. Stages must be added before the stages that call them.</summary>
    public VideoShaderBuilder AddStage(string function)
    {
        stages.Add(function);
        return this;
    }

    public VideoShaderProgram? Build(string main)
    {
        var key = new ProgramKey(uniforms, declarations, children, stages, main);
        lock (cacheSync)
        {
            if (cache.TryGetValue(key, out VideoShaderProgram? program))
                return program;

            ProgramKey storedKey = key.Snapshot();
            program = VideoShaderProgram.Compile(BuildSource(main), storedKey.Uniforms, storedKey.Children);
            cache.Add(storedKey, program);
            return program;
        }
    }

    internal string BuildSource(string main)
    {
        var source = new StringBuilder();
        foreach (string declaration in declarations)
            source.AppendLine(declaration);
        foreach (string name in children)
            source.AppendLine($"uniform shader {name};");

        source.AppendLine();
        foreach (string stage in stages)
            source.AppendLine(stage).AppendLine();
        source.AppendLine(main);
        return source.ToString();
    }

    private readonly record struct ProgramKey(
        IReadOnlyList<string> Uniforms,
        IReadOnlyList<string> Declarations,
        IReadOnlyList<string> Children,
        IReadOnlyList<string> Stages,
        string Main)
    {
        public ProgramKey Snapshot() => new(
            [.. Uniforms], [.. Declarations], [.. Children], [.. Stages], Main);

        public bool Equals(ProgramKey other) =>
            StringComparer.Ordinal.Equals(Main, other.Main) &&
            SequenceEqual(Uniforms, other.Uniforms) &&
            SequenceEqual(Declarations, other.Declarations) &&
            SequenceEqual(Children, other.Children) &&
            SequenceEqual(Stages, other.Stages);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Main, StringComparer.Ordinal);
            AddHash(ref hash, Uniforms);
            AddHash(ref hash, Declarations);
            AddHash(ref hash, Children);
            AddHash(ref hash, Stages);
            return hash.ToHashCode();
        }

        private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left.Count != right.Count)
                return false;

            for (int index = 0; index < left.Count; index++)
            {
                if (!StringComparer.Ordinal.Equals(left[index], right[index]))
                    return false;
            }
            return true;
        }

        private static void AddHash(ref HashCode hash, IReadOnlyList<string> values)
        {
            hash.Add(values.Count);
            for (int index = 0; index < values.Count; index++)
                hash.Add(values[index], StringComparer.Ordinal);
        }
    }
}
