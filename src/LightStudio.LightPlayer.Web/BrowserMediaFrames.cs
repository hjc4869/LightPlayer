using System.Runtime.Versioning;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal static class BrowserMediaFrames
{
    /// <summary>
    /// Wraps a frame a media worker decoded. Its pixels stay where the worker put them and are uploaded
    /// to the renderer's textures from there.
    /// </summary>
    internal static VideoFrame CreateVideo(BrowserFrameInfo info, MediaStreamInfo? geometry = null)
    {
        if (info.Resource == 0)
            throw new InvalidDataException("The media worker sent a frame that was not retained for the renderer.");
        var format = info.Delivery == "webcodecs-texture" ? VideoPixelFormat.ExternalTexture : info.Format?.ToLowerInvariant() switch
        {
            "yuv420p" or "yuvj420p" or "i420" => VideoPixelFormat.Yuv420p,
            "yuv422p" or "yuvj422p" or "i422" => VideoPixelFormat.Yuv422p,
            "yuv444p" or "yuvj444p" or "i444" => VideoPixelFormat.Yuv444p,
            "yuv420p10le" or "i420p10" => VideoPixelFormat.Yuv420p10,
            "yuv422p10le" or "i422p10" => VideoPixelFormat.Yuv422p10,
            "yuv444p10le" or "i444p10" => VideoPixelFormat.Yuv444p10,
            "yuv420p12le" or "i420p12" => VideoPixelFormat.Yuv420p12,
            "yuv422p12le" or "i422p12" => VideoPixelFormat.Yuv422p12,
            "yuv444p12le" or "i444p12" => VideoPixelFormat.Yuv444p12,
            "yuv420p16le" or "i420p16" => VideoPixelFormat.Yuv420p16,
            "yuv422p16le" or "i422p16" => VideoPixelFormat.Yuv422p16,
            "yuv444p16le" or "i444p16" => VideoPixelFormat.Yuv444p16,
            "nv12" => VideoPixelFormat.Nv12, "nv21" => VideoPixelFormat.Nv21,
            "p010le" => VideoPixelFormat.P010, "p012le" => VideoPixelFormat.P012, "p016le" => VideoPixelFormat.P016,
            null or "bgra" => VideoPixelFormat.Bgra32,
            "rgba64le" or "gbrap16le" => VideoPixelFormat.Rgba64,
            _ => throw new NotSupportedException($"Unsupported browser plane format: {info.Format}"),
        };
        var frame = VideoFrame.CreateHosted(info.Width, info.Height, format);
        var planes = new BrowserVideoPlanes(info.Resource, frame.PlaneCount, format == VideoPixelFormat.Bgra32, info.Format == "gbrap16le");
        if (format == VideoPixelFormat.ExternalTexture)
        {
            frame.ExternalHandleGeneration = VideoFrameCapabilities.ExternalTextureGeneration;
            frame.AcquireExternalTexture = planes.AcquireExternalTexture;
            frame.AttachResource(new MediaResourceLease(new MediaResourceHandle("browser-video", info.Resource.ToString(),
                frame.ExternalHandleGeneration), checked(info.Width * info.Height * 4), planes));
        }
        else frame.PlaneSource = planes;
        frame.Pts = info.Pts;
        frame.Duration = info.Duration;
        frame.HasAlpha = info.HasAlpha;
        frame.Serial = info.Serial;
        frame.Rotation = info.Rotation;
        frame.MirrorHorizontally = info.Mirror;
        frame.PixelAspectRatio = info.PixelAspect > 0 ? info.PixelAspect : geometry?.PixelAspectRatio ?? 1;
        frame.IsKeyFrame = info.Key;
        frame.FieldOrder = VideoFieldOrders.FromFfmpeg(info.FieldOrder);
        frame.ChromaLocation = info.Chroma is >= 1 and <= 6 ? (VideoChromaLocation)(info.Chroma - 1) : VideoChromaLocation.Left;
        frame.ColorSpace = info.Matrix switch
        {
            9 or 10 => VideoColorSpace.Bt2020, 1 => VideoColorSpace.Bt709,
            5 or 6 or 7 => VideoColorSpace.Bt601, _ => info.Height >= 720 ? VideoColorSpace.Bt709 : VideoColorSpace.Bt601,
        };
        frame.HdrPeakLuminance = info.Peak;
        frame.HdrMasteringLuminance = info.MaximumLuminance > 0 ? new VideoLuminance(info.MinimumLuminance, info.MaximumLuminance) : null;
        frame.ColorRange = format == VideoPixelFormat.Bgra32 || info.Range == 2 ? VideoColorRange.Full : VideoColorRange.Limited;
        frame.ColorPrimaries = info.Primaries switch
        {
            4 => VideoColorPrimaries.Bt470M, 5 => VideoColorPrimaries.Bt470Bg, 6 or 7 => VideoColorPrimaries.Smpte170M,
            9 => VideoColorPrimaries.Bt2020, 11 => VideoColorPrimaries.DciP3, 12 => VideoColorPrimaries.DisplayP3,
            _ => frame.ColorSpace == VideoColorSpace.Bt2020 ? VideoColorPrimaries.Bt2020 : VideoColorPrimaries.Bt709,
        };
        frame.ColorTransfer = info.Transfer switch
        {
            8 => VideoColorTransfer.Linear, 13 => VideoColorTransfer.Srgb, 16 => VideoColorTransfer.Pq,
            18 => VideoColorTransfer.Hlg, _ => VideoColorTransfer.Bt709,
        };

        // The worker decoder already applied what these imply to the color fields above.
        if (info.Hdr10PlusMetadata is { Length: > 0 } hdr10Plus && !VideoFrameMetadataCodec.TryRead(hdr10Plus, frame.Hdr10Plus))
            throw new InvalidDataException("The media worker sent unreadable HDR10+ metadata.");
        if (info.DolbyVisionMetadata is { Length: > 0 } dolbyVision && !VideoFrameMetadataCodec.TryRead(dolbyVision, frame.DolbyVision))
            throw new InvalidDataException("The media worker sent unreadable Dolby Vision metadata.");
        if (info.Enhancement is { } enhancement)
            frame.AttachEnhancementLayer(CreateVideo(enhancement));
        return frame;
    }
}

/// <summary>A frame as a media worker describes it, after the page retained its pixels as <see cref="Resource"/>.</summary>
internal sealed record BrowserFrameInfo(int Kind, int Serial, int Length, double Pts, double Duration, int Width, int Height,
    int Primaries, int Transfer, int Rotation, bool Mirror, double PixelAspect, bool Key,
    int FieldOrder, int Matrix, int Range, int Chroma, double Peak, double MinimumLuminance, double MaximumLuminance,
    string? Format, int Resource, string? Delivery, BrowserFrameInfo? Enhancement)
{
    public bool HasAlpha { get; init; }

    /// <summary>The pixel format WebCodecs decoded to, before the frame was converted for upload.</summary>
    public string? SourceFormat { get; init; }

    /// <summary>The worker decoder that produced the frame.</summary>
    public int Output { get; init; }

    /// <summary>Where a sound frame lies in the worker's memory while its batch is delivered.</summary>
    public double Address { get; init; }

    /// <summary>Parsed HDR10+ metadata in <see cref="VideoFrameMetadataCodec"/> form.</summary>
    public byte[]? Hdr10PlusMetadata { get; init; }

    /// <summary>Parsed Dolby Vision metadata in <see cref="VideoFrameMetadataCodec"/> form.</summary>
    public byte[]? DolbyVisionMetadata { get; init; }
}
