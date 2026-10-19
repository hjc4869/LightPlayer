using System;
using System.Threading;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Android.Video;

/// <summary>
/// A <see cref="SurfaceTexture"/> the MediaCodec decoder draws into, and the external texture the
/// renderer samples the result from.
/// </summary>
/// <remarks>
/// <para>
/// MediaCodec's memory output is 4:2:0 8-bit whatever the stream held, so an HDR10 source read back
/// that way loses three quarters of its steps and bands. Drawing into a surface keeps the codec's
/// own buffers, which are 10-bit. A regular external sampler lets the driver collapse those buffers
/// to 8-bit RGB, so deep sources are first copied as raw YUV into an F16 texture and converted by the
/// same color pipeline as software-decoded planes.
/// </para>
/// <para>
/// A surface texture holds one picture at a time. The decoder therefore keeps the pictures ahead of
/// the clock as tokens and only hands one over when it is drawn, which is what <see cref="Acquire"/>
/// does, on the render thread where the graphics context is bound.
/// </para>
/// </remarks>
internal sealed class MediaCodecOutputSurface : IVideoDecoderOutputSurface
{
    // Long enough for a buffer already handed to the queue to come back around, short enough that
    // missing it costs one compositor frame rather than a visible stall.
    private const int FrameWaitMilliseconds = 8;

    private static int nextGeneration;

    private readonly Context context;
    private readonly int width;
    private readonly int height;
    private readonly int bitDepth;
    private readonly bool preserveYuv;
    private readonly SurfaceTexture surfaceTexture;
    private readonly Surface surface;
    private readonly FrameListener listener;
    private readonly HandlerThread callbacks;
    private readonly AutoResetEvent frameAvailable = new(false);
    private readonly float[] transform = new float[16];
    private readonly object sync = new();

    private IntPtr surfaceReference;
    private IntPtr nativeWindow;
    private uint sourceTexture;
    private AndroidVideoInterop.ExternalYuvCopy yuvCopy;
    private bool attached;
    private bool unusable;
    private bool warnedAboutRotation;
    private bool warnedAboutCopy;
    private volatile bool disposed;

    private MediaCodecOutputSurface(Context context, int width, int height, int bitDepth)
    {
        this.context = context;
        this.width = width;
        this.height = height;
        this.bitDepth = Math.Max(bitDepth, 8);
        preserveYuv = bitDepth > 8;

        // Detached, so the texture it streams into can be made later on the thread that owns the
        // graphics context rather than on the decode thread that opens this.
        surfaceTexture = new SurfaceTexture(singleBufferMode: false);
        if (width > 0 && height > 0)
            surfaceTexture.SetDefaultBufferSize(width, height);

        callbacks = new HandlerThread("lightplayer-video-surface");
        callbacks.Start();
        listener = new FrameListener(this);
        surfaceTexture.SetOnFrameAvailableListener(listener, new Handler(callbacks.Looper!));

        surface = new Surface(surfaceTexture);
        surfaceReference = JNIEnv.NewGlobalRef(surface.Handle);
        nativeWindow = AndroidVideoInterop.NativeWindowFromSurface(JNIEnv.Handle, surface.Handle);

        TextureGeneration = Interlocked.Increment(ref nextGeneration);
        VideoFrameCapabilities.SetExternalTextureGeneration(TextureGeneration);
    }

    public IntPtr SurfaceObject => surfaceReference;

    public IntPtr NativeWindow => nativeWindow;

    public long TextureHandle => preserveYuv ? yuvCopy.Texture : sourceTexture;

    public int TextureGeneration { get; }

    public uint TextureTarget => preserveYuv
        ? AndroidVideoInterop.GlTexture2D
        : AndroidVideoInterop.GlTextureExternalOes;

    public VideoExternalTextureFormat TextureFormat => preserveYuv
        ? VideoExternalTextureFormat.Rgba16Float
        : VideoExternalTextureFormat.Rgba8;

    public VideoExternalTextureColorModel TextureColorModel => preserveYuv
        ? VideoExternalTextureColorModel.Yuv
        : VideoExternalTextureColorModel.Rgb;

    public int TextureBitDepth => bitDepth;

    /// <summary>
    /// Opens a window for one stream, or returns null when this build cannot drive one.
    /// </summary>
    public static IVideoDecoderOutputSurface TryCreate(
        Context context,
        int width,
        int height,
        int bitDepth)
    {
        if (!AndroidVideoInterop.SupportsBufferRendering(context))
        {
            AppLog.Information(
                "Android.Video",
                "The loaded FFmpeg build exports no av_mediacodec_release_buffer; decoding into a surface is off.");
            return null;
        }

        try
        {
            return new MediaCodecOutputSurface(context, width, height, bitDepth);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Android.Video", "Could not open a MediaCodec output surface.", exception);
            return null;
        }
    }

    public bool Acquire(IntPtr picture, out VideoTextureRect rect)
    {
        rect = VideoTextureRect.Whole;
        if (disposed)
            return false;

        lock (sync)
        {
            if (disposed || !EnsureAttached())
                return false;

            frameAvailable.Reset();
            if (!AndroidVideoInterop.RenderBuffer(context, picture))
                return false;

            // The buffer travels through the window's queue, so it is not necessarily there yet.
            // Missing it leaves the previous picture up for one more compositor frame, which is
            // what the wait is for.
            frameAvailable.WaitOne(FrameWaitMilliseconds);

            try
            {
                surfaceTexture.UpdateTexImage();
                surfaceTexture.GetTransformMatrix(transform);
            }
            catch (Exception exception)
            {
                AppLog.Warning("Android.Video", "Updating the decoder's surface texture failed.", exception);
                return false;
            }

            if (preserveYuv &&
                !AndroidVideoInterop.CopyExternalYuv(
                    sourceTexture,
                    yuvCopy,
                    width,
                    height,
                    out string declined))
            {
                if (!warnedAboutCopy)
                {
                    warnedAboutCopy = true;
                    AppLog.Warning("Android.Video", $"Could not preserve MediaCodec YUV precision: {declined}.");
                }
                return false;
            }

            rect = ReadTransform();
            return true;
        }
    }

    /// <summary>
    /// The part of the texture the picture occupies, out of the column-major matrix the surface
    /// texture describes its buffer with.
    /// </summary>
    private VideoTextureRect ReadTransform()
    {
        if (!warnedAboutRotation && VideoTextureRect.IsRotated(transform))
        {
            warnedAboutRotation = true;
            AppLog.Warning(
                "Android.Video",
                "The decoder's surface texture is rotated, which the renderer cannot express.");
        }

        return VideoTextureRect.FromWindowTransform(transform);
    }

    /// <summary>Makes the texture and joins the surface to it, on the thread that owns the context.</summary>
    private bool EnsureAttached()
    {
        if (attached)
            return !unusable;

        OrphanedTextures.Drain();
        sourceTexture = AndroidVideoInterop.CreateExternalTexture();
        if (sourceTexture == 0)
        {
            AppLog.Warning("Android.Video", "The graphics backend would not make an external texture.");
            return false;
        }

        try
        {
            surfaceTexture.AttachToGLContext((int)sourceTexture);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Android.Video", "Could not attach the decoder's surface texture.", exception);
            AndroidVideoInterop.DeleteTexture(sourceTexture);
            sourceTexture = 0;
            return false;
        }

        attached = true;
        if (!preserveYuv)
            return true;

        if (AndroidVideoInterop.TryCreateExternalYuvCopy(width, height, out yuvCopy, out string declined))
        {
            AppLog.Information(
                "Android.Video",
                $"MediaCodec {bitDepth}-bit output is sampled as YUV into an F16 texture.");
            return true;
        }

        unusable = true;
        AppLog.Warning("Android.Video", $"Full-precision MediaCodec output is unavailable: {declined}.");
        return false;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        // Held for the whole teardown: a draw in flight must not find the surface released between
        // the check that says it is alive and the call that reads it.
        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;

            surfaceTexture.SetOnFrameAvailableListener(null, null);
            surface.Release();
            surfaceTexture.Release();

            if (nativeWindow != IntPtr.Zero)
            {
                AndroidVideoInterop.NativeWindowRelease(nativeWindow);
                nativeWindow = IntPtr.Zero;
            }

            if (surfaceReference != IntPtr.Zero)
            {
                JNIEnv.DeleteGlobalRef(surfaceReference);
                surfaceReference = IntPtr.Zero;
            }

            // Deleting the texture needs the graphics context, which this thread does not hold; the
            // next stream's first draw clears whatever the last one left behind.
            if (yuvCopy.IsValid)
            {
                OrphanedTextures.Add(yuvCopy);
                yuvCopy = default;
            }

            if (sourceTexture != 0)
            {
                OrphanedTextures.Add(sourceTexture);
                sourceTexture = 0;
            }

            callbacks.QuitSafely();
            surface.Dispose();
            surfaceTexture.Dispose();
            listener.Dispose();
            callbacks.Dispose();
            frameAvailable.Dispose();
        }
    }

    private sealed class FrameListener(MediaCodecOutputSurface owner)
        : Java.Lang.Object, SurfaceTexture.IOnFrameAvailableListener
    {
        public void OnFrameAvailable(SurfaceTexture surfaceTexture)
        {
            if (!owner.disposed)
                owner.frameAvailable.Set();
        }
    }
}
