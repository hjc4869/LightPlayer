using System;
using System.Collections.Generic;
using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Shaders;

internal static class VideoPhotoColor
{
    private static readonly VideoShaderUniform[] Uniforms =
    [
        new("photoCurve", "uniform float4 photoCurve[2];"),
        new("photoColorMatrix", "uniform float3 photoColorMatrix[3];"),
        new("photoLinearScale", "uniform float4 photoLinearScale;"),
    ];

    private const string Stage = """
        float photoToLinear(float signal) {
            if (photoCurve[1].w == 16.0)
                return pqToNits(signal) * photoLinearScale.x;
            if (photoCurve[1].w == 18.0)
                return hlgToScene(signal);
            float magnitude = abs(signal);
            float linear = magnitude < photoCurve[1].x
                ? photoCurve[0].w * magnitude + photoCurve[1].z
                : pow(max(photoCurve[0].y * magnitude + photoCurve[0].z, 0.0), photoCurve[0].x) + photoCurve[1].y;
            return sign(signal) * linear * photoLinearScale.x;
        }
        float3 processSignal(float2 coord) {
            float3 signal = decodeSignal(coord);
            float3 linear = float3(photoToLinear(signal.r), photoToLinear(signal.g), photoToLinear(signal.b));
            linear = float3(dot(photoColorMatrix[0], linear),
                            dot(photoColorMatrix[1], linear),
                            dot(photoColorMatrix[2], linear));
            if (photoCurve[1].w == 18.0) {
                float luminance = dot(float3(0.2627, 0.6780, 0.0593), linear);
                linear *= pow(max(luminance, 1e-6), 0.2) * 1000.0 * photoLinearScale.x;
            }
            return linear;
        }
        """;

    public static bool Configure(PhotoColorProfile? profile, Dictionary<string, float[]> values)
    {
        if (profile is null)
            return false;
        ReadOnlySpan<float> transfer = profile.Transfer.Span;
        values["photoCurve"] = [transfer[0], transfer[1], transfer[2], transfer[3],
            transfer[4], transfer[5], transfer[6], profile.TransferCode];
        values["photoColorMatrix"] = profile.ToBt2020.ToArray();
        values["photoLinearScale"] = [profile.LinearScale, 0, 0, 0];
        return true;
    }

    public static void AddStage(VideoShaderBuilder builder, bool hasAlpha) => builder
        .AddUniforms(Uniforms)
        .AddStage(AlphaStage(hasAlpha))
        .AddStage(Stage);

    public static string AlphaStage(bool hasAlpha) => hasAlpha
        ? "float photoAlpha(float2 coord) { return float(image.eval(coord).a); }"
        : "float photoAlpha(float2 coord) { return 1.0; }";

    public static string Main(bool gainMap, bool output) => $$"""
        float4 main(float2 coord) {
            float opacity = clamp(photoAlpha(coord), 0.0, 1.0);
            float3 scene = toScene(toneMap(decodeTransfer({{(gainMap ? "processPhotoSignal" : "processSignal")}}(coord))));
            return float4({{(output ? "encodeOutput(fitRange(convertGamut(scene)), coord)" : "scene")}} * opacity, opacity);
        }
        """;

    public const string MainOutput = """
        float4 main(float2 coord) {
            float opacity = clamp(float(source.eval(coord).a), 0.0, 1.0);
            float3 scene = opacity > 0.0 ? scaleScene(coord) / opacity : float3(0.0);
            return float4(encodeOutput(fitRange(convertGamut(scene)), coord) * opacity, opacity);
        }
        """;
}