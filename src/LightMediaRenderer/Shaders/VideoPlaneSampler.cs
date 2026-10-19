using System;
using System.Collections.Generic;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Rendering;
using SkiaSharp;

namespace LightMediaRenderer.Shaders;

internal enum VideoSourceVariant
{
    Planar,
    Interleaved,
    WidePlanar,
    WideInterleaved,
    Packed,
    PackedYuv,
}

/// <summary>
/// A frame's planes wrapped as the shader children the source stage reads.
/// </summary>
/// <remarks>
/// Uploading each plane on its own is what lets the decoder skip swscale: 1.5 bytes per pixel for
/// 8-bit 4:2:0 instead of the 4 an interleaved BGRA frame costs. Raw shaders are used throughout,
/// because Skia's color management and alpha handling are both wrong for what is really sample
/// data rather than a picture.
/// </remarks>
internal sealed class VideoPlaneSampler : IDisposable
{
    private readonly SKImage[] images;
    private readonly SKShader[] shaders;
    private readonly bool ownsImages;
    private readonly string childPrefix;

    private VideoPlaneSampler(
        VideoSourceVariant variant,
        SKImage[] images,
        SKShader[] shaders,
        Dictionary<string, SKShader> children,
        bool ownsImages = true,
        bool clampToVisible = false,
        float[]? lumaBounds = null,
        float[]? chromaBounds = null,
        string childPrefix = "",
        float[]? fieldChromaScale = null,
        float[]? fieldChromaSize = null,
        float[]? fieldLumaScale = null,
        bool premultipliedAlpha = false)
    {
        Variant = variant;
        this.images = images;
        this.shaders = shaders;
        this.ownsImages = ownsImages;
        Children = children;
        ClampToVisible = clampToVisible;
        LumaBounds = lumaBounds;
        ChromaBounds = chromaBounds;
        this.childPrefix = childPrefix;
        FieldChromaScale = fieldChromaScale;
        FieldChromaSize = fieldChromaSize;
        FieldLumaScale = fieldLumaScale;
        PremultipliedAlpha = premultipliedAlpha;
    }

    public VideoSourceVariant Variant { get; }

    public IReadOnlyDictionary<string, SKShader> Children { get; }

    private bool ClampToVisible { get; }

    private float[]? LumaBounds { get; }

    private float[]? ChromaBounds { get; }

    private float[]? FieldChromaScale { get; }

    private float[]? FieldChromaSize { get; }

    private float[]? FieldLumaScale { get; }

    private bool PremultipliedAlpha { get; }

    /// <summary>The SkSL that reads this layout.</summary>
    public string Function
    {
        get
        {
            if (PremultipliedAlpha)
                return """
                    float3 sampleSource(float2 coord) {
                        float4 sample = float4(image.eval(coord));
                        return sample.a > 0.0 ? sample.rgb / sample.a : float3(0.0);
                    }
                    """;
            string function = (Variant, ClampToVisible) switch
            {
        (VideoSourceVariant.Planar, true) => VideoColorStages.SamplePlanarClamped,
        (VideoSourceVariant.Interleaved, true) => VideoColorStages.SampleInterleavedClamped,
        (VideoSourceVariant.WidePlanar, true) => VideoColorStages.SampleWidePlanarClamped,
        (VideoSourceVariant.WideInterleaved, true) => VideoColorStages.SampleWideInterleavedClamped,
        (VideoSourceVariant.Planar, false) => VideoColorStages.SamplePlanar,
        (VideoSourceVariant.Interleaved, false) => VideoColorStages.SampleInterleaved,
        (VideoSourceVariant.WidePlanar, false) => VideoColorStages.SampleWidePlanar,
        (VideoSourceVariant.WideInterleaved, false) => VideoColorStages.SampleWideInterleaved,
        _ => VideoColorStages.SamplePacked,
            };
            if (FieldChromaScale is not null)
            {
                function = function.Replace("sampleSource(", "samplePlanes(", StringComparison.Ordinal) +
                    VideoColorStages.SampleInterlacedChroma;
            }
            return childPrefix.Length == 0 ? function : RenameFunction(function, childPrefix);
        }
    }

    /// <summary>Whether the samples still need the color matrix applied to them.</summary>
    public bool NeedsColorMatrix => Variant != VideoSourceVariant.Packed;

    public void AddSamplingUniforms(
        VideoShaderBuilder builder,
        Dictionary<string, float[]> uniforms)
    {
        Add("lumaBounds", LumaBounds);
        Add("chromaBounds", ChromaBounds);
        Add("fieldChromaScale", FieldChromaScale);
        Add("fieldChromaSize", FieldChromaSize);
        Add("fieldLumaScale", FieldLumaScale);

        void Add(string name, float[]? values)
        {
            if (values is null)
                return;
            string uniform = childPrefix + name;
            builder.AddUniform(new VideoShaderUniform(uniform, $"uniform float4 {uniform};"));
            uniforms[uniform] = values;
        }
    }

    public static VideoPlaneSampler? CreatePacked(SKImage image, SKSamplingOptions sampling) =>
        CreatePacked(
            image,
            sampling,
            VideoTextureRect.Whole,
            VideoExternalTextureColorModel.Rgb);

    /// <summary>
    /// Wraps an already interleaved picture, reading it out of the part of <paramref name="image"/>
    /// that <paramref name="source"/> names.
    /// </summary>
    /// <remarks>
    /// A texture a platform window hands back holds the picture somewhere inside a buffer of the
    /// codec's own choosing, and often the other way up. The local matrix carries that, so the
    /// shader body still reads the picture at its own coordinates.
    /// </remarks>
    public static VideoPlaneSampler? CreatePacked(
        SKImage image,
        SKSamplingOptions sampling,
        VideoTextureRect source,
        VideoExternalTextureColorModel colorModel = VideoExternalTextureColorModel.Rgb,
        SKPoint origin = default)
    {
        SKMatrix local = SourceMatrix(source, image.Width, image.Height);
        local.TransX += origin.X;
        local.TransY += origin.Y;
        SKShader? shader = image.ToRawShader(
            SKShaderTileMode.Clamp,
            SKShaderTileMode.Clamp,
            sampling,
            local);

        return shader is null
            ? null
            : new VideoPlaneSampler(
                colorModel == VideoExternalTextureColorModel.Yuv
                    ? VideoSourceVariant.PackedYuv
                    : VideoSourceVariant.Packed,
                [],
                [shader],
                new Dictionary<string, SKShader>(StringComparer.Ordinal)
                {
                    [VideoShaderChildren.Image] = shader,
                },
                premultipliedAlpha: image.AlphaType == SKAlphaType.Premul && colorModel == VideoExternalTextureColorModel.Rgb);
    }

    /// <summary>
    /// The local matrix that puts a picture's own coordinates onto the part of a texture it
    /// occupies. Skia samples at the inverse of a local matrix, so this is built inverted.
    /// </summary>
    private static SKMatrix SourceMatrix(VideoTextureRect source, int width, int height)
    {
        if (source.IsWhole || source.ScaleX == 0 || source.ScaleY == 0)
            return SKMatrix.Identity;

        return SKMatrix.CreateScaleTranslation(
            1 / source.ScaleX,
            1 / source.ScaleY,
            -width * source.OffsetX / source.ScaleX,
            -height * source.OffsetY / source.ScaleY);
    }

    /// <summary>
    /// Wraps a planar frame's planes, or returns null when Skia rejects one of them.
    /// </summary>
    /// <param name="frame">Must stay alive and referenced until the result is disposed.</param>
    /// <param name="planeSource">
    /// Where the planes come from, or null to read them out of the frame's own buffer.
    /// </param>
    public static VideoPlaneSampler? CreatePlanar(
        VideoFrame frame,
        SKSamplingOptions lumaSampling,
        SKSamplingOptions chromaSampling,
        IVideoPlaneSource? planeSource,
        GRContext? grContext,
        string childPrefix = "",
        int coordinateWidth = 0,
        int coordinateHeight = 0,
        SKRectI? sourceBounds = null,
        SKRectI? visibleBounds = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!VideoPlanarLayout.IsPlanar(frame.Format))
            return null;

        planeSource ??= frame.PlaneSource;
        if (planeSource is null && frame.Data == IntPtr.Zero)
            return null;

        VideoPlanarLayout layout = frame.Layout;
        bool wide = layout.BytesPerSample == 2;
        bool bytePairs = wide && (planeSource?.UseBytePairPlanes ?? VideoPlaneFormatProbe.UseBytePairPlanes);
        VideoSourceVariant variant = (layout.InterleavedChroma, bytePairs) switch
        {
            (true, false) => VideoSourceVariant.Interleaved,
            (false, false) => VideoSourceVariant.Planar,
            (true, true) => VideoSourceVariant.WideInterleaved,
            (false, true) => VideoSourceVariant.WidePlanar,
        };

        // At 4:4:4 the chroma planes are the size of the picture, so they want the filter chosen
        // for the picture rather than the one chosen for upsampling.
        bool subsampled = layout.ChromaShiftX > 0 || layout.ChromaShiftY > 0;
        bool fieldChroma = frame.FieldOrder.IsInterlaced() && layout.ChromaShiftY > 0;
        bool interpolateChroma = chromaSampling.Filter != SKFilterMode.Nearest || chromaSampling.UseCubic;
        int chromaScaleX = 1 << layout.ChromaShiftX;
        int chromaScaleY = 1 << layout.ChromaShiftY;
        double layerScaleX = coordinateWidth > frame.Width
            ? coordinateWidth / (double)frame.Width
            : 1.0;
        double layerScaleY = coordinateHeight > frame.Height
            ? coordinateHeight / (double)frame.Height
            : 1.0;
        double layerX = layerScaleX > 1 ? 0.5 - (0.5 / layerScaleX) : 0;
        double layerY = 0;
        (double chromaX, double chromaY) = subsampled &&
            (chromaSampling.Filter != SKFilterMode.Nearest || chromaSampling.UseCubic)
                ? layout.ChromaSiting(frame.ChromaLocation)
                : (0.0, 0.0);
        double combinedScaleX = layerScaleX * chromaScaleX;
        double combinedScaleY = layerScaleY * chromaScaleY;
        var lumaLocal = SKMatrix.CreateScaleTranslation(
            (float)layerScaleX,
            (float)layerScaleY,
            (float)(-layerScaleX * layerX),
            (float)(-layerScaleY * layerY));
        var chromaLocal = SKMatrix.CreateScaleTranslation(
            (float)combinedScaleX,
            (float)combinedScaleY,
            (float)(-(layerScaleX * layerX) - (combinedScaleX * chromaX)),
            (float)(-(layerScaleY * layerY) - (combinedScaleY * chromaY)));
        int count = frame.PlaneCount;
        var images = new SKImage[count];
        var shaders = new SKShader[count];
        bool ownsImages = !(planeSource?.ImagesAreBorrowed ?? false);
        bool clampToVisible = visibleBounds is not null;

        try
        {
            for (int i = 0; i < count; i++)
            {
                bool interleaved = i > 0 && layout.InterleavedChroma;

                // A byte-pair plane holds the same samples in the same places; only the channel
                // count changes, so the texel count below is unaffected.
                SKColorType colorType = (interleaved, wide, bytePairs) switch
                {
                    (true, true, false) => SKColorType.Rg1616,
                    (true, true, true) => SKColorType.Rgba8888,
                    (true, false, _) => SKColorType.Rg88,
                    (false, true, false) => SKColorType.Alpha16,
                    (false, true, true) => SKColorType.Rg88,
                    (false, false, _) => SKColorType.Alpha8,
                };

                var info = new SKImageInfo(
                    frame.GetRowBytes(i) / (layout.BytesPerSample * (interleaved ? 2 : 1)),
                    frame.GetPlaneHeight(i),
                    colorType,
                    colorType is SKColorType.Alpha8 or SKColorType.Alpha16
                        ? SKAlphaType.Premul
                        : SKAlphaType.Unpremul);

                SKRectI? region = null;
                SKImage? plane;
                if (planeSource is IVideoFrameImageCache cache && sourceBounds is { } bounds)
                {
                    int shiftX = i == 0 ? 0 : layout.ChromaShiftX;
                    int shiftY = i == 0 ? 0 : layout.ChromaShiftY;
                    region = new SKRectI(
                        Math.Max(0, bounds.Left >> shiftX),
                        Math.Max(0, bounds.Top >> shiftY),
                        Math.Min(info.Width, (bounds.Right + (1 << shiftX) - 1) >> shiftX),
                        Math.Min(info.Height, (bounds.Bottom + (1 << shiftY) - 1) >> shiftY));
                    plane = cache.CreatePlaneRegion(i, info, grContext, region.Value);
                }
                else
                {
                    plane = planeSource is null
                        ? SKImage.FromPixels(info, frame.GetPlane(i), frame.GetStride(i))
                        : planeSource.CreatePlane(i, info, grContext);
                }

                if (plane is null)
                    return Fail(images, shaders, ownsImages);

                if (plane.Width < (region?.Width ?? info.Width) || plane.Height < (region?.Height ?? info.Height))
                {
                    if (ownsImages)
                        plane.Dispose();
                    return Fail(images, shaders, ownsImages);
                }

                // Hardware decoders can allocate a coded surface taller or wider than the visible
                // picture. Its external texture cannot necessarily make a subset image, so the
                // source shader clamps taps to the visible plane instead.
                clampToVisible |= region is null && (plane.Width != info.Width || plane.Height != info.Height);

                images[i] = plane;

                // The local matrix is what maps luma coordinates onto the smaller chroma planes, so
                // the shader body can sample every plane with the same coordinate. Nearest sampling
                // skips the siting offset, which would only put every other column on a tie.
                SKMatrix local = lumaLocal;
                if (i > 0 && subsampled)
                    local = chromaLocal;
                if (region is { } subset)
                {
                    local.TransX += subset.Left * local.ScaleX;
                    local.TransY += subset.Top * local.ScaleY;
                }

                SKShader? shader = plane.ToRawShader(
                    SKShaderTileMode.Clamp,
                    SKShaderTileMode.Clamp,
                    i == 0 || !subsampled ? lumaSampling : fieldChroma
                        ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
                        : chromaSampling,
                    local);

                if (shader is null)
                    return Fail(images, shaders, ownsImages);

                shaders[i] = shader;
            }

            var children = new Dictionary<string, SKShader>(StringComparer.Ordinal)
            {
                [childPrefix + VideoShaderChildren.Luma] = shaders[0],
            };
            if (layout.InterleavedChroma)
            {
                children[childPrefix + VideoShaderChildren.Chroma] = shaders[1];
            }
            else
            {
                children[childPrefix + VideoShaderChildren.Cb] = shaders[1];
                children[childPrefix + VideoShaderChildren.Cr] = shaders[2];
            }

            float[]? lumaBounds = clampToVisible
                ? PlaneBounds(
                    visibleBounds ?? SKRectI.Create(frame.GetRowBytes(0) / layout.BytesPerSample,
                        frame.GetPlaneHeight(0)),
                    1,
                    1,
                    0,
                    0)
                : null;
            float[]? chromaBounds = clampToVisible
                ? PlaneBounds(
                    visibleBounds is { } crop
                        ? new SKRectI(crop.Left >> layout.ChromaShiftX, crop.Top >> layout.ChromaShiftY,
                            layout.ChromaWidth(crop.Right), layout.ChromaHeight(crop.Bottom))
                        : SKRectI.Create(frame.GetRowBytes(1) /
                            (layout.BytesPerSample * (layout.InterleavedChroma ? 2 : 1)), frame.GetPlaneHeight(1)),
                    chromaScaleX,
                    chromaScaleY,
                    chromaX,
                    chromaY)
                : null;

            return new VideoPlaneSampler(
                variant,
                images,
                shaders,
                children,
                ownsImages,
                clampToVisible,
                lumaBounds,
                chromaBounds,
                childPrefix,
                fieldChroma ? [(float)combinedScaleX, (float)combinedScaleY, (float)chromaX, (float)chromaY] : null,
                fieldChroma ? [layout.ChromaWidth(frame.Width), layout.ChromaHeight(frame.Height), interpolateChroma ? 1 : 0, 0] : null,
                fieldChroma ? [(float)layerScaleX, (float)layerScaleY, (float)layerX, (float)layerY] : null);
        }
        catch
        {
            Fail(images, shaders, ownsImages);
            throw;
        }
    }

    private static string RenameFunction(string function, string prefix) => function
        .Replace("sampleSource", "sampleEnhancement", StringComparison.Ordinal)
        .Replace("samplePlanes", prefix + "SamplePlanes", StringComparison.Ordinal)
        .Replace("sampleFieldChroma", prefix + "SampleFieldChroma", StringComparison.Ordinal)
        .Replace("fieldChromaScale", prefix + "fieldChromaScale", StringComparison.Ordinal)
        .Replace("fieldChromaSize", prefix + "fieldChromaSize", StringComparison.Ordinal)
        .Replace("fieldLumaScale", prefix + "fieldLumaScale", StringComparison.Ordinal)
        .Replace("planeCoord", prefix + "PlaneCoord", StringComparison.Ordinal)
        .Replace("wide(", prefix + "Wide(", StringComparison.Ordinal)
        .Replace("lumaBounds", prefix + "LumaBounds", StringComparison.Ordinal)
        .Replace("chromaBounds", prefix + "ChromaBounds", StringComparison.Ordinal)
        .Replace("luma.", prefix + "luma.", StringComparison.Ordinal)
        .Replace("chroma.", prefix + "chroma.", StringComparison.Ordinal)
        .Replace("cb.", prefix + "cb.", StringComparison.Ordinal)
        .Replace("cr.", prefix + "cr.", StringComparison.Ordinal);

    /// <summary>
    /// Bounds in luma-coordinate space that map to the first and last visible texel centers of a
    /// plane. The child shader's local matrix then performs the same subsampling and siting
    /// transform as usual after the coordinate has been clamped.
    /// </summary>
    private static float[] PlaneBounds(
        SKRectI bounds,
        int scaleX,
        int scaleY,
        double offsetX,
        double offsetY) =>
    [
        (float)(scaleX * (bounds.Left + 0.5 - offsetX)),
        (float)(scaleY * (bounds.Top + 0.5 - offsetY)),
        (float)(scaleX * (bounds.Right - 0.5 - offsetX)),
        (float)(scaleY * (bounds.Bottom - 0.5 - offsetY)),
    ];

    private static VideoPlaneSampler? Fail(
        SKImage?[] images,
        SKShader?[] shaders,
        bool ownsImages = true)
    {
        foreach (SKShader? shader in shaders)
            shader?.Dispose();
        if (ownsImages)
        {
            foreach (SKImage? image in images)
                image?.Dispose();
        }
        return null;
    }

    public void Dispose()
    {
        foreach (SKShader shader in shaders)
            shader?.Dispose();
        if (ownsImages)
        {
            foreach (SKImage image in images)
                image?.Dispose();
        }
    }
}
