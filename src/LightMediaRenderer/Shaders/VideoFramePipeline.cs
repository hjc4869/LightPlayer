using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Rendering;
using SkiaSharp;

namespace LightMediaRenderer.Shaders;

/// <summary>
/// Draws a frame, in one pass where the filter can run on the decoder's samples, in two where it
/// needs scene light, or through an additional nonlinear RGB graph before scene conversion.
/// </summary>
/// <remarks>
/// The scene buffer holds linear light in the source's own primaries. Nothing in it is negative and
/// nothing exceeds the source's own peak, so a filter can be written without regard for the display
/// it will end up on, and the buffer never has to be rescaled to survive one. What the display can
/// actually show is settled afterwards, in the second pass, where it costs one matrix.
/// </remarks>
public static class VideoFramePipeline
{
    private const int PhotoTileSize = 2048;
    private const long MaximumPhotoScenePixels = 16 * 1024 * 1024;

    public static bool NeedsColorProcessing(VideoFrame frame) => NeedsColorProcessing(frame, VideoOutputColorSpace.Srgb);

    public static bool NeedsColorProcessing(VideoFrame frame, VideoOutputColorSpace target) =>
        VideoColorPlan.NeedsColorProcessing(frame, target);

    public static bool NeedsDeinterlacing(VideoFrame frame) => VideoColorPlan.NeedsDeinterlacing(frame);

    public static VideoHdrOutputMapping ResolveOutput(VideoFrame frame,
        VideoOutputColorSpace target, bool hdrPassthrough, VideoOutputColorVolume? colorVolume) =>
        VideoColorPlan.ResolveOutput(frame, target, hdrPassthrough, colorVolume);

    public static VideoHdrOutputMapping ResolveOutput(VideoFrame frame,
        VideoOutputColorSpace target, bool hdrPassthrough, VideoOutputColorVolume? colorVolume,
        VideoSdrToneMappingOptions? sdrToneMapping) =>
        VideoColorPlan.ResolveOutput(frame, target, hdrPassthrough, colorVolume, sdrToneMapping);

    /// <summary>
    /// Maps neutral static-metadata PQ luminance, in nits, to the output the renderer writes, relative
    /// to SDR white and before the surface's white scale.
    /// </summary>
    public static VideoHdrOutputMapping EvaluateToneCurve(VideoOutputColorSpace target, bool hdrPassthrough,
        VideoOutputColorVolume? colorVolume, VideoSdrToneMappingOptions? sdrToneMapping, float sourcePeak,
        ReadOnlySpan<float> sourceNits, Span<float> output) =>
        VideoColorPlan.EvaluateToneCurve(target, hdrPassthrough, colorVolume, sdrToneMapping, sourcePeak,
            sourceNits, output);

    public static float GainMapWeight(PhotoGainMap? gainMap, VideoOutputColorSpace target,
        bool hdrPassthrough, VideoOutputColorVolume? colorVolume) =>
        VideoPhotoGainMap.Weight(gainMap, target, hdrPassthrough, colorVolume);

    public static SKImage? CreateGainMapImage(PhotoGainMap gainMap) => VideoPhotoGainMap.CreateImage(gainMap);

    public static bool Render(in VideoRenderRequest request)
        => Render(request, out _);

    public static bool Render(in VideoRenderRequest request, out string failure)
    {
        failure = string.Empty;
        VideoFrame frame = request.Frame;
        if (request.PackedImage is null && !frame.HasSurface &&
            frame.Format != VideoPixelFormat.ExternalTexture && !VideoPlanarLayout.IsPlanar(frame.Format) &&
            frame.Data != IntPtr.Zero)
        {
            var info = new SKImageInfo(frame.Width, frame.Height, frame.Format switch
            {
                VideoPixelFormat.Rgba64 => SKColorType.Rgba16161616,
                VideoPixelFormat.RgbaF16 => SKColorType.RgbaF16,
                _ => SKColorType.Bgra8888,
            }, frame.HasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque);
            using SKImage? packed = SKImage.FromPixels(info, frame.Data, frame.Stride);
            if (packed is null)
            {
                failure = "the packed frame could not be sampled";
                return false;
            }
            return Render(request with { PackedImage = packed }, out failure);
        }

        var scaleContext = new VideoScaleStageContext(
            CreateScalerContext(request, request.Frame.Width, request.Frame.Height),
            request.Frame.ColorTransfer);
        IVideoScaleStage? preScale = request.Scaler.PreScaleStage?.Resolve(scaleContext);
        IVideoScaler scaler = VideoScalers.WithoutPreScaleStage(request.Scaler);
        if (preScale is null && scaler.RequiresSceneBuffer && IsAtOutputSize(request, scaleContext.Scaling))
            scaler = VideoScalers.For(VideoScalingQuality.Bilinear);

        VideoRenderRequest resolved = VideoDebanding.Applies(request.Debanding, request.Layout)
            ? request
            : request with { Debanding = VideoDebandingQuality.Off };
        VideoRenderRequest fusedRequest = resolved;
        string declined = string.Empty;

        // Fields are told apart by the row they sit on, which only survives while the picture is
        // read at its own size: resizing while sampling would blend the two before either could be
        // recognized. So an interlaced frame is separated in a pass of its own and resized after.
        bool deinterlace = VideoColorPlan.NeedsDeinterlacing(request.Frame);
        if (request.ImageCache is not null && request.SceneBounds is null && preScale is null &&
            !scaler.RequiresSceneBuffer && resolved.Debanding == VideoDebandingQuality.Off && !deinterlace &&
            request.GrContext is { } gpu &&
            (request.Frame.Width > gpu.MaxTextureSize || request.Frame.Height > gpu.MaxTextureSize))
        {
            return RenderPhotoTiles(resolved, out failure);
        }

        if (deinterlace && request.DeinterlaceHistory is { } history)
        {
            history.Prepare(request);
            if (history.Samples is null)
                history.SetSamples(RenderSamples(request));
            resolved = resolved with
            {
                SampledImage = history.Samples,
                PreviousSamples = history.PreviousSamples,
            };
            fusedRequest = resolved;
        }
        if (preScale is not null || scaler.RequiresSceneBuffer || request.Frame.HasAlpha ||
            resolved.Debanding != VideoDebandingQuality.Off || deinterlace)
        {
            if (TryRenderScaled(resolved, scaler, preScale, scaleContext, out declined))
            {
                request.Diagnostics?.RecordColorProgram(drawnWithoutShader: false);
                return true;
            }

            // A scratch buffer the backend would not give up costs the chosen filter, not the
            // frame: the picture is still drawn with one Skia can apply to the planes directly.
            scaler = VideoScalers.For(deinterlace
                ? VideoScalingQuality.NearestNeighbour
                : VideoScalingQuality.Bilinear);
            fusedRequest = resolved with { Debanding = VideoDebandingQuality.Off };
        }

        if (!TryRenderFused(fusedRequest, scaler, out failure))
            return false;

        request.Diagnostics?.RecordScaling(scaler.Quality, declined);
        request.Diagnostics?.RecordColorProgram(drawnWithoutShader: false);
        return true;
    }

    private static bool TryRenderFused(
        in VideoRenderRequest request,
        IVideoScaler scaler,
        out string failure)
    {
        failure = string.Empty;
        VideoFrame frame = request.Frame;
        VideoPlanarLayout layout = request.Layout;
        VideoColorPlan plan = CreatePlan(request, requiresSceneBuffer: false, layout);

        using VideoPlaneSampler? sampler = CreateSampler(request, scaler.Sampling);
        if (sampler is null)
        {
            failure = "the base-layer planes could not be sampled";
            return false;
        }
        using VideoPlaneSampler? enhancementSampler = CreateEnhancementSampler(request, plan, scaler.Sampling);
        using SKShader? previous = CreatePreviousSampler(request, plan);
        using SKShader? gainMap = VideoPhotoGainMap.CreateShader(request, plan);
        if (plan.PhotoGainMap && gainMap is null)
        {
            failure = "the photo gain map could not be sampled";
            return false;
        }
        if (plan.DolbyVisionEnhancementLayer && enhancementSampler is null)
        {
            failure = "the Dolby Vision enhancement-layer planes could not be sampled";
            return false;
        }

        if (sampler.NeedsColorMatrix)
            plan.SetColorMatrix(frame, layout);
        plan.SetDitherTransform(SourceToDevice(request));

        var builder = new VideoShaderBuilder();
        var children = new Dictionary<string, SKShader>(sampler.Children, StringComparer.Ordinal);
        if (gainMap is not null)
            children[VideoPhotoGainMap.Child] = gainMap;
        if (previous is not null)
            children[VideoShaderChildren.PreviousSamples] = previous;
        foreach (string child in sampler.Children.Keys)
            builder.AddChild(child);
        if (enhancementSampler is not null)
        {
            foreach ((string child, SKShader childShader) in enhancementSampler.Children)
            {
                builder.AddChild(child);
                children[child] = childShader;
            }
            enhancementSampler.AddSamplingUniforms(builder, plan.Uniforms);
            builder.AddStage(enhancementSampler.Function);
        }
        sampler.AddSamplingUniforms(builder, plan.Uniforms);
        plan.AddSceneStages(builder, sampler.Function, sampler.NeedsColorMatrix);
        plan.AddOutputStages(builder);

        VideoShaderProgram? program = builder.Build(plan.PhotoAlpha ? VideoPhotoColor.Main(plan.PhotoGainMap, output: true) :
            plan.PhotoGainMap ? VideoPhotoGainMap.MainFused : VideoColorStages.MainFused);
        if (program is null)
        {
            failure = "the fused video shader did not compile";
            return false;
        }

        using SKShader? shader = program.ToShader(plan.Uniforms, children, out failure);
        if (shader is null)
            return false;

        Draw(request, shader);
        RecordDynamicHdr(request, plan);
        request.Diagnostics?.RecordPreScale(active: null);
        request.Diagnostics?.RecordDither(plan.OutputBitDepth, plan.Dither, request.PanelBitDepth > 0);
        return true;
    }

    private static bool TryRenderScaled(
        in VideoRenderRequest request,
        IVideoScaler scaler,
        IVideoScaleStage? preScale,
        in VideoScaleStageContext scaleContext,
        out string declined)
    {
        bool photo = request.ImageCache is not null && preScale is null &&
            VideoScalers.SupportsPhotoSceneCaching(scaler);
        if (photo && request.SceneBounds is null && scaler.Quality != VideoScalingQuality.Trilinear)
        {
            SKRectI visible = VisibleSourceBounds(request);
            if ((long)visible.Width * visible.Height > MaximumPhotoScenePixels)
                return RenderPhotoTiles(request, out declined);
        }

        VideoPlanarLayout layout = request.Layout;
        VideoColorPlan plan = CreatePlan(request, requiresSceneBuffer: true, layout);

        VideoRenderRequest sceneRequest = request;
        SKImage? scene;
        SKRectI? sceneBounds;
        int reduction = 1;
        bool borrowed = false;
        VideoPreScaleStatus? activePreScale = null;
        if (photo && scaler is SkiaVideoScaler { Quality: VideoScalingQuality.Trilinear })
        {
            scene = RenderPhotoScene(request, scaleContext.Scaling, plan, layout,
                out SKRectI photoBounds, out reduction, out borrowed, out declined);
            sceneBounds = photoBounds;
        }
        else
        {
            if (photo && request.SceneBounds is null)
                sceneRequest = request with { SceneBounds = VisibleSourceBounds(request) };
            sceneBounds = sceneRequest.SceneBounds;
            scene = RenderSceneWithPreScale(sceneRequest, preScale, scaleContext, plan, layout,
                out declined, out activePreScale);
        }

        using SKImage? ownedScene = borrowed ? null : scene;
        if (scene is null)
        {
            if (photo && request.SceneBounds is null && scaler.Quality == VideoScalingQuality.Trilinear)
                return RenderPhotoTiles(request, out declined);
            request.Diagnostics?.RecordPreScale(active: null);
            return false;
        }

        int sourceWidth = sceneBounds is null ? scene.Width : request.Frame.Width;
        int sourceHeight = sceneBounds is null ? scene.Height : request.Frame.Height;
        VideoScalerContext context = CreateScalerContext(request, sourceWidth, sourceHeight);
        if (activePreScale is not null &&
            request.UpscalingQuality is { } upscaling &&
            request.DownscalingQuality is { } downscaling)
        {
            scaler = VideoScalers.For(VideoScalers.SelectQuality(
                upscaling,
                downscaling,
                context.SourceWidth,
                context.SourceHeight,
                context.DestinationWidth,
                context.DestinationHeight));
        }

        if (scaler.RequiresSceneBuffer && IsAtOutputSize(request, context))
            scaler = VideoScalers.For(VideoScalingQuality.Bilinear);

        using SKShader? source = scene.ToRawShader(
            SKShaderTileMode.Clamp,
            SKShaderTileMode.Clamp,
            scaler.Sampling,
            SKMatrix.CreateScaleTranslation(reduction, reduction, sceneBounds?.Left ?? 0, sceneBounds?.Top ?? 0));
        if (source is null)
        {
            request.Diagnostics?.RecordPreScale(active: null);
            declined = "the scene buffer would not be sampled";
            return false;
        }

        plan.SetDitherTransform(SourceToDevice(request, sourceWidth, sourceHeight));

        var builder = new VideoShaderBuilder();
        builder.AddChild(VideoShaderChildren.Source);
        if (scaler.Function is { } function)
        {
            builder.AddUniforms(scaler.Uniforms).AddStage(function);
            scaler.AddUniforms(plan.Uniforms, context);
        }
        else
        {
            builder.AddStage(VideoColorStages.ScaleDirect);
        }

        plan.AddOutputStages(builder);

        VideoShaderProgram? program = builder.Build(plan.PhotoAlpha ? VideoPhotoColor.MainOutput : VideoColorStages.MainOutput);
        var children = new Dictionary<string, SKShader>(StringComparer.Ordinal)
        {
            [VideoShaderChildren.Source] = source,
        };

        using SKShader? shader = program?.ToShader(plan.Uniforms, children);
        if (shader is null)
        {
            request.Diagnostics?.RecordPreScale(active: null);
            declined = "the filter's own program did not survive the graphics backend";
            return false;
        }

        Draw(request, shader, sourceWidth, sourceHeight);
        RecordDynamicHdr(request, plan);
        request.Diagnostics?.RecordScaling(scaler.Quality, declined);
        request.Diagnostics?.RecordPreScale(activePreScale);
        request.Diagnostics?.RecordDither(plan.OutputBitDepth, plan.Dither, request.PanelBitDepth > 0);
        return true;
    }

    private static SKImage? RenderPhotoScene(
        in VideoRenderRequest request,
        in VideoScalerContext context,
        VideoColorPlan plan,
        VideoPlanarLayout layout,
        out SKRectI bounds,
        out int reduction,
        out bool borrowed,
        out string declined)
    {
        double minification = Math.Min(
            request.Frame.Width / Math.Max(1, context.DestinationWidth),
            request.Frame.Height / Math.Max(1, context.DestinationHeight));
        reduction = 1 << Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, minification))) - 1, 0, 10);
        SKRectI visible = VisibleSourceBounds(request, (2 * reduction) + 4);
        IVideoFrameImageCache cache = request.ImageCache!;
        declined = string.Empty;
        borrowed = cache.TryGetScene(request, visible, reduction, out SKImage? cached, out bounds);
        if (borrowed)
            return cached;

        int alignment = 128 * reduction;
        bounds = new SKRectI(
            visible.Left / alignment * alignment,
            visible.Top / alignment * alignment,
            (int)Math.Min(request.Frame.Width, ((long)visible.Right + alignment - 1) / alignment * alignment),
            (int)Math.Min(request.Frame.Height, ((long)visible.Bottom + alignment - 1) / alignment * alignment));
        int width = (bounds.Width + reduction - 1) / reduction;
        int height = (bounds.Height + reduction - 1) / reduction;
        if ((long)width * height * 8 * 4 / 3 > cache.GetSceneBudget(request))
        {
            declined = "the photo scene exceeds the viewport cache budget";
            return null;
        }

        var info = new SKImageInfo(width, height, SKColorType.RgbaF16,
            plan.PhotoAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque);
        using SKSurface? surface = request.GrContext is null
            ? SKSurface.Create(info)
            : SKSurface.Create(request.GrContext, false, info);
        if (surface is null)
        {
            declined = $"the backend refused a {width}x{height} photo scene buffer";
            return null;
        }

        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Nearest);
        using var paint = new SKPaint { IsAntialias = false, BlendMode = SKBlendMode.Src };
        for (int top = bounds.Top; top < bounds.Bottom; top += PhotoTileSize)
        {
            for (int left = bounds.Left; left < bounds.Right; left += PhotoTileSize)
            {
                var tileBounds = new SKRectI(left, top,
                    Math.Min(left + PhotoTileSize, bounds.Right), Math.Min(top + PhotoTileSize, bounds.Bottom));
                using SKImage? tile = RenderScene(request with { SceneBounds = tileBounds }, plan, layout, out declined);
                if (tile is null)
                    return null;

                var destination = new SKRect(
                    (tileBounds.Left - bounds.Left) / (float)reduction,
                    (tileBounds.Top - bounds.Top) / (float)reduction,
                    MathF.Ceiling((tileBounds.Right - bounds.Left) / (float)reduction),
                    MathF.Ceiling((tileBounds.Bottom - bounds.Top) / (float)reduction));
                using SKShader? tileShader = tile.ToRawShader(
                    SKShaderTileMode.Clamp,
                    SKShaderTileMode.Clamp,
                    sampling,
                    SKMatrix.CreateScaleTranslation(1f / reduction, 1f / reduction, destination.Left, destination.Top));
                if (tileShader is null)
                {
                    declined = "the photo scene tile could not be sampled";
                    return null;
                }

                paint.Shader = tileShader;
                surface.Canvas.DrawRect(destination, paint);
            }
        }

        SKImage image = surface.Snapshot();
        if (request.GrContext is { } gpu && !OperatingSystem.IsBrowser())
        {
            SKImage? texture = image.ToTextureImage(gpu, mipmapped: true, budgeted: false);
            if (texture is not null && !ReferenceEquals(texture, image))
            {
                image.Dispose();
                image = texture;
            }
        }

        borrowed = cache.StoreScene(request, bounds, reduction, image);
        return image;
    }

    private static bool RenderPhotoTiles(
        in VideoRenderRequest request,
        out string failure)
    {
        failure = string.Empty;
        SKCanvas canvas = request.Canvas;
        SKMatrix view = canvas.TotalMatrix;
        bool retainPlanes = request.PlaneSource is IVideoFrameImageCache cache && cache.CanRetainPlanes(request.GrContext);
        SKRectI clip = canvas.DeviceClipBounds;
        SKMatrix sourceToDevice = SourceToDevice(request);
        SKRect destination = sourceToDevice.MapRect(SKRect.Create(request.Frame.Width, request.Frame.Height));
        var visible = new SKRectI(
            Math.Max(clip.Left, (int)MathF.Floor(destination.Left)),
            Math.Max(clip.Top, (int)MathF.Floor(destination.Top)),
            Math.Min(clip.Right, (int)MathF.Ceiling(destination.Right)),
            Math.Min(clip.Bottom, (int)MathF.Ceiling(destination.Bottom)));
        int stepX = (int)Math.Clamp(PhotoTileSize * double.Hypot(sourceToDevice.ScaleX, sourceToDevice.SkewX), 1, PhotoTileSize);
        int stepY = (int)Math.Clamp(PhotoTileSize * double.Hypot(sourceToDevice.SkewY, sourceToDevice.ScaleY), 1, PhotoTileSize);
        for (int top = visible.Top; top < visible.Bottom; top += stepY)
        {
            for (int left = visible.Left; left < visible.Right; left += stepX)
            {
                int saved = canvas.Save();
                try
                {
                    canvas.ResetMatrix();
                    canvas.ClipRect(new SKRect(left, top,
                        Math.Min(left + stepX, visible.Right), Math.Min(top + stepY, visible.Bottom)),
                        SKClipOperation.Intersect, antialias: false);
                    canvas.SetMatrix(view);
                    VideoRenderRequest tile = request with
                    {
                        SceneBounds = VisibleSourceBounds(request),
                        RetainFullPlanes = retainPlanes,
                    };
                    if (!Render(tile, out failure))
                        return false;
                }
                finally
                {
                    canvas.RestoreToCount(saved);
                }
            }
        }

        return true;
    }

    private static SKImage? RenderSceneWithPreScale(
        in VideoRenderRequest request,
        IVideoScaleStage? stage,
        in VideoScaleStageContext context,
        VideoColorPlan plan,
        VideoPlanarLayout layout,
        out string declined,
        out VideoPreScaleStatus? activePreScale)
    {
        activePreScale = null;
        if (stage is null)
            return RenderScene(request, plan, layout, out declined);

        SKImage? scene = RenderPreScaledScene(
            request,
            plan,
            layout,
            stage,
            context,
            out string stageDeclined,
            out activePreScale);
        if (scene is null)
            return FallBackToScene(request, plan, layout, stageDeclined, out declined);

        declined = string.Empty;
        return scene;
    }

    private static SKImage? RenderPreScaledScene(
        in VideoRenderRequest request,
        VideoColorPlan plan,
        VideoPlanarLayout layout,
        IVideoScaleStage stage,
        in VideoScaleStageContext context,
        out string declined,
        out VideoPreScaleStatus? activePreScale)
    {
        activePreScale = null;
        using SKImage? signal = RenderSignal(request, plan, layout, out declined);
        if (signal is null)
            return null;

        using SKImage? stagedSignal = stage.Render(signal, request.GrContext, context, out declined);
        if (stagedSignal is null)
            return null;

        SKImage? scene = RenderSceneFromSignal(request, plan, stagedSignal, out declined);
        if (scene is null)
            return null;

        activePreScale = new VideoPreScaleStatus(
            stage.Name,
            signal.Width,
            signal.Height,
            stagedSignal.Width,
            stagedSignal.Height,
            stage.PassCount);
        declined = string.Empty;
        return scene;
    }

    private static SKImage? FallBackToScene(
        in VideoRenderRequest request,
        VideoColorPlan plan,
        VideoPlanarLayout layout,
        string stageDeclined,
        out string declined)
    {
        SKImage? scene = RenderScene(request, plan, layout, out string sceneDeclined);
        declined = scene is null ? sceneDeclined : stageDeclined;
        return scene;
    }

    public static unsafe double MeasurePhotoPeak(VideoFrame frame, CancellationToken cancellationToken = default,
        SKRectI? sourceBounds = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.GainMap is not null || frame.ColorTransfer != VideoColorTransfer.Pq && frame.PhotoColor?.TransferCode != 16)
            throw new ArgumentException("Peak measurement requires an absolute-luminance PQ photo.", nameof(frame));
        using SKImage? packed = !VideoPlanarLayout.IsPlanar(frame.Format) && frame.Data != IntPtr.Zero
            ? SKImage.FromPixels(new SKImageInfo(frame.Width, frame.Height, frame.Format switch
            {
                VideoPixelFormat.Rgba64 => SKColorType.Rgba16161616,
                VideoPixelFormat.RgbaF16 => SKColorType.RgbaF16,
                _ => SKColorType.Bgra8888,
            }, frame.HasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque), frame.Data, frame.Stride)
            : null;
        var request = new VideoRenderRequest(null!, null, frame, SKRect.Create(frame.Width, frame.Height), 255,
            VideoScalers.For(VideoScalingQuality.Bilinear), VideoOutputColorSpace.ScRgbLinear, true,
            new VideoOutputColorVolume
            {
                ToneMapping = VideoToneMappingMode.Platform,
                ReferenceWhiteScale = frame.ColorTransfer == VideoColorTransfer.Pq
                    ? VideoOutputColorSpaceExtensions.HdrReferenceWhiteNits : 1,
            })
        {
            PackedImage = packed,
        };
        VideoColorPlan plan = CreatePlan(request, requiresSceneBuffer: true, request.Layout);
        SKRectI region = sourceBounds ?? SKRectI.Create(frame.Width, frame.Height);
        int left = Math.Clamp(region.Left, 0, frame.Width);
        int right = Math.Clamp(region.Right, left, frame.Width);
        int firstRow = Math.Clamp(region.Top, 0, frame.Height);
        int lastRow = Math.Clamp(region.Bottom, firstRow, frame.Height);
        if (right == left || firstRow == lastRow)
            return 0;
        double peak = 0;
        int stripHeight = Math.Clamp(262144 / (right - left), 1, 128);
        for (int top = firstRow; top < lastRow; top += stripHeight)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bounds = SKRectI.Create(left, top, right - left, Math.Min(stripHeight, lastRow - top));
            using SKImage scene = RenderScene(request with { SceneBounds = bounds }, plan, request.Layout, out string failure)
                ?? throw new InvalidDataException($"Could not measure photo luminance: {failure}.");
            using var pixels = new SKBitmap(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.RgbaF16,
                frame.HasAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque));
            if (!scene.ReadPixels(pixels.Info, pixels.GetPixels(), pixels.RowBytes, 0, 0))
                throw new InvalidDataException("Could not read photo luminance samples.");
            for (int row = 0; row < pixels.Height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var samples = (Half*)((byte*)pixels.GetPixels() + (long)row * pixels.RowBytes);
                for (int column = 0; column < pixels.Width; column++)
                for (int channel = 0; channel < 3; channel++)
                {
                    float value = (float)samples[column * 4 + channel];
                    if (float.IsFinite(value))
                        peak = Math.Max(peak, value);
                }
            }
        }
        return Math.Min(peak, 10000);
    }

    /// <summary>Converts the frame to scene light at its own size, which is where a filter reads it.</summary>
    public static SKImage? RenderPhotoLinear(in VideoRenderRequest request, out string failure)
    {
        VideoRenderRequest linear = request with
        {
            Target = VideoOutputColorSpace.Srgb,
            ColorVolume = null,
            HdrPassthrough = false,
        };
        return RenderScene(linear, CreatePlan(linear, requiresSceneBuffer: true, linear.Layout), linear.Layout, out failure);
    }

    private static SKImage? RenderScene(
        in VideoRenderRequest request,
        VideoColorPlan plan,
        VideoPlanarLayout layout,
        out string declined)
    {
        VideoFrame frame = request.Frame;

        // The picture is not resized here, so the luma plane is read at its own texel centers.
        var nearest = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        using VideoPlaneSampler? sampler = CreateSampler(request, nearest);
        if (sampler is null)
        {
            declined = "the frame's planes could not be sampled into a scene buffer";
            return null;
        }
        using VideoPlaneSampler? enhancementSampler = CreateEnhancementSampler(request, plan, nearest);
        using SKShader? previous = CreatePreviousSampler(request, plan);
        using SKShader? gainMap = VideoPhotoGainMap.CreateShader(request, plan);
        if (plan.PhotoGainMap && gainMap is null)
        {
            declined = "the photo gain map could not be sampled into a scene buffer";
            return null;
        }

        if (sampler.NeedsColorMatrix)
            plan.SetColorMatrix(frame, layout);

        var builder = new VideoShaderBuilder();
        var children = new Dictionary<string, SKShader>(sampler.Children, StringComparer.Ordinal);
        if (gainMap is not null)
            children[VideoPhotoGainMap.Child] = gainMap;
        if (previous is not null)
            children[VideoShaderChildren.PreviousSamples] = previous;
        foreach (string child in sampler.Children.Keys)
            builder.AddChild(child);
        if (enhancementSampler is not null)
        {
            foreach ((string child, SKShader childShader) in enhancementSampler.Children)
            {
                builder.AddChild(child);
                children[child] = childShader;
            }
            enhancementSampler.AddSamplingUniforms(builder, plan.Uniforms);
            builder.AddStage(enhancementSampler.Function);
        }
        sampler.AddSamplingUniforms(builder, plan.Uniforms);
        plan.AddSceneStages(builder, sampler.Function, sampler.NeedsColorMatrix);

        VideoShaderProgram? program = builder.Build(plan.PhotoAlpha ? VideoPhotoColor.Main(plan.PhotoGainMap, output: false) :
            plan.PhotoGainMap ? VideoPhotoGainMap.MainScene : VideoColorStages.MainScene);
        using SKShader? shader = program?.ToShader(plan.Uniforms, children);
        if (shader is null)
        {
            declined = "the scene program did not survive the graphics backend";
            return null;
        }

        // Half floats, because scene light runs from the darkest step a display can show to the
        // source's peak, and a format with an exponent keeps the same relative precision at both.
        SKRectI bounds = request.SceneBounds ?? SKRectI.Create(frame.Width, frame.Height);
        var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.RgbaF16,
            plan.PhotoAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque);
        using SKSurface? surface = request.GrContext is null
            ? SKSurface.Create(info)
            : SKSurface.Create(request.GrContext, true, info);
        if (surface is null)
        {
            declined = $"the backend refused a {bounds.Width}x{bounds.Height} half-float scene buffer";
            return null;
        }

        declined = string.Empty;
        surface.Canvas.Translate(-bounds.Left, -bounds.Top);
        using (var paint = new SKPaint { IsAntialias = false, BlendMode = SKBlendMode.Src, Shader = shader })
            surface.Canvas.DrawRect(bounds, paint);

        return surface.Snapshot();
    }

    /// <summary>Converts the frame to normalized nonlinear RGB for a learned pre-scale stage.</summary>
    private static SKImage? RenderSignal(
        in VideoRenderRequest request,
        VideoColorPlan plan,
        VideoPlanarLayout layout,
        out string declined)
    {
        VideoFrame frame = request.Frame;
        var nearest = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        using VideoPlaneSampler? sampler = CreateSampler(request, nearest);
        if (sampler is null)
        {
            declined = "the frame's planes could not be sampled into a signal buffer";
            return null;
        }
        using VideoPlaneSampler? enhancementSampler = CreateEnhancementSampler(request, plan, nearest);
        using SKShader? previous = CreatePreviousSampler(request, plan);

        if (sampler.NeedsColorMatrix)
            plan.SetColorMatrix(frame, layout);

        var builder = new VideoShaderBuilder();
        var children = new Dictionary<string, SKShader>(sampler.Children, StringComparer.Ordinal);
        if (previous is not null)
            children[VideoShaderChildren.PreviousSamples] = previous;
        foreach (string child in sampler.Children.Keys)
            builder.AddChild(child);
        if (enhancementSampler is not null)
        {
            foreach ((string child, SKShader childShader) in enhancementSampler.Children)
            {
                builder.AddChild(child);
                children[child] = childShader;
            }
            enhancementSampler.AddSamplingUniforms(builder, plan.Uniforms);
            builder.AddStage(enhancementSampler.Function);
        }
        sampler.AddSamplingUniforms(builder, plan.Uniforms);
        plan.AddSignalStages(builder, sampler.Function, sampler.NeedsColorMatrix);
        plan.AddDebandingStage(builder);

        VideoShaderProgram? program = builder.Build(VideoColorStages.MainSignal);
        using SKShader? shader = program?.ToShader(plan.Uniforms, children);
        if (shader is null)
        {
            declined = "the signal program did not survive the graphics backend";
            return null;
        }

        return RenderIntermediate(
            request.GrContext,
            shader,
            frame.Width,
            frame.Height,
            "signal",
            out declined);
    }

    /// <summary>Decodes a staged nonlinear RGB image into scene light at its staged size.</summary>
    private static SKImage? RenderSceneFromSignal(
        in VideoRenderRequest request,
        VideoColorPlan plan,
        SKImage signal,
        out string declined)
    {
        var nearest = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        using SKShader? source = signal.ToRawShader(
            SKShaderTileMode.Clamp,
            SKShaderTileMode.Clamp,
            nearest,
            SKMatrix.Identity);
        if (source is null)
        {
            declined = "the staged signal image could not be sampled";
            return null;
        }

        using SKShader? gainMap = VideoPhotoGainMap.CreateShader(request, plan, signal.Width, signal.Height);
        if (plan.PhotoGainMap && gainMap is null)
        {
            declined = "the photo gain map could not be sampled for the staged signal";
            return null;
        }

        var builder = new VideoShaderBuilder();
        builder.AddChild(VideoShaderChildren.Source);
        plan.AddSceneFromSignalStages(builder);
        VideoShaderProgram? program = builder.Build(plan.PhotoGainMap ? VideoPhotoGainMap.MainScene : VideoColorStages.MainScene);
        var children = new Dictionary<string, SKShader>(StringComparer.Ordinal)
        {
            [VideoShaderChildren.Source] = source,
        };
        if (gainMap is not null)
            children[VideoPhotoGainMap.Child] = gainMap;
        using SKShader? shader = program?.ToShader(plan.Uniforms, children);
        if (shader is null)
        {
            declined = "the staged scene program did not survive the graphics backend";
            return null;
        }

        return RenderIntermediate(
            request.GrContext,
            shader,
            signal.Width,
            signal.Height,
            "staged scene",
            out declined);
    }

    private static SKImage? RenderSamples(in VideoRenderRequest request)
    {
        var nearest = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        using VideoPlaneSampler? sampler = CreateSampler(request, nearest);
        if (sampler is null)
            return null;

        var builder = new VideoShaderBuilder();
        foreach (string child in sampler.Children.Keys)
            builder.AddChild(child);
        var uniforms = new Dictionary<string, float[]>(StringComparer.Ordinal);
        sampler.AddSamplingUniforms(builder, uniforms);
        builder.AddStage(sampler.Function);
        VideoShaderProgram? program = builder.Build(VideoColorStages.MainSamples);
        using SKShader? shader = program?.ToShader(uniforms, sampler.Children);
        return shader is null ? null : RenderIntermediate(
            request.GrContext, shader, request.Frame.Width, request.Frame.Height, "field samples", out _);
    }

    private static SKShader? CreatePreviousSampler(in VideoRenderRequest request, VideoColorPlan plan)
    {
        SKShader? shader = request.PreviousSamples?.ToRawShader(
            SKShaderTileMode.Clamp,
            SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
            SKMatrix.Identity);
        plan.HasPreviousFrame = shader is not null;
        return shader;
    }

    private static SKImage? RenderIntermediate(
        GRContext? grContext,
        SKShader shader,
        int width,
        int height,
        string name,
        out string declined)
    {
        var info = new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Opaque);
        using SKSurface? surface = grContext is null
            ? SKSurface.Create(info)
            : SKSurface.Create(grContext, true, info);
        if (surface is null)
        {
            declined = $"the backend refused a {width}x{height} half-float {name} buffer";
            return null;
        }

        using (var paint = new SKPaint { IsAntialias = false, BlendMode = SKBlendMode.Src, Shader = shader })
            surface.Canvas.DrawRect(SKRect.Create(0, 0, width, height), paint);

        declined = string.Empty;
        return surface.Snapshot();
    }

    private static VideoColorPlan CreatePlan(
        in VideoRenderRequest request,
        bool requiresSceneBuffer,
        VideoPlanarLayout layout) =>
        VideoColorPlan.Create(
            request.Frame,
            layout,
            requiresSceneBuffer,
            request.Target,
            request.HdrPassthrough,
            request.ColorVolume,
            request.PanelBitDepth,
            request.Debanding,
            request.SdrToneMapping);

    private static VideoPlaneSampler? CreateSampler(in VideoRenderRequest request, SKSamplingOptions sampling)
    {
        if (VideoColorPlan.NeedsDeinterlacing(request.Frame))
            sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);

        if (request.SampledImage is { } sampled)
        {
            bool yuv = request.PackedImage is null ||
                request.Frame.ExternalTextureColorModel == VideoExternalTextureColorModel.Yuv;
            return VideoPlaneSampler.CreatePacked(sampled, sampling, VideoTextureRect.Whole,
                yuv ? VideoExternalTextureColorModel.Yuv : VideoExternalTextureColorModel.Rgb);
        }

        if (request.PackedImage is { } image)
        {
            if (request.ImageCache is not null && request.SceneBounds is { } packedBounds &&
                request.Frame.ExternalTextureRect.IsWhole)
            {
                SKRectI padded = PaddedSourceBounds(request, packedBounds);
                if (padded == SKRectI.Create(image.Width, image.Height))
                    return VideoPlaneSampler.CreatePacked(image, sampling, VideoTextureRect.Whole,
                        request.Frame.ExternalTextureColorModel);

                using SKImage? region = image.Subset(padded);
                if (region is null)
                    return null;

                return VideoPlaneSampler.CreatePacked(region, sampling, VideoTextureRect.Whole,
                    request.Frame.ExternalTextureColorModel, new SKPoint(padded.Left, padded.Top));
            }

            return VideoPlaneSampler.CreatePacked(
                image,
                sampling,
                request.Frame.ExternalTextureRect,
                request.Frame.ExternalTextureColorModel);
        }

        // Chroma is half resolution, so it is always interpolated unless the caller explicitly
        // asked for the blocky look; a cubic filter there would cost more than it shows.
        SKSamplingOptions chroma = request.Scaler.Quality == VideoScalingQuality.NearestNeighbour
            ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        SKRectI? sourceBounds = request.ImageCache is null || request.RetainFullPlanes ||
            request.ImageCache.HasFullPlaneTextures(request.GrContext)
                ? null
                : request.SceneBounds;
        if (sourceBounds is { } bounds)
            sourceBounds = PaddedSourceBounds(request, bounds);

        return VideoPlaneSampler.CreatePlanar(
            request.Frame,
            sampling,
            chroma,
            request.PlaneSource,
            request.GrContext,
            sourceBounds: sourceBounds,
            visibleBounds: request.SamplingBounds);
    }

    private static SKRectI PaddedSourceBounds(in VideoRenderRequest request, SKRectI bounds)
    {
        VideoDebandingProfile profile = VideoDebandingProfile.For(request.Debanding);
        int padding = 2 + (int)MathF.Ceiling(profile.Radius * profile.Iterations);
        return new SKRectI(
            Math.Max(0, bounds.Left - padding),
            Math.Max(0, bounds.Top - padding),
            Math.Min(request.Frame.Width, bounds.Right + padding),
            Math.Min(request.Frame.Height, bounds.Bottom + padding));
    }

    private static VideoPlaneSampler? CreateEnhancementSampler(
        in VideoRenderRequest request,
        VideoColorPlan plan,
        SKSamplingOptions sampling)
    {
        if (!plan.DolbyVisionEnhancementLayer || request.Frame.EnhancementLayer is not { } enhancement)
            return null;

        var chroma = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        return VideoPlaneSampler.CreatePlanar(
            enhancement,
            sampling,
            chroma,
            null,
            request.GrContext,
            "el",
            request.Frame.Width,
            request.Frame.Height);
    }

    private static bool IsAtOutputSize(in VideoRenderRequest request, in VideoScalerContext context)
    {
        const double pixelTolerance = 1e-3;
        const float axisTolerance = 1e-6f;
        SKMatrix transform = SourceToDevice(request, context.SourceWidth, context.SourceHeight);
        return Math.Abs(context.DestinationWidth - context.SourceWidth) < pixelTolerance &&
            Math.Abs(context.DestinationHeight - context.SourceHeight) < pixelTolerance &&
            transform.Persp0 == 0 && transform.Persp1 == 0 && transform.Persp2 == 1 &&
            ((Math.Abs(transform.SkewX) < axisTolerance && Math.Abs(transform.SkewY) < axisTolerance) ||
             (Math.Abs(transform.ScaleX) < axisTolerance && Math.Abs(transform.ScaleY) < axisTolerance)) &&
            Math.Abs(transform.TransX - MathF.Round(transform.TransX)) < pixelTolerance &&
            Math.Abs(transform.TransY - MathF.Round(transform.TransY)) < pixelTolerance;
    }

    private static SKRectI VisibleSourceBounds(in VideoRenderRequest request, int margin = 4)
    {
        SKRectI whole = SKRectI.Create(request.Frame.Width, request.Frame.Height);
        if (!SourceToDevice(request).TryInvert(out SKMatrix inverse))
            return whole;

        SKRect visible = inverse.MapRect(request.Canvas.DeviceClipBounds);
        visible.Inflate(margin, margin);
        SKRect clipped = SKRect.Intersect(visible, whole);
        return clipped.IsEmpty
            ? whole
            : new SKRectI(
                (int)MathF.Floor(clipped.Left),
                (int)MathF.Floor(clipped.Top),
                (int)MathF.Ceiling(clipped.Right),
                (int)MathF.Ceiling(clipped.Bottom));
    }

    private static VideoScalerContext CreateScalerContext(
        in VideoRenderRequest request,
        int sourceWidth,
        int sourceHeight)
    {
        SKMatrix view = request.Canvas.TotalMatrix;
        return new VideoScalerContext(
            sourceWidth,
            sourceHeight,
            Math.Abs(request.Destination.Width) * double.Hypot(view.ScaleX, view.SkewY),
            Math.Abs(request.Destination.Height) * double.Hypot(view.SkewX, view.ScaleY));
    }

    private static void Draw(in VideoRenderRequest request, SKShader shader)
    {
        VideoFrame frame = request.Frame;
        Draw(request, shader, frame.Width, frame.Height);
    }

    private static void Draw(in VideoRenderRequest request, SKShader shader, int sourceWidth, int sourceHeight)
    {
        using var paint = new SKPaint
        {
            IsAntialias = false,
            Shader = shader,
            Color = SKColors.White.WithAlpha(request.Alpha),
        };

        // The shader works in the frame's pixel space, so the fit to the control is a transform
        // rather than a destination rectangle.
        SKCanvas canvas = request.Canvas;
        int saved = canvas.Save();
        canvas.Translate(request.Destination.Left, request.Destination.Top);
        canvas.Scale(request.Destination.Width / sourceWidth, request.Destination.Height / sourceHeight);
        canvas.DrawRect(SKRect.Create(0, 0, sourceWidth, sourceHeight), paint);
        canvas.RestoreToCount(saved);
    }

    /// <summary>Maps shader coordinates in the source frame to physical canvas pixels.</summary>
    private static SKMatrix SourceToDevice(in VideoRenderRequest request)
    {
        VideoFrame frame = request.Frame;
        return SourceToDevice(request, frame.Width, frame.Height);
    }

    private static SKMatrix SourceToDevice(in VideoRenderRequest request, int sourceWidth, int sourceHeight)
    {
        SKMatrix view = request.Canvas.TotalMatrix;
        float scaleX = request.Destination.Width / sourceWidth;
        float scaleY = request.Destination.Height / sourceHeight;
        float offsetX = request.Destination.Left;
        float offsetY = request.Destination.Top;

        return new SKMatrix(
            view.ScaleX * scaleX,
            view.SkewX * scaleY,
            (view.ScaleX * offsetX) + (view.SkewX * offsetY) + view.TransX,
            view.SkewY * scaleX,
            view.ScaleY * scaleY,
            (view.SkewY * offsetX) + (view.ScaleY * offsetY) + view.TransY,
            view.Persp0 * scaleX,
            view.Persp1 * scaleY,
            (view.Persp0 * offsetX) + (view.Persp1 * offsetY) + view.Persp2);
    }

    private static void RecordDynamicHdr(in VideoRenderRequest request, VideoColorPlan plan)
    {
        if (request.Diagnostics is IVideoToneMappingDiagnostics toneMappingDiagnostics)
            toneMappingDiagnostics.RecordToneMapping(plan.ToneMappingAlgorithm);

        if (plan.DolbyVision)
        {
            request.Diagnostics?.RecordDynamicHdr(dolbyVisionApplied: true, hdr10Plus: null);
            return;
        }

        if (!plan.Hdr10Plus)
        {
            request.Diagnostics?.RecordDynamicHdr(dolbyVisionApplied: false, hdr10Plus: null);
            return;
        }

        Hdr10PlusMetadata metadata = request.Frame.Hdr10Plus;
        Hdr10PlusProcessingWindow window = metadata.Windows[0];
        request.Diagnostics?.RecordDynamicHdr(
            dolbyVisionApplied: false,
            new VideoHdr10PlusStatus(
                metadata.ApplicationVersion,
                metadata.WindowCount,
                metadata.TargetedSystemDisplayMaximumLuminance,
                metadata.EffectivePeakLuminance,
                metadata.SceneAverageLuminance,
                window.AnchorCount,
                plan.Hdr10PlusToneMap
                    ? VideoHdr10PlusMapping.St2094_40
                    : plan.SceneAdaptiveToneMap
                        ? VideoHdr10PlusMapping.SceneAdaptiveSpline
                        : VideoHdr10PlusMapping.MetadataOnly));
    }
}
