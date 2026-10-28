using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim.Demuxing;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Software probe used by the video indexer: reads a container's geometry and encodes the decoded preview
/// frame. Shared by every platform that has no better native path.
/// </summary>
public static class VideoPreviewFactory
{
    /// <summary>Longest edge of the stored preview, chosen to cover a video tile at 2x scaling.</summary>
    public const int PreviewMaxDimension = 720;

    internal const int PreviewJpegQuality = 82;

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
            probe = VideoProbe.Probe(stream, withPreview, PreviewMaxDimension, leaveOpen, retainPreviewFrame: true);
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
        VideoProbeResult? probe = VideoProbe.Probe(demuxer, withPreview, PreviewMaxDimension, retainPreviewFrame: true);
        return probe is null ? null : CreateProbeInfo(probe);
    }

    private static VideoProbeInfo CreateProbeInfo(VideoProbeResult probe) => CreateProbeInfo(probe, Encode(probe));

    /// <summary>Describes <paramref name="probe"/> with a preview that was already encoded elsewhere.</summary>
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

    /// <summary>Encodes a photo's preview for transport through the same path as local tiles.</summary>
    public static async Task<byte[]?> CreatePhotoPreviewAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            return await PhotoThumbnailEncoder.EncodePhotoAsync(stream, PreviewMaxDimension, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Warning("Photo", "Could not decode photo preview.", exception);
            return null;
        }
    }

    /// <summary>Whether an encoded preview is JPEG XL, which stores HDR previews of absolute luminance.</summary>
    public static bool IsJpegXl(byte[] preview) => PhotoDecoder.IsJpegXl(preview);

    /// <summary>
    /// Re-encodes a JPEG XL preview as JPEG, keeping HDR as a gain map, for viewers that cannot decode JPEG XL.
    /// Returns null when the preview cannot be decoded.
    /// </summary>
    public static Task<byte[]?> ConvertToJpegAsync(byte[] preview, CancellationToken cancellationToken) =>
        PhotoThumbnailEncoder.TranscodeToJpegAsync(preview, PreviewMaxDimension, cancellationToken);

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
    {
        if (probe.PreviewFrame is not { } frame)
            return probe.PreviewBgra is null ? null : EncodeJpeg(probe.PreviewBgra, probe.PreviewWidth, probe.PreviewHeight);

        try
        {
            try
            {
                return PhotoThumbnailEncoder.Encode(frame.AddRef(), PreviewMaxDimension, CancellationToken.None);
            }
            catch (Exception exception)
            {
                AppLog.Warning("Preview", "Could not encode a color-managed video preview; encoding it in sRGB.", exception);
            }

            using NativeImageThumbnail thumbnail = StillImage.RenderThumbnail(frame, PreviewMaxDimension);
            return EncodeJpeg(thumbnail.Pixels.GetSpan().ToArray(), thumbnail.Width, thumbnail.Height);
        }
        catch (Exception exception)
        {
            AppLog.Warning("Preview", "Could not render the video preview.", exception);
            return null;
        }
        finally
        {
            frame.Release();
        }
    }
}
