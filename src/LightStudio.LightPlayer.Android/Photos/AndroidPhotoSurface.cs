#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics;
using Android.Hardware;
using Android.Runtime;
using LightMediaRenderer.Contracts;
using LightStudio.Logging;
using SkiaSharp;

namespace LightStudio.LightPlayer.Android.Photos;

internal sealed unsafe partial class AndroidPhotoSurface : IPhotoSurface
{
    private static int maximumDimension = 16384;
    private readonly AndroidPhotoStorage owner;
    private readonly Bitmap bitmap;
    private readonly uint format;
    private int disposed;

    public AndroidPhotoSurface(AndroidPhotoStorage owner, Bitmap bitmap)
    {
        this.owner = owner;
        this.bitmap = bitmap;
        using HardwareBuffer buffer = bitmap.HardwareBuffer ?? throw new NotSupportedException("The photo has no hardware buffer.");
        BufferDescription description;
        try
        {
            IntPtr native = AHardwareBuffer_fromHardwareBuffer(JNIEnv.Handle, buffer.Handle);
            if (native == IntPtr.Zero)
                throw new NotSupportedException("The photo hardware buffer is unavailable.");
            AHardwareBuffer_describe(native, out description);
        }
        finally { buffer.Close(); }
        if (description.Format is not (1 or 2 or 0x16 or 0x2b or 0x38))
            throw new NotSupportedException($"Unsupported photo hardware format {description.Format}.");
        format = description.Format;
        SizeInBytes = checked((long)description.Stride * description.Height * (format == 0x16 ? 8 : format == 0x38 ? 1 : 4));
        owner.AddRef();
    }

    public static int MaximumDimension => Volatile.Read(ref maximumDimension);
    public long SizeInBytes { get; }
    public Task WhenReleased => owner.WhenReleased;
    public int BitDepth => format == 0x16 ? 16 : format == 0x2b ? 10 : 8;

    public SKImage? TryImport(GRContext context, Func<IDisposable>? makeCurrent)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (context.IsAbandoned || makeCurrent is null)
            return null;
        Volatile.Write(ref maximumDimension, Math.Min(MaximumDimension, context.MaxTextureSize));
        if (bitmap.Width > context.MaxTextureSize || bitmap.Height > context.MaxTextureSize)
            return null;
        IntPtr display = eglGetCurrentDisplay();
        IntPtr graphics = eglGetCurrentContext();
        if (display == IntPtr.Zero || graphics == IntPtr.Zero)
            return null;
        var getBuffer = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)eglGetProcAddress("eglGetNativeClientBufferANDROID");
        var createImage = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, int*, IntPtr>)eglGetProcAddress("eglCreateImageKHR");
        var targetImage = (delegate* unmanaged[Cdecl]<uint, IntPtr, void>)eglGetProcAddress("glEGLImageTargetTexture2DOES");
        IntPtr destroyImage = eglGetProcAddress("eglDestroyImageKHR");
        if (getBuffer == null || createImage == null || targetImage == null || destroyImage == IntPtr.Zero)
            return null;

        using HardwareBuffer buffer = bitmap.HardwareBuffer!;
        IntPtr image;
        try
        {
            IntPtr native = AHardwareBuffer_fromHardwareBuffer(JNIEnv.Handle, buffer.Handle);
            int* attributes = stackalloc int[] { 0x30d2, 1, 0x3038 };
            image = createImage(display, IntPtr.Zero, 0x3140, getBuffer(native), attributes);
        }
        finally { buffer.Close(); }
        if (image == IntPtr.Zero)
            return null;
        uint texture = 0;
        owner.AddRef();
        var import = new Imported(owner, context, makeCurrent, display, graphics, image, destroyImage);
        try
        {
            glGenTextures(1, &texture);
            import.Texture = texture;
            glBindTexture(0x0de1, texture);
            glTexParameteri(0x0de1, 0x2801, 0x2601);
            glTexParameteri(0x0de1, 0x2800, 0x2601);
            glTexParameteri(0x0de1, 0x2802, 0x812f);
            glTexParameteri(0x0de1, 0x2803, 0x812f);
            targetImage(0x0de1, image);
            uint error = glGetError();
            if (texture == 0 || error != 0)
                throw new InvalidOperationException($"Could not import the photo texture: GL error 0x{error:X}.");
            (uint internalFormat, SKColorType colorType) = format switch
            {
                2 => (0x8051u, SKColorType.Rgb888x),
                0x16 => (0x881au, SKColorType.RgbaF16),
                0x2b => (0x8059u, SKColorType.Rgba1010102),
                0x38 => (0x8229u, SKColorType.Gray8),
                _ => (0x8058u, SKColorType.Rgba8888),
            };
            using var backend = new GRBackendTexture(bitmap.Width, bitmap.Height, false,
                new GRGlTextureInfo(0x0de1, texture, internalFormat));
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
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            owner.Release();
    }

    private sealed class Imported(AndroidPhotoStorage owner, GRContext context, Func<IDisposable> makeCurrent,
        IntPtr display, IntPtr graphics, IntPtr image, IntPtr destroyImage) : IDisposable
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
                try { ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)destroyImage)(display, image); }
                finally { owner.Release(); }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDescription
    {
        public uint Width, Height, Layers, Format;
        public ulong Usage;
        public uint Stride, Reserved;
        public ulong ReservedLong;
    }

    [LibraryImport("android")] private static partial IntPtr AHardwareBuffer_fromHardwareBuffer(IntPtr environment, IntPtr buffer);
    [LibraryImport("android")] private static partial void AHardwareBuffer_describe(IntPtr buffer, out BufferDescription description);
    [LibraryImport("EGL")] private static partial IntPtr eglGetCurrentDisplay();
    [LibraryImport("EGL")] private static partial IntPtr eglGetCurrentContext();
    [LibraryImport("EGL", StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr eglGetProcAddress(string name);
    [LibraryImport("GLESv2")] private static partial void glGenTextures(int count, uint* texture);
    [LibraryImport("GLESv2")] private static partial void glDeleteTextures(int count, uint* texture);
    [LibraryImport("GLESv2")] private static partial void glBindTexture(uint target, uint texture);
    [LibraryImport("GLESv2")] private static partial void glTexParameteri(uint target, uint parameter, int value);
    [LibraryImport("GLESv2")] private static partial uint glGetError();
}
