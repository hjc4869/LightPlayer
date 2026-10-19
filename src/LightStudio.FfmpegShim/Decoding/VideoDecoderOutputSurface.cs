using System;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;

namespace LightStudio.FfmpegShim.Decoding;

/// <summary>
/// A platform window a hardware decoder draws its pictures into, and the texture the renderer reads
/// them back out of.
/// </summary>
/// <remarks>
/// <para>
/// Some decoders will only hand pixels back at full precision if they are given somewhere to draw:
/// Android's MediaCodec copies 8-bit 4:2:0 out to memory whatever the source was, and reaches its
/// own 10-bit output only through a surface. Platforms that can sample the surface as YUV keep those
/// code values intact for the renderer's color pipeline; an ordinary external RGB texture is only
/// sufficient for sources whose depth already fits it.
/// </para>
/// <para>
/// One window serves a whole stream: only the picture being shown is in it at any moment, and the
/// decoder keeps the ones ahead of it as tokens until they are due.
/// </para>
/// </remarks>
public interface IVideoDecoderOutputSurface : IDisposable
{
    /// <summary>The platform object the decoder is to draw into, or zero where it has none.</summary>
    /// <remarks>On Android, a JNI global reference to an <c>android.view.Surface</c>.</remarks>
    IntPtr SurfaceObject { get; }

    /// <summary>The same window as a native handle, or zero.</summary>
    /// <remarks>On Android, an <c>ANativeWindow*</c>.</remarks>
    IntPtr NativeWindow { get; }

    /// <summary>Texture the renderer samples the window's current picture from.</summary>
    long TextureHandle { get; }

    /// <summary>Generation of the graphics context <see cref="TextureHandle"/> belongs to.</summary>
    int TextureGeneration { get; }

    /// <summary>How the renderer must bind <see cref="TextureHandle"/>, as a GL texture target.</summary>
    uint TextureTarget { get; }

    /// <summary>Storage format the renderer must use when it wraps <see cref="TextureHandle"/>.</summary>
    VideoExternalTextureFormat TextureFormat { get; }

    /// <summary>What the channels of <see cref="TextureHandle"/> represent.</summary>
    VideoExternalTextureColorModel TextureColorModel { get; }

    /// <summary>Meaningful source bits retained in each channel.</summary>
    int TextureBitDepth { get; }

    /// <summary>
    /// Shows one decoded picture and leaves it in <see cref="TextureHandle"/>. Runs on the render
    /// thread with the graphics context bound.
    /// </summary>
    /// <param name="picture">
    /// The decoder's token for one picture, whose meaning is between the decoder and this window.
    /// </param>
    /// <param name="rect">Where the picture ended up inside the texture.</param>
    /// <returns>False when the picture did not arrive, which leaves the frame undrawn.</returns>
    bool Acquire(IntPtr picture, out VideoTextureRect rect);
}

/// <summary>
/// Where the application head says that decoders on this platform may draw into a window.
/// </summary>
/// <remarks>
/// Creating one means talking to the platform's graphics stack, which belongs to the head built for
/// that platform. A head that registers nothing leaves every decoder reading its pixels back.
/// </remarks>
public static class VideoDecoderOutputSurfaces
{
    // A picture can go missing while the graphics context is still settling, so the path is only
    // given up on after enough draws in a row have found nothing to show.
    private const int FailureThreshold = 8;

    private static int consecutiveFailures;
    private static string rejection = string.Empty;

    /// <summary>
    /// Opens a window for one stream of the given coded size, or returns null when this platform or
    /// this moment cannot provide one. Set during start-up and read on decode threads.
    /// </summary>
    public static Func<int, int, int, IVideoDecoderOutputSurface> Factory { get; set; }

    public static bool IsAvailable => Factory is not null && Volatile.Read(ref rejection).Length == 0;

    /// <summary>
    /// Why this build stopped decoding into a window, or empty while it still does. Set from the
    /// render thread and read by decoders.
    /// </summary>
    public static string Rejection => Volatile.Read(ref rejection);

    /// <summary>Opens a window, or returns null when none can be had.</summary>
    public static IVideoDecoderOutputSurface TryCreate(
        int width,
        int height,
        int bitDepth,
        out string declined)
    {
        Func<int, int, int, IVideoDecoderOutputSurface> factory = Factory;
        if (factory is null)
        {
            declined = "this platform registered no decoder output window";
            return null;
        }

        string rejected = Rejection;
        if (rejected.Length != 0)
        {
            declined = rejected;
            return null;
        }

        try
        {
            IVideoDecoderOutputSurface surface = factory(width, height, bitDepth);
            declined = surface is null ? "the platform would not open a decoder output window" : string.Empty;
            return surface;
        }
        catch (Exception exception)
        {
            AppLog.Warning("Video.DecoderSurface", "Could not create the decoder output surface.", exception);
            declined = exception.Message;
            return null;
        }
    }

    /// <summary>One picture reached the screen, so whatever went wrong before was passing.</summary>
    public static void NoteAcquired() => Volatile.Write(ref consecutiveFailures, 0);

    /// <summary>
    /// One picture could not be shown. Enough of these in a row and the path is abandoned, which
    /// makes the decoders holding a window ask to be replaced rather than leave the picture blank.
    /// </summary>
    public static void NoteAcquireFailed(string reason)
    {
        if (Interlocked.Increment(ref consecutiveFailures) < FailureThreshold || Rejection.Length != 0)
            return;

        Volatile.Write(
            ref rejection,
            string.IsNullOrEmpty(reason) ? "the decoder's output window would not draw" : reason);
    }
}
