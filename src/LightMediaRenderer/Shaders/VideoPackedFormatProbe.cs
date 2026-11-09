using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using SkiaSharp;

namespace LightMediaRenderer.Shaders;

/// <summary>
/// Establishes whether 16-bit RGBA raster images keep their precision when the graphics backend
/// samples them. Where they would not, frames for the shader pipeline are taken apart into byte
/// pairs, and other images are wrapped as half floats.
/// </summary>
/// <remarks>
/// Skia uploads a raster image whose color type the backend holds no texture format for as RGBA8888
/// without failing, and backends differ in, or misreport, their 16-bit normalized formats. A ramp
/// finer than an 8-bit step therefore goes through the same upload as a photo and is read back
/// amplified, so the answer covers the route the pixels take rather than the advertised formats.
/// Half floats keep at least 11 significant bits, which every OpenGL ES 3 and WebGL 2 context holds.
/// </remarks>
public static class VideoPackedFormatProbe
{
    private const int Size = 16;
    private const int BaseCode = 4096;
    private const int StepCode = 64;
    private const int Tolerance = 3;

    /// <summary>Unanswered probes before the native format is taken on trust rather than asked again.</summary>
    private const int MaxUnanswered = 8;

    private const string AmplifySource = """
        uniform shader image;
        uniform float base;
        uniform float scale;

        half4 main(float2 coord) {
            float3 sample = float3(image.eval(coord).rgb);
            return half4(half3(saturate((sample - base) * scale)), 1);
        }
        """;

    private static readonly object sync = new();
    private static GRContext? probedContext;
    private static bool? halfFloat;
    private static int unanswered;
    private static SKRuntimeEffect? amplify;

    private enum ProbeOutcome
    {
        Displayable,
        NotDisplayable,

        /// <summary>The scratch target or readback failed, which is not an answer about the upload.</summary>
        Unanswered,
    }

    /// <summary>Whether 16-bit RGBA pixels are wrapped as half floats for this context.</summary>
    public static bool UsesHalfFloat(GRContext? context)
    {
        if (context is null)
            return false;

        lock (sync)
        {
            if (!ReferenceEquals(probedContext, context))
            {
                probedContext = context;
                halfFloat = null;
                unanswered = 0;
            }

            if (halfFloat is { } known)
                return known;

            ProbeOutcome native = Probe(context, halfFloatSamples: false);
            if (native == ProbeOutcome.Displayable)
            {
                RendererLog.Information("Video.Planes", "16-bit RGBA uploads keep their precision on this backend.");
                halfFloat = false;
                return false;
            }

            if (native == ProbeOutcome.Unanswered)
            {
                if (++unanswered < MaxUnanswered)
                    return false;
                RendererLog.Warning("Video.Planes",
                    "The backend never completed the 16-bit RGBA probe; those uploads are taken on trust.");
                halfFloat = false;
                return false;
            }

            ProbeOutcome half = Probe(context, halfFloatSamples: true);
            if (half == ProbeOutcome.NotDisplayable)
            {
                RendererLog.Warning("Video.Planes",
                    "Neither 16-bit RGBA nor half-float uploads keep their precision on this backend.");
                halfFloat = false;
                return false;
            }

            RendererLog.Information("Video.Planes", half == ProbeOutcome.Displayable
                ? "16-bit RGBA uploads lose precision on this backend; using byte pairs, or half floats outside the pipeline."
                : "16-bit RGBA uploads lose precision on this backend; using byte pairs, or half floats unverified outside the pipeline.");
            halfFloat = true;
            return true;
        }
    }

    /// <summary>
    /// Wraps packed pixels without copying them, or as a half-float copy where 16-bit RGBA would
    /// lose precision in this context. The caller keeps the pixels alive while the image refers to them.
    /// </summary>
    public static SKImage? CreateRaster(SKImageInfo info, IntPtr pixels, int rowBytes, GRContext? context) =>
        ConvertsToHalfFloat(info.ColorType, context)
            ? CreateHalfFloatRaster(info, pixels, rowBytes)
            : SKImage.FromPixels(info, pixels, rowBytes);

    public static bool ConvertsToHalfFloat(SKColorType colorType, GRContext? context) =>
        colorType == SKColorType.Rgba16161616 && UsesHalfFloat(context);

    /// <summary>
    /// Whether 16-bit RGBA frames are taken apart into byte pairs for the shader pipeline in this context:
    /// where 16-bit RGBA uploads lose precision, and where shader samples are 16-bit floats, which only hold a
    /// 16-bit sample exactly in two halves.
    /// </summary>
    public static bool UsesBytePairs(GRContext? context) =>
        context is not null && (UsesHalfFloat(context) || VideoSamplePrecisionProbe.RoundsToHalf(context));

    /// <summary>
    /// How a packed frame's pixels are wrapped for the shader pipeline in this context: in their own
    /// format, or for 16-bit RGBA taken apart, as the same bytes read as 8-bit RGBA twice as wide, each
    /// sample a byte pair. Only the pipeline reads byte pairs, so a picture Skia draws itself keeps its
    /// own format.
    /// </summary>
    public static SKImageInfo FrameImageInfo(VideoFrame frame, GRContext? context, SKAlphaType alphaType)
    {
        if (frame.Format == VideoPixelFormat.Rgba64 && UsesBytePairs(context))
            return new SKImageInfo(frame.Width * 2, frame.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        return new SKImageInfo(frame.Width, frame.Height, frame.Format switch
        {
            VideoPixelFormat.Rgba64 => SKColorType.Rgba16161616,
            VideoPixelFormat.RgbaF16 => SKColorType.RgbaF16,
            _ => SKColorType.Bgra8888,
        }, alphaType);
    }

    /// <summary>Copies 16-bit RGBA pixels into an image of the same values stored as half floats.</summary>
    public static unsafe SKImage? CreateHalfFloatRaster(SKImageInfo info, IntPtr pixels, int rowBytes)
    {
        if (info.ColorType != SKColorType.Rgba16161616)
            throw new ArgumentException("Only 16-bit RGBA pixels have a half-float representation here.", nameof(info));
        if (pixels == IntPtr.Zero || info.Width <= 0 || info.Height <= 0)
            return null;

        SKImageInfo target = info.WithColorType(SKColorType.RgbaF16);
        int targetRowBytes = target.RowBytes;
        SKData data = SKData.Create(checked((long)targetRowBytes * info.Height));
        try
        {
            byte* source = (byte*)pixels;
            byte* destination = (byte*)data.Data;
            int width = info.Width;
            int height = info.Height;
            int bands = OperatingSystem.IsBrowser() ? 1 : Math.Clamp(height / 256, 1, Math.Min(Environment.ProcessorCount, 16));
            if (bands == 1)
                ConvertRows(source, rowBytes, destination, targetRowBytes, width, 0, height);
            else
            {
                Parallel.For(0, bands, band =>
                {
                    int first = (int)((long)height * band / bands);
                    int last = (int)((long)height * (band + 1) / bands);
                    ConvertRows(source, rowBytes, destination, targetRowBytes, width, first, last);
                });
            }

            return SKImage.FromPixels(target, data, targetRowBytes);
        }
        finally
        {
            data.Dispose();
        }
    }

    private static unsafe void ConvertRows(byte* source, int sourceRowBytes, byte* destination, int destinationRowBytes,
        int width, int firstRow, int lastRow)
    {
        ReadOnlySpan<ushort> table = HalfFloatTable.Values;
        int samples = width * 4;
        for (int row = firstRow; row < lastRow; row++)
        {
            var input = new ReadOnlySpan<ushort>(source + (long)row * sourceRowBytes, samples);
            var output = new Span<ushort>(destination + (long)row * destinationRowBytes, samples);
            for (int index = 0; index < samples; index++)
                output[index] = table[input[index]];
        }
    }

    /// <summary>
    /// Uploads a ramp of steps a quarter of an 8-bit step apart, with each channel in a different
    /// order, and checks that an amplified rendering of it comes back in steps.
    /// </summary>
    private static unsafe ProbeOutcome Probe(GRContext context, bool halfFloatSamples)
    {
        try
        {
            SKRuntimeEffect? effect = amplify ??= CreateEffect();
            if (effect is null)
                return ProbeOutcome.Unanswered;

            var samples = new ushort[Size * 4];
            for (int column = 0; column < Size; column++)
            {
                samples[column * 4] = Encode(StepIndex(column, 0), halfFloatSamples);
                samples[column * 4 + 1] = Encode(StepIndex(column, 1), halfFloatSamples);
                samples[column * 4 + 2] = Encode(StepIndex(column, 2), halfFloatSamples);
                samples[column * 4 + 3] = halfFloatSamples ? BitConverter.HalfToUInt16Bits(Half.One) : ushort.MaxValue;
            }

            var info = new SKImageInfo(Size, 1, halfFloatSamples ? SKColorType.RgbaF16 : SKColorType.Rgba16161616,
                SKAlphaType.Opaque);
            using SKImage raster = SKImage.FromPixelCopy(info, MemoryMarshal.AsBytes(samples.AsSpan()), info.RowBytes);

            // Browser photo rasters are left to Skia's upload cache; elsewhere they are promoted.
            using SKImage? promoted = OperatingSystem.IsBrowser() ? null : raster.ToTextureImage(context, false, false);
            SKImage source = promoted ?? raster;

            var targetInfo = new SKImageInfo(Size, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
            using SKSurface? surface = SKSurface.Create(context, false, targetInfo);
            if (surface is null)
            {
                RendererLog.Debug("Video.Planes", "No scratch target to probe 16-bit RGBA uploads into.");
                return ProbeOutcome.Unanswered;
            }

            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["base"] = BaseCode / 65535f,
                ["scale"] = 65535f / (StepCode * Size),
            };
            using SKShader image = source.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
            var children = new SKRuntimeEffectChildren(effect) { ["image"] = image };
            using SKShader amplified = effect.ToShader(uniforms, children);
            using var paint = new SKPaint { Shader = amplified, BlendMode = SKBlendMode.Src, IsAntialias = false };
            surface.Canvas.DrawRect(SKRect.Create(Size, 1), paint);

            var pixels = new byte[Size * 4];
            fixed (byte* readback = pixels)
            {
                if (!surface.ReadPixels(targetInfo, (IntPtr)readback, targetInfo.RowBytes, 0, 0))
                {
                    RendererLog.Debug("Video.Planes", "The 16-bit RGBA probe target refused readback.");
                    return ProbeOutcome.Unanswered;
                }
            }

            for (int column = 0; column < Size; column++)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    int expected = (int)Math.Round(StepIndex(column, channel) * 255.0 / Size);
                    int actual = pixels[column * 4 + channel];
                    if (Math.Abs(actual - expected) > Tolerance)
                    {
                        RendererLog.Information("Video.Planes",
                            $"{info.ColorType} uploads are not faithful here at column {column}, channel {channel}: " +
                            $"expected {expected}, got {actual}.");
                        return ProbeOutcome.NotDisplayable;
                    }
                }
            }

            return ProbeOutcome.Displayable;
        }
        catch (Exception exception)
        {
            RendererLog.Warning("Video.Planes", "The 16-bit RGBA probe failed.", exception);
            return ProbeOutcome.Unanswered;
        }
    }

    private static SKRuntimeEffect? CreateEffect()
    {
        SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(AmplifySource, out string errors);
        if (effect is null)
            RendererLog.Error("Video.Shader", $"The 16-bit RGBA probe shader did not compile: {errors}");
        return effect;
    }

    // 5 is coprime with the ramp length, so the blue channel visits every step in a third order.
    private static int StepIndex(int column, int channel) => channel switch
    {
        0 => column,
        1 => Size - 1 - column,
        _ => column * 5 % Size,
    };

    private static ushort Encode(int step, bool halfFloatSamples)
    {
        int code = BaseCode + step * StepCode;
        return halfFloatSamples ? HalfFloatTable.Values[code] : (ushort)code;
    }


    private static class HalfFloatTable
    {
        public static readonly ushort[] Values = Create();

        private static ushort[] Create()
        {
            var values = new ushort[65536];
            for (int code = 0; code < values.Length; code++)
                values[code] = BitConverter.HalfToUInt16Bits((Half)(code / 65535.0));
            return values;
        }
    }
}
