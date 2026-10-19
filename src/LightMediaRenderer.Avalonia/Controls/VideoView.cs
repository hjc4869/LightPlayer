using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Rendering.Composition;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using LightMediaRenderer.Contracts;
using LightMediaRenderer.Shaders;
using LightMediaRenderer.Subtitles;
using LightStudio.FfmpegShim.Core;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Media;
using LightStudio.FfmpegShim.Subtitles;
using LightMediaRenderer.Avalonia.Pipeline;
using LightMediaRenderer.Avalonia.Rendering;
using LightMediaRenderer.Avalonia.Shaders;
using LightMediaRenderer.Avalonia.Threading;
using LightStudio.Logging;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Controls;

/// <summary>
/// Displays the video output of a <see cref="MediaPlayer"/>.
/// </summary>
/// <remarks>
/// The control is the pipeline's <see cref="IVideoSink"/>. Frames arrive on the presentation thread
/// and are only swapped into a field there; the UI thread is asked for nothing more than an
/// invalidation, and the actual pixel work happens on the render thread inside
/// <see cref="VideoDrawOperation"/>. Invalidations are coalesced, so a decoder that briefly runs
/// ahead of the compositor cannot flood the dispatcher.
/// </remarks>
public class VideoView : Control, IVideoSink, IVideoDrawFeedback
{
    public static readonly StyledProperty<MediaPlayer?> PlayerProperty =
        AvaloniaProperty.Register<VideoView, MediaPlayer?>(nameof(Player));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<VideoView, Stretch>(nameof(Stretch), Stretch.Uniform);

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<VideoView>();

    public static readonly StyledProperty<AssDocument?> SubtitlesProperty =
        AvaloniaProperty.Register<VideoView, AssDocument?>(nameof(Subtitles));

    public static readonly StyledProperty<BitmapSubtitleDocument?> BitmapSubtitlesProperty =
        AvaloniaProperty.Register<VideoView, BitmapSubtitleDocument?>(nameof(BitmapSubtitles));

    public static readonly StyledProperty<VideoScalingQuality> ScalingQualityProperty =
        AvaloniaProperty.Register<VideoView, VideoScalingQuality>(nameof(ScalingQuality), VideoScalingQuality.Bilinear);

    public static readonly StyledProperty<VideoScalingQuality> DownscalingQualityProperty =
        AvaloniaProperty.Register<VideoView, VideoScalingQuality>(
            nameof(DownscalingQuality),
            VideoScalingQuality.Bilinear);

    public static readonly StyledProperty<VideoDebandingQuality> DebandingQualityProperty =
        AvaloniaProperty.Register<VideoView, VideoDebandingQuality>(nameof(DebandingQuality));

    public static readonly StyledProperty<bool> PreferHardwareProcessingProperty =
        AvaloniaProperty.Register<VideoView, bool>(nameof(PreferHardwareProcessing));

    public static readonly StyledProperty<string?> ShaderPackageOverrideProperty =
        AvaloniaProperty.Register<VideoView, string?>(nameof(ShaderPackageOverride));

    public static readonly StyledProperty<bool> EnableHdrPassthroughProperty =
        AvaloniaProperty.Register<VideoView, bool>(nameof(EnableHdrPassthrough), true);

    public static readonly StyledProperty<int> DisplayPanelBitDepthProperty =
        AvaloniaProperty.Register<VideoView, int>(nameof(DisplayPanelBitDepth));

    public static readonly StyledProperty<SubtitleBrightnessMode> SubtitleBrightnessModeProperty =
        AvaloniaProperty.Register<VideoView, SubtitleBrightnessMode>(
            nameof(SubtitleBrightnessMode),
            SubtitleBrightnessMode.FollowVideo);

    public static readonly DirectProperty<VideoView, bool> IsExtendedRangeOutputProperty =
        AvaloniaProperty.RegisterDirect<VideoView, bool>(nameof(IsExtendedRangeOutput), view => view.IsExtendedRangeOutput);

    public static readonly StyledProperty<AudioTrack?> SelectedAudioTrackProperty =
        AvaloniaProperty.Register<VideoView, AudioTrack?>(
            nameof(SelectedAudioTrack),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<MediaChapter?> SelectedChapterProperty =
        AvaloniaProperty.Register<VideoView, MediaChapter?>(
            nameof(SelectedChapter),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<SubtitleTrack?> SelectedSubtitleTrackProperty =
        AvaloniaProperty.Register<VideoView, SubtitleTrack?>(
            nameof(SelectedSubtitleTrack),
            defaultBindingMode: BindingMode.TwoWay);

    private readonly object frameSync = new();
    private readonly ObservableCollection<AudioTrack> audioTracks = new();
    private readonly ObservableCollection<SubtitleTrack> subtitleTracks = new();
    private readonly ObservableCollection<MediaChapter> chapters = new();
    private readonly VideoOutputLuminanceState outputLuminance = new();
    private readonly HdrContentHint hdrContentHint;
    private readonly AsyncSignal drawn = new();
    private VideoFrame? currentFrame;
    private VideoDeinterlaceHistory? deinterlaceHistory;
    private long frameSequence;
    private long drawnSequence;
    private TimeSpan currentFrameTime;
    private AssSubtitleRenderer? subtitleRenderer;
    private BitmapSubtitleRenderer? bitmapSubtitleRenderer;
    private VideoRenderCaptureRequest? captureRequest;
    private DispatcherTimer? captureRedrawTimer;
    private int invalidationPending;
    private int reportedExtendedRange = -1;
    private bool isExtendedRangeOutput;
    private bool syncingAudioTrack;
    private bool syncingSubtitleTrack;
    private bool syncingChapter;

    static VideoView()
    {
        AffectsRender<VideoView>(
            StretchProperty,
            BackgroundProperty,
            SubtitlesProperty,
            BitmapSubtitlesProperty,
            ScalingQualityProperty,
            DownscalingQualityProperty,
            DebandingQualityProperty,
            PreferHardwareProcessingProperty,
            ShaderPackageOverrideProperty,
            EnableHdrPassthroughProperty,
            DisplayPanelBitDepthProperty,
            SubtitleBrightnessModeProperty);
        PlayerProperty.Changed.AddClassHandler<VideoView>((view, e) => view.OnPlayerChanged(e));
        SelectedAudioTrackProperty.Changed.AddClassHandler<VideoView>((view, e) => view.OnSelectedAudioTrackChanged(e));
        SelectedSubtitleTrackProperty.Changed.AddClassHandler<VideoView>((view, e) => view.OnSelectedSubtitleTrackChanged(e));
        SelectedChapterProperty.Changed.AddClassHandler<VideoView>((view, e) => view.OnSelectedChapterChanged(e));
    }

    public VideoView()
    {
        hdrContentHint = new HdrContentHint(this);
    }

    /// <summary>Player whose video is shown. Setting it wires this control up as the player's sink.</summary>
    public MediaPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <summary>Painted behind and around the picture (the letterbox bars).</summary>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>ASS script drawn over the picture, or <c>null</c> for no subtitles.</summary>
    /// <remarks>
    /// Events are picked by the timestamp of the frame on screen, so they stay in step with the
    /// picture rather than with the wall clock. Selecting a text <see cref="SubtitleTrack"/> writes
    /// its script here once it has been read; a bitmap track writes <see cref="BitmapSubtitles"/>.
    /// Assign this directly only for a script the player knows nothing about.
    /// </remarks>
    public AssDocument? Subtitles
    {
        get => GetValue(SubtitlesProperty);
        set => SetValue(SubtitlesProperty, value);
    }

    /// <summary>Timed bitmap subtitle compositions drawn over the picture, or <c>null</c>.</summary>
    public BitmapSubtitleDocument? BitmapSubtitles
    {
        get => GetValue(BitmapSubtitlesProperty);
        set => SetValue(BitmapSubtitlesProperty, value);
    }

    /// <summary>Filter used when the picture is enlarged or rendered at source size.</summary>
    public VideoScalingQuality ScalingQuality
    {
        get => GetValue(ScalingQualityProperty);
        set => SetValue(ScalingQualityProperty, value);
    }

    /// <summary>Filter used when the picture is reduced in physical output pixels.</summary>
    public VideoScalingQuality DownscalingQuality
    {
        get => GetValue(DownscalingQualityProperty);
        set => SetValue(DownscalingQualityProperty, value);
    }

    /// <summary>Strength of source-gradient reconstruction before scaling.</summary>
    public VideoDebandingQuality DebandingQuality
    {
        get => GetValue(DebandingQualityProperty);
        set => SetValue(DebandingQualityProperty, value);
    }

    /// <summary>
    /// Whether the decoder's own device may resize the picture to output size, leaving the display
    /// filter one sample per pixel. Only honored while nothing else in the pipeline needs the
    /// picture at its coded size.
    /// </summary>
    public bool PreferHardwareProcessing
    {
        get => GetValue(PreferHardwareProcessingProperty);
        set => SetValue(PreferHardwareProcessingProperty, value);
    }

    /// <summary>Null for automatic package priority, <c>$off</c> for none, or an installed package id.</summary>
    public string? ShaderPackageOverride
    {
        get => GetValue(ShaderPackageOverrideProperty);
        set => SetValue(ShaderPackageOverrideProperty, value);
    }

    /// <summary>
    /// Whether video may use the platform's wide-gamut, extended-range surface. False uses the
    /// built-in tone mapping and dithering path for a standard 8-bit sRGB surface.
    /// </summary>
    public bool EnableHdrPassthrough
    {
        get => GetValue(EnableHdrPassthroughProperty);
        set => SetValue(EnableHdrPassthroughProperty, value);
    }

    /// <summary>
    /// Bit depth the display panel resolves, or 0 to follow the output surface. Where it is coarser
    /// than what the picture carries, the picture is dithered towards it instead of being left to
    /// band on whatever quantizes it out of the process's sight.
    /// </summary>
    public int DisplayPanelBitDepth
    {
        get => GetValue(DisplayPanelBitDepthProperty);
        set => SetValue(DisplayPanelBitDepthProperty, value);
    }

    /// <summary>How subtitles are composited on an extended-linear output.</summary>
    public SubtitleBrightnessMode SubtitleBrightnessMode
    {
        get => GetValue(SubtitleBrightnessModeProperty);
        set => SetValue(SubtitleBrightnessModeProperty, value);
    }

    /// <summary>Whether the most recent draw used an extended-linear output surface.</summary>
    public bool IsExtendedRangeOutput
    {
        get => isExtendedRangeOutput;
        private set => SetAndRaise(IsExtendedRangeOutputProperty, ref isExtendedRangeOutput, value);
    }

    /// <summary>
    /// Sound tracks the player can switch to: the media's own plus any added with
    /// <see cref="AddExternalAudioAsync"/>. Bind a track picker to this.
    /// </summary>
    /// <remarks>
    /// The same collection throughout the control's life, edited in place. An items control bound
    /// to it therefore keeps its selection when a track is added, instead of dropping it and
    /// writing the stale value back through a two-way binding.
    /// </remarks>
    public IReadOnlyList<AudioTrack> AudioTracks => audioTracks;

    /// <summary>
    /// The track being heard. Assigning one switches playback over to it; the property follows the
    /// player, so it also reports switches made elsewhere.
    /// </summary>
    public AudioTrack? SelectedAudioTrack
    {
        get => GetValue(SelectedAudioTrackProperty);
        set => SetValue(SelectedAudioTrackProperty, value);
    }

    /// <summary>
    /// Sections of the open media, in order, or empty when the container declares none. Bind a
    /// chapter list to this.
    /// </summary>
    /// <remarks>The same collection throughout the control's life, edited in place, for the same
    /// reason as <see cref="AudioTracks"/>.</remarks>
    public IReadOnlyList<MediaChapter> Chapters => chapters;

    /// <summary>
    /// The chapter being played. Assigning one jumps to its start; the property otherwise follows
    /// the picture, so a list bound to it marks the section on screen rather than the one last
    /// clicked.
    /// </summary>
    public MediaChapter? SelectedChapter
    {
        get => GetValue(SelectedChapterProperty);
        set => SetValue(SelectedChapterProperty, value);
    }

    /// <summary>
    /// Adds every sound track of <paramref name="path"/> to <see cref="AudioTracks"/> without
    /// selecting any of them. The file is probed off the UI thread.
    /// </summary>
    public async Task<IReadOnlyList<AudioTrack>> AddExternalAudioAsync(string path)
    {
        MediaPlayer player = Player ?? throw new InvalidOperationException("No player is attached to the view.");
        IReadOnlyList<AudioTrack> added = await player.AddExternalAudioAsync(path).ConfigureAwait(true);

        // So the caller sees the new entries in AudioTracks the moment this returns, whichever
        // thread it happens to be resumed on.
        await Dispatcher.UIThread.InvokeAsync(RefreshAudioTracks);
        return added;
    }

    /// <summary>
    /// Subtitles that can be shown: the media's own text and bitmap streams plus any added with
    /// <see cref="AddExternalSubtitleAsync"/>. Bind a subtitle picker to this.
    /// </summary>
    /// <remarks>The same collection throughout the control's life, edited in place, for the same
    /// reason as <see cref="AudioTracks"/>.</remarks>
    public IReadOnlyList<SubtitleTrack> SubtitleTracks => subtitleTracks;

    /// <summary>
    /// The subtitle being shown, or <c>null</c> for none. Assigning one puts it on screen, which
    /// for a track that has not been read yet happens once its stream has been gone over.
    /// </summary>
    public SubtitleTrack? SelectedSubtitleTrack
    {
        get => GetValue(SelectedSubtitleTrackProperty);
        set => SetValue(SelectedSubtitleTrackProperty, value);
    }

    /// <summary>
    /// Adds the subtitles of <paramref name="path"/> to <see cref="SubtitleTracks"/> without
    /// selecting any of them: the script itself when the file is one, otherwise every supported
    /// subtitle stream of the container. The file is probed off the UI thread.
    /// </summary>
    public async Task<IReadOnlyList<SubtitleTrack>> AddExternalSubtitleAsync(string path)
    {
        MediaPlayer player = Player ?? throw new InvalidOperationException("No player is attached to the view.");
        IReadOnlyList<SubtitleTrack> added = await player.AddExternalSubtitleAsync(path).ConfigureAwait(true);

        await Dispatcher.UIThread.InvokeAsync(RefreshSubtitleTracks);
        return added;
    }

    /// <summary>Native size of the frame currently displayed, corrected for pixel aspect ratio.</summary>
    public Size VideoSize
    {
        get
        {
            lock (frameSync)
            {
                if (currentFrame is null || currentFrame.Width <= 0)
                    return default;
                return NaturalSize(currentFrame);
            }
        }
    }

    /// <summary>Size the picture is meant to be seen at, with pixel aspect and rotation applied.</summary>
    private static Size NaturalSize(VideoFrame frame)
    {
        // The decoded size, so that a picture the decoder's device already resized still lays out
        // where the picture itself says, rather than where the last output size put it.
        int coded = frame.SourceWidth > 0 ? frame.SourceWidth : frame.Width;
        int rows = frame.SourceHeight > 0 ? frame.SourceHeight : frame.Height;
        double width = coded * (frame.PixelAspectRatio > 0 ? frame.PixelAspectRatio : 1.0);
        return frame.Rotation is 90 or 270 ? new Size(rows, width) : new Size(width, rows);
    }

    /// <summary>Captures the next completed video draw as BGRA pixels in physical output size.</summary>
    /// <remarks>
    /// The read happens on the render thread after the production shader and resampler have drawn,
    /// so it includes hardware-surface imports and the negotiated output color path. Letterbox
    /// bars and subtitles are intentionally excluded.
    /// </remarks>
    public Task<VideoRenderCapture> CaptureRenderedFrameAsync(CancellationToken cancellationToken = default)
    {
        lock (frameSync)
        {
            if (captureRequest is null || captureRequest.Task.IsCompleted)
            {
                captureRequest = new VideoRenderCaptureRequest(cancellationToken);
                captureRedrawTimer ??= CreateCaptureRedrawTimer();
                captureRedrawTimer.Start();
            }

            AppLog.Debug("Video.Capture", $"Requested rendered frame; bounds={Bounds}; attached={VisualRoot is not null}.");
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
            return captureRequest.Task;
        }
    }

    private DispatcherTimer CreateCaptureRedrawTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            VideoRenderCaptureRequest? request;
            lock (frameSync)
                request = captureRequest;

            if (request is null || request.Task.IsCompleted)
            {
                timer.Stop();
                return;
            }

            InvalidateVisual();
        };
        return timer;
    }

    void IVideoSink.Present(VideoFrame frame)
    {
        if (!VideoFrameCapabilities.IsExternalTextureCurrent(frame))
            return;

        VideoFrame? previous;
        VideoDeinterlaceHistory? previousHistory = null;
        lock (frameSync)
        {
            previous = currentFrame;
            currentFrame = frame.AddRef();
            currentFrameTime = TimeSpan.FromSeconds(frame.Pts);
            frameSequence++;
            if (!frame.FieldOrder.IsInterlaced())
            {
                previousHistory = deinterlaceHistory;
                deinterlaceHistory = null;
            }
        }

        previous?.Release();
        previousHistory?.Release();
        RequestRedraw();
    }

    void IVideoSink.ClearVideo()
    {
        VideoFrame? previous;
        VideoDeinterlaceHistory? previousHistory;
        lock (frameSync)
        {
            previous = currentFrame;
            currentFrame = null;
            currentFrameTime = default;
            previousHistory = deinterlaceHistory;
            deinterlaceHistory = null;
        }

        drawn.Pulse();
        previous?.Release();
        previousHistory?.Release();
        RequestRedraw();
    }

    bool IVideoDrawFeedback.IsDrawPending
    {
        get
        {
            lock (frameSync)
                return currentFrame is not null && drawnSequence != frameSequence;
        }
    }

    Task IVideoDrawFeedback.WaitForDrawAsync(CancellationToken cancellationToken)
    {
        int version = drawn.Version;
        return ((IVideoDrawFeedback)this).IsDrawPending ? drawn.WaitAsync(version, cancellationToken) : Task.CompletedTask;
    }

    public sealed override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);

        IBrush? background = Background;
        if (background is not null)
            context.FillRectangle(background, bounds);

        VideoFrame? frame;
        VideoDeinterlaceHistory? history;
        long sequence;
        TimeSpan time;
        VideoRenderCaptureRequest? capture;
        lock (frameSync)
        {
            frame = currentFrame?.AddRef();
            history = frame is not null && frame.FieldOrder.IsInterlaced()
                ? (deinterlaceHistory ??= new VideoDeinterlaceHistory(RenderThreadScheduler.DisposeOnRenderThread)).AddRef()
                : null;
            sequence = frameSequence;
            drawnSequence = sequence;
            time = currentFrameTime;
            capture = captureRequest is { Task.IsCompleted: false } pending ? pending : null;
        }

        drawn.Pulse();

        bool hasHdrContent = EnableHdrPassthrough && frame is { Width: > 0, Height: > 0 } &&
            bounds.Width > 0 && bounds.Height > 0 && HdrContentHint.IsHdr(frame);
        hdrContentHint.Update(hasHdrContent, hasHdrContent ? HdrContentHint.Describe(frame!) : null);

        // Without a picture the subtitles still have to land somewhere, so they use the whole control.
        Rect destination = bounds;
        SKSize storage = default;

        if (frame is not null)
        {
            if (frame.Width > 0 && frame.Height > 0 && bounds.Width > 0 && bounds.Height > 0)
            {
                destination = ComputeDestination(frame, bounds);
                double renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
                double outputWidth = destination.Width * renderScaling;
                double outputHeight = destination.Height * renderScaling;
                OfferDeviceScaling(frame, bounds, destination, renderScaling);

                // A picture carrying its own letterbox already covers the control, so it is drawn
                // over the whole of it rather than placed inside it.
                Rect drawn = frame.CoversOutput ? bounds : destination;

                // A picture the device already fitted to the output is sampled one to one, and a
                // kernel weighing its neighbors would only soften what it was handed.
                VideoScalingQuality quality = IsAtOutputSize(frame, drawn, renderScaling)
                    ? VideoScalingQuality.Bilinear
                    : VideoScalers.SelectQuality(
                        ScalingQuality,
                        DownscalingQuality,
                        frame.Width,
                        frame.Height,
                        outputWidth,
                        outputHeight);

                // VSFilter measured \frx, \fry and \blur in video pixels, not script pixels.
                Size natural = NaturalSize(frame);
                storage = new SKSize((float)natural.Width, (float)natural.Height);

                // The operation takes over the reference and releases it once the render thread is done.
                context.Custom(new VideoDrawOperation(
                    frame,
                    bounds,
                    drawn,
                    DebandingQuality,
                    quality,
                    ScalingQuality,
                    DownscalingQuality,
                    ShaderPackageOverride,
                    EnableHdrPassthrough,
                    DisplayPanelBitDepth,
                    capture,
                    outputChanged: ReportOutput,
                    deinterlaceHistory: history,
                    frameSequence: sequence,
                    compositor: ElementComposition.GetElementVisual(this)?.Compositor));
            }
            else
            {
                frame.Release();
                history?.Release();
            }
        }
        else if (bounds.Width > 0 && bounds.Height > 0)
        {
            // An empty view is the only chance to tell the decoders what this backend can draw
            // before they open, which is when they have to decide how to decode.
            context.Custom(new VideoSurfaceProbeOperation(
                bounds,
                ScalingQuality,
                EnableHdrPassthrough,
                ReportOutput));
        }

        if (Subtitles is { } subtitles && bounds.Width > 0 && bounds.Height > 0)
        {
            subtitleRenderer ??= new AssSubtitleRenderer();
            context.Custom(new SubtitleDrawOperation(
                subtitleRenderer,
                subtitles,
                time,
                bounds,
                destination,
                storage,
                SubtitleBrightnessMode,
                outputLuminance));
        }
        else if (BitmapSubtitles is { } bitmapSubtitles && bounds.Width > 0 && bounds.Height > 0)
        {
            bitmapSubtitleRenderer ??= new BitmapSubtitleRenderer();
            context.Custom(new BitmapSubtitleDrawOperation(
                bitmapSubtitleRenderer,
                bitmapSubtitles,
                time,
                bounds,
                destination,
                storage,
                SubtitleBrightnessMode,
                outputLuminance));
            }
    }

    private void ReportOutput(bool extended, double peakScale)
    {
        outputLuminance.PeakScale = peakScale;
        int value = extended ? 1 : 0;
        if (Interlocked.Exchange(ref reportedExtendedRange, value) == value)
            return;

        Dispatcher.UIThread.Post(() => IsExtendedRangeOutput = extended);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size video = VideoSize;
        if (video.Width <= 0 || video.Height <= 0)
            return default;

        bool unboundedWidth = double.IsInfinity(availableSize.Width);
        bool unboundedHeight = double.IsInfinity(availableSize.Height);
        if (unboundedWidth && unboundedHeight)
            return video;

        if (Stretch == Stretch.None)
            return video;

        double scale = Math.Min(
            unboundedWidth ? double.PositiveInfinity : availableSize.Width / video.Width,
            unboundedHeight ? double.PositiveInfinity : availableSize.Height / video.Height);
        return double.IsInfinity(scale) ? video : new Size(video.Width * scale, video.Height * scale);
    }

    /// <summary>
    /// Tells the player what size the picture is about to be drawn at, so its decoder may resize on
    /// the device it decoded with, or withdraws the offer where something still needs the coded
    /// picture.
    /// </summary>
    private void OfferDeviceScaling(VideoFrame frame, Rect bounds, Rect destination, double renderScaling)
    {
        if (Player is not { } player)
            return;

        if (!PreferHardwareProcessing || !frame.HasSurface || !CanScaleOnDevice(frame))
        {
            player.RequestVideoOutput(null);
            return;
        }

        (int width, int height) = CodedOutputSize(frame, destination.Width * renderScaling, destination.Height * renderScaling);
        bool packedRgb = CanConvertOnDevice(frame, VideoOutputDiagnostics.Current);

        // Letterboxing on the device makes the picture cover the control, which is what would let it
        // be handed to the compositor whole. Only the unrotated, contained case can be expressed as
        // a pad, so anything else asks for the picture alone.
        if (packedRgb && CanPadOnDevice(frame, bounds, destination))
        {
            int surfaceWidth = (int)Math.Round(bounds.Width * renderScaling) & ~1;
            int surfaceHeight = (int)Math.Round(bounds.Height * renderScaling) & ~1;
            int offsetX = (int)Math.Round((destination.X - bounds.X) * renderScaling) & ~1;
            int offsetY = (int)Math.Round((destination.Y - bounds.Y) * renderScaling) & ~1;
            if (offsetX + width <= surfaceWidth && offsetY + height <= surfaceHeight)
            {
                player.RequestVideoOutput(new VideoOutputRequest(
                    width, height, surfaceWidth, surfaceHeight, offsetX, offsetY, packedRgb));
                return;
            }
        }

        player.RequestVideoOutput(VideoOutputRequest.ForPicture(width, height, packedRgb));
    }

    /// <summary>
    /// Whether the picture's place in the control can be expressed as a pad around it: upright, and
    /// wholly inside the control rather than cropped by it.
    /// </summary>
    private static bool CanPadOnDevice(VideoFrame frame, Rect bounds, Rect destination) =>
        frame.Rotation == 0 &&
        !frame.MirrorHorizontally &&
        destination.X >= bounds.X - 0.5 &&
        destination.Y >= bounds.Y - 0.5 &&
        destination.Right <= bounds.Right + 0.5 &&
        destination.Bottom <= bounds.Bottom + 0.5;

    /// <summary>
    /// Whether the device's own converter can be trusted with this picture's color, which is the
    /// difference between a frame the shader has to interpret and one Skia can simply draw.
    /// </summary>
    /// <remarks>
    /// Fixed-function conversion knows one thing: the matrix. A picture that says anything about
    /// its own light -- a transfer curve to undo, primaries to move, a peak to map -- needs the
    /// color pipeline, and handing it over converted would lose all of that. The converter's
    /// packed output is eight-bit, so a wider source stays planar rather than losing precision.
    /// An extended surface stores light rather than signal, so it needs the pipeline whatever the
    /// picture says.
    /// </remarks>
    internal static bool CanConvertOnDevice(VideoFrame frame, VideoOutputStatus output)
    {
        return frame.Layout.BitDepth <= 8 &&
               !VideoFramePipeline.NeedsColorProcessing(frame) &&
               output.SurfaceObserved &&
               !output.Target.IsExtendedRange();
    }

    /// <summary>
    /// The output size expressed in the frame's own layout: frames are laid out exactly as coded,
    /// so a picture that will be turned a quarter turn is produced with its sides the other way
    /// round. Sides are even because chroma is subsampled and half of an odd side is no plane.
    /// </summary>
    private static (int Width, int Height) CodedOutputSize(
        VideoFrame frame,
        double outputWidth,
        double outputHeight)
    {
        bool turned = frame.Rotation is 90 or 270;
        return (
            (int)Math.Round(turned ? outputHeight : outputWidth) & ~1,
            (int)Math.Round(turned ? outputWidth : outputHeight) & ~1);
    }

    /// <summary>
    /// Whether resizing before the shader would still leave the same picture. Every stage listed
    /// here either reads the coded samples where they lie or writes its own picture from them, so
    /// handing it one already resized would change what it sees rather than save it work.
    /// </summary>
    private bool CanScaleOnDevice(VideoFrame frame) =>
        !VideoDebanding.Applies(DebandingQuality, frame.Layout) &&
        !VideoFramePipeline.NeedsDeinterlacing(frame) &&
        !VideoScaleStageConfiguration.HasStage(ShaderPackageOverride);

    /// <summary>Whether the frame arrived already fitted to the pixels it is about to fill.</summary>
    private static bool IsAtOutputSize(VideoFrame frame, Rect drawn, double renderScaling)
    {
        if (!frame.IsHardwareScaled)
            return false;

        (int width, int height) = CodedOutputSize(
            frame, drawn.Width * renderScaling, drawn.Height * renderScaling);
        return frame.Width == width && frame.Height == height;
    }

    private Rect ComputeDestination(VideoFrame frame, Rect bounds)
    {
        Size natural = NaturalSize(frame);
        double width = natural.Width;
        double height = natural.Height;

        double scaleX;
        double scaleY;
        switch (Stretch)
        {
            case Stretch.Fill:
                scaleX = bounds.Width / width;
                scaleY = bounds.Height / height;
                break;

            case Stretch.UniformToFill:
                scaleX = scaleY = Math.Max(bounds.Width / width, bounds.Height / height);
                break;

            case Stretch.None:
                scaleX = scaleY = 1;
                break;

            default:
                scaleX = scaleY = Math.Min(bounds.Width / width, bounds.Height / height);
                break;
        }

        double targetWidth = width * scaleX;
        double targetHeight = height * scaleY;
        return new Rect(
            bounds.X + (bounds.Width - targetWidth) / 2,
            bounds.Y + (bounds.Height - targetHeight) / 2,
            targetWidth,
            targetHeight);
    }

    private void RequestRedraw()
    {
        // Coalesce: the presentation thread can produce several frames before the compositor picks
        // one up, and posting an invalidation for each would only grow the dispatcher queue.
        if (Interlocked.Exchange(ref invalidationPending, 1) == 1)
            return;

        Dispatcher.UIThread.Post(
            () =>
            {
                Interlocked.Exchange(ref invalidationPending, 0);
                SyncSelectedChapter();
                InvalidateVisual();
            },
            DispatcherPriority.Render);
    }

    /// <summary>
    /// Takes the picture back after another view showed it, which is how it returns from a detached
    /// window: the pipeline pushes frames at one sink, and the last view to ask for it wins.
    /// </summary>
    public void ClaimVideo()
    {
        PrepareRenderTarget();

        if (Player is { } player && !ReferenceEquals(player.VideoSink, this))
            player.VideoSink = this;
    }

    /// <summary>Primes this view's graphics context before it becomes the player's sink.</summary>
    public void PrepareRenderTarget()
    {
        RenderThreadScheduler.RenderPassVisual = this;
        InvalidateVisual();
    }

    private void OnPlayerChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is MediaPlayer previous)
        {
            previous.AudioTracksChanged -= OnPlayerAudioTracksChanged;
            previous.AudioTrackChanged -= OnPlayerAudioTrackChanged;
            previous.SubtitleTracksChanged -= OnPlayerSubtitleTracksChanged;
            previous.SubtitleTrackChanged -= OnPlayerSubtitleTrackChanged;
            previous.SubtitlesChanged -= OnPlayerSubtitlesChanged;
            previous.BitmapSubtitlesChanged -= OnPlayerBitmapSubtitlesChanged;
            previous.ChaptersChanged -= OnPlayerChaptersChanged;
            if (ReferenceEquals(previous.VideoSink, this))
                previous.VideoSink = null;

            // The held frame belongs to the old player's pool, and at 8K it is 130 MB.
            ((IVideoSink)this).ClearVideo();
        }

        if (e.NewValue is MediaPlayer next)
        {
            next.VideoSink = this;
            RenderThreadScheduler.RenderPassVisual = this;
            next.AudioTracksChanged += OnPlayerAudioTracksChanged;
            next.AudioTrackChanged += OnPlayerAudioTrackChanged;
            next.SubtitleTracksChanged += OnPlayerSubtitleTracksChanged;
            next.SubtitleTrackChanged += OnPlayerSubtitleTrackChanged;
            next.SubtitlesChanged += OnPlayerSubtitlesChanged;
            next.BitmapSubtitlesChanged += OnPlayerBitmapSubtitlesChanged;
            next.ChaptersChanged += OnPlayerChaptersChanged;
        }

        RefreshAudioTracks();
        RefreshSubtitleTracks();
        RefreshChapters();

        // The document is published by an event, so a view that joins a player mid-playback would
        // otherwise draw no subtitles until the track is next changed.
        SetCurrentValue(SubtitlesProperty, (e.NewValue as MediaPlayer)?.Subtitles);
        SetCurrentValue(BitmapSubtitlesProperty, (e.NewValue as MediaPlayer)?.BitmapSubtitles);
    }

    // Both are raised from the demux thread.
    private void OnPlayerAudioTracksChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RefreshAudioTracks);

    private void OnPlayerAudioTrackChanged(object? sender, AudioTrack? track) =>
        Dispatcher.UIThread.Post(RefreshAudioTracks);

    private void OnPlayerSubtitleTracksChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RefreshSubtitleTracks);

    private void OnPlayerSubtitleTrackChanged(object? sender, SubtitleTrack? track) =>
        Dispatcher.UIThread.Post(RefreshSubtitleTracks);

    // Raised from whichever thread finished reading the track.
    private void OnPlayerSubtitlesChanged(object? sender, AssDocument? document) =>
        Dispatcher.UIThread.Post(() =>
        {
            SetCurrentValue(SubtitlesProperty, document);
            if (document is not null)
                SetCurrentValue(BitmapSubtitlesProperty, null);
        });

    private void OnPlayerBitmapSubtitlesChanged(object? sender, BitmapSubtitleDocument? document) =>
        Dispatcher.UIThread.Post(() =>
        {
            SetCurrentValue(BitmapSubtitlesProperty, document);
            if (document is not null)
                SetCurrentValue(SubtitlesProperty, null);
        });

    private void OnPlayerChaptersChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RefreshChapters);

    private void RefreshChapters()
    {
        IReadOnlyList<MediaChapter> current = Player?.Chapters ?? Array.Empty<MediaChapter>();

        for (int i = chapters.Count - 1; i >= 0; i--)
        {
            if (i >= current.Count || chapters[i] != current[i])
                chapters.RemoveAt(i);
        }

        for (int i = chapters.Count; i < current.Count; i++)
            chapters.Add(current[i]);

        if (chapters.Count == 0)
            SetSelectedChapter(null);
        else
            SyncSelectedChapter();
    }

    /// <summary>Moves the selection to the chapter the frame on screen belongs to.</summary>
    /// <remarks>Driven by the picture rather than a timer, so it costs nothing beyond the
    /// invalidation the frame already causes.</remarks>
    private void SyncSelectedChapter()
    {
        if (Player is not { } player || chapters.Count == 0)
            return;

        TimeSpan time;
        lock (frameSync)
            time = currentFrameTime;

        if (player.FindChapter(time) is { } chapter)
            SetSelectedChapter(chapter);
    }

    private void SetSelectedChapter(MediaChapter? chapter)
    {
        if (Equals(SelectedChapter, chapter))
            return;

        // Reporting where playback is must not be mistaken for the user asking to go somewhere.
        syncingChapter = true;
        try
        {
            SetCurrentValue(SelectedChapterProperty, chapter);
        }
        finally
        {
            syncingChapter = false;
        }
    }

    private void OnSelectedChapterChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (syncingChapter)
            return;

        if (Player is { } player && e.NewValue is MediaChapter chapter)
            player.Seek(chapter.Start);
    }

    private void RefreshAudioTracks()
    {
        MediaPlayer? player = Player;
        IReadOnlyList<AudioTrack> current = player?.AudioTracks ?? Array.Empty<AudioTrack>();

        // Edited rather than replaced, and only where the two lists actually differ, so adding a
        // track leaves the entries before it (and any selection on them) alone.
        for (int i = audioTracks.Count - 1; i >= 0; i--)
        {
            if (i >= current.Count || !ReferenceEquals(audioTracks[i], current[i]))
                audioTracks.RemoveAt(i);
        }

        for (int i = audioTracks.Count; i < current.Count; i++)
            audioTracks.Add(current[i]);

        AudioTrack? active = player?.ActiveAudioTrack;
        if (ReferenceEquals(SelectedAudioTrack, active))
            return;

        // Reporting the player's choice must not be mistaken for the user making one.
        syncingAudioTrack = true;
        try
        {
            SetCurrentValue(SelectedAudioTrackProperty, active);
        }
        finally
        {
            syncingAudioTrack = false;
        }
    }

    private void OnSelectedAudioTrackChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (syncingAudioTrack)
            return;

        if (Player is { } player && e.NewValue is AudioTrack track)
            player.SelectAudioTrack(track);
    }

    private void RefreshSubtitleTracks()
    {
        MediaPlayer? player = Player;
        IReadOnlyList<SubtitleTrack> current = player?.SubtitleTracks ?? Array.Empty<SubtitleTrack>();

        // Edited in place rather than replaced, for the same reason as the audio tracks.
        for (int i = subtitleTracks.Count - 1; i >= 0; i--)
        {
            if (i >= current.Count || !ReferenceEquals(subtitleTracks[i], current[i]))
                subtitleTracks.RemoveAt(i);
        }

        for (int i = subtitleTracks.Count; i < current.Count; i++)
            subtitleTracks.Add(current[i]);

        SubtitleTrack? active = player?.ActiveSubtitleTrack;
        if (ReferenceEquals(SelectedSubtitleTrack, active))
            return;

        // Reporting the player's choice must not be mistaken for the user making one.
        syncingSubtitleTrack = true;
        try
        {
            SetCurrentValue(SelectedSubtitleTrackProperty, active);
        }
        finally
        {
            syncingSubtitleTrack = false;
        }
    }

    private void OnSelectedSubtitleTrackChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (syncingSubtitleTrack)
            return;

        // Unlike a sound track, no selection is a selection: it is how subtitles are turned off.
        Player?.SelectSubtitleTrack(e.NewValue as SubtitleTrack);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        VideoSurfaceTransfers.Bt709PreferenceChanged += RequestRedraw;

        // Some decoders can only hand over their output on the thread that owns the rendering
        // context, and this is the earliest point at which that thread can be reached.
        PrepareRenderTarget();
        RenderThreadScheduler.Attach(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        VideoSurfaceTransfers.Bt709PreferenceChanged -= RequestRedraw;

        VideoDeinterlaceHistory? history;
        lock (frameSync)
        {
            history = deinterlaceHistory;
            deinterlaceHistory = null;
        }
        history?.Release();

        RenderThreadScheduler.Detach(this);
        captureRedrawTimer?.Stop();

        // Fonts, shaped runs and glyph blobs add up to megabytes. A render pass already in flight is
        // safe: the renderer refuses to draw once it has been disposed.
        Interlocked.Exchange(ref subtitleRenderer, null)?.Dispose();
        Interlocked.Exchange(ref bitmapSubtitleRenderer, null)?.Dispose();
    }
}
