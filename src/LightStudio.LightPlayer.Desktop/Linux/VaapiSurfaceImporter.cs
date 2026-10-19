#if LINUX
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using LightMediaRenderer.Avalonia.Rendering;
using SkiaSharp;

namespace LightStudio.LightPlayer.Desktop.Linux;

/// <summary>
/// Imports VAAPI surfaces, without the pixels ever reaching CPU memory.
/// </summary>
/// <remarks>
/// <para>
/// The surface is exported as one DRM PRIME file descriptor per plane and imported through
/// <c>EGL_EXT_image_dma_buf_import</c>, which lands the decoder's own memory in a texture rather
/// than copying it. Avalonia already binds every entry point this needs, including <c>eglCreateImageKHR</c>
/// on its <see cref="EglInterface"/> and <c>glEGLImageTargetTexture2DOES</c> on its
/// <see cref="GlInterface"/>, so nothing here talks to EGL directly.
/// </para>
/// <para>
/// It follows that this only works where Avalonia renders through EGL: Wayland always does, X11
/// only when asked to. A backend that cannot leaves the decoders reading their frames back.
/// </para>
/// </remarks>
internal sealed class VaapiSurfaceImporter : IVideoSurfaceImporter
{
    private const int EglExtensions = 0x3055;
    private const int EglNone = 0x3038;
    private const int EglTrue = 1;
    private const int EglWidth = 0x3057;
    private const int EglHeight = 0x3056;
    private const int EglLinuxDmaBuf = 0x3270;
    private const int EglLinuxDrmFourcc = 0x3271;
    private const int EglImagePreserved = 0x30D2;

    // Plane attributes are contiguous per plane, except the modifiers, which were added later.
    private static readonly int[] PlaneFd = [0x3272, 0x3275, 0x3278, 0x3440];
    private static readonly int[] PlaneOffset = [0x3273, 0x3276, 0x3279, 0x3441];
    private static readonly int[] PlanePitch = [0x3274, 0x3277, 0x327A, 0x3442];
    private static readonly int[] PlaneModifierLow = [0x3443, 0x3445, 0x3447, 0x3449];
    private static readonly int[] PlaneModifierHigh = [0x3444, 0x3446, 0x3448, 0x344A];

    private const ulong ModifierInvalid = 0x00FF_FFFF_FFFF_FFFF;

    /// <summary>Offers this build's VAAPI import to the player's renderer.</summary>
    public static void Register() => VideoSurfaceImporterFactory.Register(new VaapiSurfaceImporter());

    public VideoSurfaceKind Kind => VideoSurfaceKind.Vaapi;

    /// <summary>Any VADisplay's surfaces can be exported, so the decoder keeps its own.</summary>
    public IntPtr Device => IntPtr.Zero;

    public bool IsSupportedBy(IPlatformGraphicsContext context)
    {
        if (context is not EglContext egl || !VaapiLibrary.IsAvailable)
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
        if (!extensions.Contains("EGL_EXT_image_dma_buf_import", StringComparison.Ordinal))
        {
            Debug.WriteLine("Decoder surfaces need EGL_EXT_image_dma_buf_import, which this display lacks.");
            return false;
        }

        return true;
    }

    public void WaitForDecode(VideoFrame frame) =>
        VaapiLibrary.SyncSurface(frame.Surface.Device, (uint)frame.Surface.Id);

    public IVideoSurfaceTextures? Import(IPlatformGraphicsContext context, VideoFrame frame)
    {
        if (context is not EglContext egl)
            return null;

        var surface = (uint)frame.Surface.Id;
        IntPtr display = frame.Surface.Device;

        if (!VaapiLibrary.TryExportPlanes(display, surface, out DrmPrimeSurface exported))
            return null;

        try
        {
            int planeCount = frame.PlaneCount;
            if (exported.LayerCount < planeCount)
                return null;

            var imported = new ImportedSurface(new IntPtr[planeCount], new int[planeCount], new (int, int)[planeCount]);
            for (int plane = 0; plane < planeCount; plane++)
            {
                VideoPlaneTexture description = VideoPlaneTexture.For(frame, plane);
                ref DrmPrimeLayer layer = ref exported.Layer(plane);
                if (!description.Accepts(layer.DrmFormat))
                {
                    Debug.WriteLine(
                        $"Video surface plane {plane} exported as 0x{layer.DrmFormat:X8}, " +
                        $"expected 0x{description.DrmFormat:X8}.");
                    imported.Destroy(egl);
                    return null;
                }

                IntPtr image = CreateImage(egl, ref exported, ref layer, description);
                if (image == IntPtr.Zero)
                {
                    imported.Destroy(egl);
                    return null;
                }

                imported.Images[plane] = image;
                imported.Textures[plane] = BindTexture(egl, image);
                imported.Sizes[plane] = (description.Width, description.Height);
            }

            return imported;
        }
        finally
        {
            exported.CloseDescriptors();
        }
    }

    private static IntPtr CreateImage(
        EglContext egl,
        ref DrmPrimeSurface exported,
        ref DrmPrimeLayer layer,
        VideoPlaneTexture description)
    {
        var attributes = new List<int>(24)
        {
            EglWidth, description.Width,
            EglHeight, description.Height,
            EglLinuxDrmFourcc, (int)layer.DrmFormat,
            EglImagePreserved, EglTrue,
        };

        int subPlanes = Math.Min((int)layer.PlaneCount, 4);
        for (int i = 0; i < subPlanes; i++)
        {
            ref DrmPrimeObject entry = ref exported.Object((int)layer.ObjectIndex(i));
            attributes.Add(PlaneFd[i]);
            attributes.Add(entry.FileDescriptor);
            attributes.Add(PlaneOffset[i]);
            attributes.Add((int)layer.Offset(i));
            attributes.Add(PlanePitch[i]);
            attributes.Add((int)layer.Pitch(i));

            if (entry.Modifier != ModifierInvalid)
            {
                attributes.Add(PlaneModifierLow[i]);
                attributes.Add((int)(entry.Modifier & 0xFFFF_FFFF));
                attributes.Add(PlaneModifierHigh[i]);
                attributes.Add((int)(entry.Modifier >> 32));
            }
        }

        attributes.Add(EglNone);

        return egl.EglInterface.CreateImageKHR(
            egl.Display.Handle,
            IntPtr.Zero,
            EglLinuxDmaBuf,
            IntPtr.Zero,
            attributes.ToArray());
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

        // The image was created with the plane's own width and height, so it has no padding.
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
