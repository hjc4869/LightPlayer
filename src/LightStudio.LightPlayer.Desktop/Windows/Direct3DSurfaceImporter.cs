#if WINDOWS
using System;
using System.Diagnostics;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using LightMediaRenderer.Avalonia.Rendering;
using SkiaSharp;

namespace LightStudio.LightPlayer.Desktop.Windows;

/// <summary>
/// Imports Direct3D 11 surfaces, without the pixels ever reaching CPU memory.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia renders on Windows through ANGLE, which is OpenGL over Direct3D 11, so a decoded
/// texture and the textures Skia draws with are already the same kind of object on the same device.
/// <c>EGL_ANGLE_image_d3d11_texture</c> is what says so: one plane of one slice of the decoder's
/// texture array becomes an EGL image, which <c>glEGLImageTargetTexture2DOES</c> then makes a GL
/// texture of, following the same two steps as VAAPI import over a handle that needs no exporting.
/// </para>
/// <para>
/// The catch is that ANGLE will only accept textures its own device made, so this publishes that
/// device for decoders to open their Direct3D hardware context with. Where the decoder got there
/// first and opened one of its own, its frames are read back as they were before.
/// </para>
/// </remarks>
internal sealed class Direct3DSurfaceImporter : IVideoSurfaceImporter
{
    private const int EglExtensions = 0x3055;
    private const int EglNone = 0x3038;

    // EGL_EXT_device_query, then EGL_ANGLE_device_d3d, which together name the Direct3D device the
    // display draws with.
    private const int EglDeviceExt = 0x322C;
    private const int EglD3D11DeviceAngle = 0x33A1;

    // EGL_ANGLE_image_d3d11_texture, whose plane and slice attributes it shares with
    // EGL_ANGLE_d3d_texture_client_buffer.
    private const int EglD3D11TextureAngle = 0x3484;
    private const int EglD3D11TexturePlaneAngle = 0x3492;
    private const int EglD3D11TextureArraySliceAngle = 0x3493;

    /// <summary>Offers this build's Direct3D import to the player's renderer.</summary>
    public static void Register() => VideoSurfaceImporterFactory.Register(new Direct3DSurfaceImporter());

    public VideoSurfaceKind Kind => VideoSurfaceKind.Direct3D11;

    public IntPtr Device { get; private set; }

    public bool IsSupportedBy(IPlatformGraphicsContext context)
    {
        if (context is not EglContext egl)
            return false;

        EglInterface interop = egl.EglInterface;
        if (!interop.IsCreateImageKHRAvailable ||
            !interop.IsDestroyImageKHRAvailable ||
            !egl.GlInterface.IsEGLImageTargetTexture2DOESAvailable)
        {
            Debug.WriteLine("Decoder surfaces need the EGL image entry points, which are missing here.");
            return false;
        }

        string extensions = interop.QueryString(egl.Display.Handle, EglExtensions) ?? string.Empty;
        if (!extensions.Contains("EGL_ANGLE_image_d3d11_texture", StringComparison.Ordinal))
        {
            Debug.WriteLine("Decoder surfaces need EGL_ANGLE_image_d3d11_texture, which this display lacks.");
            return false;
        }

        if (!interop.IsQueryDisplayAttribExtAvailable || !interop.IsQueryDeviceAttribExtAvailable)
        {
            Debug.WriteLine("Decoder surfaces need the EGL device query entry points, which are missing here.");
            return false;
        }

        if (!interop.QueryDisplayAttribExt(egl.Display.Handle, EglDeviceExt, out IntPtr device) ||
            !interop.QueryDeviceAttribExt(device, EglD3D11DeviceAngle, out IntPtr direct3D) ||
            direct3D == IntPtr.Zero)
        {
            Debug.WriteLine("This display renders through ANGLE but not over Direct3D 11.");
            return false;
        }

        Device = direct3D;
        return true;
    }

    /// <summary>
    /// Decoding and sampling go through the one device, which orders them against each other, so
    /// there is nothing to wait for.
    /// </summary>
    public void WaitForDecode(VideoFrame frame)
    {
    }

    public IVideoSurfaceTextures? Import(IPlatformGraphicsContext context, VideoFrame frame)
    {
        if (context is not EglContext egl)
            return null;

        IntPtr texture = (IntPtr)frame.Surface.Id;
        var slice = (int)frame.Surface.Index;

        int planeCount = frame.PlaneCount;
        var imported = new ImportedSurface(
            new IntPtr[planeCount],
            new int[planeCount],
            new (int, int)[planeCount]);

        for (int plane = 0; plane < planeCount; plane++)
        {
            int[] attributes =
            [
                EglD3D11TexturePlaneAngle, plane,
                EglD3D11TextureArraySliceAngle, slice,
                EglNone,
            ];

            // The image describes the whole surface, whose size ANGLE takes from the texture and
            // halves for the chroma plane, so nothing here gets to state it.
            IntPtr image = egl.EglInterface.CreateImageKHR(
                egl.Display.Handle,
                IntPtr.Zero,
                EglD3D11TextureAngle,
                texture,
                attributes);

            if (image == IntPtr.Zero)
            {
                Debug.WriteLine($"Video surface plane {plane} could not be imported into ANGLE.");
                imported.Destroy(egl);
                return null;
            }

            imported.Images[plane] = image;
            imported.Textures[plane] = BindTexture(egl, image);
            imported.Sizes[plane] = SurfacePlaneSize(frame, plane);
        }

        return imported;
    }

    /// <summary>
    /// How large ANGLE will have made the plane, which follows from the surface rather than from
    /// the picture: a decoder's pool is allocated at the codec's coded size, so the picture can sit
    /// in the corner of something wider and taller than it is.
    /// </summary>
    private static (int Width, int Height) SurfacePlaneSize(VideoFrame frame, int plane)
    {
        int width = Math.Max(frame.SurfaceWidth, frame.Width);
        int height = Math.Max(frame.SurfaceHeight, frame.Height);
        if (plane == 0)
            return (width, height);

        VideoPlanarLayout layout = frame.Layout;
        return (width >> layout.ChromaShiftX, height >> layout.ChromaShiftY);
    }

    private static int BindTexture(EglContext egl, IntPtr image)
    {
        GlInterface gl = egl.GlInterface;
        int texture = VideoSurfaceTextures.CreateBound(gl);
        gl.EGLImageTargetTexture2DOES(VideoSurfaceTextures.GlTexture2D, image);
        gl.BindTexture(VideoSurfaceTextures.GlTexture2D, 0);
        return texture;
    }

    private sealed class ImportedSurface(IntPtr[] images, int[] textures, (int Width, int Height)[] sizes)
        : IVideoSurfaceTextures
    {
        public IntPtr[] Images { get; } = images;

        public int[] Textures { get; } = textures;

        public (int Width, int Height)[] Sizes { get; } = sizes;

        public (int Width, int Height) PlaneSize(int index) => Sizes[index];

        public SKImage? CreateImage(GRContext context, VideoFrame frame, int index, SKImageInfo info) =>
            VideoSurfaceTextures.CreateImage(context, frame, this, Textures, index, info);

        public void Destroy(IPlatformGraphicsContext context)
        {
            var egl = (EglContext)context;
            VideoSurfaceTextures.Delete(egl.GlInterface, Textures);
            Abandon(egl);
        }

        public void Abandon(IPlatformGraphicsContext context)
        {
            var egl = (EglContext)context;
            for (int i = 0; i < Images.Length; i++)
            {
                if (Images[i] != IntPtr.Zero)
                {
                    egl.EglInterface.DestroyImageKHR(egl.Display.Handle, Images[i]);
                    Images[i] = IntPtr.Zero;
                }
            }
        }
    }
}
#endif
