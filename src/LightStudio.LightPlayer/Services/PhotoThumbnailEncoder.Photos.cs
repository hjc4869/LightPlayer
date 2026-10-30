using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Services;

internal static partial class PhotoThumbnailEncoder
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

    /// <summary>Re-encodes a JPEG XL thumbnail as JPEG, keeping HDR as a gain map.</summary>
    public static async Task<byte[]?> TranscodeToJpegAsync(byte[] thumbnail, int maxDimension, CancellationToken cancellationToken)
    {
        VideoFrame? frame = PhotoDecoder.DecodeThumbnailFrame(thumbnail, maxDimension, cancellationToken);
        return frame is null ? null : await EncodeAsync(frame, maxDimension, cancellationToken, allowJpegXl: false).ConfigureAwait(false);
    }
}