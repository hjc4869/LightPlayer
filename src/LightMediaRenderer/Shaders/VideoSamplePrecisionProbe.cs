using System;
using SkiaSharp;

namespace LightMediaRenderer.Shaders;

/// <summary>
/// Establishes whether the samples a shader reads from its children keep 32-bit precision.
/// </summary>
/// <remarks>
/// SkSL child samples are <c>half4</c>, which Metal and many OpenGL ES and Vulkan drivers evaluate as
/// 16-bit floats where desktop drivers compute 32 bits. Eleven significant bits cannot carry a 16-bit
/// sample, and PQ turns the rounding into a constant luminance error, so on such a backend 16-bit samples
/// travel as byte pairs, whose whole bytes survive the rounding. The probe reads an 8-bit code whose
/// normalized value lies far from every 16-bit float, through the same raw image shader the planes use,
/// and measures how far the sample landed.
/// </remarks>
public static class VideoSamplePrecisionProbe
{
    private const int Size = 4;

    /// <summary>Unanswered probes before full precision is taken on trust rather than asked again.</summary>
    private const int MaxUnanswered = 8;

    // 239/255 is 0.062 of a code from the nearest 16-bit float, and within 1e-5 of a code in 32 bits.
    private const byte Code = 239;

    private const string MeasureSource = """
        uniform shader image;

        half4 main(float2 coord) {
            float sample = float(image.eval(coord).r);
            return half4(half(saturate(abs(sample * 255.0 - 239.0) * 8.0)), 0.0, 0.0, 1.0);
        }
        """;

    private static readonly object sync = new();
    private static GRContext? probedContext;
    private static bool? roundsToHalf;
    private static int unanswered;
    private static SKRuntimeEffect? measure;

    /// <summary>Whether child samples are rounded to 16-bit floats in this context; never on the rasterizer.</summary>
    public static bool RoundsToHalf(GRContext? context)
    {
        if (context is null)
            return false;

        lock (sync)
        {
            if (!ReferenceEquals(probedContext, context))
            {
                probedContext = context;
                roundsToHalf = null;
                unanswered = 0;
            }

            if (roundsToHalf is { } known)
                return known;

            bool? answer = Probe(context);
            if (answer is null)
            {
                if (++unanswered < MaxUnanswered)
                    return false;
                RendererLog.Warning("Video.Planes",
                    "The backend never completed the sample precision probe; full precision is taken on trust.");
                answer = false;
            }
            else
            {
                RendererLog.Information("Video.Planes", answer.Value
                    ? "Shader samples are 16-bit floats on this backend; 16-bit samples are read as byte pairs."
                    : "Shader samples keep 32-bit precision on this backend.");
            }

            roundsToHalf = answer;
            return answer.Value;
        }
    }

    /// <summary>Whether the sample came back rounded, or null when the backend gave no answer.</summary>
    private static unsafe bool? Probe(GRContext context)
    {
        try
        {
            SKRuntimeEffect? effect = measure ??= CreateEffect();
            if (effect is null)
                return null;

            var info = new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var codes = new byte[Size * Size * 4];
            codes.AsSpan().Fill(Code);
            using SKImage raster = SKImage.FromPixelCopy(info, codes, info.RowBytes);

            var targetInfo = new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);
            using SKSurface? surface = SKSurface.Create(context, false, targetInfo);
            if (surface is null)
            {
                RendererLog.Debug("Video.Planes", "No scratch target to probe sample precision into.");
                return null;
            }

            using SKShader image = raster.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
            var children = new SKRuntimeEffectChildren(effect) { ["image"] = image };
            using SKShader measured = effect.ToShader(new SKRuntimeEffectUniforms(effect), children);
            using var paint = new SKPaint { Shader = measured, BlendMode = SKBlendMode.Src, IsAntialias = false };
            surface.Canvas.DrawRect(SKRect.Create(Size, Size), paint);

            var pixels = new byte[Size * Size * 4];
            fixed (byte* readback = pixels)
            {
                if (!surface.ReadPixels(targetInfo, (IntPtr)readback, targetInfo.RowBytes, 0, 0))
                {
                    RendererLog.Debug("Video.Planes", "The sample precision probe target refused readback.");
                    return null;
                }
            }

            // A 16-bit float lands half the output range away, a 32-bit one on zero.
            return pixels[0] >= 64;
        }
        catch (Exception exception)
        {
            RendererLog.Warning("Video.Planes", "The sample precision probe failed.", exception);
            return null;
        }
    }

    private static SKRuntimeEffect? CreateEffect()
    {
        SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(MeasureSource, out string errors);
        if (effect is null)
            RendererLog.Error("Video.Shader", $"The sample precision probe shader did not compile: {errors}");
        return effect;
    }
}
