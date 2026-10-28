using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LightMediaRenderer.Contracts;

/// <summary>
/// A decoded, renderer-ready video frame backed by pooled native memory.
/// </summary>
/// <remarks>
/// Frames are reference counted so consumers can retain them for repainting and in-flight draws.
/// The buffer returns to its pool only when the last reference is released.
/// </remarks>
public sealed class VideoFrame : IMediaQueueItem
{
    private const int MaxPlanes = 3;

    private readonly VideoFramePool? owner;

    // Allocated once per pooled frame, so reconfiguring a frame never allocates.
    private readonly int[] planeOffsets = new int[MaxPlanes];
    private readonly int[] planeStrides = new int[MaxPlanes];
    private readonly int[] planeRowBytes = new int[MaxPlanes];
    private readonly int[] planeHeights = new int[MaxPlanes];
    private readonly IntPtr[] borrowedPlanes = new IntPtr[MaxPlanes];

    private IntPtr buffer;
    private int capacity;
    private int planeCount;
    private int usedBytes;
    private int references;
    private bool externalTextureAcquisitionAttempted;
    private bool externalTextureAcquired;
    private bool hasAlpha;

    internal VideoFrame(VideoFramePool? owner) => this.owner = owner;

    // The buffer is unmanaged, so a frame that is dropped without its last reference being released
    // -- a control torn down mid-playback, a decoder swapped out under frames still in flight --
    // would otherwise leak it outright. At 8K that is 130 MB per stranded frame.
    ~VideoFrame() => FreeBuffer();

    /// <summary>An unpooled, buffer-less frame that terminates a run through the frame queue.</summary>
    public static VideoFrame CreateEndOfStreamMarker()
    {
        var marker = new VideoFrame(null);
        marker.Configure(0, 0, VideoPixelFormat.Bgra32, endOfStream: true);
        return marker;
    }

    /// <summary>
    /// A frame that owns its buffer outright and frees it on the last release, for a picture
    /// decoded outside playback. A still is decoded once and looked at for as long as it is open,
    /// so there is nothing for a pool to amortize.
    /// </summary>
    public static VideoFrame CreateStandalone(int width, int height, VideoPixelFormat format)
    {
        var frame = new VideoFrame(null);
        frame.Configure(width, height, format);
        return frame;
    }

    public static VideoFrame CreateBorrowed(int width, int height, VideoPixelFormat format,
        ReadOnlySpan<IntPtr> planes, ReadOnlySpan<int> strides, Action<VideoFrame> release)
        => CreateBorrowed(width, height, format, planes, strides, release, null);

    public static VideoFrame CreateBorrowed(int width, int height, VideoPixelFormat format,
        ReadOnlySpan<IntPtr> planes, ReadOnlySpan<int> strides, Action<VideoFrame> release, VideoFramePool? pool)
    {
        VideoFrame frame = pool?.RentSurface(width, height, format) ?? CreateHosted(width, height, format);
        planes[..frame.PlaneCount].CopyTo(frame.borrowedPlanes);
        strides[..frame.PlaneCount].CopyTo(frame.planeStrides);
        frame.Recycled = release;
        return frame;
    }

    public static VideoFrame CreateHosted(int width, int height, VideoPixelFormat format)
    {
        var frame = new VideoFrame(null);
        frame.ConfigureHosted(width, height, format);
        return frame;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Bytes per row of the first plane, including alignment padding.</summary>
    public int Stride => planeCount > 0 ? planeStrides[0] : 0;

    public VideoPixelFormat Format { get; private set; }

    /// <summary>Geometry and sample packing of the current <see cref="Format"/>.</summary>
    public VideoPlanarLayout Layout { get; private set; } = VideoPlanarLayout.Packed;

    /// <summary>Planes the current <see cref="Format"/> is split across.</summary>
    public int PlaneCount => planeCount;

    /// <summary>Pointer to the first pixel. Valid while the caller holds a reference.</summary>
    public IntPtr Data => GetPlane(0);

    /// <summary>Color matrix the chroma planes were encoded with. Meaningless for BGRA frames.</summary>
    public VideoColorSpace ColorSpace { get; set; }

    /// <summary>Chromaticities of the RGB signal represented by this frame.</summary>
    public VideoColorPrimaries ColorPrimaries { get; set; }

    /// <summary>Transfer function encoding the RGB signal, including HDR PQ and HLG.</summary>
    public VideoColorTransfer ColorTransfer { get; set; }

    /// <summary>
    /// Content or mastering peak in cd/m^2 for HDR tone mapping, or zero when it was not declared.
    /// </summary>
    public double HdrPeakLuminance { get; set; }

    public double? PhotoPeakLuminance { get; set; }

    public VideoLuminance? HdrMasteringLuminance { get; set; }

    /// <summary>Display-adaptive gain map for an SDR-base still photo, or null for other frames.</summary>
    public PhotoGainMap? GainMap { get; set; }

    public bool IsEmbeddedRawPreview { get; set; }

    public PhotoColorProfile? PhotoColor { get; set; }

    public bool HasAlpha
    {
        get => hasAlpha || PhotoColor?.HasAlpha == true;
        set => hasAlpha = value;
    }

    public IPhotoTileSource? PhotoTiles { get; set; }

    public IPhotoAnimationSource? PhotoAnimation { get; set; }

    public IPhotoSurface? PhotoSurface { get; set; }

    public Func<VideoFrame?>? DownloadPhotoSurface { get; set; }

    public Func<CancellationToken, ValueTask<VideoFrame?>>? DownloadPhotoSurfaceAsync { get; set; }

    public bool CanDownloadPhotoSurface => DownloadPhotoSurfaceAsync is not null || DownloadPhotoSurface is not null;

    /// <summary>
    /// Copies a graphics-memory frame's samples into an owned CPU frame with the same description, or
    /// null where its producer offers no copy. Call only while holding a reference, off the UI and
    /// render threads.
    /// </summary>
    public Func<VideoFrame?>? DownloadSamples { get; set; }

    /// <summary>Returns owned software pixels, retaining this frame until the asynchronous download or native worker completes.</summary>
    public async ValueTask<VideoFrame?> DownloadPhotoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AddRef();
        try
        {
            VideoFrame? downloaded = DownloadPhotoSurfaceAsync is { } asynchronous
                ? await asynchronous(cancellationToken).ConfigureAwait(false)
                : DownloadPhotoSurface is { } synchronous
                    ? await Task.Run(synchronous, cancellationToken).ConfigureAwait(false) : null;
            if (cancellationToken.IsCancellationRequested)
            {
                downloaded?.Release();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return downloaded;
        }
        finally
        {
            Release();
        }
    }

    /// <summary>Per-picture Dolby Vision reshaping and display metadata supplied by FFmpeg.</summary>
    public DolbyVisionMetadata DolbyVision { get; } = new();

    /// <summary>Decoded Profile 7 enhancement-layer residual owned by this base-layer frame.</summary>
    public VideoFrame? EnhancementLayer { get; private set; }

    /// <summary>Per-picture SMPTE ST 2094-40 metadata supplied by FFmpeg.</summary>
    public Hdr10PlusMetadata Hdr10Plus { get; } = new();

    /// <summary>Sample range of the planes. Meaningless for BGRA frames.</summary>
    public VideoColorRange ColorRange { get; set; }

    /// <summary>Where the chroma samples sit relative to the luma grid.</summary>
    public VideoChromaLocation ChromaLocation { get; set; }

    /// <summary>
    /// Whether the rows were sampled at one instant or as two fields half a frame apart, and in the
    /// latter case which of them came first. The rows are laid out exactly as they were coded, so
    /// separating them again is the renderer's to do.
    /// </summary>
    public VideoFieldOrder FieldOrder { get; set; }

    /// <summary>
    /// Renderer-owned texture backing an <see cref="VideoPixelFormat.ExternalTexture"/> frame. What
    /// the value names is between whoever produced the frame and whoever draws it.
    /// </summary>
    public long ExternalHandle { get; set; }

    public MediaResourceLease? Resource { get; private set; }

    public IVideoPlaneSource? PlaneSource { get; set; }

    public void AttachResource(MediaResourceLease resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Resource?.Dispose();
        Resource = resource;
    }

    /// <summary>
    /// Opaque generation of the graphics context that owns <see cref="ExternalHandle"/>. A value
    /// from an earlier generation cannot be sampled after the renderer changes targets.
    /// </summary>
    public int ExternalHandleGeneration { get; set; }

    /// <summary>
    /// How the renderer must bind <see cref="ExternalHandle"/>, where the producer needs a target
    /// other than the plain two-dimensional one. Zero leaves the choice to the renderer.
    /// </summary>
    /// <remarks>
    /// A decoder that draws into a platform window may hand its pictures back through an external
    /// texture, or copy raw YUV into an ordinary high-precision texture where direct YUV sampling
    /// is available.
    /// </remarks>
    public uint ExternalTextureTarget { get; set; }

    /// <summary>Storage format the renderer must use when it wraps the external texture.</summary>
    public VideoExternalTextureFormat ExternalTextureFormat { get; set; }

    /// <summary>Whether the external texture contains display RGB or unconverted YUV samples.</summary>
    public VideoExternalTextureColorModel ExternalTextureColorModel { get; set; }

    /// <summary>Meaningful source bits in each external texture channel.</summary>
    public int ExternalTextureBitDepth { get; set; } = 8;

    /// <summary>
    /// Where the picture sits inside <see cref="ExternalHandle"/>, in normalized texture
    /// coordinates: the scale and offset that map the picture's own corners onto the texture.
    /// </summary>
    /// <remarks>
    /// A platform window hands back a texture that is neither the picture's size nor necessarily
    /// the right way up: a codec's coded size carries padding the picture must not sample, and the
    /// texture's origin is at the bottom. The default maps the whole texture unchanged.
    /// </remarks>
    public VideoTextureRect ExternalTextureRect { get; set; } = VideoTextureRect.Whole;

    /// <summary>
    /// Makes this frame's picture the one <see cref="ExternalHandle"/> holds, for a producer that
    /// decodes into a platform window instead of into a texture of its own. Runs on the render
    /// thread immediately before the texture is sampled; returning false leaves the frame undrawn.
    /// </summary>
    public Func<VideoFrame, bool>? AcquireExternalTexture { get; set; }

    /// <summary>
    /// Acquires a producer's one-shot picture token at most once, while allowing later repaints to
    /// keep sampling the texture left behind by a successful acquisition.
    /// </summary>
    /// <param name="attemptedNow">
    /// True only for the draw that invoked the producer, so one failed frame is counted once.
    /// </param>
    public bool TryAcquireExternalTexture(out bool attemptedNow)
    {
        if (AcquireExternalTexture is not { } acquire)
        {
            attemptedNow = false;
            return true;
        }

        if (externalTextureAcquisitionAttempted)
        {
            attemptedNow = false;
            return externalTextureAcquired;
        }

        externalTextureAcquisitionAttempted = true;
        attemptedNow = true;
        externalTextureAcquired = acquire(this);
        return externalTextureAcquired;
    }

    /// <summary>
    /// The graphics-memory surface holding this frame's planes, when it has one. The layout and
    /// color description still describe the samples, so a renderer that cannot import the surface
    /// has everything it needs except the pixels.
    /// </summary>
    public VideoSurfaceHandle Surface { get; set; }

    /// <summary>
    /// Size of the surface the frame occupies, which is at least the picture's and is larger
    /// wherever the codec rounded the coded size up. Zero when it is not known to differ.
    /// </summary>
    /// <remarks>
    /// A renderer sampling the surface has to know this: the padding is part of the texture it
    /// imports, so the picture would be stretched over it if the two were assumed to be the same.
    /// </remarks>
    public int SurfaceWidth { get; set; }

    /// <inheritdoc cref="SurfaceWidth"/>
    public int SurfaceHeight { get; set; }

    /// <summary>Whether the planes live in graphics memory rather than in this frame's buffer.</summary>
    public bool HasSurface => Surface.IsValid;

    /// <summary>
    /// Whether the picture was already resized to its output size by the device that decoded it, so
    /// the display filter has nothing left to do but sample it.
    /// </summary>
    public bool IsHardwareScaled { get; set; }

    /// <summary>
    /// Whether the picture already carries its own letterbox, so it covers the control exactly and
    /// is drawn in one piece rather than placed on a cleared background.
    /// </summary>
    public bool CoversOutput { get; set; }

    /// <summary>
    /// Size the picture was decoded at, which is what its aspect and its place in a layout follow
    /// from. Zero where nothing resized it and <see cref="Width"/> is the whole story.
    /// </summary>
    /// <remarks>
    /// Laying a resized picture out by the size it now has would let the rounding of one output
    /// size decide the next, and the two would drift apart a fraction of a percent at a time.
    /// </remarks>
    public int SourceWidth { get; set; }

    /// <inheritdoc cref="SourceWidth"/>
    public int SourceHeight { get; set; }

    /// <summary>
    /// Runs when the last reference goes, before the buffer returns to the pool. This is the only
    /// point at which a producer can reclaim a resource it attached to the frame.
    /// </summary>
    public Action<VideoFrame>? Recycled { get; set; }

    /// <summary>
    /// Asks the decoder that owns this frame to stop delivering surfaces after one cannot be
    /// imported or rendered. The rejection is scoped to that decoder rather than the process.
    /// Returns true only when this call changed the decoder's delivery path.
    /// </summary>
    public Func<VideoFrame, bool>? RejectSurfaceDelivery { get; set; }

    /// <summary>Pointer to the first pixel of a plane, or zero when the frame has no buffer.</summary>
    public IntPtr GetPlane(int index)
    {
        ValidatePlane(index);
        return borrowedPlanes[index] != IntPtr.Zero ? borrowedPlanes[index]
            : buffer == IntPtr.Zero ? IntPtr.Zero : buffer + planeOffsets[index];
    }

    /// <summary>Bytes between successive rows of a plane, including alignment padding.</summary>
    public int GetStride(int index)
    {
        ValidatePlane(index);
        return planeStrides[index];
    }

    /// <summary>Bytes of actual samples in a row of a plane, excluding alignment padding.</summary>
    public int GetRowBytes(int index)
    {
        ValidatePlane(index);
        return planeRowBytes[index];
    }

    /// <summary>Rows in a plane; chroma planes of subsampled formats are shorter than the frame.</summary>
    public int GetPlaneHeight(int index)
    {
        ValidatePlane(index);
        return planeHeights[index];
    }

    private void ValidatePlane(int index)
    {
        if ((uint)index >= (uint)planeCount)
            throw new ArgumentOutOfRangeException(nameof(index));
    }

    // The four properties below are written by whichever decoder produced the frame, including
    // decoders living outside this assembly, so they are settable rather than internal.

    /// <summary>Presentation timestamp in seconds on the media timeline.</summary>
    public double Pts { get; set; }

    /// <summary>Duration in seconds, used for video late-frame accounting and photo animation pacing.</summary>
    public double Duration { get; set; }

    /// <summary>Pixel aspect ratio to apply when laying the frame out (non-square pixel sources).</summary>
    public double PixelAspectRatio { get; set; } = 1.0;

    /// <summary>
    /// Degrees the picture has to be turned clockwise before it is shown: 0, 90, 180 or 270. The
    /// samples are laid out exactly as they were coded, so this is the renderer's to apply.
    /// </summary>
    public int Rotation { get; set; }

    /// <summary>Whether the picture is mirrored left to right before <see cref="Rotation"/>.</summary>
    public bool MirrorHorizontally { get; set; }

    public bool IsKeyFrame { get; set; }

    public int Serial { get; set; }

    public bool IsEndOfStream { get; internal set; }

    public int SizeInBytes => (int)Math.Min(int.MaxValue,
        (PhotoSurface?.SizeInBytes ?? PhotoTiles?.EncodedBytes ?? Resource?.ByteLength ?? usedBytes) +
        (PhotoAnimation?.EncodedBytes ?? 0) + (EnhancementLayer?.SizeInBytes ?? 0) + (GainMap?.SizeInBytes ?? 0));

    /// <summary>Bytes actually reserved, which outlast any one configuration of the frame.</summary>
    internal int BufferBytes => capacity;

    internal void Configure(int width, int height, VideoPixelFormat format, bool endOfStream = false) =>
        Configure(width, height, format, endOfStream, hosted: false);

    /// <summary>
    /// Describes a frame whose planes are held elsewhere: geometry and color are filled in as
    /// usual, but no buffer is reserved and every plane pointer stays null.
    /// </summary>
    internal void ConfigureHosted(int width, int height, VideoPixelFormat format) =>
        Configure(width, height, format, endOfStream: false, hosted: true);

    private void Configure(int width, int height, VideoPixelFormat format, bool endOfStream, bool hosted)
    {
        Width = width;
        Height = height;
        Format = format;
        Layout = VideoPlanarLayout.For(format);
        IsEndOfStream = endOfStream;
        ColorSpace = VideoColorSpace.Bt709;
        ColorPrimaries = VideoColorPrimaries.Bt709;
        ColorTransfer = VideoColorTransfer.Bt709;
        HdrPeakLuminance = 0;
        PhotoPeakLuminance = null;
        HdrMasteringLuminance = null;
        GainMap = null;
        IsEmbeddedRawPreview = false;
        hasAlpha = false;
        PhotoColor = null;
        PhotoTiles = null;
        PhotoAnimation = null;
        PhotoSurface = null;
        PlaneSource = null;
        DownloadPhotoSurface = null;
        DownloadPhotoSurfaceAsync = null;
        DownloadSamples = null;
        DolbyVision.Reset();
        EnhancementLayer = null;
        Hdr10Plus.Reset();
        ColorRange = VideoColorRange.Limited;
        ChromaLocation = VideoChromaLocation.Left;
        FieldOrder = VideoFieldOrder.Unknown;
        ExternalHandle = 0;
        ExternalHandleGeneration = 0;
        ExternalTextureTarget = 0;
        ExternalTextureFormat = VideoExternalTextureFormat.Rgba8;
        ExternalTextureColorModel = VideoExternalTextureColorModel.Rgb;
        ExternalTextureBitDepth = 8;
        ExternalTextureRect = VideoTextureRect.Whole;
        AcquireExternalTexture = null;
        externalTextureAcquisitionAttempted = false;
        externalTextureAcquired = false;
        Surface = VideoSurfaceHandle.None;
        SurfaceWidth = 0;
        SurfaceHeight = 0;
        IsHardwareScaled = false;
        CoversOutput = false;
        SourceWidth = 0;
        SourceHeight = 0;
        Rotation = 0;
        MirrorHorizontally = false;
        Recycled = null;
        RejectSurfaceDelivery = null;
        references = 1;

        long required = BuildLayout(width, height, format, hosted);

        // Grown when too small, but also shrunk when far too large: without this an end-of-stream
        // marker or a later low-resolution source keeps a full 8K buffer reserved for the rest of
        // the session.
        if (required > capacity || required <= capacity / 4)
        {
            FreeBuffer(keepLayout: true);
            if (required > 0)
            {
                buffer = Marshal.AllocHGlobal((int)required);
                capacity = (int)required;

                // Unmanaged, so the GC cannot see it and would happily let hundreds of megabytes of
                // stranded frames sit unfinalized.
                GC.AddMemoryPressure(capacity);
            }
        }
    }

    /// <summary>Lays the planes out back to back and returns the bytes the frame needs.</summary>
    private long BuildLayout(int width, int height, VideoPixelFormat format, bool hosted)
    {
        Array.Clear(planeOffsets);
        Array.Clear(planeStrides);
        Array.Clear(planeRowBytes);
        Array.Clear(planeHeights);
        Array.Clear(borrowedPlanes);

        if (width <= 0 || height <= 0 || format == VideoPixelFormat.ExternalTexture)
        {
            planeCount = 0;
            usedBytes = 0;
            return 0;
        }

        VideoPlanarLayout layout = Layout;
        planeCount = layout.PlaneCount;
        planeRowBytes[0] = width * layout.BytesPerSample;
        planeHeights[0] = height;

        // Both chroma components share one sample position in the interleaved layouts.
        int components = layout.InterleavedChroma ? 2 : 1;
        for (int i = 1; i < planeCount; i++)
        {
            planeRowBytes[i] = layout.ChromaWidth(width) * components * layout.BytesPerSample;
            planeHeights[i] = layout.ChromaHeight(height);
        }

        // A hosted frame still describes its planes, because the renderer sizes its imports from
        // them, but their rows belong to whoever allocated the surface. It is still charged for
        // what it occupies: the queues bound themselves in bytes, and a decoder's surface that a
        // queue is holding is exactly the kind of memory that bound exists to cap -- more so than a
        // pooled buffer, because it comes out of a fixed allocation the decoder cannot grow.
        if (hosted)
        {
            long hostedBytes = 0;
            for (int i = 0; i < planeCount; i++)
                hostedBytes += (long)planeRowBytes[i] * planeHeights[i];

            usedBytes = (int)Math.Min(hostedBytes, int.MaxValue);
            return 0;
        }

        long offset = 0;
        for (int i = 0; i < planeCount; i++)
        {
            // 64-byte rows keep the plane copies and swscale on their SIMD paths, and match every
            // cache line size in use.
            planeStrides[i] = (planeRowBytes[i] + 63) & ~63;
            if (offset > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(width), "The frame is too large to buffer.");

            planeOffsets[i] = (int)offset;
            offset += (long)planeStrides[i] * planeHeights[i];
        }

        if (offset > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "The frame is too large to buffer.");

        usedBytes = (int)offset;
        return offset;
    }

    /// <summary>Takes an extra reference. Returns the frame so calls can be chained.</summary>
    public VideoFrame AddRef()
    {
        Interlocked.Increment(ref references);
        return this;
    }

    public void AttachEnhancementLayer(VideoFrame enhancementLayer)
    {
        EnhancementLayer?.Release();
        EnhancementLayer = enhancementLayer;
    }

    /// <summary>Drops a reference, returning the frame to its pool when the last one goes.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref references) > 0)
            return;

        Action<VideoFrame>? recycled = Recycled;
        Recycled = null;
        recycled?.Invoke(this);

        Resource?.Dispose();
        Resource = null;
        (PlaneSource as IDisposable)?.Dispose();
        PlaneSource = null;

        VideoFrame? enhancementLayer = EnhancementLayer;
        EnhancementLayer = null;
        enhancementLayer?.Release();
        GainMap?.Release();
        GainMap = null;
        PhotoColor = null;
        PhotoTiles?.Dispose();
        PhotoTiles = null;
        PhotoAnimation?.Dispose();
        PhotoAnimation = null;
        PhotoSurface?.Dispose();
        PhotoSurface = null;
        DownloadPhotoSurface = null;
        DownloadPhotoSurfaceAsync = null;
        DownloadSamples = null;

        if (owner is null)
            FreeBuffer();
        else
            owner.Return(this);
    }

    internal void FreeBuffer() => FreeBuffer(keepLayout: false);

    private void FreeBuffer(bool keepLayout)
    {
        if (buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(buffer);
            buffer = IntPtr.Zero;
            GC.RemoveMemoryPressure(capacity);
        }

        capacity = 0;
        if (keepLayout)
            return;

        planeCount = 0;
        usedBytes = 0;
        Array.Clear(borrowedPlanes);
    }
}

/// <summary>
/// Fixed-size pool of decoded-frame buffers. The pool is deliberately small: it only has to cover
/// the frames in flight between the decoder, the queue and the renderer, and every extra 4K buffer
/// costs ~33 MB.
/// </summary>
public sealed class VideoFramePool
{
    private readonly ConcurrentBag<VideoFrame> idle = new();
    private readonly int maxIdle;
    private readonly long maxIdleBytes;
    private long idleBytes;
    private volatile bool closed;

    /// <param name="maxIdleBytes">
    /// Ceiling on the buffers kept for reuse, so the count alone does not decide how much is held:
    /// twelve idle 8K frames would be 1.6 GB.
    /// </param>
    public VideoFramePool(int maxIdle = 12, long maxIdleBytes = 256L * 1024 * 1024)
    {
        this.maxIdle = maxIdle;
        this.maxIdleBytes = maxIdleBytes;
    }

    public VideoFrame Rent(int width, int height) => Rent(width, height, VideoPixelFormat.Bgra32);

    public VideoFrame Rent(int width, int height, VideoPixelFormat format)
    {
        VideoFrame frame = Take();
        frame.Configure(width, height, format);
        return frame;
    }

    /// <summary>
    /// Takes a frame that will describe planes held in graphics memory. The caller must set
    /// <see cref="VideoFrame.Surface"/> before handing it on, and must keep that surface alive
    /// until the frame is recycled.
    /// </summary>
    public VideoFrame RentSurface(int width, int height, VideoPixelFormat format)
    {
        VideoFrame frame = Take();
        frame.ConfigureHosted(width, height, format);
        return frame;
    }

    /// <summary>Allocates a zero-sized frame used purely as an end-of-stream marker.</summary>
    public VideoFrame RentEndOfStream()
    {
        VideoFrame frame = Take();
        frame.Configure(0, 0, VideoPixelFormat.Bgra32, endOfStream: true);
        return frame;
    }

    private VideoFrame Take()
    {
        if (idle.TryTake(out var frame))
            Interlocked.Add(ref idleBytes, -frame.BufferBytes);
        else
            frame = new VideoFrame(this);

        return frame;
    }

    internal void Return(VideoFrame frame)
    {
        // Frames outlive the pool: the control and an in-flight draw operation still hold the last
        // picture when the decoder is disposed or swapped out. Without this they would join the bag
        // of a pool nobody will ever clear again and their buffers would never come back.
        int size = frame.BufferBytes;
        if (closed || idle.Count >= maxIdle || Interlocked.Read(ref idleBytes) + size > maxIdleBytes)
        {
            frame.FreeBuffer();
            return;
        }

        Interlocked.Add(ref idleBytes, size);
        idle.Add(frame);
    }

    /// <summary>
    /// Frees every pooled buffer and retires the pool: frames still in flight free theirs as they
    /// are released instead of coming back here.
    /// </summary>
    public void Clear()
    {
        closed = true;
        while (idle.TryTake(out var frame))
            frame.FreeBuffer();
        Interlocked.Exchange(ref idleBytes, 0);
    }
}
