using System;
using System.Globalization;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Interop;
using LightStudio.FfmpegShim.Media;
using LightStudio.NativeBindings.Types;
using LightStudio.Logging;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>Which device filter resizes a surface of one graphics API, and what it calls its size.</summary>
/// <remarks>
/// Only surfaces the renderer can go on to import are worth resizing on the device, so a kind is
/// listed here when its scaler's output pool is one the player can sample. Direct3D 11 is not:
/// <c>scale_d3d11</c> allocates its output with <c>D3D11_BIND_RENDER_TARGET</c> and
/// <c>D3D11_BIND_VIDEO_ENCODER</c> alone, and a texture with no shader-resource binding cannot
/// become an ANGLE image, so its frames would have to be read back -- the opposite of the point.
/// </remarks>
internal readonly partial record struct HardwareScaleFilter(
    string Name,
    string WidthOption,
    string HeightOption,
    string PackedRgbArguments,
    string PadName)
{
    public static HardwareScaleFilter For(VideoSurfaceKind kind) => kind switch
    {
        // Full range because the picture leaves as RGB: without it the conversion keeps the studio
        // subset the samples were coded in and black lands on 16 rather than 0.
        VideoSurfaceKind.Vaapi => new HardwareScaleFilter(
            "scale_vaapi", "w", "h", "format=bgra:out_range=full", "pad_vaapi"),
        _ => default,
    };

    public bool IsDefined => !string.IsNullOrEmpty(Name);

    public bool ConvertsToPackedRgb => !string.IsNullOrEmpty(PackedRgbArguments);

    public bool Pads => !string.IsNullOrEmpty(PadName);
}

/// <summary>
/// Resizes decoded surfaces to the size they will be shown at, on the device that decoded them,
/// before anything else in the player has looked at them.
/// </summary>
/// <remarks>
/// <para>
/// The picture leaves this in the same kind of surface it arrived in, so the renderer imports and
/// samples it exactly as it did before; only its size has changed. What that buys is the display
/// filter: a frame already at output size is drawn with one sample per pixel, which is a whole
/// scene buffer and a kernel of up to sixty-four taps that never run.
/// </para>
/// <para>
/// Fixed-function video processing is not the equal of those kernels. It resizes coded samples in
/// the transfer curve they were coded in, with chroma still subsampled, so this is a trade offered
/// to the user rather than one made for them.
/// </para>
/// </remarks>
internal sealed unsafe partial class HardwareVideoScaler : IHardwareVideoScaler
{
    /// <summary><c>AV_BUFFERSRC_FLAG_KEEP_REF</c>.</summary>
    private const int KeepFrameReference = 8;

    // Rebuilding the graph allocates a whole pool of surfaces at the new size, so a window being
    // dragged has to settle before the device is asked to follow it. Frames keep coming out at the
    // size it was last given until then, and the display filter takes up the difference.
    private const double RebuildCooldownSeconds = 0.4;

    private readonly long id;
    private readonly HardwareScaleFilter filter;
    private readonly AVRational timeBase;
    private readonly int surfaceBudget;

    // Borrowed from the decoder's codec context, which outlives this.
    private readonly AVBufferRef* device;

    private AVFilterGraph* graph;
    private AVFilterContext* source;
    private AVFilterContext* sink;
    private AVFrame* output;

    private int sourceWidth;
    private int sourceHeight;
    private AVPixelFormat sourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private IntPtr sourcePool;
    private int targetWidth;
    private int targetHeight;
    private bool targetPackedRgb;
    private VideoOutputRequest configured;
    private (int Width, int Height) activeSize;
    private double lastConfigured = double.NegativeInfinity;
    private bool abandoned;

    public HardwareVideoScaler(
        long id,
        VideoSurfaceKind kind,
        AVBufferRef* device,
        AVRational timeBase,
        int surfaceBudget)
    {
        this.id = id;
        this.device = device;
        this.timeBase = timeBase;
        this.surfaceBudget = surfaceBudget;
        filter = HardwareScaleFilter.For(kind);
        if (!IsAvailable)
        {
            Status = filter.IsDefined
                ? $"this build has no '{filter.Name}' filter"
                : $"no device scaler exists for {kind} surfaces";
        }
    }

    /// <summary>Whether this build has the device filter for the surfaces the decoder produces.</summary>
    public bool IsAvailable =>
        filter.IsDefined && device != null && avfilter_get_by_name(filter.Name) != null;

    /// <summary>Whether the filter can also hand the picture back already converted to RGB.</summary>
    public bool CanConvertToPackedRgb => filter.ConvertsToPackedRgb;

    /// <summary>Why the device scaler is not resizing frames, or empty while it is.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>The size frames are currently leaving at, or zero while none are being resized.</summary>
    public (int Width, int Height) ActiveSize => activeSize;

    public bool CoversOutput => activeSize != default && configured?.IsPadded == true;

    /// <summary>
    /// Puts a decoded surface at the size it is about to be drawn at, optionally letterboxed out
    /// to the whole control. False leaves the caller with the frame it already had, which is always
    /// a valid outcome: it costs the fast path, never the picture.
    /// </summary>
    /// <remarks>The frame handed back belongs to this and stays valid until the next call.</remarks>
    public bool TryScale(AVFrame* input, VideoOutputRequest request, out AVFrame* result)
    {
        result = input;
        if (abandoned || request.PictureWidth <= 0 || request.PictureHeight <= 0 ||
            input->hw_frames_ctx == null)
        {
            return false;
        }

        if (request.PackedRgb && !filter.ConvertsToPackedRgb)
            request = request with { PackedRgb = false };
        if (request.IsPadded && !filter.Pads)
            request = request with { SurfaceWidth = request.PictureWidth, SurfaceHeight = request.PictureHeight, OffsetX = 0, OffsetY = 0 };

        if (!Accepts(input))
        {
            // Nothing of the graph survives a new pool or a new size of picture, so this one is
            // rebuilt whatever the last was built for.
            if (!TryConfigure(input, request, immediate: true))
                return false;
        }
        else if (request != configured &&
                 !TryConfigure(input, request, immediate: request.PackedRgb != configured.PackedRgb))
        {
            // A resize the graph has not caught up with yet. What it does produce is nearer the
            // display than the coded picture is, so it is still worth having; the display filter
            // covers the difference until the size settles.
            request = configured;
        }

        // Resizing a picture to the size it already has would cost a surface and a pass to copy it,
        // but the answer is still yes: nothing downstream has any resizing left to do. A conversion
        // or a pad is work in its own right, so those still have to run.
        if (request.PictureWidth == input->width && request.PictureHeight == input->height &&
            !request.PackedRgb && !request.IsPadded)
        {
            activeSize = (request.PictureWidth, request.PictureHeight);
            return true;
        }

        int sent = av_buffersrc_add_frame_flags(source, input, KeepFrameReference);
        if (sent < 0)
        {
            Abandon($"av_buffersrc_add_frame_flags: {FfmpegError.Describe(sent)}");
            return false;
        }

        av_frame_unref(output);
        int received = av_buffersink_get_frame(sink, output);
        if (received < 0)
        {
            // A filter asking for more input has produced nothing wrong, only nothing yet.
            if (!FfmpegError.IsTryAgain(received))
                Abandon($"av_buffersink_get_frame: {FfmpegError.Describe(received)}");
            return false;
        }

        // The layout above is settled by the picture's own geometry, which resizing here must not
        // appear to have changed: the samples are the same picture, at a different rate.
        output->sample_aspect_ratio = input->sample_aspect_ratio;
        activeSize = (request.SurfaceWidth, request.SurfaceHeight);
        result = output;
        return true;
    }

    /// <summary>Whether the graph as built can still be fed this frame.</summary>
    private bool Accepts(AVFrame* input) =>
        graph != null &&
        input->width == sourceWidth &&
        input->height == sourceHeight &&
        (AVPixelFormat)input->format == sourceFormat &&
        (IntPtr)input->hw_frames_ctx->data == sourcePool;

    private bool TryConfigure(AVFrame* input, VideoOutputRequest request, bool immediate)
    {
        // A graph that is already running is left alone until the size it was built for has been
        // out of date long enough to look like a decision rather than a frame of an animation.
        if (!immediate && graph != null && MediaTime.Now - lastConfigured < RebuildCooldownSeconds)
            return false;

        Release();

        try
        {
            Build(input, request);
        }
        catch (FfmpegException exception)
        {
            Release();
            AppLog.Warning("FFmpeg.Scale", "Hardware video scaling failed.", exception);
            Abandon(exception.Message);
            return false;
        }

        sourceWidth = input->width;
        sourceHeight = input->height;
        sourceFormat = (AVPixelFormat)input->format;
        sourcePool = (IntPtr)input->hw_frames_ctx->data;
        targetWidth = request.PictureWidth;
        targetHeight = request.PictureHeight;
        targetPackedRgb = request.PackedRgb;
        configured = request;
        lastConfigured = MediaTime.Now;
        Status = string.Empty;
        AppLog.Information(
            "FFmpeg.Decode",
            $"#{id} {filter.Name} is resizing {sourceWidth}x{sourceHeight} to " +
            $"{request.PictureWidth}x{request.PictureHeight} on the device" +
            (request.IsPadded
                ? $", padded to {request.SurfaceWidth}x{request.SurfaceHeight} at " +
                  $"{request.OffsetX},{request.OffsetY}"
                : string.Empty) +
            (request.PackedRgb ? ", converting to RGB." : "."));
        return true;
    }

    private void Build(AVFrame* input, VideoOutputRequest request)
    {
        graph = avfilter_graph_alloc();
        if (graph == null)
            throw new FfmpegException("avfilter_graph_alloc returned null", 0);

        output = av_frame_alloc();
        if (output == null)
            throw new FfmpegException("av_frame_alloc returned null", 0);

        source = Allocate("buffer", "in");

        // The pool has to reach the source before it opens: a buffer told to expect a hardware
        // format with no frames context yet refuses to initialize at all.
        AVBufferSrcParameters* parameters = av_buffersrc_parameters_alloc();
        if (parameters == null)
            throw new FfmpegException("av_buffersrc_parameters_alloc returned null", 0);

        parameters->hw_frames_ctx = input->hw_frames_ctx;
        int applied = av_buffersrc_parameters_set(source, parameters);
        av_free(parameters);
        FfmpegError.ThrowIfError(applied, "av_buffersrc_parameters_set");

        AVRational aspect = input->sample_aspect_ratio.num > 0 && input->sample_aspect_ratio.den > 0
            ? input->sample_aspect_ratio
            : new AVRational { num = 1, den = 1 };

        Initialize(
            source,
            "buffer",
            string.Create(
                CultureInfo.InvariantCulture,
                $"video_size={input->width}x{input->height}:pix_fmt={input->format}:" +
                $"time_base={timeBase.num}/{timeBase.den}:" +
                $"pixel_aspect={aspect.num}/{aspect.den}:" +
                $"colorspace={(int)input->colorspace}:range={(int)input->color_range}"));

        AVFilterContext* scaler = Allocate(filter.Name, "scale");

        // A filter that allocates on the device has to be told which one before it opens, which is
        // why the chain is built a filter at a time rather than parsed whole.
        scaler->hw_device_ctx = av_buffer_ref(device);

        // The filter's pool is static like the decoder's, so it has to cover every resized surface
        // the pipeline can be holding at once or a later frame finds nothing to be scaled into.
        scaler->extra_hw_frames = surfaceBudget;
        // With no format argument scale_vaapi inherits the input pool's software layout, which is
        // what keeps P010 and the other wide planar formats at their original precision.
        string conversion = request.PackedRgb ? ":" + filter.PackedRgbArguments : string.Empty;
        Initialize(
            scaler,
            filter.Name,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{filter.WidthOption}={request.PictureWidth}:" +
                $"{filter.HeightOption}={request.PictureHeight}{conversion}"));

        AVFilterContext* last = scaler;
        if (request.IsPadded)
        {
            // Letterboxing on the device is what lets the picture leave as one surface covering the
            // whole control, rather than something the renderer has to place on a cleared background.
            AVFilterContext* pad = Allocate(filter.PadName, "pad");
            pad->hw_device_ctx = av_buffer_ref(device);
            pad->extra_hw_frames = surfaceBudget;
            Initialize(
                pad,
                filter.PadName,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"w={request.SurfaceWidth}:h={request.SurfaceHeight}:" +
                    $"x={request.OffsetX}:y={request.OffsetY}:color=black"));
            FfmpegError.ThrowIfError(avfilter_link(scaler, 0, pad, 0), "avfilter_link(pad)");
            last = pad;
        }

        sink = Allocate("buffersink", "out");
        Initialize(sink, "buffersink", null);

        FfmpegError.ThrowIfError(avfilter_link(source, 0, scaler, 0), "avfilter_link(scale)");
        FfmpegError.ThrowIfError(avfilter_link(last, 0, sink, 0), "avfilter_link(buffersink)");
        FfmpegError.ThrowIfError(avfilter_graph_config(graph, null), "avfilter_graph_config");
    }

    private AVFilterContext* Allocate(string name, string label)
    {
        AVFilter* definition = avfilter_get_by_name(name);
        if (definition == null)
            throw new FfmpegException($"This build has no '{name}' filter.", 0);

        AVFilterContext* context = avfilter_graph_alloc_filter(graph, definition, label);
        if (context == null)
            throw new FfmpegException($"avfilter_graph_alloc_filter({name}) returned null", 0);

        return context;
    }

    private static void Initialize(AVFilterContext* context, string name, string arguments) =>
        FfmpegError.ThrowIfError(
            avfilter_init_str(context, arguments),
            $"avfilter_init_str({name}, '{arguments}')");

    /// <summary>
    /// Gives up on device scaling for the rest of the stream. Whatever the driver refused it will
    /// refuse again, and retrying it every frame would cost more than the filter ever saved.
    /// </summary>
    private void Abandon(string reason)
    {
        abandoned = true;
        Status = reason;
        Release();
        AppLog.Warning("FFmpeg.Decode", $"#{id} device scaling was withdrawn: {reason}.");
    }

    private void Release()
    {
        if (graph != null)
        {
            AVFilterGraph* handle = graph;
            avfilter_graph_free(&handle);
            graph = null;
        }

        if (output != null)
        {
            AVFrame* handle = output;
            av_frame_free(&handle);
            output = null;
        }

        source = null;
        sink = null;
        activeSize = default;
        targetPackedRgb = false;
        sourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        sourcePool = IntPtr.Zero;
        sourceWidth = 0;
        sourceHeight = 0;
        targetWidth = 0;
        targetHeight = 0;
    }

    public void Dispose() => Release();
}
