using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Software probe used by the video indexer: reads a container's geometry and turns the decoded
/// preview frame into a compact JPEG. Shared by every platform that has no better native path.
/// </summary>
public static class VideoPreviewFactory
{
    /// <summary>Longest edge of the stored preview, chosen to cover a video tile at 2x scaling.</summary>
    public const int PreviewMaxDimension = 720;

    private const int PreviewJpegQuality = 82;

    /// <summary>
    /// Probes <paramref name="stream"/>, returning null when it carries no video stream. Preview
    /// decoding is best-effort: failures leave <see cref="VideoProbeInfo.Preview"/> null rather than
    /// discarding an otherwise valid video.
    /// </summary>
    public static VideoProbeInfo? Probe(Stream stream, bool withPreview, bool leaveOpen = false)
    {
        VideoProbeResult? probe;
        try
        {
            probe = VideoProbe.Probe(stream, withPreview, PreviewMaxDimension, leaveOpen);
        }
        catch (Exception)
        {
            return null;
        }

        if (probe is null)
        {
            return null;
        }

        return CreateProbeInfo(probe);
    }

    /// <summary>
    /// Probes an input that FFmpeg opened directly, such as a selected DVD title or Blu-ray
    /// playlist. The demuxer remains owned by the caller.
    /// </summary>
    public static VideoProbeInfo? Probe(FfmpegDemuxer demuxer, bool withPreview)
    {
        VideoProbeResult? probe = VideoProbe.Probe(demuxer, withPreview, PreviewMaxDimension);
        return probe is null ? null : CreateProbeInfo(probe);
    }

    private static VideoProbeInfo CreateProbeInfo(VideoProbeResult probe) => CreateProbeInfo(probe, Encode(probe));

    /// <summary>Describes <paramref name="probe"/> with a preview that was already encoded as JPEG elsewhere.</summary>
    public static VideoProbeInfo CreateProbeInfo(VideoProbeResult probe, byte[]? preview)
    {
        return new VideoProbeInfo
        {
            Duration = probe.Duration,
            Width = probe.Width,
            Height = probe.Height,
            FrameRate = probe.FrameRate,
            VideoCodec = probe.VideoCodec,
            VideoStreamIndex = probe.VideoStreamIndex,
            IsStillImage = probe.IsStillImage,
            Title = probe.Title,
            AudioStreams = MapAudioStreams(probe.AudioStreams),
            Preview = preview,
            PreviewWidth = preview is null ? 0 : probe.PreviewWidth,
            PreviewHeight = preview is null ? 0 : probe.PreviewHeight,
            PreviewPosition = probe.PreviewPosition,
        };
    }

    /// <summary>Decodes a photo through the same path used by local tiles and encodes it for transport.</summary>
    public static byte[]? CreatePhotoPreview(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            StillImageThumbnail? thumbnail = PhotoBitmap.DecodeThumbnail(
                stream,
                PreviewMaxDimension,
                leaveOpen: true);
            return thumbnail is null
                ? null
                : EncodeJpeg(thumbnail.Pixels, thumbnail.Width, thumbnail.Height);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Photo", "Could not decode photo preview.", exception);
            return null;
        }
        finally
        {
            if (!leaveOpen)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>Reads scan-time stream facts without requiring a video stream or decoding a frame.</summary>
    public static IReadOnlyList<AudioStreamProbeInfo> ProbeAudioStreams(Stream stream, bool leaveOpen = false)
    {
        try
        {
            return MapAudioStreams(VideoProbe.ProbeAudioStreams(stream, leaveOpen));
        }
        catch (Exception)
        {
            return Array.Empty<AudioStreamProbeInfo>();
        }
    }

    public static AudioStreamProbeInfo[] MapAudioStreams(IReadOnlyList<VideoProbeAudioStreamInfo> streams) =>
        streams.Select(stream => new AudioStreamProbeInfo
        {
            StreamIndex = stream.Index,
            CodecName = stream.CodecName,
        }).ToArray();

    /// <summary>Encodes tightly packed BGRA32 pixels as JPEG, or null when encoding fails.</summary>
    public static byte[]? EncodeJpeg(byte[] bgra, int width, int height)
    {
        if (bgra is null || width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return null;
        }

        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var bitmap = new SKBitmap();
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                if (!bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), width * 4))
                {
                    return null;
                }

                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image?.Encode(SKEncodedImageFormat.Jpeg, PreviewJpegQuality);
                return encoded?.ToArray();
            }
            finally
            {
                handle.Free();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[]? Encode(VideoProbeResult probe)
        => probe.PreviewBgra is null
            ? null
            : EncodeJpeg(probe.PreviewBgra, probe.PreviewWidth, probe.PreviewHeight);
}
