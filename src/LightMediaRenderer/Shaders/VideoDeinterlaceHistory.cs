using System;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Rendering;
using SkiaSharp;

namespace LightMediaRenderer.Shaders;

public sealed class VideoDeinterlaceHistory(Action<Action>? disposeOnRenderThread = null)
{
    private int references = 1;
    private long sequence = -1;
    private double pts;
    private double duration;
    private GRContext? context;
    private SourceKey key;

    public SKImage? Samples { get; private set; }

    public SKImage? PreviousSamples { get; private set; }

    public VideoDeinterlaceHistory AddRef()
    {
        Interlocked.Increment(ref references);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref references) != 0)
            return;

        if (disposeOnRenderThread is { } dispose)
            dispose(Clear);
        else
            Clear();
    }

    public void Prepare(in VideoRenderRequest request)
    {
        VideoFrame frame = request.Frame;
        var nextKey = new SourceKey(
            frame.Width, frame.Height, request.Layout, frame.FieldOrder,
            frame.ColorRange, frame.ChromaLocation, frame.ExternalTextureColorModel,
            request.PackedImage is not null, request.Scaler.Quality == VideoScalingQuality.NearestNeighbour);
        bool compatible = nextKey == key && ReferenceEquals(context, request.GrContext);
        if (compatible && sequence == request.FrameSequence)
            return;

        // Timestamps tell a seek from the next frame; a stream without them falls back to the
        // host's sequence, whose next number is the next frame.
        double elapsed = frame.Pts - pts;
        bool adjacent = elapsed > 0
            ? elapsed <= Math.Max(0.1, duration * 1.5)
            : elapsed == 0 && request.FrameSequence == sequence + 1;
        bool consecutive = compatible && request.FrameSequence > sequence && adjacent;
        PreviousSamples?.Dispose();
        PreviousSamples = consecutive ? Samples : null;
        if (!consecutive)
            Samples?.Dispose();
        Samples = null;
        key = nextKey;
        context = request.GrContext;
        sequence = request.FrameSequence;
        pts = frame.Pts;
        duration = frame.Duration;
    }

    public void SetSamples(SKImage? samples) => Samples = samples;

    private void Clear()
    {
        Samples?.Dispose();
        PreviousSamples?.Dispose();
        Samples = null;
        PreviousSamples = null;
        context = null;
    }

    private readonly record struct SourceKey(
        int Width,
        int Height,
        VideoPlanarLayout Layout,
        VideoFieldOrder FieldOrder,
        VideoColorRange Range,
        VideoChromaLocation ChromaLocation,
        VideoExternalTextureColorModel ColorModel,
        bool Packed,
        bool Nearest);
}