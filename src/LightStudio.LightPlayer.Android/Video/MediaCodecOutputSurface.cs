using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Android.Content;
using Android.Runtime;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;
using LightStudio.NativeBindings.Types;
using static LightStudio.LightPlayer.Android.Video.AndroidVideoInterop;

namespace LightStudio.LightPlayer.Android.Video;

/// <summary>
/// An image reader the MediaCodec decoder draws into, and the external texture the renderer samples
/// the result from.
/// </summary>
/// <remarks>
/// <para>
/// MediaCodec's memory output is 4:2:0 8-bit whatever the stream held, so an HDR10 source read back
/// that way loses three quarters of its steps and bands. Drawing into a window keeps the codec's
/// own buffers, which are 10-bit. A regular external sampler lets the driver collapse those buffers
/// to 8-bit RGB, so deep sources are first copied as raw YUV into an F16 texture and converted by the
/// same color pipeline as software-decoded planes.
/// </para>
/// <para>
/// The reader queues the pictures the codec renders and hands them over as hardware buffers, which
/// <see cref="Acquire"/> wraps as images of the renderer's own context, on the render thread where
/// that context is bound. The decoder keeps the pictures ahead of the clock as tokens and only hands
/// one over when it is drawn. Nothing on this path goes through the Java runtime: the reader, its
/// window, its listener, and the buffers are native objects. The codec is configured with a surface
/// object, which the platform makes from the window.
/// </para>
/// </remarks>
internal sealed unsafe class MediaCodecOutputSurface : IVideoDecoderOutputSurface
{
    // Long enough for a buffer already handed to the queue to come back around, short enough that
    // missing it costs one compositor frame rather than a visible stall.
    private const int FrameWaitMilliseconds = 8;

    // One picture is held while it is shown; the margin lets the reader skip to the newest one.
    private const int MaxImages = 3;

    private static int nextGeneration;

    private readonly Context context;
    private readonly int width;
    private readonly int height;
    private readonly int bitDepth;
    private readonly bool preserveYuv;
    private readonly AutoResetEvent frameAvailable = new(false);
    private readonly object sync = new();
    private readonly GCHandle self;

    private AImageReader* reader;
    private IntPtr nativeWindow;
    private IntPtr surfaceReference;
    private AImage* picture;
    private void* image;
    private void* display;
    private VideoTextureRect rect = VideoTextureRect.Whole;
    private uint sourceTexture;
    private ExternalYuvCopy yuvCopy;
    private bool attached;
    private bool unusable;
    private bool warnedAboutPicture;
    private bool warnedAboutCopy;
    private volatile bool disposed;

    private MediaCodecOutputSurface(Context context, int width, int height, int bitDepth)
    {
        this.context = context;
        this.width = width;
        this.height = height;
        this.bitDepth = Math.Max(bitDepth, 8);
        preserveYuv = bitDepth > 8;

        AImageReader* created = null;
        media_status_t status = AImageReader_newWithUsage(
            width,
            height,
            (int)AIMAGE_FORMAT_PRIVATE,
            AHARDWAREBUFFER_USAGE_GPU_SAMPLED_IMAGE,
            MaxImages,
            &created);
        if (status != media_status_t.AMEDIA_OK || created == null)
            throw new InvalidOperationException($"The platform would not open an image reader: {status}.");

        reader = created;
        self = GCHandle.Alloc(this);
        try
        {
            var listener = new AImageReader_ImageListener
            {
                context = (void*)GCHandle.ToIntPtr(self),
                onImageAvailable = &OnImageAvailable,
            };
            ThrowIfFailed(AImageReader_setImageListener(reader, &listener), "listen to the image reader");
            ANativeWindow* window = null;
            ThrowIfFailed(AImageReader_getWindow(reader, &window), "read the image reader's window");
            nativeWindow = (IntPtr)window;

            // The codec is configured through the Java API, which takes the window as a surface object.
            void* surface = ANativeWindow_toSurface((JNINativeInterface**)JNIEnv.Handle, window);
            if (surface == null)
                throw new InvalidOperationException("The platform would not wrap the image reader's window.");
            surfaceReference = JNIEnv.NewGlobalRef((IntPtr)surface);
            JNIEnv.DeleteLocalRef((IntPtr)surface);
        }
        catch
        {
            Dispose();
            throw;
        }

        TextureGeneration = Interlocked.Increment(ref nextGeneration);
        VideoFrameCapabilities.SetExternalTextureGeneration(TextureGeneration);
    }

    public IntPtr SurfaceObject => surfaceReference;

    public IntPtr NativeWindow => nativeWindow;

    public long TextureHandle => preserveYuv ? yuvCopy.Texture : sourceTexture;

    public int TextureGeneration { get; }

    public uint TextureTarget => preserveYuv ? (uint)GL_TEXTURE_2D : (uint)GL_TEXTURE_EXTERNAL_OES;

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
        if (!SupportsBufferRendering(context))
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

    public bool Acquire(IntPtr token, out VideoTextureRect rect)
    {
        rect = VideoTextureRect.Whole;
        if (disposed)
            return false;

        lock (sync)
        {
            if (disposed || !EnsureAttached())
                return false;

            frameAvailable.Reset();
            if (!RenderBuffer(context, token))
                return false;

            // The buffer travels through the window's queue, so it is not necessarily there yet.
            // Missing it leaves the previous picture up for one more compositor frame, which is
            // what the wait is for.
            frameAvailable.WaitOne(FrameWaitMilliseconds);

            AImage* latest = null;
            media_status_t status = AImageReader_acquireLatestImage(reader, &latest);
            if (status == media_status_t.AMEDIA_OK && latest != null)
            {
                if (!Show(latest))
                    return false;
            }
            else if (picture == null || status != media_status_t.AMEDIA_IMGREADER_NO_BUFFER_AVAILABLE)
            {
                if (!warnedAboutPicture)
                {
                    warnedAboutPicture = true;
                    AppLog.Warning("Android.Video", $"The decoder's picture did not reach the image reader: {status}.");
                }
                return false;
            }

            if (preserveYuv &&
                !CopyExternalYuv(
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

            rect = this.rect;
            return true;
        }
    }

    /// <summary>Points the external texture at one picture and retires the one before it.</summary>
    private bool Show(AImage* latest)
    {
        AHardwareBuffer* buffer = null;
        AImageCropRect crop;
        if (AImage_getHardwareBuffer(latest, &buffer) != media_status_t.AMEDIA_OK || buffer == null ||
            AImage_getCropRect(latest, &crop) != media_status_t.AMEDIA_OK)
        {
            AImage_delete(latest);
            Warn("The image reader handed over a picture without a hardware buffer.");
            return false;
        }

        AHardwareBuffer_Desc description;
        AHardwareBuffer_describe(buffer, &description);
        void* created = CreateImage(display, buffer);
        if (created == null)
        {
            AImage_delete(latest);
            Warn("The picture's hardware buffer could not be wrapped as an image.");
            return false;
        }

        glBindTexture(GL_TEXTURE_EXTERNAL_OES, sourceTexture);
        glEGLImageTargetTexture2DOES(GL_TEXTURE_EXTERNAL_OES, created);
        uint error = glGetError();
        glBindTexture(GL_TEXTURE_EXTERNAL_OES, 0);
        if (error != GL_NO_ERROR)
        {
            eglDestroyImageKHR(display, created);
            AImage_delete(latest);
            Warn($"The picture could not be bound to the external texture: GL error 0x{error:X}.");
            return false;
        }

        Retire();
        picture = latest;
        image = created;
        rect = ReadRect(description, crop);
        return true;
    }

    /// <summary>
    /// Returns the shown picture's buffer to the queue once the GPU has finished with it, as the
    /// draws that sample it were only issued and have not necessarily run.
    /// </summary>
    private void Retire()
    {
        if (image != null)
        {
            eglDestroyImageKHR(display, image);
            image = null;
        }

        if (picture == null)
            return;

        int fence = CreateReleaseFence(display);
        if (fence >= 0)
            AImage_deleteAsync(picture, fence);
        else
        {
            glFinish();
            AImage_delete(picture);
        }
        picture = null;
    }

    /// <summary>
    /// The part of the buffer the picture occupies, as the renderer reads the texture from the top
    /// down. Where the crop leaves a margin, the edges move in by a texel, or half of one for RGBA,
    /// so that filtering stays inside the picture, which is what the platform's own window
    /// transform does.
    /// </summary>
    private static VideoTextureRect ReadRect(in AHardwareBuffer_Desc description, in AImageCropRect crop)
    {
        float bufferWidth = description.width;
        float bufferHeight = description.height;
        float cropWidth = crop.right - crop.left;
        float cropHeight = crop.bottom - crop.top;
        if (bufferWidth <= 0 || bufferHeight <= 0 || cropWidth <= 0 || cropHeight <= 0)
            return VideoTextureRect.Whole;

        float inset = description.format == (uint)AHARDWAREBUFFER_FORMAT_R8G8B8A8_UNORM ? 0.5f : 1f;
        float scaleX = 1, scaleY = 1, offsetX = 0, offsetY = 0;
        if (cropWidth < bufferWidth)
        {
            offsetX = (crop.left + inset) / bufferWidth;
            scaleX = (cropWidth - 2 * inset) / bufferWidth;
        }
        if (cropHeight < bufferHeight)
        {
            offsetY = (crop.top + inset) / bufferHeight;
            scaleY = (cropHeight - 2 * inset) / bufferHeight;
        }
        return new VideoTextureRect(scaleX, scaleY, offsetX, offsetY);
    }

    /// <summary>Makes the texture on the thread that owns the context.</summary>
    private bool EnsureAttached()
    {
        if (attached)
            return !unusable;

        OrphanedTextures.Drain();
        display = eglGetCurrentDisplay();
        if (display == null)
        {
            AppLog.Warning("Android.Video", "The graphics context has no display to wrap pictures for.");
            return false;
        }

        sourceTexture = CreateExternalTexture();
        if (sourceTexture == 0)
        {
            AppLog.Warning("Android.Video", "The graphics backend would not make an external texture.");
            return false;
        }

        attached = true;
        if (!preserveYuv)
            return true;

        if (TryCreateExternalYuvCopy(width, height, out yuvCopy, out string declined))
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

        // Held for the whole teardown: a draw in flight must not find the reader deleted between
        // the check that says it is alive and the call that reads it.
        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;

            if (reader != null)
            {
                AImageReader_setImageListener(reader, null);
                if (image != null)
                {
                    eglDestroyImageKHR(display, image);
                    image = null;
                }

                if (picture != null)
                {
                    AImage_delete(picture);
                    picture = null;
                }

                // Deleting the reader stops its callback thread before returning, so the handle
                // outlives every callback; the window belongs to the reader.
                AImageReader_delete(reader);
                reader = null;
            }

            nativeWindow = IntPtr.Zero;
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

            if (self.IsAllocated)
                self.Free();
            frameAvailable.Dispose();
        }
    }

    private void Warn(string message)
    {
        if (warnedAboutPicture)
            return;
        warnedAboutPicture = true;
        AppLog.Warning("Android.Video", message);
    }

    private static void ThrowIfFailed(media_status_t status, string operation)
    {
        if (status != media_status_t.AMEDIA_OK)
            throw new InvalidOperationException($"The platform could not {operation}: {status}.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnImageAvailable(void* context, AImageReader* reader)
    {
        try
        {
            if (GCHandle.FromIntPtr((nint)context).Target is MediaCodecOutputSurface owner && !owner.disposed)
                owner.frameAvailable.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
