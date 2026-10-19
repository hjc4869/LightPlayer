using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Media;
using LightMediaRenderer.Avalonia.Rendering;
using LightMediaRenderer.Avalonia.Shaders;
using LightStudio.Logging;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Controls;

/// <summary>
/// Blits one decoded frame through Avalonia's Skia lease.
/// </summary>
/// <remarks>
/// Leasing the <see cref="SKCanvas"/> is what keeps this graphics-API agnostic: Avalonia hands over
/// whatever surface the active backend is drawing into (software, OpenGL, Vulkan, Metal, D3D) and
/// the same call renders on all of them. The frame is wrapped as a raster <see cref="SKImage"/>
/// directly over the decoder's buffer, so there is no copy on the way to the GPU upload. Planar
/// frames take the same route one plane at a time, with the color conversion the decoder skipped
/// happening in <see cref="VideoFramePipeline"/>.
/// </remarks>
internal sealed class VideoDrawOperation : ICustomDrawOperation
{
    private const uint GlTexture2D = 0x0DE1;
    private const uint GlRgba8 = 0x8058;
    private const uint GlRgba16Float = 0x881A;

    // Images Skia is left to draw on its own are tagged, so a color managed surface gets them
    // converted rather than reinterpreted. On a surface that is not, Skia ignores the tag.
    private static readonly SKColorSpace SrgbCurve = SKColorSpace.CreateSrgb();
    private static readonly object curveSync = new();
    private static SKColorSpace? powerCurve;
    private static float powerCurveExponent;

    private static SKColorSpace SourceColorSpace(VideoSurfaceTransfer transfer)
    {
        if (transfer.Kind != VideoSurfaceTransferKind.Power)
            return SrgbCurve;

        lock (curveSync)
        {
            // Not disposed on replacement: the previous one may still be held by a draw in flight,
            // and the exponent only changes when the window moves to another display.
            if (powerCurve is null || powerCurveExponent != transfer.Exponent)
            {
                powerCurveExponent = transfer.Exponent;
                powerCurve = SKColorSpace.CreateRgb(
                    new SKColorSpaceTransferFn { G = transfer.Exponent, A = 1 },
                    SKColorSpaceXyz.Srgb);
            }

            return powerCurve;
        }
    }

    private readonly VideoFrame frame;
    private readonly Rect destination;
    private readonly IVideoScaler scaler;
    private readonly IVideoScaler directScaler;
    private readonly VideoScalingQuality quality;
    private readonly VideoScalingQuality upscalingQuality;
    private readonly VideoScalingQuality downscalingQuality;
    private readonly VideoDebandingQuality debanding;
    private readonly bool hdrPassthrough;
    private readonly VideoSdrToneMappingOptions? toneMapping;
    private readonly int panelBitDepth;
    private readonly VideoRenderCaptureRequest? captureRequest;
    private readonly VideoFrameImageCache? imageCache;
    private readonly int imageVersion;
    private readonly VideoDeinterlaceHistory? deinterlaceHistory;
    private readonly long frameSequence;
    private readonly string? shaderPackageOverride;
    private readonly Action<bool, double>? outputChanged;
    private readonly Action<bool>? hdrOutputChanged;
    private readonly Action<VideoHdrOutputMapping>? toneMappingOutputChanged;
    private readonly CompositionCustomVisual? fencePollTarget;

    /// <summary>
    /// Clockwise turn asked for on top of the one the frame carries, which the photo viewer uses to
    /// stand a picture up without rewriting the file.
    /// </summary>
    private readonly int extraRotation;

    // Taken from the lease on every draw: the window can move to a surface with a different color
    // format or display volume at any time, and nothing outside the lease has a frame-stable view.
    private VideoOutputColorSpace target;
    private SKColorSpace? targetColorSpace;
    private VideoOutputColorVolume? preferredColorVolume;
    private SKRect deviceViewport;

    /// <summary>Takes ownership of one reference on <paramref name="frame"/>.</summary>
    public VideoDrawOperation(
        VideoFrame frame,
        Rect bounds,
        Rect destination,
        VideoDebandingQuality debanding,
        VideoScalingQuality quality,
        VideoScalingQuality upscalingQuality,
        VideoScalingQuality downscalingQuality,
        string? shaderPackageOverride,
        bool hdrPassthrough,
        int panelBitDepth = 0,
        VideoRenderCaptureRequest? captureRequest = null,
        VideoFrameImageCache? imageCache = null,
        int extraRotation = 0,
        Action<bool, double>? outputChanged = null,
        VideoDeinterlaceHistory? deinterlaceHistory = null,
        long frameSequence = 0,
        Action<bool>? hdrOutputChanged = null,
        Compositor? compositor = null,
        VideoSdrToneMappingOptions? toneMapping = null,
        Action<VideoHdrOutputMapping>? toneMappingOutputChanged = null)
    {
        this.frame = frame;
        fencePollTarget = frame.HasSurface ? VideoSurfaceReadFence.GetPollTarget(compositor) : null;
        this.destination = destination;
        this.debanding = debanding;
        this.quality = quality;
        this.upscalingQuality = upscalingQuality;
        this.downscalingQuality = downscalingQuality;
        this.hdrPassthrough = hdrPassthrough;
        this.toneMapping = toneMapping;
        this.panelBitDepth = panelBitDepth;
        this.captureRequest = captureRequest;
        this.imageCache = imageCache;
        imageVersion = imageCache?.Version ?? 0;
        this.deinterlaceHistory = deinterlaceHistory;
        this.frameSequence = frameSequence;
        this.shaderPackageOverride = shaderPackageOverride;
        this.outputChanged = outputChanged;
        this.hdrOutputChanged = hdrOutputChanged;
        this.toneMappingOutputChanged = toneMappingOutputChanged;
        this.extraRotation = ((extraRotation % 360) + 360) % 360;
        IVideoScaler selectedScaler = VideoScalers.For(quality);
        directScaler = VideoScalers.ForDirectDraw(selectedScaler);
        scaler = VideoScaleStageConfiguration.Attach(selectedScaler, shaderPackageOverride);
        Bounds = bounds;
    }

    public Rect Bounds { get; }

    public bool HitTest(Point p) => false;

    // Playback operations have no image cache and are never reused. An immutable photo may keep
    // its scene node until either its viewport or one of the rendering choices changes.
    public bool Equals(ICustomDrawOperation? other) =>
        imageCache is not null &&
        other is VideoDrawOperation operation &&
        ReferenceEquals(imageCache, operation.imageCache) &&
        imageVersion == operation.imageVersion &&
        Bounds == operation.Bounds &&
        destination == operation.destination &&
        quality == operation.quality &&
        upscalingQuality == operation.upscalingQuality &&
        downscalingQuality == operation.downscalingQuality &&
        debanding == operation.debanding &&
        hdrPassthrough == operation.hdrPassthrough &&
        toneMapping == operation.toneMapping &&
        panelBitDepth == operation.panelBitDepth &&
        extraRotation == operation.extraRotation &&
        ReferenceEquals(captureRequest, operation.captureRequest) &&
        string.Equals(shaderPackageOverride, operation.shaderPackageOverride, StringComparison.Ordinal);

    public void Render(ImmediateDrawingContext context)
    {
        bool capturing = captureRequest is { IsCompleted: false };
        if (frame.Width <= 0 || frame.Height <= 0)
            return;

        bool external = frame.Format == VideoPixelFormat.ExternalTexture;
        if (external && !VideoFrameCapabilities.IsExternalTextureCurrent(frame))
            return;

        if (!external && !frame.HasSurface && frame.PhotoSurface is null && frame.PhotoTiles is null && frame.PlaneSource is null && frame.Data == IntPtr.Zero)
            return;

        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
        {
            if (capturing)
                AppLog.Warning("Video.Capture", "The custom draw had no Skia lease feature.");
            return;
        }

        double renderStarted = MediaTime.Now;
        using ISkiaSharpApiLease lease = feature.Lease();
        SKCanvas canvas = lease.SkCanvas;
        int canvasState = canvas.SaveCount;
        try
        {
            RenderFrame(lease, canvas, external, renderStarted);
        }
        catch (Exception exception)
        {
            // Left to propagate, it would abandon the compositor's frame for every window.
            DrawOperationFailures.Report(nameof(VideoDrawOperation), exception);
            lease.GrContext?.ResetContext();
        }
        finally
        {
            canvas.RestoreToCount(canvasState);
        }
    }

    private void RenderFrame(ISkiaSharpApiLease lease, SKCanvas canvas, bool external, double renderStarted)
    {
        imageCache?.SetRenderContext(lease);

        // Where each top level draws with a graphics context of its own (the browser), this is the
        // only paint known to belong to the view showing the picture, and so the only one whose
        // context the decoders may hand their frames to.
        RenderThreadScheduler.EnterRenderPass();

        byte alpha = (byte)Math.Clamp(lease.CurrentOpacity * 255, 0, 255);
        target = lease.GetOutputColorSpace();
        targetColorSpace = lease.GetOutputSkColorSpace();
        preferredColorVolume = lease.GetOutputColorVolume();
        hdrOutputChanged?.Invoke(target.IsExtendedRange() && preferredColorVolume is
            { MaximumHeadroomRatio: > 1 } or { ToneMapping: VideoToneMappingMode.Platform });
        bool hdr = frame.DolbyVision.IsPresent ||
            frame.ColorTransfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg;
        VideoHdrOutputMapping hdrOutput = VideoFramePipeline.ResolveOutput(
            frame,
            target,
            hdrPassthrough,
            preferredColorVolume,
            toneMapping);
        toneMappingOutputChanged?.Invoke(hdrOutput);
        outputChanged?.Invoke(target.IsExtendedRange(), hdr
            ? hdrOutput.ToneMapPeakNits / hdrOutput.LinearNitsPerUnit
            : 1);
        VideoOutputDiagnostics.RecordSurface(
            lease,
            target,
            frame.ColorTransfer,
            hdrOutput,
            quality,
            hdrPassthrough,
            lease.GetOutputColorVolume());

        // Decoders cannot ask the graphics backend anything from where they run, so the answer to
        // whether it takes their surfaces is established here, where the context is in hand.
        VideoSurfaceImport.Evaluate(lease);
        VideoSurfaceImport.ReleaseRetired(lease);

        deviceViewport = canvas.TotalMatrix.MapRect(new SKRect(
            (float)Bounds.Left, (float)Bounds.Top, (float)Bounds.Right, (float)Bounds.Bottom));
        int savedCanvas = ApplyOrientation(canvas);

        if (frame.PhotoSurface is not null && imageCache is not null)
            imageCache.RenderPlatformPhoto(Request(canvas, lease.GrContext, alpha), lease);
        else
        {
            bool gainReady = imageCache?.PrepareGainMap(Request(canvas, lease.GrContext, alpha), lease) ?? true;
            if (frame.PhotoTiles is not null && imageCache is not null)
                imageCache.RenderPhotoTiles(Request(canvas, lease.GrContext, alpha), lease, gainReady);
            else if (gainReady && external)
                RenderExternal(canvas, lease.GrContext, alpha);
            else if (gainReady && frame.HasSurface)
                RenderSurface(lease, canvas, alpha);
            else if (gainReady && !VideoPlanarLayout.IsPlanar(frame.Format))
                RenderPacked(canvas, lease.GrContext, alpha);
            else if (gainReady)
                RenderPlanar(canvas, lease.GrContext, alpha, imageCache);
        }

        if (savedCanvas != 0)
            canvas.RestoreToCount(savedCanvas);

        VideoOutputDiagnostics.RecordRenderCpuTime(MediaTime.Now - renderStarted);

        if (captureRequest is { IsCompleted: false } capture)
            Capture(canvas, capture);
    }

    private void Capture(SKCanvas canvas, VideoRenderCaptureRequest request)
    {
        try
        {
            SKRect logical = new(
                (float)destination.X,
                (float)destination.Y,
                (float)destination.Right,
                (float)destination.Bottom);
            SKRect device = canvas.TotalMatrix.MapRect(logical);
            int left = (int)Math.Floor(device.Left);
            int top = (int)Math.Floor(device.Top);
            int right = (int)Math.Ceiling(device.Right);
            int bottom = (int)Math.Ceiling(device.Bottom);
            int width = right - left;
            int height = bottom - top;
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("The rendered video has no physical area to capture.");

            if (canvas.Surface is not { } surface)
                throw new InvalidOperationException("The active Skia canvas exposes no surface for pixel readback.");

            int rowBytes = checked(width * 4);
            var pixels = new byte[checked(rowBytes * height)];
            var previewInfo = new SKImageInfo(
                width,
                height,
                SKColorType.Bgra8888,
                SKAlphaType.Opaque,
                SKColorSpace.CreateSrgb());
            if (!ReadPixels(surface, previewInfo, pixels, rowBytes, left, top))
                throw new InvalidOperationException("The active Skia surface refused 8-bit pixel readback.");

            float[]? nativePixels = null;
            if (target == VideoOutputColorSpace.ScRgbLinear)
            {
                nativePixels = new float[checked(width * height * 4)];
                var nativeInfo = new SKImageInfo(
                    width,
                    height,
                    SKColorType.RgbaF32,
                    SKAlphaType.Premul,
                    targetColorSpace);
                if (!ReadPixels(surface, nativeInfo, nativePixels, checked(width * 4 * sizeof(float)), left, top))
                {
                    nativePixels = null;
                    AppLog.Warning("Video.Capture", "The scRGB surface refused native float pixel readback.");
                }
            }

            request.TrySetResult(new VideoRenderCapture(width, height, rowBytes, pixels, target, nativePixels));
            AppLog.Debug(
                "Video.Capture",
                $"Captured {width}x{height} rendered pixels; nativeFloat={nativePixels is not null}; target={target}.");
        }
        catch (Exception exception)
        {
            request.TrySetException(exception);
            AppLog.Error("Video.Capture", "Rendered-frame capture failed.", exception);
        }
    }

    private static bool ReadPixels<T>(
        SKSurface surface,
        SKImageInfo info,
        T[] pixels,
        int rowBytes,
        int left,
        int top)
        where T : struct
    {
        GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return surface.ReadPixels(info, pinned.AddrOfPinnedObject(), rowBytes, left, top);
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>
    /// Draws a frame the decoder left in the graphics memory it was decoded into, importing its
    /// planes as textures. Failing here withdraws surfaces from that decoder, which goes back to
    /// reading its frames out, so a backend that cannot do this loses one frame and no more.
    /// </summary>
    private void RenderSurface(ISkiaSharpApiLease lease, SKCanvas canvas, byte alpha)
    {
        using VideoSurfacePlanes? planes = VideoSurfaceImport.TryImport(lease, frame);
        if (planes is null)
        {
            if (frame.CanDownloadPhotoSurface)
            {
                VideoFrame? pixels = imageCache is not null ? imageCache.GetDownloadedPhoto() : frame.DownloadPhotoSurface?.Invoke();
                if (pixels is not null)
                {
                    try
                    {
                        VideoFramePipeline.Render(Request(canvas, lease.GrContext, alpha) with
                        {
                            Frame = pixels,
                            PlaneSource = null,
                            ImageCache = null,
                        });
                    }
                    finally
                    {
                        if (imageCache is null)
                            pixels.Release();
                    }
                }
                return;
            }
            if (frame.RejectSurfaceDelivery?.Invoke(frame) == true)
            {
                AppLog.Warning(
                    "Video.Surface",
                    $"Could not import {frame.Surface.Kind} surface {frame.Surface.Id}; " +
                    "falling back for its decoder.");
            }
            return;
        }

        try
        {
            if (!RenderImported(canvas, lease.GrContext, alpha, planes, out string failure) &&
                frame.RejectSurfaceDelivery?.Invoke(frame) == true)
            {
                AppLog.Warning(
                    "Video.Surface",
                    $"Could not render imported {frame.Surface.Kind} surface {frame.Surface.Id}: " +
                    $"{failure}; falling back for its decoder.");
            }
        }
        finally
        {
            if (planes.RequiresReadFence)
                VideoSurfaceReadFence.CompleteRead(lease, frame, fencePollTarget);
            else
                lease.GrContext?.Flush(submit: true);
        }
    }

    /// <summary>
    /// Draws an imported surface: as planes through the color pipeline, or, where the device
    /// already converted the picture, as the one interleaved texture it now is.
    /// </summary>
    private bool RenderImported(
        SKCanvas canvas,
        GRContext? grContext,
        byte alpha,
        VideoSurfacePlanes planes,
        out string failure)
    {
        if (VideoPlanarLayout.IsPlanar(frame.Format))
            return RenderPlanar(canvas, grContext, alpha, planes, out failure);

        var info = new SKImageInfo(
            frame.Width,
            frame.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Opaque,
            SourceColorSpace(VideoSurfaceTransfers.For(preferredColorVolume)));

        using SKImage? image = planes.CreatePlane(0, info);
        if (image is null)
        {
            failure = "the converted surface texture could not be wrapped for Skia";
            return false;
        }

        bool rendered = RenderImage(canvas, grContext, image, alpha);
        failure = rendered ? string.Empty : "the converted surface image could not be rendered";
        return rendered;
    }

    /// <summary>
    /// Draws a frame whose pixels were uploaded straight into a GPU texture by its producer, so
    /// nothing about it ever passed through CPU memory.
    /// </summary>
    private void RenderExternal(SKCanvas canvas, GRContext? grContext, byte alpha)
    {
        if (grContext is null)
            return;

        // A decoder drawing into a platform window has not shown this picture yet: the window holds
        // one at a time, and which one that is only settles here, at the moment it is drawn. The
        // texture itself may not exist until then either, so the name is read afterwards.
        bool fromWindow = frame.AcquireExternalTexture is not null;
        bool acquired = frame.TryAcquireExternalTexture(out bool attemptedNow);

        // The producer may have bound textures, framebuffers, and programs even when acquisition
        // failed, so Skia's cached idea of the GL state is stale on both paths.
        if (attemptedNow)
            grContext.ResetContext();
        if (!acquired)
        {
            if (fromWindow && attemptedNow)
                VideoDecoderOutputSurfaces.NoteAcquireFailed("the decoder's window showed no picture");
            return;
        }

        if (frame.ExternalHandle <= 0)
        {
            if (fromWindow && attemptedNow)
                VideoDecoderOutputSurfaces.NoteAcquireFailed("the decoder's window has no texture to sample");
            return;
        }

        uint textureTarget = frame.ExternalTextureTarget != 0 ? frame.ExternalTextureTarget : GlTexture2D;
        (uint glFormat, SKColorType colorType) = frame.ExternalTextureFormat switch
        {
            VideoExternalTextureFormat.Rgba16Float => (GlRgba16Float, SKColorType.RgbaF16),
            _ => (GlRgba8, SKColorType.Rgba8888),
        };
        var textureInfo = new GRGlTextureInfo(textureTarget, (uint)frame.ExternalHandle, glFormat);
        using var backendTexture = new GRBackendTexture(frame.Width, frame.Height, false, textureInfo);

        // Skia samples the borrowed texture on its own schedule, so the frame has to outlive the
        // image rather than the draw call: releasing it here would let its owner recycle the
        // texture and overwrite the picture before the GPU had read it.
        frame.AddRef();
        SKImage? image = SKImage.FromTexture(
            grContext,
            backendTexture,
            GRSurfaceOrigin.TopLeft,
            colorType,
            SKAlphaType.Opaque,
            SourceColorSpace(VideoSurfaceTransfers.For(preferredColorVolume)),
            ReleaseFrame,
            frame);

        // Skia has already invoked ReleaseFrame when it could not wrap the texture.
        if (image is null)
        {
            if (fromWindow && attemptedNow)
                VideoDecoderOutputSurfaces.NoteAcquireFailed("Skia would not sample the decoder's texture");
            return;
        }

        bool drawn;
        using (image)
            drawn = RenderImage(canvas, grContext, image, alpha);

        if (fromWindow)
        {
            if (drawn)
                VideoDecoderOutputSurfaces.NoteAcquired();
            else if (attemptedNow)
                VideoDecoderOutputSurfaces.NoteAcquireFailed("the decoder's YUV texture could not be color converted");
        }

        // Without this the draw sits unsubmitted and Skia has no reason to let the texture go.
        grContext.Flush(submit: true);
    }

    private static readonly SKImageTextureReleaseDelegate ReleaseFrame =
        static context => ((VideoFrame)context).Release();

    private void RenderPacked(SKCanvas canvas, GRContext? grContext, byte alpha)
    {
        var info = new SKImageInfo(frame.Width, frame.Height, frame.Format switch
            {
                VideoPixelFormat.Rgba64 => SKColorType.Rgba16161616,
                VideoPixelFormat.RgbaF16 => SKColorType.RgbaF16,
                _ => SKColorType.Bgra8888,
            }, frame.HasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque,
            SourceColorSpace(VideoSurfaceTransfers.For(preferredColorVolume)));

        if (frame.PlaneSource is { } source)
        {
            SKImage? supplied = source.CreatePlane(0, info, grContext);
            if (supplied is not null) RenderImage(canvas, grContext, supplied, alpha);
            return;
        }

        if (imageCache is { } cache)
        {
            bool tiledScene = scaler.PreScaleStage is null &&
                VideoScalers.SupportsPhotoSceneCaching(scaler) &&
                (scaler.RequiresSceneBuffer || VideoDebanding.Applies(debanding, VideoPlanarLayout.Packed));
            SKImage? cached = cache.GetPackedImage(info, grContext, promote: !tiledScene);
            if (cached is not null)
                RenderImage(canvas, grContext, cached, alpha);
            return;
        }

        using SKImage? image = SKImage.FromPixels(info, frame.Data, frame.Stride);
        if (image is null)
            return;

        RenderImage(canvas, grContext, image, alpha);
    }

    /// <summary>
    /// Draws an already interleaved picture. One the surface can take as it stands is left to Skia,
    /// whose own color management then converts it; anything else goes through the pipeline.
    /// </summary>
    private bool RenderImage(SKCanvas canvas, GRContext? grContext, SKImage image, byte alpha)
    {
        bool rawYuv = frame.ExternalTextureColorModel == VideoExternalTextureColorModel.Yuv;

        // An extended surface stores light, so even an otherwise ordinary packed picture needs its
        // transfer decoded by the pipeline. A filter carrying its own kernel likewise exists only
        // there, and Skia does not know that alternate rows were sampled at different instants.
        if ((rawYuv ||
             VideoFramePipeline.NeedsColorProcessing(frame) ||
                         target.IsExtendedRange() ||
                         VideoFramePipeline.NeedsDeinterlacing(frame) ||
                         scaler.RequiresSceneBuffer ||
                         !frame.ExternalTextureRect.IsWhole ||
                         VideoDebanding.Applies(debanding, VideoPlanarLayout.Packed)) &&
            VideoFramePipeline.Render(Request(canvas, grContext, alpha) with { PackedImage = image }))
        {
            return true;
        }

        // These channels are Y, Cb, and Cr rather than a display color. Showing them as RGB would
        // hide a shader failure behind a plausible but corrupt picture and keep hardware active.
        if (rawYuv)
            return false;

        // Skia would read the whole texture, which for a picture sitting inside a larger one is
        // the padding and the wrong way up as well.
        if (!frame.ExternalTextureRect.IsWhole)
            return false;

        using var paint = new SKPaint
        {
            IsAntialias = false,
            Color = SKColors.White.WithAlpha(alpha),
        };

        // Sampling only matters when the control is not at native resolution.
        canvas.DrawImage(image, DestinationRect, directScaler.Sampling, paint);
        VideoOutputDiagnostics.RecordPreScale(active: null);
        VideoOutputDiagnostics.RecordDynamicHdr(dolbyVisionApplied: false, hdr10Plus: null);
        VideoOutputDiagnostics.RecordColorProgram(drawnWithoutShader: true);

        // Skia's own conversion has no dither stage to put anything in.
        VideoOutputDiagnostics.RecordDither(outputBitDepth: 0, dithered: false, panelBitDepth > 0);
        VideoOutputDiagnostics.RecordScaling(
            directScaler.Quality,
            ReferenceEquals(directScaler, scaler)
                ? string.Empty
                : "Skia drew the picture itself, and the filter's program is no part of that");
        return true;
    }

    /// <summary>
    /// Draws a frame the decoder never converted, doing the color work in the shader pipeline.
    /// Returns false when nothing was drawn.
    /// </summary>
    private bool RenderPlanar(SKCanvas canvas, GRContext? grContext, byte alpha, IVideoPlaneSource? planeSource)
    {
        // A layout this backend cannot reproduce is left undrawn for the one frame it takes the
        // decoder to start converting instead, which beats showing a frame known to be wrong.
        // Imported surfaces are already format-typed GPU textures, not CPU planes this probe wraps.
        if (planeSource is not VideoSurfacePlanes &&
            !VideoPlaneFormatProbe.Supports(grContext, frame.Format))
            return false;

        return VideoFramePipeline.Render(Request(canvas, grContext, alpha) with { PlaneSource = planeSource });
    }

    private bool RenderPlanar(
        SKCanvas canvas,
        GRContext? grContext,
        byte alpha,
        IVideoPlaneSource planeSource,
        out string failure) =>
        VideoFramePipeline.Render(
            Request(canvas, grContext, alpha) with { PlaneSource = planeSource },
            out failure);

    private VideoRenderRequest Request(SKCanvas canvas, GRContext? grContext, byte alpha) =>
        new(
            canvas,
            grContext,
            frame,
            DestinationRect,
            alpha,
            scaler,
            target,
            hdrPassthrough,
            preferredColorVolume,
            panelBitDepth,
            debanding)
        {
            Diagnostics = VideoOutputDiagnostics.Renderer,
            DeinterlaceHistory = deinterlaceHistory,
            FrameSequence = frameSequence,
            ImageCache = imageCache,
            DeviceViewport = imageCache is null ? null : deviceViewport,
            UpscalingQuality = upscalingQuality,
            DownscalingQuality = downscalingQuality,
            SdrToneMapping = toneMapping,
        };

    /// <summary>
    /// Turns the canvas so the picture the camera recorded sideways comes out upright, leaving
    /// everything downstream to draw it as if it had been coded that way. Returns the save count to
    /// restore, or 0 when nothing was changed.
    /// </summary>
    /// <remarks>
    /// A transform rather than a rotated blit: the shader pipeline works from the canvas matrix, so
    /// the turn costs nothing and none of the sampling, scaling or color work has to know of it.
    /// </remarks>
    private int ApplyOrientation(SKCanvas canvas)
    {
        int rotation = Rotation;
        if (rotation == 0 && !frame.MirrorHorizontally)
            return 0;

        var center = new SKPoint((float)destination.Center.X, (float)destination.Center.Y);
        int saved = canvas.Save();
        canvas.Translate(center.X, center.Y);
        canvas.RotateDegrees(rotation);

        // The container mirrors the coded picture, so this belongs on the source side of the turn.
        if (frame.MirrorHorizontally)
            canvas.Scale(-1, 1);

        canvas.Translate(-center.X, -center.Y);
        return saved;
    }

    /// <summary>Total clockwise turn between the coded picture and the screen.</summary>
    private int Rotation => (frame.Rotation + extraRotation) % 360;

    /// <summary>
    /// Where the picture is drawn before the orientation is applied: the destination laid back down
    /// the way the samples are stored, which for a quarter turn means its extents swapped.
    /// </summary>
    private SKRect DestinationRect
    {
        get
        {
            if (Rotation is not (90 or 270))
            {
                return new SKRect(
                    (float)destination.X,
                    (float)destination.Y,
                    (float)destination.Right,
                    (float)destination.Bottom);
            }

            var center = destination.Center;
            float halfWidth = (float)destination.Height / 2;
            float halfHeight = (float)destination.Width / 2;
            return new SKRect(
                (float)center.X - halfWidth,
                (float)center.Y - halfHeight,
                (float)center.X + halfWidth,
                (float)center.Y + halfHeight);
        }
    }

    public void Dispose()
    {
        imageCache?.Release();
        deinterlaceHistory?.Release();
        frame.Release();
    }
}

/// <summary>Post-render pixels read from the active video output surface.</summary>
/// <param name="Pixels">sRGB BGRA8 preview pixels, suitable for PNG output.</param>
/// <param name="NativeColorSpace">Encoding of <paramref name="NativeRgbaF32"/>.</param>
/// <param name="NativeRgbaF32">
/// Native RGBA float pixels when the output surface supports extended linear light; otherwise null.
/// Values are deliberately not clamped, so HDR and out-of-gamut output remain measurable.
/// </param>
public sealed record VideoRenderCapture(
    int Width,
    int Height,
    int RowBytes,
    byte[] Pixels,
    VideoOutputColorSpace NativeColorSpace,
    float[]? NativeRgbaF32);

internal sealed class VideoRenderCaptureRequest
{
    private readonly TaskCompletionSource<VideoRenderCapture> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration cancellationRegistration;

    public VideoRenderCaptureRequest(CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
            cancellationRegistration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
    }

    public Task<VideoRenderCapture> Task => completion.Task;

    public bool IsCompleted => completion.Task.IsCompleted;

    public void TrySetResult(VideoRenderCapture capture)
    {
        if (completion.TrySetResult(capture))
            cancellationRegistration.Dispose();
    }

    public void TrySetException(Exception exception)
    {
        if (completion.TrySetException(exception))
            cancellationRegistration.Dispose();
    }
}

/// <summary>Logs failed custom draws, each distinct failure once, since a broken draw repeats every frame.</summary>
internal static class DrawOperationFailures
{
    private static readonly Dictionary<string, string> lastReported = new(StringComparer.Ordinal);

    public static void Report(string operation, Exception exception)
    {
        string signature = $"{exception.GetType().FullName}: {exception.Message}";
        lock (lastReported)
        {
            if (lastReported.TryGetValue(operation, out string? previous) && previous == signature)
                return;
            lastReported[operation] = signature;
        }

        AppLog.Error("Video.Rendering", $"{operation} failed and its draw was skipped.", exception);
    }
}
