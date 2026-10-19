using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Skia;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Media;
using LightMediaRenderer.Avalonia.Rendering;
using LightStudio.Logging;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Shaders;

internal sealed class PhotoTileCache(VideoFrame frame, Action invalidate, Action<Exception> reportFailure, bool samplesOnly = false) : IDisposable
{
    private static readonly PhotoColorProfile LinearColor = new([1, 1, 0, 1, 0, 0, 0],
        [1, 0, 0, 0, 1, 0, 0, 0, 1]);
    private static readonly PhotoColorProfile LinearAlphaColor = new([1, 1, 0, 1, 0, 0, 0],
        [1, 0, 0, 0, 1, 0, 0, 0, 1], hasAlpha: true);
    private readonly IPhotoTileSource source = frame.PhotoTiles!;
    private readonly bool preferThroughput = PhotoDecodeResources.PreferThroughput;
    private readonly object sync = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<Work, Tile> details = [];
    private readonly HashSet<Work> failed = [];
    private readonly HashSet<int> overviewTiles = [];
    private List<Decoded>? ready;
    private bool working;
    private int recovering;
    private bool disposed;
    private bool failureReported;
    private int overviewReduction;
    private SKSurface? overview;
    private Tile? overviewImage;
    private Tile? sampleWindow;
    private SKRectI sampleBounds;
    private int sampleReduction;
    private int revision;
    private int sampleRevision = -1;
    private long loadStarted;
    private long decodeTicks;
    private long uploadTicks;
    private int decodedBatches;
    private int hardwareFrames;

    private bool HasScaledOverview => source.HasScaledOverview && !preferThroughput;

    private readonly record struct Work(int Index, int Reduction, bool Overview);
    private readonly record struct Decoded(Work Work, VideoFrame? Frame, Exception? Error = null);

    private sealed class Tile(SKImage image, VideoFrame samples, PhotoTileBounds bounds) : IDisposable
    {
        public SKImage Image { get; } = image;
        public VideoFrame Samples { get; } = samples;
        public PhotoTileBounds Bounds { get; } = bounds;

        public void Dispose()
        {
            Image.Dispose();
            Samples.Release();
        }
    }

    public void Render(in VideoRenderRequest request, ISkiaSharpApiLease lease, bool draw = true)
    {
        if (disposed)
            return;
        SKMatrix toDevice = SKMatrix.Concat(request.Canvas.TotalMatrix,
            SKMatrix.CreateScaleTranslation(request.Destination.Width / frame.Width, request.Destination.Height / frame.Height,
                request.Destination.Left, request.Destination.Top));
        if (!toDevice.TryInvert(out SKMatrix inverse))
            return;
        SKRect viewport = request.DeviceViewport ?? request.Canvas.DeviceClipBounds;
        SKRect visible = inverse.MapRect(viewport);
        visible.Intersect(SKRect.Create(frame.Width, frame.Height));
        if (visible.IsEmpty)
            return;
        double minification = 1 / Math.Max(1e-9,
            Math.Max(double.Hypot(toDevice.ScaleX, toDevice.SkewY), double.Hypot(toDevice.ScaleY, toDevice.SkewX)));
        int reduction = 1 << Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, minification))) -
            (HasScaledOverview ? 0 : 1), 0, 10);
        long budget = Math.Clamp((long)viewport.Width * (long)viewport.Height * 48,
            16L * 1024 * 1024, 96L * 1024 * 1024);
        int maximumEdge = preferThroughput || HasScaledOverview
            ? Math.Min(8192, lease.GrContext?.MaxTextureSize ?? 8192) : 2048;
        int minimum = 1 << Math.Clamp((int)Math.Ceiling(Math.Log2(Math.Max(1, Math.Max(frame.Width, frame.Height) / (double)maximumEdge))), 0, 16);
        while ((long)((frame.Width + minimum - 1) / minimum) * ((frame.Height + minimum - 1) / minimum) * 8 > budget)
            minimum *= 2;
        if (overviewReduction == 0)
        {
            overviewReduction = Math.Max(minimum, reduction);
        }
        else if (HasScaledOverview && Math.Max(minimum, reduction) < overviewReduction &&
            visible.Width >= frame.Width - 0.5f && visible.Height >= frame.Height - 0.5f)
        {
            overviewReduction = Math.Max(minimum, reduction);
            overviewTiles.Clear();
        }
        else if (!preferThroughput && !HasScaledOverview && overviewImage is { } previous && reduction >= overviewReduction * 2)
        {
            SKImage compact = Reduce(previous.Image, Math.Max(1, (frame.Width + reduction - 1) / reduction),
                Math.Max(1, (frame.Height + reduction - 1) / reduction), lease.GrContext);
            overviewImage = CreateTile(compact, previous.Bounds);
            previous.Dispose();
            overviewReduction = reduction;
            revision++;
        }

        UploadReady(request, lease);
        var wanted = new HashSet<Work>();
        int detailReduction = preferThroughput ? 1 : reduction;
        while (preferThroughput || detailReduction < overviewReduction)
        {
            wanted.Clear();
            long bytes = 0;
            for (int index = 0; index < source.Count; index++)
            {
                PhotoTileBounds bounds = source.GetBounds(index);
                if (!preferThroughput && !visible.IntersectsWith(SKRect.Create(bounds.X, bounds.Y, bounds.Width, bounds.Height)))
                    continue;
                wanted.Add(new Work(index, detailReduction, false));
                bytes += (long)((bounds.Width + detailReduction - 1) / detailReduction) * ((bounds.Height + detailReduction - 1) / detailReduction) * 8;
            }
            if (preferThroughput || bytes <= budget)
                break;
            detailReduction *= 2;
        }
        if (!preferThroughput && detailReduction >= overviewReduction)
            wanted.Clear();
        foreach (Work key in new List<Work>(details.Keys))
        {
            if (wanted.Contains(key))
                continue;
            details[key].Dispose();
            details.Remove(key);
            revision++;
        }

        if (samplesOnly || frame.HasAlpha)
        {
            UpdateSampleWindow(visible, preferThroughput ? reduction : detailReduction, lease.GrContext);
            if (!samplesOnly && draw && sampleWindow is { } combined)
                Draw(request, combined);
        }
        else if (draw)
        {
            int saved = request.Canvas.Save();
            try
            {
                request.Canvas.ClipRect(request.Destination);
                if (overviewImage is { } complete)
                {
                    Draw(request, complete);
                    bool detailReady = true;
                    foreach (Work item in wanted)
                        detailReady &= details.ContainsKey(item);
                    if (detailReady)
                        foreach (Tile tile in details.Values)
                            Draw(request, tile);
                }
            }
            finally { request.Canvas.RestoreToCount(saved); }
        }

        int maximumBatch = preferThroughput ? Math.Min(Environment.ProcessorCount, 8) * 16 : 16;
        long maximumPixels = (preferThroughput ? 32L : 4L) * 1024 * 1024;
        int preferredBatch = source.PreferredBatchSize;
        if (preferredBatch > 0)
        {
            if (preferredBatch > maximumBatch)
                maximumPixels = maximumPixels * preferredBatch / maximumBatch;
            maximumBatch = preferredBatch;
        }
        var work = new List<Work>(maximumBatch);
        long pendingPixels = 0;
        foreach (Work candidate in wanted)
        {
            if (!details.ContainsKey(candidate) && !failed.Contains(candidate) && !AddWork(candidate))
                break;
        }
        for (int index = HasScaledOverview ? -1 : 0; !preferThroughput && index < (HasScaledOverview ? 0 : source.Count); index++)
        {
            var candidate = new Work(index, overviewReduction, true);
            if (!overviewTiles.Contains(index) && !failed.Contains(candidate) && !AddWork(candidate))
                break;
        }
        Queue(work);

        bool AddWork(Work candidate)
        {
            if (work.Exists(item => item.Index == candidate.Index))
                return true;
            PhotoTileBounds bounds = source.GetBounds(candidate.Index);
            int divisor = HasScaledOverview ? Math.Min(8, candidate.Reduction) : 1;
            long pixels = (long)((bounds.Width + divisor - 1) / divisor) * ((bounds.Height + divisor - 1) / divisor);
            if (work.Count >= maximumBatch || work.Count > 0 && pendingPixels + pixels > maximumPixels)
                return false;
            pendingPixels += pixels;
            work.Add(candidate);
            return true;
        }
    }

    private void Queue(List<Work> work)
    {
        lock (sync)
        {
            if (disposed || working || recovering > 0 || ready is not null || work.Count == 0)
                return;
            if (loadStarted == 0)
                loadStarted = Stopwatch.GetTimestamp();
            working = true;
        }
        CancellationToken token = cancellation.Token;
        frame.AddRef();
        _ = DecodeAsync();

        async Task DecodeAsync()
        {
            var decoded = new List<Decoded>(work.Count);
            long started = Stopwatch.GetTimestamp();
            try
            {
                var requests = new PhotoTileRequest[work.Count];
                for (int index = 0; index < requests.Length; index++)
                    requests[index] = new PhotoTileRequest(work[index].Index, work[index].Reduction);
                VideoFrame[] frames = await source.DecodeBatchAsync(requests, token).ConfigureAwait(false);
                if (frames.Length != work.Count)
                {
                    foreach (VideoFrame result in frames)
                        result?.Release();
                    throw new InvalidOperationException("The photo decoder returned an unexpected number of tiles.");
                }
                for (int index = 0; index < frames.Length; index++)
                {
                    decoded.Add(new Decoded(work[index], frames[index]));
                    if (frames[index] is { } decodedFrame &&
                        (decodedFrame.HasSurface || decodedFrame.Format == VideoPixelFormat.ExternalTexture))
                        Interlocked.Increment(ref hardwareFrames);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                foreach (Work item in work)
                    decoded.Add(new Decoded(item, null, exception));
                AppLog.Error("Photo.Rendering", "Could not decode a photo batch.", exception);
            }
            finally
            {
                Interlocked.Add(ref decodeTicks, Stopwatch.GetTimestamp() - started);
                Interlocked.Increment(ref decodedBatches);
                lock (sync)
                {
                    working = false;
                    if (disposed)
                    {
                        foreach (Decoded item in decoded)
                            item.Frame?.Release();
                    }
                    else
                        ready = decoded;
                }
                frame.Release();
                if (!token.IsCancellationRequested)
                    invalidate();
            }
        }
    }

    private void UploadReady(in VideoRenderRequest request, ISkiaSharpApiLease lease)
    {
        List<Decoded>? decoded;
        List<Decoded>? recovery = null;
        bool pending = false;
        lock (sync)
        {
            decoded = ready;
            ready = null;
        }
        if (decoded is null)
            return;
        long uploadStarted = Stopwatch.GetTimestamp();
        int consumed = 0;
        bool overviewCompleted = false;
        revision++;
        try
        {
            foreach (Decoded item in decoded)
            {
                if (OperatingSystem.IsAndroid() && consumed > 0 && Stopwatch.GetElapsedTime(uploadStarted).TotalMilliseconds >= 6)
                    break;
                consumed++;
                if (item.Frame is not { } pixels)
                {
                    failed.Add(item.Work);
                    if (item.Error is { } error)
                        ReportFailure(error);
                    continue;
                }
                try
                {
                    if (HasScaledOverview && item.Work.Overview && item.Work.Reduction != overviewReduction)
                        continue;
                    PhotoTileBounds bounds = source.GetBounds(item.Work.Index);
                    SKRectI original = SKRectI.Create(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    SKRectI visible = SKRectI.Intersect(original, SKRectI.Create(frame.Width, frame.Height));
                    if (visible.IsEmpty)
                    {
                        if (item.Work.Overview)
                            overviewTiles.Add(item.Work.Index);
                        continue;
                    }
                    SKRectI? samplingBounds = visible == original ? null : new SKRectI(
                        (int)((long)(visible.Left - bounds.X) * pixels.Width / bounds.Width),
                        (int)((long)(visible.Top - bounds.Y) * pixels.Height / bounds.Height),
                        (int)(((long)(visible.Right - bounds.X) * pixels.Width + bounds.Width - 1) / bounds.Width),
                        (int)(((long)(visible.Bottom - bounds.Y) * pixels.Height + bounds.Height - 1) / bounds.Height));
                    using SKImage? linear = Linearize(pixels, samplingBounds, request, lease);
                    if (linear is null)
                    {
                        (recovery ??= []).Add(new Decoded(item.Work, pixels.AddRef()));
                        continue;
                    }
                    bounds = new PhotoTileBounds(visible.Left, visible.Top, visible.Width, visible.Height);
                    int width = Math.Max(1, (bounds.Width + item.Work.Reduction - 1) / item.Work.Reduction);
                    int height = Math.Max(1, (bounds.Height + item.Work.Reduction - 1) / item.Work.Reduction);
                    SKImage reduced = Reduce(linear, width, height, lease.GrContext);
                    bool retained = false;
                    try
                    {
                        if (HasScaledOverview && item.Work.Overview)
                        {
                            Tile? previous = overviewImage;
                            overviewImage = CreateTile(reduced, bounds);
                            retained = true;
                            previous?.Dispose();
                            overviewTiles.Add(item.Work.Index);
                            overviewCompleted = true;
                        }
                        else if (overviewImage is null && (item.Work.Overview || !HasScaledOverview) &&
                            !overviewTiles.Contains(item.Work.Index))
                        {
                            using SKImage? overviewReduced = item.Work.Reduction == overviewReduction ? null : Reduce(linear,
                                Math.Max(1, (bounds.Width + overviewReduction - 1) / overviewReduction),
                                Math.Max(1, (bounds.Height + overviewReduction - 1) / overviewReduction), lease.GrContext);
                            overview ??= CreateSurface(lease.GrContext, (frame.Width + overviewReduction - 1) / overviewReduction,
                                (frame.Height + overviewReduction - 1) / overviewReduction);
                            using var paint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false };
                            overview.Canvas.DrawImage(overviewReduced ?? reduced,
                                SKRect.Create((float)bounds.X / overviewReduction, (float)bounds.Y / overviewReduction,
                                    (float)bounds.Width / overviewReduction, (float)bounds.Height / overviewReduction),
                                new SKSamplingOptions(SKFilterMode.Linear), paint);
                            overviewTiles.Add(item.Work.Index);
                        }
                        if (!item.Work.Overview)
                        {
                            if (details.Remove(item.Work, out Tile? old))
                                old.Dispose();
                            details[item.Work] = CreateTile(reduced, bounds);
                            retained = true;
                        }
                    }
                    finally
                    {
                        if (!retained)
                            reduced.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    failed.Add(item.Work);
                    AppLog.Error("Photo.Rendering", $"Could not upload photo region {item.Work.Index}.", exception);
                    ReportFailure(exception);
                }
                finally
                {
                    if (pixels.AcquireExternalTexture is not null)
                        lease.GrContext?.Flush(submit: true);
                }
            }
        }
        finally
        {
            FinishUpload(decoded, consumed, recovery, lease, uploadStarted, out pending);
        }
        if (overviewImage is null && overview is not null && overviewTiles.Count == (HasScaledOverview ? 1 : source.Count))
        {
            overviewImage = CreateTile(overview.Snapshot(), new PhotoTileBounds(0, 0, frame.Width, frame.Height));
            overview.Dispose();
            overview = null;
            overviewCompleted = true;
        }
        if (overviewCompleted)
        {
            AppLog.Information("Photo.Rendering", $"Photo {(samplesOnly ? "samples" : "overview")} {frame.Width}x{frame.Height}: " +
                $"tiles={source.Count}, reduction={overviewReduction}, batches={Volatile.Read(ref decodedBatches)}, hardwareFrames={Volatile.Read(ref hardwareFrames)}, " +
                $"elapsed={Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds:F1} ms, " +
                $"decode={Interlocked.Read(ref decodeTicks) * 1000.0 / Stopwatch.Frequency:F1} ms, " +
                $"upload={uploadTicks * 1000.0 / Stopwatch.Frequency:F1} ms.");
        }
        lease.GrContext?.Flush(submit: true);
        if (pending)
            invalidate();
    }

    private void FinishUpload(List<Decoded> decoded, int consumed, List<Decoded>? recovery, ISkiaSharpApiLease lease,
        long uploadStarted, out bool pending)
    {
        pending = false;
        try
        {
            lease.GrContext?.Flush(submit: true);
            if (decoded.Exists(item => item.Frame?.HasSurface == true || item.Frame?.AcquireExternalTexture is not null))
                lease.GrContext?.Submit(synchronous: true);
        }
        finally
        {
            for (int index = 0; index < consumed; index++)
                decoded[index].Frame?.Release();
            lock (sync)
            {
                if (disposed)
                {
                    for (int index = consumed; index < decoded.Count; index++)
                        decoded[index].Frame?.Release();
                }
                else if (consumed < decoded.Count)
                    ready = decoded.GetRange(consumed, decoded.Count - consumed);
                pending = ready is not null;
            }
            uploadTicks += Stopwatch.GetTimestamp() - uploadStarted;
            if (recovery is not null)
                Recover(recovery);
        }
    }

    private void Recover(List<Decoded> retries)
    {
        CancellationToken token;
        lock (sync)
        {
            if (disposed)
            {
                foreach (Decoded item in retries)
                    item.Frame?.Release();
                return;
            }
            recovering++;
            token = cancellation.Token;
            frame.AddRef();
        }
        _ = RecoverAsync();

        async Task RecoverAsync()
        {
            var recovered = new List<Decoded>(retries.Count);
            try
            {
                foreach (Decoded item in retries)
                {
                    try
                    {
                        VideoFrame pixels = await item.Frame!.DownloadPhotoAsync(token).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("The photo region could not be decoded in software.");
                        if (pixels.HasSurface || pixels.Format == VideoPixelFormat.ExternalTexture ||
                            pixels.PhotoSurface is not null || pixels.PhotoTiles is not null)
                        {
                            pixels.Release();
                            throw new InvalidOperationException("The photo region fallback did not return software pixels.");
                        }
                        pixels.PhotoColor = item.Frame.PhotoColor;
                        pixels.ColorPrimaries = item.Frame.ColorPrimaries;
                        pixels.ColorTransfer = item.Frame.ColorTransfer;
                        recovered.Add(new Decoded(item.Work, pixels));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        recovered.Add(new Decoded(item.Work, null, exception));
                        AppLog.Error("Photo.Rendering", "Could not load a software photo region.", exception);
                    }
                }
            }
            finally
            {
                foreach (Decoded item in retries)
                    item.Frame?.Release();
                lock (sync)
                {
                    recovering--;
                    if (disposed)
                    {
                        foreach (Decoded item in recovered)
                            item.Frame?.Release();
                    }
                    else if (ready is null)
                        ready = recovered;
                    else
                        ready.AddRange(recovered);
                }
                frame.Release();
                if (!token.IsCancellationRequested)
                    invalidate();
            }
        }
    }

    private void ReportFailure(Exception exception)
    {
        if (failureReported)
            return;
        failureReported = true;
        reportFailure(exception);
    }

    private static SKImage? Linearize(VideoFrame decoded, SKRectI? samplingBounds,
        in VideoRenderRequest request, ISkiaSharpApiLease lease)
    {
        using SKImage? external = decoded.Format == VideoPixelFormat.ExternalTexture ? ImportTexture(decoded, lease) : null;
        using VideoSurfacePlanes? imported = decoded.HasSurface ? VideoSurfaceImport.TryImport(lease, decoded) : null;
        if (decoded.HasSurface && imported is null || decoded.Format == VideoPixelFormat.ExternalTexture && external is null)
        {
            if (decoded.CanDownloadPhotoSurface)
                return null;
            throw new InvalidOperationException("The photo region could not be imported and has no software fallback.");
        }
        VideoFrame pixels = decoded;
        var cache = new VideoFrameImageCache(pixels);
        try
        {
            cache.SetRenderContext(lease);
            SKImage? packed = external is null && !VideoPlanarLayout.IsPlanar(pixels.Format) && !pixels.HasSurface
                ? cache.GetPackedImage(new SKImageInfo(pixels.Width, pixels.Height, pixels.Format switch
                    {
                        VideoPixelFormat.Rgba64 => SKColorType.Rgba16161616,
                        VideoPixelFormat.RgbaF16 => SKColorType.RgbaF16,
                        _ => SKColorType.Bgra8888,
                    }, SKAlphaType.Unpremul),
                    lease.GrContext) : null;
            VideoRenderRequest linear = request with
            {
                Frame = pixels,
                PlaneSource = imported ?? (IVideoPlaneSource)cache,
                PackedImage = external ?? packed,
                ImageCache = external is null ? cache : null,
                Scaler = VideoScalers.For(VideoScalingQuality.Bilinear),
                SceneBounds = samplingBounds,
                SamplingBounds = samplingBounds,
            };
            return VideoFramePipeline.RenderPhotoLinear(linear, out string failure)
                ?? throw new InvalidOperationException($"Could not process photo pixels: {failure}.");
        }
        finally
        {
            lease.GrContext?.Flush(submit: true);
            cache.Release();
        }
    }

    private static SKImage? ImportTexture(VideoFrame pixels, ISkiaSharpApiLease lease)
    {
        if (lease.GrContext is not { } context || !VideoFrameCapabilities.IsExternalTextureCurrent(pixels))
            return null;
        bool acquired = pixels.TryAcquireExternalTexture(out bool attempted);
        if (attempted)
            context.ResetContext();
        if (!acquired || pixels.ExternalHandle <= 0)
            return null;
        uint target = pixels.ExternalTextureTarget != 0 ? pixels.ExternalTextureTarget : 0x0DE1;
        bool halfFloat = pixels.ExternalTextureFormat == VideoExternalTextureFormat.Rgba16Float;
        using var texture = new GRBackendTexture(pixels.Width, pixels.Height, false,
            new GRGlTextureInfo(target, (uint)pixels.ExternalHandle, halfFloat ? 0x881Au : 0x8058u));
        // Skia invokes the release callback itself when it cannot wrap the texture.
        pixels.AddRef();
        return SKImage.FromTexture(context, texture, GRSurfaceOrigin.TopLeft,
            halfFloat ? SKColorType.RgbaF16 : SKColorType.Rgba8888, SKAlphaType.Opaque, null,
            static owner => ((VideoFrame)owner).Release(), pixels);
    }

    private static SKSurface CreateSurface(GRContext? context, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul);
        SKSurface surface = (context is null ? SKSurface.Create(info) : SKSurface.Create(context, false, info))
            ?? throw new InvalidOperationException($"Could not allocate a {width}x{height} photo surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        return surface;
    }

    private static SKImage Reduce(SKImage image, int width, int height, GRContext? context)
    {
        using SKSurface? mipSurface = context is null ? null : SKSurface.Create((GRRecordingContext)context, false,
            new SKImageInfo(image.Width, image.Height, SKColorType.RgbaF16, SKAlphaType.Premul),
            0, GRSurfaceOrigin.TopLeft, null, shouldCreateWithMips: true);
        using var copyPaint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false };
        mipSurface?.Canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), copyPaint);
        using SKImage? mipmapped = mipSurface?.Snapshot();
        using SKSurface surface = CreateSurface(context, width, height);
        using SKShader shader = (mipmapped ?? image).ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
            SKMatrix.CreateScale((float)width / image.Width, (float)height / image.Height));
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src, IsAntialias = false };
        surface.Canvas.DrawRect(SKRect.Create(width, height), paint);
        return surface.Snapshot();
    }

    private Tile CreateTile(SKImage image, PhotoTileBounds bounds)
    {
        VideoFrame samples = VideoFrame.CreateHosted(image.Width, image.Height, VideoPixelFormat.Bgra32);
        samples.PhotoColor = frame.HasAlpha ? LinearAlphaColor : LinearColor;
        samples.ColorPrimaries = VideoColorPrimaries.Bt2020;
        samples.ColorTransfer = frame.ColorTransfer;
        samples.HdrPeakLuminance = frame.HdrPeakLuminance;
        samples.HdrMasteringLuminance = frame.HdrMasteringLuminance;
        samples.ColorRange = VideoColorRange.Full;
        samples.FieldOrder = VideoFieldOrder.Progressive;
        samples.GainMap = frame.GainMap?.AddRef();
        return new Tile(image, samples, bounds);
    }

    public SKImage? GetSampleImage(out SKRect? bounds)
    {
        bounds = sampleWindow is not null ? sampleBounds : null;
        return sampleWindow?.Image;
    }

    private void UpdateSampleWindow(SKRect visible, int reduction, GRContext? context)
    {
        if (overviewImage is null)
            return;
        int alignment = 32 * reduction;
        var bounds = new SKRectI(
            Math.Max(0, ((int)MathF.Floor(visible.Left) - reduction * 2) / alignment * alignment),
            Math.Max(0, ((int)MathF.Floor(visible.Top) - reduction * 2) / alignment * alignment),
            Math.Min(frame.Width, ((int)MathF.Ceiling(visible.Right) + reduction * 2 + alignment - 1) / alignment * alignment),
            Math.Min(frame.Height, ((int)MathF.Ceiling(visible.Bottom) + reduction * 2 + alignment - 1) / alignment * alignment));
        if (sampleWindow is not null && sampleBounds == bounds && sampleReduction == reduction && sampleRevision == revision)
            return;
        int width = Math.Max(1, (bounds.Width + reduction - 1) / reduction);
        int height = Math.Max(1, (bounds.Height + reduction - 1) / reduction);
        using SKSurface surface = CreateSurface(context, width, height);
        surface.Canvas.Scale((float)width / bounds.Width, (float)height / bounds.Height);
        surface.Canvas.Translate(-bounds.Left, -bounds.Top);
        DrawSamples(surface.Canvas, overviewImage);
        foreach (Tile tile in details.Values)
            DrawSamples(surface.Canvas, tile);
        sampleWindow?.Dispose();
        sampleWindow = CreateTile(surface.Snapshot(), new PhotoTileBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
        sampleBounds = bounds;
        sampleReduction = reduction;
        sampleRevision = revision;
    }

    private static void DrawSamples(SKCanvas canvas, Tile tile)
    {
        PhotoTileBounds bounds = tile.Bounds;
        using SKShader shader = tile.Image.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear),
            SKMatrix.CreateScaleTranslation((float)bounds.Width / tile.Image.Width, (float)bounds.Height / tile.Image.Height,
                bounds.X, bounds.Y));
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src, IsAntialias = false };
        canvas.DrawRect(SKRect.Create(bounds.X, bounds.Y, bounds.Width, bounds.Height), paint);
    }

    private void Draw(in VideoRenderRequest request, Tile tile)
    {
        PhotoTileBounds bounds = tile.Bounds;
        float horizontal = request.Destination.Width / frame.Width;
        float vertical = request.Destination.Height / frame.Height;
        VideoRenderRequest draw = request with
        {
            Frame = tile.Samples,
            PackedImage = tile.Image,
            PlaneSource = null,
            Destination = SKRect.Create(request.Destination.Left + bounds.X * horizontal,
                request.Destination.Top + bounds.Y * vertical, bounds.Width * horizontal, bounds.Height * vertical),
            Scaler = VideoScalers.For(VideoScalingQuality.Bilinear),
            PhotoSourceBounds = SKRect.Create(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            PhotoSourceSize = new SKSizeI(frame.Width, frame.Height),
        };
        if (!VideoFramePipeline.Render(draw, out string failure))
            throw new InvalidOperationException($"Could not display a photo region: {failure}.");
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            cancellation.Cancel();
            if (ready is not null)
                foreach (Decoded item in ready)
                    item.Frame?.Release();
            ready = null;
        }
        cancellation.Dispose();
        sampleWindow?.Dispose();
        overviewImage?.Dispose();
        overview?.Dispose();
        foreach (Tile tile in details.Values)
            tile.Dispose();
        details.Clear();
    }
}