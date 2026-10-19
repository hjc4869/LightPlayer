using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.Database;

namespace LightStudio.LocalServices.Photos;

internal static class PhotoMetadataReader
{
    internal static async Task<PhotoMetadata> ReadAsync(Stream stream, PhotoMetadata photo, CancellationToken cancellationToken)
    {
        var exif = await ExifMetadata.ReadPhotoAsync(stream, cancellationToken).ConfigureAwait(false);
        return ApplyExif(photo, exif, exif.Width, exif.Height);
    }

    internal static PhotoMetadata Read(Stream stream, PhotoMetadata photo)
    {
        if (!stream.CanSeek)
        {
            using var buffered = new MemoryStream();
            stream.CopyTo(buffered);
            buffered.Position = 0;
            return Read(buffered, photo);
        }

        var exif = new ExifPhotoMetadata();
        int width = 0;
        int height = 0;
        long origin = stream.Position;
        try
        {
            exif = ExifMetadata.ReadPhoto(stream);
            width = exif.Width;
            height = exif.Height;
            stream.Position = 0;
            (int pixelWidth, int pixelHeight) = StillImage.ReadPixelSize(stream);
            if (pixelWidth > 0 && pixelHeight > 0)
                (width, height) = (pixelWidth, pixelHeight);
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            AppLog.Warning("PhotoIndex", $"Could not read photo metadata for {photo.Path}.", exception);
        }
        finally
        {
            stream.Position = origin;
        }
        return ApplyExif(photo, exif, width, height);
    }

    private static PhotoMetadata ApplyExif(PhotoMetadata photo, ExifPhotoMetadata exif, int width, int height)
    {
        return photo with
        {
            Width = Math.Max(0, width),
            Height = Math.Max(0, height),
            DateTaken = exif.DateTaken,
            CameraMake = exif.CameraMake,
            CameraModel = exif.CameraModel,
            Lens = exif.Lens,
            ExposureSeconds = exif.ExposureSeconds,
            Aperture = exif.Aperture,
            FocalLength = exif.FocalLength,
            Iso = exif.Iso,
            Orientation = exif.Orientation,
            Latitude = exif.Latitude,
            Longitude = exif.Longitude,
            Altitude = exif.Altitude,
            Description = exif.Description,
            Artist = exif.Artist,
            Copyright = exif.Copyright,
        };
    }
}