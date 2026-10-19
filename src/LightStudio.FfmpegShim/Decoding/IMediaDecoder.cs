using System;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.FfmpegShim.Decoding;

public enum DecoderStatus
{
    /// <summary>A frame was produced.</summary>
    Ok,

    /// <summary>The decoder needs more packets before it can produce another frame.</summary>
    NeedMoreInput,

    /// <summary>The decoder has been drained after an end-of-stream packet.</summary>
    EndOfStream,

    /// <summary>The frame was discarded but decoding can continue (corrupt data, missing packets).</summary>
    Recovered,

    /// <summary>Unrecoverable decoder failure.</summary>
    Fatal,
}

/// <summary>
/// A packet-in / frame-out decoder. Deliberately pull based so the calling thread owns the pacing
/// and no callbacks run on a decoder-internal thread.
/// </summary>
public interface IMediaDecoder<TFrame> : IDisposable where TFrame : class
{
    /// <summary>Stream this decoder consumes packets from.</summary>
    int StreamIndex { get; }

    string CodecName { get; }

    /// <summary>Discards buffered state after a seek. Does not change configuration.</summary>
    void Flush();

    /// <summary>
    /// Cancels decoder-owned waits during terminal teardown. Must be safe to call from outside the
    /// decode thread; no further packets are submitted afterwards.
    /// </summary>
    void CancelPendingOperations()
    {
    }

    /// <summary>
    /// Submits one packet. An end-of-stream packet starts the drain that eventually yields
    /// <see cref="DecoderStatus.EndOfStream"/> from <see cref="Receive"/>.
    /// </summary>
    DecoderStatus Send(MediaPacket packet);

    /// <summary>Pulls the next decoded frame, if one is ready.</summary>
    DecoderStatus Receive(out TFrame frame);
}

public interface IVideoDecoder : IMediaDecoder<VideoFrame>
{
    int Width { get; }

    int Height { get; }

    double PixelAspectRatio { get; }

    /// <summary>
    /// The configuration this decoder settled on, for the diagnostics overlay. A snapshot the
    /// decoder republishes when something changes, so it is safe to read from any thread.
    /// </summary>
    VideoDecoderDiagnostics Diagnostics { get; }

    /// <summary>Whether the decoder can be recreated with the current platform settings.</summary>
    bool CanRestart => Diagnostics.IsHardwareAccelerated;

    /// <summary>
    /// Reopens this stream without hardware acceleration after the device failed. The caller must
    /// feed a new keyframe before expecting output. False when this decoder cannot do it itself, in
    /// which case the caller has to replace it outright.
    /// </summary>
    bool TryFallbackToSoftware();

    /// <summary>
    /// Why this decoder requires replacement by a software decoder, or empty when an ordinary
    /// failure may be retried on the same device.
    /// </summary>
    string RequiredSoftwareFallbackReason => string.Empty;

    /// <summary>
    /// Offers the size the picture is about to be shown at, in physical output pixels, so that a
    /// decoder able to resize on its own device may hand frames over ready to draw. Null withdraws
    /// the offer. A decoder that cannot do it ignores this and nothing else changes.
    /// </summary>
    void RequestOutput(VideoOutputRequest request)
    {
    }
}

/// <summary>
/// What the renderer would like the decoder's device to hand it, in physical output pixels.
/// </summary>
/// <param name="PictureWidth">Width the picture itself should occupy.</param>
/// <param name="PictureHeight">Height the picture itself should occupy.</param>
/// <param name="SurfaceWidth">
/// Width of the whole surface to produce. Larger than the picture means the device is being asked
/// to letterbox it, so what comes back covers the control exactly and can be drawn in one piece.
/// </param>
/// <param name="SurfaceHeight"><inheritdoc cref="SurfaceWidth" path="/summary"/></param>
/// <param name="OffsetX">Where the picture sits inside the surface.</param>
/// <param name="OffsetY"><inheritdoc cref="OffsetX" path="/summary"/></param>
/// <param name="PackedRgb">
/// Whether the device may also convert to RGB, so the picture can be drawn with no color program.
/// Only ask for signals fixed-function conversion describes correctly.
/// </param>
public sealed record VideoOutputRequest(
    int PictureWidth,
    int PictureHeight,
    int SurfaceWidth,
    int SurfaceHeight,
    int OffsetX,
    int OffsetY,
    bool PackedRgb)
{
    /// <summary>A request for the picture alone, with nothing padded around it.</summary>
    public static VideoOutputRequest ForPicture(int width, int height, bool packedRgb) =>
        new(width, height, width, height, 0, 0, packedRgb);

    /// <summary>Whether the device is being asked to pad the picture out to the surface.</summary>
    public bool IsPadded => SurfaceWidth != PictureWidth || SurfaceHeight != PictureHeight;
}

public interface IAudioDecoder : IMediaDecoder<AudioFrame>
{
    /// <summary>Format the decoder resamples to, i.e. the format the audio device was opened with.</summary>
    AudioStreamFormat OutputFormat { get; }

    /// <summary>
    /// Retargets the decoder. <paramref name="speed"/> greater than 1 makes it emit less audio per
    /// second of media, time stretched rather than resampled so the pitch does not move with it.
    /// </summary>
    void Configure(AudioStreamFormat format, double speed);
}
