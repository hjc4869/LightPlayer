using System;
using System.Collections.Generic;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Rendering;
using SkiaSharp;

namespace LightMediaRenderer.Shaders;

internal static class VideoPhotoGainMap
{
    public const string Child = "photoGainMap";

    private static readonly VideoShaderUniform[] Uniforms =
    [
        new("photoGainWeight", "uniform float4 photoGainWeight;"),
        new("photoGainMin", "uniform float3 photoGainMin;"),
        new("photoGainMax", "uniform float3 photoGainMax;"),
        new("photoGainGamma", "uniform float3 photoGainGamma;"),
        new("photoOffsetSdr", "uniform float3 photoOffsetSdr;"),
        new("photoOffsetHdr", "uniform float3 photoOffsetHdr;"),
        new("photoToBase", "uniform float3 photoToBase[3];"),
        new("photoToBt2020", "uniform float3 photoToBt2020[3];"),
    ];

    private const string Stage = """
        float3 processPhotoSignal(float2 coord) {
            float3 standard = processSignal(coord);
            float3 base = float3(dot(photoToBase[0], standard),
                                dot(photoToBase[1], standard),
                                dot(photoToBase[2], standard));
            float3 encoded = clamp(float3(photoGainMap.eval(coord).rgb), float3(0.0), float3(1.0));
            float3 logarithm = mix(photoGainMin, photoGainMax, pow(encoded, float3(1.0) / photoGainGamma));
            if (photoGainWeight.y > 0.0)
                logarithm = log2(float3(1.0) + (photoGainWeight.y - 1.0) * pow(encoded, float3(1.961)));
            if (photoGainWeight.z > 0.0)
                base = (base + photoOffsetHdr) * exp2(-logarithm) - photoOffsetSdr;
            float3 adapted = photoGainWeight.x > 0.0
                ? (max(base, float3(0.0)) + photoOffsetSdr)
                    * exp2(logarithm * photoGainWeight.x) - photoOffsetHdr
                : base;
            return float3(dot(photoToBt2020[0], adapted),
                          dot(photoToBt2020[1], adapted),
                          dot(photoToBt2020[2], adapted));
        }
        """;

    public const string MainScene = """
        float4 main(float2 coord) {
            return float4(toScene(toneMap(decodeTransfer(processPhotoSignal(coord)))), 1.0);
        }
        """;

    public const string MainFused = """
        float4 main(float2 coord) {
            float3 scene = toScene(toneMap(decodeTransfer(processPhotoSignal(coord))));
            return float4(encodeOutput(fitRange(convertGamut(scene)), coord), 1.0);
        }
        """;

    public static float Weight(PhotoGainMap? gainMap, VideoOutputColorSpace target,
        bool hdrPassthrough, VideoOutputColorVolume? volume)
    {
        if (gainMap is null || !target.IsExtendedRange() || !hdrPassthrough ||
            volume is not { ReferenceWhiteScale: > 0 } display ||
            !double.IsFinite(display.ReferenceWhiteScale.Value))
            return 0;

        return display.ToneMapping switch
        {
            VideoToneMappingMode.Platform => 1,
            VideoToneMappingMode.Client => gainMap.Weight(display.HeadroomRatio ?? 1),
            _ => 0,
        };
    }

    public static bool Configure(PhotoGainMap? gainMap, VideoOutputColorSpace target,
        bool hdrPassthrough, VideoOutputColorVolume? volume, Dictionary<string, float[]> values)
    {
        float weight = Weight(gainMap, target, hdrPassthrough, volume);
        if (gainMap is null || weight <= 0 && !gainMap.BaseIsHdr)
            return false;

        values["photoGainWeight"] = [weight, gainMap.IsApple ? MathF.Pow(2, gainMap.CapacityMax) : 0,
            gainMap.BaseIsHdr ? 1 : 0, 0];
        values["photoGainMin"] = [gainMap.Minimum.X, gainMap.Minimum.Y, gainMap.Minimum.Z];
        values["photoGainMax"] = [gainMap.Maximum.X, gainMap.Maximum.Y, gainMap.Maximum.Z];
        values["photoGainGamma"] = [gainMap.Gamma.X, gainMap.Gamma.Y, gainMap.Gamma.Z];
        values["photoOffsetSdr"] = [gainMap.OffsetSdr.X, gainMap.OffsetSdr.Y, gainMap.OffsetSdr.Z];
        values["photoOffsetHdr"] = [gainMap.OffsetHdr.X, gainMap.OffsetHdr.Y, gainMap.OffsetHdr.Z];
        values["photoToBase"] = gainMap.ToBase.ToArray();
        values["photoToBt2020"] = gainMap.ToBt2020.ToArray();
        return true;
    }

    public static void AddStage(VideoShaderBuilder builder) =>
        builder.AddChild(Child).AddUniforms(Uniforms).AddStage(Stage);

    public static unsafe SKImage? CreateImage(PhotoGainMap gainMap)
    {
        var info = new SKImageInfo(gainMap.Width, gainMap.Height,
            gainMap.BitsPerComponent == 16 ? SKColorType.Rgba16161616 : SKColorType.Rgba8888, SKAlphaType.Opaque);
        fixed (byte* pixels = gainMap.Pixels.Span)
            return SKImage.FromPixelCopy(info, (IntPtr)pixels, info.RowBytes);
    }

    public static SKShader? CreateShader(in VideoRenderRequest request, VideoColorPlan plan,
        int width = 0, int height = 0)
    {
        if (!plan.PhotoGainMap || request.Frame.GainMap is not { } gainMap)
            return null;

        using SKImage? owned = request.ImageCache is null ? CreateImage(gainMap) : null;
        SKRect? sampleBounds = null;
        SKImage? image = request.ImageCache?.GetGainMapImage(out sampleBounds) ?? owned;
        SKMatrix local = SKMatrix.CreateScale((float)(width > 0 ? width : request.Frame.Width) / gainMap.Width,
            (float)(height > 0 ? height : request.Frame.Height) / gainMap.Height);
        if (request.PhotoSourceBounds is { Width: > 0, Height: > 0 } bounds)
        {
            float horizontal = request.Frame.Width / bounds.Width;
            float vertical = request.Frame.Height / bounds.Height;
            local = SKMatrix.CreateScaleTranslation(horizontal * request.PhotoSourceSize.Width / gainMap.Width,
                vertical * request.PhotoSourceSize.Height / gainMap.Height,
                -bounds.Left * horizontal, -bounds.Top * vertical);
        }
        if (sampleBounds is { } samples && image is not null)
            local = SKMatrix.Concat(local, SKMatrix.CreateScaleTranslation(samples.Width / image.Width,
                samples.Height / image.Height, samples.Left, samples.Top));
        return image?.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            local);
    }
}