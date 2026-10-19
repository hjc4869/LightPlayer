using System;
using System.IO;
using System.Threading;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.LightPlayer.Services;

internal sealed record EmbeddedPhotoPreview(uint? Item, byte[]? Encoded, int Rotation = 0, bool Mirror = false, int Reduction = 1);

internal static partial class PhotoDecoder
{
    internal static EmbeddedPhotoPreview? FindEmbeddedPreview(Stream stream, PhotoPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (!stream.CanSeek || request.ViewportWidth <= 0 || request.ViewportHeight <= 0)
            return null;
        long origin = stream.Position;
        try
        {
            EmbeddedPhotoPreview? selected = null;
            double distance = double.PositiveInfinity;
            if (FindContainerThumbnail(stream, request, cancellationToken) is { } thumbnail)
            {
                selected = new EmbeddedPhotoPreview(thumbnail.Item, null);
                distance = request.ResolutionDistance(thumbnail.Width, thumbnail.Height);
            }

            byte[]? exif = StillImage.ReadExifThumbnail(stream, out int rotation, out bool mirror, cancellationToken);
            if (exif is { Length: > 0 })
                Consider(exif, rotation, mirror);
            foreach (byte[] jpeg in FindJpegThumbnails(stream, cancellationToken))
                Consider(jpeg, 0, false);

            foreach (DngPreview preview in FindDngPreviews(stream, cancellationToken, includeReduced: true))
            {
                if ((ulong)preview.Width * preview.Height > 16 * 1024 * 1024)
                    continue;
                ExifOrientation.TryDecode((int)preview.Orientation, out rotation, out mirror);
                uint width = rotation % 180 == 0 ? preview.Width : preview.Height;
                uint height = rotation % 180 == 0 ? preview.Height : preview.Width;
                double candidate = request.ResolutionDistance(width, height);
                if (!request.IsBetter(width, height) || candidate >= distance)
                    continue;
                stream.Position = preview.Offset;
                selected = new EmbeddedPhotoPreview(null, ReadBytes(stream, preview.Length), rotation, mirror);
                distance = candidate;
            }
            stream.Position = origin;
            Span<byte> signature = stackalloc byte[4];
            if (stream.ReadAtLeast(signature, signature.Length, false) == signature.Length && JxrDecoder.IsJxr(signature) &&
                stream.Length - origin <= MaxGainMapBytes)
            {
                stream.Position = origin;
                byte[] encoded = ReadBytes(stream, (int)(stream.Length - origin));
                JxrImageInfo info = JxrDecoder.Identify(encoded);
                if (JxrPixelFormat.FromId(info.PixelFormat).ColorModel is not (JxrColorModel.Yuv420 or JxrColorModel.Yuv422))
                {
                    foreach (int reduction in new[] { 4, 16 })
                    {
                        int width = (info.Width + reduction - 1) / reduction;
                        int height = (info.Height + reduction - 1) / reduction;
                        if (info.Orientation >= 4)
                            (width, height) = (height, width);
                        double candidate = request.ResolutionDistance(width, height);
                        if ((long)width * height <= 16 * 1024 * 1024 && request.IsBetter(width, height) && candidate < distance)
                        {
                            selected = new EmbeddedPhotoPreview(null, encoded, Reduction: reduction);
                            distance = candidate;
                        }
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return selected;

            void Consider(byte[] data, int fallbackRotation, bool fallbackMirror)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var encoded = new MemoryStream(data, writable: false);
                (int width, int height) = StillImage.ReadPixelSize(encoded);
                if (width <= 0 || height <= 0 || (long)width * height > 16 * 1024 * 1024)
                    return;
                if (!ExifOrientation.TryRead(encoded, out int turn, out bool flipped))
                {
                    turn = fallbackRotation;
                    flipped = fallbackMirror;
                }
                if (turn % 180 != 0)
                    (width, height) = (height, width);
                double candidate = request.ResolutionDistance(width, height);
                if (request.IsBetter(width, height) && candidate < distance)
                {
                    selected = new EmbeddedPhotoPreview(null, data, turn, flipped);
                    distance = candidate;
                }
            }
        }
        finally { stream.Position = origin; }
    }
}