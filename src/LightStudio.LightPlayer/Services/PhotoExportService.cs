using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.FfmpegShim.ImageEncoding;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services.PhotoExport;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Services;

/// <summary>Saves photos in another format or size, and lists the FFmpeg still-image encoders that can.</summary>
public interface IPhotoExportService
{
    /// <summary>Encoders for AVIF, HEIC and JPEG XL; formats without one are not offered.</summary>
    Task<IReadOnlyList<StillImageEncoderInfo>> GetEncodersAsync();

    /// <summary>Decodes a photo again at full resolution and encodes it. The caller disposes the returned file.</summary>
    Task<Stream> ExportAsync(IClientMediaResolver mediaResolver, MediaReference media, bool? renderRaw,
        PhotoExportOptions options, CancellationToken cancellationToken);
}

/// <summary>Decodes and encodes on this process's thread pool.</summary>
internal sealed class InProcessPhotoExportService : IPhotoExportService
{
    public Task<IReadOnlyList<StillImageEncoderInfo>> GetEncodersAsync() => StillImageEncoders.ProbeAsync();

    public async Task<Stream> ExportAsync(IClientMediaResolver mediaResolver, MediaReference media, bool? renderRaw,
        PhotoExportOptions options, CancellationToken cancellationToken)
    {
        IMultiMediaPlatformIO platform = await mediaResolver.ResolvePlatformAsync(media, cancellationToken).ConfigureAwait(false);
        return await PhotoExporter.ExportAsync(
            token => ReadMetadataAsync(platform, media.Path, token),
            token => ImageDecoding.DecodePhotoAsync(
                new PhotoDecodeRequest(platform, media.Path) { RenderRaw = renderRaw, WholeImage = true }, null, token),
            options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PhotoExportSourceMetadata?> ReadMetadataAsync(IMultiMediaPlatformIO platform, string path,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await platform.OpenMediaFileAsync(path, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            if (stream.CanSeek)
                return PhotoExporter.ReadMetadata(stream, path, cancellationToken);
            using var buffered = new MemoryStream();
            stream.CopyTo(buffered);
            buffered.Position = 0;
            return PhotoExporter.ReadMetadata(buffered, path, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }
}
