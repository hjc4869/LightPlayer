#nullable enable
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics;
using Android.Runtime;
using LightMediaRenderer.Contracts;
using LightStudio.Logging;
using LightStudio.NativeBindings;
using LightStudio.NativeBindings.Types;
using AndroidBitmapInfo = LightStudio.NativeBindings.Types.AndroidBitmapInfo;
using SkiaSharp;
using static LightStudio.LightPlayer.Android.Video.AndroidVideoInterop;

namespace LightStudio.LightPlayer.Android.Photos;

/// <summary>
/// A hardware bitmap's buffer, held natively for the photo's lifetime and wrapped as an image of
/// the renderer's context on each import.
/// </summary>
[Include("android/bitmap.h", Access = BindingFlags.Public)]
internal sealed unsafe partial class AndroidPhotoSurface : IPhotoSurface
{
    private static int maximumDimension = 16384;
    private readonly AndroidPhotoStorage owner;
    private readonly AHardwareBuffer* buffer;
    private readonly uint format;
    private int disposed;

    public AndroidPhotoSurface(AndroidPhotoStorage owner, Bitmap bitmap)
    {
        this.owner = owner;
        AHardwareBuffer* native = null;
        if (AndroidBitmap_getHardwareBuffer((JNINativeInterface**)JNIEnv.Handle, (void*)bitmap.Handle, &native) != ANDROID_BITMAP_RESULT_SUCCESS ||
            native == null)
            throw new NotSupportedException("The photo has no hardware buffer.");
        AHardwareBuffer_Desc description;
        AHardwareBuffer_describe(native, &description);
        if (description.format is not (AHARDWAREBUFFER_FORMAT_R8G8B8A8_UNORM or AHARDWAREBUFFER_FORMAT_R8G8B8X8_UNORM or
            AHARDWAREBUFFER_FORMAT_R16G16B16A16_FLOAT or AHARDWAREBUFFER_FORMAT_R10G10B10A2_UNORM or AHARDWAREBUFFER_FORMAT_R8_UNORM))
        {
            AHardwareBuffer_release(native);
            throw new NotSupportedException($"Unsupported photo hardware format {description.format}.");
        }
        buffer = native;
        format = description.format;
        Width = (int)description.width;
        Height = (int)description.height;
        SizeInBytes = checked((long)description.stride * description.height *
            (format == AHARDWAREBUFFER_FORMAT_R16G16B16A16_FLOAT ? 8 : format == AHARDWAREBUFFER_FORMAT_R8_UNORM ? 1 : 4));
        owner.AddRef();
    }

    public static int MaximumDimension => Volatile.Read(ref maximumDimension);
    public int Width { get; }
    public int Height { get; }
    public long SizeInBytes { get; }
    public Task WhenReleased => owner.WhenReleased;
    public int BitDepth => format == AHARDWAREBUFFER_FORMAT_R16G16B16A16_FLOAT ? 16 : format == AHARDWAREBUFFER_FORMAT_R10G10B10A2_UNORM ? 10 : 8;

    /// <summary>Reads a bitmap's geometry and storage flags.</summary>
    public static AndroidBitmapInfo ReadInfo(Bitmap bitmap)
    {
        AndroidBitmapInfo info;
        if (AndroidBitmap_getInfo((JNINativeInterface**)JNIEnv.Handle, (void*)bitmap.Handle, &info) != ANDROID_BITMAP_RESULT_SUCCESS)
            throw new InvalidOperationException("The platform did not describe the photo bitmap.");
        return info;
    }

    public static bool IsHardware(in AndroidBitmapInfo info) => (info.flags & unchecked((uint)ANDROID_BITMAP_FLAGS_IS_HARDWARE)) != 0;

    public static bool IsOpaque(in AndroidBitmapInfo info) => (info.flags & ANDROID_BITMAP_FLAGS_ALPHA_MASK) == ANDROID_BITMAP_FLAGS_ALPHA_OPAQUE;

    public SKImage? TryImport(GRContext context, Func<IDisposable>? makeCurrent)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (context.IsAbandoned || makeCurrent is null)
            return null;
        Volatile.Write(ref maximumDimension, Math.Min(MaximumDimension, context.MaxTextureSize));
        if (Width > context.MaxTextureSize || Height > context.MaxTextureSize)
            return null;
        void* display = eglGetCurrentDisplay();
        void* graphics = eglGetCurrentContext();
        if (display == null || graphics == null)
            return null;

        void* image = CreateImage(display, buffer);
        if (image == null)
            return null;
        uint texture = 0;
        owner.AddRef();
        var import = new Imported(owner, context, makeCurrent, display, graphics, image);
        try
        {
            glGenTextures(1, &texture);
            import.Texture = texture;
            glBindTexture(GL_TEXTURE_2D, texture);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
            glEGLImageTargetTexture2DOES(GL_TEXTURE_2D, image);
            uint error = glGetError();
            if (texture == 0 || error != GL_NO_ERROR)
                throw new InvalidOperationException($"Could not import the photo texture: GL error 0x{error:X}.");
            (uint internalFormat, SKColorType colorType) = format switch
            {
                AHARDWAREBUFFER_FORMAT_R8G8B8X8_UNORM => ((uint)GL_RGB8, SKColorType.Rgb888x),
                AHARDWAREBUFFER_FORMAT_R16G16B16A16_FLOAT => ((uint)GL_RGBA16F, SKColorType.RgbaF16),
                AHARDWAREBUFFER_FORMAT_R10G10B10A2_UNORM => ((uint)GL_RGB10_A2, SKColorType.Rgba1010102),
                AHARDWAREBUFFER_FORMAT_R8_UNORM => ((uint)GL_R8, SKColorType.Gray8),
                _ => ((uint)GL_RGBA8, SKColorType.Rgba8888),
            };
            using var backend = new GRBackendTexture(Width, Height, false,
                new GRGlTextureInfo((uint)GL_TEXTURE_2D, texture, internalFormat));
            SKImage? result = SKImage.FromTexture(context, backend, GRSurfaceOrigin.TopLeft, colorType,
                SKAlphaType.Opaque, null, static state => ((Imported)state).Dispose(), import);
            if (result is null)
                import.Dispose();
            return result;
        }
        catch
        {
            import.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        AHardwareBuffer_release(buffer);
        owner.Release();
    }

    private sealed class Imported(AndroidPhotoStorage owner, GRContext context, Func<IDisposable> makeCurrent,
        void* display, void* graphics, void* image) : IDisposable
    {
        private int disposed;
        public uint Texture;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            try
            {
                bool available = context.Handle != IntPtr.Zero && !context.IsAbandoned;
                using IDisposable? current = available ? makeCurrent() : null;
                if (available && eglGetCurrentContext() == graphics && Texture != 0)
                {
                    uint texture = Texture;
                    glDeleteTextures(1, &texture);
                }
            }
            catch (Exception exception)
            {
                AppLog.Warning("Photo.Rendering", "Could not release a platform photo import.", exception);
            }
            finally
            {
                try { eglDestroyImageKHR(display, image); }
                finally { owner.Release(); }
            }
        }
    }
}
