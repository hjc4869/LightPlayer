using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using LightMediaRenderer.Contracts;
using LightStudio.Logging;
using SkiaSharp;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal sealed partial class BrowserVideoPlanes(int resource, int planeCount, bool packed, bool rgbPlanes) : IVideoPlaneSource, IMediaResourceOwner, IDisposable
{
    private static SKRuntimeEffect? packedEffect;
    private static SKRuntimeEffect? wideEffect;
    private static SKRuntimeEffect? rgbEffect;
    internal static int ReleasedCount;
    private readonly SKImage?[] images = new SKImage?[planeCount];
    private string? contextIdentity;
    private int textureToken;
    private int released;
    public bool UseBytePairPlanes => true;
    public bool ImagesAreBorrowed => true;
    public SKImage? CreatePlane(int index, SKImageInfo info) => CreatePlane(index, info, null);

    internal bool AcquireExternalTexture(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(released != 0, this);
        _ = CurrentContext();
        using var json = JsonDocument.Parse(UploadPlane(resource, 0, frame.Width, frame.Height, 4));
        var root = json.RootElement;
        textureToken = root.GetProperty("token").GetInt32();
        frame.ExternalHandle = root.GetProperty("name").GetUInt32();
        return true;
    }

    public SKImage? CreatePlane(int index, SKImageInfo info, GRContext? context)
    {
        ObjectDisposedException.ThrowIf(released != 0, this);
        if (context is null) throw new NotSupportedException("Browser video planes require the GPU renderer.");
        string current = CurrentContext();
        if (contextIdentity != current)
        {
            foreach (var image in images) image?.Dispose();
            Array.Clear(images);
            contextIdentity = current;
        }
        if (images[index] is { } existing) return existing;
        if (rgbPlanes) return images[index] = CreateRgbImage(info, context);
        bool widePacked = info.ColorType == SKColorType.Rgba16161616;
        int textureWidth = widePacked ? checked(info.Width * 2) : info.Width;
        using var json = JsonDocument.Parse(UploadPlane(resource, index, textureWidth, info.Height, packed || widePacked ? 4 : info.BytesPerPixel));
        var root = json.RootElement;
        int token = root.GetProperty("token").GetInt32();
        try
        {
            using var texture = new GRBackendTexture(textureWidth, info.Height, false, new GRGlTextureInfo(0x0DE1,
                root.GetProperty("name").GetUInt32(), root.GetProperty("format").GetUInt32()));
            context.ResetContext();
            var image = SKImage.FromTexture(context, texture, GRSurfaceOrigin.TopLeft, packed || widePacked ? SKColorType.Rgba8888 : info.ColorType,
                SKAlphaType.Opaque, null, static value => Observe(ReleaseTextureAsync((int)value!)), token);
            if (image is null) throw new InvalidOperationException("Skia rejected the uploaded video plane.");
            token = 0;
            if (packed || widePacked)
            {
                using var source = image;
                var effect = widePacked ? wideEffect ??= CreatePackedEffect(true) : packedEffect ??= CreatePackedEffect(false);
                using var samples = source.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest));
                using var shader = effect.ToShader(new SKRuntimeEffectUniforms(effect), new SKRuntimeEffectChildren(effect) { ["samples"] = samples });
                using var surface = SKSurface.Create(context, false, new SKImageInfo(info.Width, info.Height,
                    widePacked || info.AlphaType != SKAlphaType.Opaque ? SKColorType.RgbaF16 : SKColorType.Rgba8888, SKAlphaType.Premul));
                if (surface is null || shader is null) throw new InvalidOperationException("Could not create the packed photo GPU conversion.");
                using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
                surface.Canvas.DrawPaint(paint);
                image = surface.Snapshot();
            }
            images[index] = image;
            return image;
        }
        catch { if (token != 0) ReleaseTexture(token); throw; }
    }

    private static SKRuntimeEffect CreatePackedEffect(bool wide)
    {
        string source = wide ? """
            uniform shader samples;
            half4 main(float2 position) {
                float2 coordinate = float2(floor(position.x) * 2 + 0.5, position.y);
                float4 first = float4(samples.eval(coordinate));
                float4 second = float4(samples.eval(coordinate + float2(1, 0)));
                float4 color = float4(first.r + first.g * 256, first.b + first.a * 256,
                    second.r + second.g * 256, second.b + second.a * 256) * (255.0 / 65535.0);
                return half4(color.rgb * color.a, color.a);
            }
            """ : """
            uniform shader samples;
            half4 main(float2 position) {
                half4 color = samples.eval(position).bgra;
                return half4(color.rgb * color.a, color.a);
            }
            """;
        return SKRuntimeEffect.CreateShader(source, out string errors) ?? throw new InvalidOperationException(errors);
    }

    private SKImage CreateRgbImage(SKImageInfo info, GRContext context)
    {
        var components = new SKImage?[4];
        var shaders = new SKShader?[4];
        try
        {
            for (int plane = 0; plane < components.Length; plane++)
            {
                using var json = JsonDocument.Parse(UploadPlane(resource, plane, info.Width, info.Height, 2));
                var root = json.RootElement;
                int token = root.GetProperty("token").GetInt32();
                try
                {
                    using var texture = new GRBackendTexture(info.Width, info.Height, false, new GRGlTextureInfo(0x0DE1,
                        root.GetProperty("name").GetUInt32(), root.GetProperty("format").GetUInt32()));
                    context.ResetContext();
                    components[plane] = SKImage.FromTexture(context, texture, GRSurfaceOrigin.TopLeft, SKColorType.Rg88,
                        SKAlphaType.Opaque, null, static value => Observe(ReleaseTextureAsync((int)value!)), token)
                        ?? throw new InvalidOperationException("Skia rejected a photo component plane.");
                }
                catch { ReleaseTexture(token); throw; }
                shaders[plane] = components[plane]!.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest));
            }
            rgbEffect ??= SKRuntimeEffect.CreateShader("""
                uniform shader green;
                uniform shader blue;
                uniform shader red;
                uniform shader alpha;
                float sample16(float2 bytes) { return dot(bytes, float2(1, 256)) * (255.0 / 65535.0); }
                half4 main(float2 position) {
                    float3 color = float3(sample16(red.eval(position).rg), sample16(green.eval(position).rg), sample16(blue.eval(position).rg));
                    float opacity = sample16(alpha.eval(position).rg);
                    return half4(color * opacity, opacity);
                }
                """, out string errors) ?? throw new InvalidOperationException(errors);
            using var shader = rgbEffect.ToShader(new SKRuntimeEffectUniforms(rgbEffect), new SKRuntimeEffectChildren(rgbEffect)
            {
                ["green"] = shaders[0], ["blue"] = shaders[1], ["red"] = shaders[2], ["alpha"] = shaders[3],
            });
            using var surface = SKSurface.Create(context, false, new SKImageInfo(info.Width, info.Height, SKColorType.RgbaF16, SKAlphaType.Premul));
            if (surface is null || shader is null) throw new InvalidOperationException("Could not create the photo component GPU conversion.");
            using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
            surface.Canvas.DrawPaint(paint);
            return surface.Snapshot();
        }
        finally
        {
            foreach (var shader in shaders) shader?.Dispose();
            foreach (var image in components) image?.Dispose();
        }
    }

    public void Release(MediaResourceHandle handle) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) != 0) return;
        Interlocked.Increment(ref ReleasedCount);
        Observe(BrowserMainThread.InvokeAsync(async () =>
        {
            foreach (var image in images) image?.Dispose();
            Array.Clear(images);
            if (textureToken != 0) ReleaseTexture(textureToken);
            await ReleaseFrame(resource);
        }));
    }

    internal static Task ReleaseAsync(int resource) => BrowserMainThread.InvokeAsync(() => ReleaseFrame(resource));
    private static Task ReleaseTextureAsync(int token) => BrowserMainThread.InvokeAsync(() => { ReleaseTexture(token); return Task.CompletedTask; });
    private static void Observe(Task operation) => _ = ObserveAsync(operation);
    private static async Task ObserveAsync(Task operation)
    {
        try { await operation; }
        catch (Exception exception) { AppLog.Error("Web.Video", "Could not retire video resources.", exception); }
    }

    [JSImport("uploadPlane", MediaWorkerConnection.ModuleName)]
    private static partial string UploadPlane(int resource, int index, int width, int height, int channels);
    [JSImport("currentContext", MediaWorkerConnection.ModuleName)]
    private static partial string CurrentContext();
    [JSImport("releaseTexture", MediaWorkerConnection.ModuleName)]
    private static partial void ReleaseTexture(int token);
    [JSImport("releaseFrame", MediaWorkerConnection.ModuleName)]
    private static partial Task ReleaseFrame(int resource);
}