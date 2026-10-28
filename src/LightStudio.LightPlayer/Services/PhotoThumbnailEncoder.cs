using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.ImageEncoding;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Services.PhotoExport;
using LightStudio.Logging;
using LightStudio.NativeBindings.Types;

namespace LightStudio.LightPlayer.Services;

/// <summary>
/// Encodes library thumbnails of photos and video frames with the photo save pipeline. SDR pictures become 8-bit
/// JPEG on the smallest of sRGB, Display P3 and BT.2020 that holds their visible colors; gain-map pictures become
/// JPEG with a gain map at the thumbnail's resolution; absolute-luminance pictures become 16-bit PQ JPEG XL, or JPEG
/// with a gain map where no JPEG XL encoder is available.
/// </summary>
internal static class PhotoThumbnailEncoder
{
    /// <summary>
    /// Encodes the thumbnail of the photo in <paramref name="stream"/>, or returns null when it cannot be decoded.
    /// RAW files and sRGB pictures without HDR use the sRGB thumbnail decoders.
    /// </summary>
    public static async Task<byte[]?> EncodePhotoAsync(Stream stream, int maxDimension, bool? renderRaw,
        CancellationToken cancellationToken)
    {
        if (!stream.CanSeek)
        {
            using var buffered = new MemoryStream();
            await stream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
            buffered.Position = 0;
            return await EncodePhotoAsync(buffered, maxDimension, renderRaw, cancellationToken).ConfigureAwait(false);
        }

        StillImageThumbnail? thumbnail = renderRaw == false
            ? PhotoDecoder.DecodeDngThumbnail(stream, maxDimension, cancellationToken) : null;
        if (thumbnail is null)
        {
            try
            {
                if (PhotoDecoder.DecodeThumbnailSource(stream, maxDimension, cancellationToken) is { } frame)
                    return await EncodeAsync(frame, maxDimension, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLog.Warning("Photo", "Could not encode a color-managed thumbnail; encoding it in sRGB.", exception);
            }

            thumbnail = PhotoBitmap.DecodeThumbnail(stream, maxDimension, leaveOpen: true, cancellationToken);
        }

        return thumbnail is null
            ? null
            : VideoPreviewFactory.EncodeJpeg(thumbnail.Pixels, thumbnail.Width, thumbnail.Height)
                ?? throw new InvalidDataException("The thumbnail could not be encoded.");
    }

    /// <summary>Encodes a thumbnail fitting <paramref name="maxDimension"/>, taking ownership of the frame.</summary>
    /// <param name="allowJpegXl">Whether absolute-luminance pictures may be stored as JPEG XL.</param>
    public static async Task<byte[]> EncodeAsync(VideoFrame frame, int maxDimension, CancellationToken cancellationToken,
        bool allowJpegXl = true)
    {
        PhotoExportInput? input = null;
        try
        {
            StillImageEncoderInfo? jpegXl = allowJpegXl && PhotoExportSource.Classify(frame) == PhotoExportSourceKind.Absolute
                ? await FindJpegXlEncoderAsync().ConfigureAwait(false)
                : null;
            input = await PhotoExportSource.OpenAsync(frame, cancellationToken, maxDimension).ConfigureAwait(false);
            return Encode(input, maxDimension, jpegXl, cancellationToken);
        }
        finally
        {
            input?.Dispose();
            Task? released = frame.PhotoSurface?.WhenReleased;
            frame.Release();
            if (released is not null)
                await released.ConfigureAwait(false);
        }
    }

    /// <summary>Encodes a frame whose pixels are in memory, taking ownership of it.</summary>
    public static byte[] Encode(VideoFrame frame, int maxDimension, CancellationToken cancellationToken)
    {
        try
        {
            StillImageEncoderInfo? jpegXl = PhotoExportSource.Classify(frame) == PhotoExportSourceKind.Absolute
                ? FindJpegXlEncoderAsync().GetAwaiter().GetResult()
                : null;
            using PhotoExportInput input = PhotoExportSource.Open(frame);
            return Encode(input, maxDimension, jpegXl, cancellationToken);
        }
        finally
        {
            frame.Release();
        }
    }

    /// <summary>Re-encodes a JPEG XL thumbnail as JPEG, keeping HDR as a gain map.</summary>
    public static async Task<byte[]?> TranscodeToJpegAsync(byte[] thumbnail, int maxDimension, CancellationToken cancellationToken)
    {
        VideoFrame? frame = PhotoDecoder.DecodeThumbnailFrame(thumbnail, maxDimension, cancellationToken);
        return frame is null ? null : await EncodeAsync(frame, maxDimension, cancellationToken, allowJpegXl: false).ConfigureAwait(false);
    }

    private static byte[] Encode(PhotoExportInput input, int maxDimension, StillImageEncoderInfo? jpegXl,
        CancellationToken cancellationToken)
    {
        var orientation = new PhotoExportOrientation(input.Rotation, input.Mirror, input.PixelAspectRatio, input.Width, input.Height);
        double scale = Math.Min(1, maxDimension / Math.Max(orientation.DisplayWidth, orientation.DisplayHeight));
        var options = new PhotoExportOptions
        {
            Format = jpegXl is null ? PhotoExportFormat.Jpeg : PhotoExportFormat.JpegXl,
            Encoder = jpegXl,
            PixelFormat = jpegXl is null ? PhotoExportPixelFormat.Yuv420 : PhotoExportPixelFormat.Rgb16,
            Quality = VideoPreviewFactory.PreviewJpegQuality,
            Speed = StillEncodeSpeed.Faster,
            Width = Math.Max(1, (int)Math.Round(orientation.DisplayWidth * scale)),
            Height = Math.Max(1, (int)Math.Round(orientation.DisplayHeight * scale)),
            Hdr = jpegXl is null ? PhotoExportHdrEncoding.GainMap : PhotoExportHdrEncoding.Pq,
        };
        using PhotoExportScene scene = PhotoExporter.Place(input, options, cancellationToken, gainScale: 1);
        options = PhotoExporter.ResolveHdr(scene, options);
        PhotoExportGamutStatistics colors = scene.Statistics;
        PhotoExportPrimaries primaries = options.Hdr == PhotoExportHdrEncoding.Pq ||
            colors.OutsideDisplayP3 >= Math.Max(16, colors.Total / 10000) ? PhotoExportPrimaries.Bt2020
            : colors.NeedsWideGamut ? PhotoExportPrimaries.DisplayP3
            : PhotoExportPrimaries.Bt709;
        using PhotoExportPicture picture = PhotoExporter.Render(scene, options, primaries, cancellationToken);
        using var output = new MemoryStream();
        PhotoExportEncoders.Write(output, picture, options, null, cancellationToken);
        return output.ToArray();
    }

    private static async Task<StillImageEncoderInfo?> FindJpegXlEncoderAsync() =>
        (await StillImageEncoders.ProbeAsync().ConfigureAwait(false)).FirstOrDefault(encoder =>
            encoder.Codec == StillImageCodec.JpegXl && encoder.Supports(AVPixelFormat.AV_PIX_FMT_RGB48LE));
}
