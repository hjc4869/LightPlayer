using System;
using System.Collections.Generic;
using Avalonia.OpenGL;
using Avalonia.Platform;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Rendering;

/// <summary>
/// Turns a decoded surface into textures Skia can sample, for one graphics API's idea of what a
/// surface is.
/// </summary>
/// <remarks>
/// <para>
/// The APIs differ in what has to happen for the decoder's memory to become a texture -- VAAPI
/// exports file descriptors that EGL imports, Direct3D hands over a texture the graphics device
/// already owns -- but nothing above this cares which, so the player's drawing is written once
/// against whichever implementation the application head registered.
/// </para>
/// <para>
/// Every member touches the graphics context and is called on the render thread from inside a draw.
/// </para>
/// </remarks>
public interface IVideoSurfaceImporter
{
    /// <summary>The kind of surface decoders must produce for this to have anything to import.</summary>
    VideoSurfaceKind Kind { get; }

    /// <summary>
    /// The graphics device decoders have to have produced their surfaces with, or zero where this
    /// API can import a surface whatever produced it. Only read once <see cref="IsSupportedBy"/>
    /// has agreed.
    /// </summary>
    IntPtr Device { get; }

    /// <summary>Whether imports can be retained across draws without preventing decoder-pool reuse.</summary>
    bool CacheSurfaces => true;

    /// <summary>
    /// Establishes whether this backend can import surfaces at all, which is the only way to know:
    /// the entry points and extensions involved are a property of the driver rather than of the
    /// platform. Called once per graphics context, before anything else.
    /// </summary>
    bool IsSupportedBy(IPlatformGraphicsContext context);

    /// <summary>
    /// Ensures decoding has finished before every draw, including draws that reuse an import.
    /// Backends whose decoder delivers completed frames need no additional wait.
    /// </summary>
    void WaitForDecode(VideoFrame frame);

    /// <summary>Imports one frame's surface, or returns null when it cannot be sampled.</summary>
    IVideoSurfaceTextures? Import(IPlatformGraphicsContext context, VideoFrame frame);
}

/// <summary>
/// The textures one imported surface became, and whatever the import holds open behind them.
/// </summary>
public interface IVideoSurfaceTextures
{
    /// <summary>Whether the caller must keep the frame alive with a separate GPU read fence.</summary>
    bool RequiresReadFence => true;

    /// <summary>Wraps one plane for Skia, retaining any resources needed until GPU reads finish.</summary>
    SKImage? CreateImage(GRContext context, VideoFrame frame, int index, SKImageInfo info);

    /// <summary>
    /// How large a plane's texture actually is, which is the picture's plane size or more where the
    /// surface was allocated with the padding a codec's coded size carries.
    /// </summary>
    (int Width, int Height) PlaneSize(int index);

    /// <summary>Releases the import's ownership on the render thread; images retain outstanding reads.</summary>
    void Destroy(IPlatformGraphicsContext context);

    /// <summary>
    /// Releases the import but not the textures, which went with the context they were made in.
    /// Called on the render thread with the context that replaced it, on the same display.
    /// </summary>
    void Abandon(IPlatformGraphicsContext context);
}

/// <summary>
/// Where the application head says which graphics APIs' surfaces this build can import.
/// </summary>
/// <remarks>
/// Importing one means talking to the API the frame was decoded by, which is a platform's business
/// rather than the player's; registering it here is what keeps that code in the head built for the
/// platform. A head that registers nothing simply has every frame read back.
/// </remarks>
public static class VideoSurfaceImporterFactory
{
    private static readonly List<IVideoSurfaceImporter> importers = [];

    /// <summary>
    /// The importers to try, in the order they were registered. Read on the render thread once a
    /// graphics context exists, so registering belongs to start-up.
    /// </summary>
    public static IReadOnlyList<IVideoSurfaceImporter> Importers => importers;

    public static void Register(IVideoSurfaceImporter importer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        importers.Add(importer);
    }
}

/// <summary>Texture creation and Skia image wrapping for EGL-backed surface importers.</summary>
public static class VideoSurfaceTextures
{
    public const int GlTexture2D = 0x0DE1;

    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlTextureWrapS = 0x2802;
    private const int GlTextureWrapT = 0x2803;
    private const int GlLinear = 0x2601;
    private const int GlClampToEdge = 0x812F;

    public static SKImage? CreateImage(GRContext context, VideoFrame frame,
        IVideoSurfaceTextures imported, int[] textures, int index, SKImageInfo info)
    {
        if ((uint)index >= (uint)textures.Length)
            return null;

        VideoPlaneTexture description = VideoPlaneTexture.For(frame, index);
        (int width, int height) = imported.PlaneSize(index);
        if (info.ColorType != description.ColorType || width < info.Width || height < info.Height)
            return null;

        var textureInfo = new GRGlTextureInfo(GlTexture2D, (uint)textures[index], (uint)description.GlFormat);
        using var backendTexture = new GRBackendTexture(width, height, false, textureInfo);
        return SKImage.FromTexture(context, backendTexture, GRSurfaceOrigin.TopLeft,
            info.ColorType, info.AlphaType, info.ColorSpace);
    }

    /// <summary>
    /// Creates a texture to attach a plane to and leaves it bound, so the caller can attach it.
    /// </summary>
    public static int CreateBound(GlInterface gl)
    {
        ArgumentNullException.ThrowIfNull(gl);

        int texture = gl.GenTexture();
        gl.BindTexture(GlTexture2D, texture);
        gl.TexParameteri(GlTexture2D, GlTextureMinFilter, GlLinear);
        gl.TexParameteri(GlTexture2D, GlTextureMagFilter, GlLinear);
        gl.TexParameteri(GlTexture2D, GlTextureWrapS, GlClampToEdge);

        // Chroma is sampled at half the luma's rate, so the edge texels are read past the plane;
        // clamping is what keeps the last column from wrapping round to the first.
        gl.TexParameteri(GlTexture2D, GlTextureWrapT, GlClampToEdge);
        return texture;
    }

    public static void Delete(GlInterface gl, int[] textures)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(textures);

        for (int i = 0; i < textures.Length; i++)
        {
            if (textures[i] != 0)
            {
                gl.DeleteTexture(textures[i]);
                textures[i] = 0;
            }
        }
    }
}
