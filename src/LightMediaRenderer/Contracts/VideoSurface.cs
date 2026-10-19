using System;

namespace LightMediaRenderer.Contracts;

/// <summary>Where a decoded frame's samples live when they never entered CPU memory.</summary>
public enum VideoSurfaceKind
{
    /// <summary>The frame owns its planes; nothing outside it is involved.</summary>
    None,

    /// <summary>
    /// A VAAPI surface still held by the decoder's frame pool, exportable as DRM PRIME file
    /// descriptors and importable into a graphics API from there.
    /// </summary>
    Vaapi,

    /// <summary>
    /// One slice of a Direct3D 11 texture array still held by the decoder's frame pool. The texture
    /// belongs to the graphics device the renderer draws with, so it needs no export to be sampled.
    /// </summary>
    Direct3D11,

    /// <summary>A CoreVideo pixel buffer backed by an IOSurface, importable into Metal.</summary>
    VideoToolbox,
}

/// <summary>Storage used by a texture a frame producer lends to the renderer.</summary>
public enum VideoExternalTextureFormat
{
    Rgba8,
    Rgba16Float,
}

/// <summary>What the channels of an external texture represent.</summary>
public enum VideoExternalTextureColorModel
{
    Rgb,

    /// <summary>Normalized Y, Cb, and Cr code values in the R, G, and B channels.</summary>
    Yuv,
}

/// <summary>
/// Names a decoded frame that is still in graphics memory, so a renderer able to import it can draw
/// it without the readback the frame would otherwise need.
/// </summary>
/// <remarks>
/// The producer keeps the surface alive for as long as the frame holds a reference, so the handle
/// stays valid until the frame is released and no longer afterwards.
/// </remarks>
/// <param name="Kind">Which graphics API the handle belongs to.</param>
/// <param name="Device">
/// The API's device or display handle; <c>VADisplay</c> for VAAPI, <c>ID3D11Device*</c> for Direct3D.
/// Zero for VideoToolbox, whose pixel buffers are independent of the rendering device.
/// </param>
/// <param name="Id">
/// The API's identifier for the surface itself; <c>VASurfaceID</c> for VAAPI,
/// <c>ID3D11Texture2D*</c> for Direct3D, <c>CVPixelBufferRef</c> for VideoToolbox.
/// </param>
/// <param name="Index">
/// Which element of <paramref name="Id"/> the frame occupies, for APIs that decode into an array of
/// surfaces rather than into one surface each. Always zero for VAAPI and VideoToolbox.
/// </param>
/// <param name="Pool">
/// Numbers the pool the surface was allocated from, since the identifiers of a pool that has been
/// freed are handed out again by the next one. A renderer keeping what it made of a surface has to
/// tell the two apart; nothing else about the value means anything.
/// </param>
public readonly record struct VideoSurfaceHandle(
    VideoSurfaceKind Kind,
    IntPtr Device,
    long Id,
    long Index = 0,
    long Pool = 0)
{
    public static VideoSurfaceHandle None { get; }

    public bool IsValid => Kind == VideoSurfaceKind.VideoToolbox
        ? Id != 0
        : Kind != VideoSurfaceKind.None && Device != IntPtr.Zero;
}

/// <summary>
/// Where a picture sits inside the texture holding it, in normalized texture coordinates.
/// </summary>
/// <remarks>
/// A negative scale names an axis the texture runs the other way along, which is how a platform
/// window whose origin is at the bottom is read the right way up.
/// </remarks>
/// <param name="ScaleX">Width of the picture as a fraction of the texture's.</param>
/// <param name="ScaleY">Height of the picture as a fraction of the texture's.</param>
/// <param name="OffsetX">Texture coordinate the picture's left edge sits at.</param>
/// <param name="OffsetY">Texture coordinate the picture's top edge sits at.</param>
public readonly record struct VideoTextureRect(
    float ScaleX,
    float ScaleY,
    float OffsetX,
    float OffsetY)
{
    /// <summary>The whole texture, read in the order it is stored.</summary>
    public static VideoTextureRect Whole { get; } = new(1, 1, 0, 0);

    public bool IsWhole => this == Whole;

    /// <summary>
    /// Reads the rectangle out of the 4x4 column-major matrix a platform window describes its
    /// buffer with, such as Android's <c>SurfaceTexture</c>.
    /// </summary>
    /// <remarks>
    /// Such a matrix takes the coordinate GL would sample a quad with, whose vertical axis runs
    /// from the bottom of the picture upwards, while the renderer works from the top down.
    /// Substituting <c>1 - y</c> into the matrix's own row is what turns the one into the other;
    /// the horizontal axis agrees between them and passes straight through. A rotation is no
    /// rectangle and is read as if the diagonal were the whole of the matrix.
    /// </remarks>
    public static VideoTextureRect FromWindowTransform(ReadOnlySpan<float> transform)
    {
        if (transform.Length < 16)
            return Whole;

        float scaleX = transform[0];
        float scaleY = transform[5];
        return scaleX == 0 || scaleY == 0
            ? Whole
            : new VideoTextureRect(scaleX, -scaleY, transform[12], scaleY + transform[13]);
    }

    /// <summary>Whether a window transform turns the picture, which a rectangle cannot carry.</summary>
    public static bool IsRotated(ReadOnlySpan<float> transform) =>
        transform.Length >= 16 && (transform[1] != 0 || transform[4] != 0);
}
