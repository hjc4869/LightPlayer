using System;
using System.Collections.Generic;
using Android.Content;
using LightStudio.FfmpegShim.Decoding;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Android.Video;

/// <summary>
/// Textures left behind when a stream ended somewhere the graphics context was not bound. They are
/// deleted by the next draw that binds it, which is the first one of the stream after.
/// </summary>
internal static class OrphanedTextures
{
    private static readonly List<uint> pending = [];
    private static readonly List<AndroidVideoInterop.ExternalYuvCopy> pendingCopies = [];

    public static void Add(uint texture)
    {
        lock (pending)
            pending.Add(texture);
    }

    public static void Add(AndroidVideoInterop.ExternalYuvCopy copy)
    {
        lock (pending)
            pendingCopies.Add(copy);
    }

    /// <summary>Deletes everything queued. Must run on the thread that owns the context.</summary>
    public static void Drain()
    {
        uint[] textures;
        AndroidVideoInterop.ExternalYuvCopy[] copies;
        lock (pending)
        {
            if (pending.Count == 0 && pendingCopies.Count == 0)
                return;

            textures = pending.ToArray();
            copies = pendingCopies.ToArray();
            pending.Clear();
            pendingCopies.Clear();
        }

        foreach (AndroidVideoInterop.ExternalYuvCopy copy in copies)
            AndroidVideoInterop.DeleteExternalYuvCopy(copy);
        foreach (uint texture in textures)
            AndroidVideoInterop.DeleteTexture(texture);
    }
}

/// <summary>Registers the Android decoder output window with the shared decoding stack.</summary>
internal static class AndroidVideoOutput
{
    public static void Install(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        AndroidVideoInterop.PublishJavaVirtualMachine(context);
        VideoDecoderOutputSurfaces.Factory = (width, height, bitDepth) =>
            MediaCodecOutputSurface.TryCreate(context, width, height, bitDepth);

        AppLog.Information("Android.Video", "MediaCodec may decode into a surface where precision needs it.");
    }
}
