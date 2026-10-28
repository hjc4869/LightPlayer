using System.Runtime.Versioning;
using LightStudio.FfmpegShim.ImageEncoding;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Services.PhotoExport;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Web;

/// <summary>Saves photos in the shared media worker, which runs the apps' save pipeline off the page's thread.</summary>
[SupportedOSPlatform("browser")]
internal sealed class BrowserPhotoExportService : IPhotoExportService
{
    public async Task<IReadOnlyList<StillImageEncoderInfo>> GetEncodersAsync()
    {
        try
        {
            return (await BrowserMediaOperations.ListStillImageEncodersAsync()).Select(StillImageEncoderInfo.FromWire).ToArray();
        }
        catch (Exception exception)
        {
            AppLog.Warning("Web.Photo", "Could not list the media worker's still-image encoders.", exception);
            return [];
        }
    }

    public async Task<Stream> ExportAsync(IClientMediaResolver mediaResolver, MediaReference media, bool? renderRaw,
        PhotoExportOptions options, CancellationToken cancellationToken)
    {
        IMultiMediaPlatformIO platform = await mediaResolver.ResolvePlatformAsync(media, cancellationToken);
        NativePayload encoded = await BrowserMediaOperations.ExportPhotoAsync(
            BrowserSessionInput.Describe(media.Path, token => platform.OpenMediaFileAsync(media.Path, token)),
            renderRaw, options, cancellationToken);
        return new PayloadStream(encoded);
    }

    /// <summary>Reads a worker payload and frees it on disposal.</summary>
    private sealed unsafe class PayloadStream : UnmanagedMemoryStream
    {
        private readonly NativePayload payload;

        public PayloadStream(NativePayload payload) : base((byte*)payload.Address, payload.Length) => this.payload = payload;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                payload.Dispose();
        }
    }
}
