using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LightMediaRenderer.Avalonia.Controls;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Engine;
using LightStudio.FfmpegShim.Media;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.IO;
using SkiaSharp;

namespace LightStudio.LightPlayer.Web;

[SupportedOSPlatform("browser")]
internal sealed partial class BrowserImageDecoder : IImageDecoder
{
    public async Task<FastImageSource?> LoadArtworkAsync(PhotoDecodeRequest request, int pixelSize, CancellationToken cancellationToken)
    {
        if (request.Platform is BrowserMultimediaPlatformIO browser)
            return await browser.LoadArtworkAsync(request.Path, pixelSize, cancellationToken);
        using var content = await request.Platform.GetAlbumCoverBufferAsync(request.Path, cancellationToken);
        return content is null ? null : await DecodeArtworkAsync(content, pixelSize, cancellationToken);
    }

    internal static async Task<FastImageSource?> ReadArtworkAsync(string path, Func<CancellationToken, Task<Stream>>? openSource,
        bool embedded, int pixelSize, CancellationToken cancellationToken)
    {
        using BrowserPicture? picture = await BrowserMediaOperations.DecodeImageAsync(BrowserSessionInput.Describe(path, openSource),
            pixelSize, embedded, MaxArtworkBytes, cancellationToken);
        return picture is null ? null : CreateArtwork(picture);
    }

    private const long MaxArtworkBytes = 32 * 1024 * 1024;

    public Task<VideoFrame?> DecodePhotoAsync(PhotoDecodeRequest request, IProgress<Size>? displaySize, CancellationToken cancellationToken) =>
        DecodePhotoAsync(request.Path, token => request.Platform.OpenMediaFileAsync(request.Path, token), displaySize, cancellationToken,
            request.Preview, request.RenderRaw, request.WholeImage);

    public async Task<StillImageThumbnail?> DecodePhotoThumbnailAsync(PhotoDecodeRequest request, int maxDimension, CancellationToken cancellationToken)
    {
        using BrowserPicture? picture = await BrowserMediaOperations.DecodeImageAsync(
            BrowserSessionInput.Describe(request.Path, token => request.Platform.OpenMediaFileAsync(request.Path, token)),
            maxDimension, embedded: false, maxEncodedBytes: 0, cancellationToken, renderRaw: request.RenderRaw);
        return picture is null
            ? null
            : new StillImageThumbnail { Width = picture.Width, Height = picture.Height, Pixels = picture.Pixels.Span.ToArray() };
    }

    public Task<byte[]?> EncodePhotoThumbnailAsync(PhotoDecodeRequest request, int maxDimension, CancellationToken cancellationToken) =>
        BrowserMediaOperations.EncodePhotoThumbnailAsync(
            BrowserSessionInput.Describe(request.Path, token => request.Platform.OpenMediaFileAsync(request.Path, token)),
            maxDimension, cancellationToken, request.RenderRaw);

    public bool CanDecodeJpegXl => MediaWorkerConnection.CanDecodeJpegXl;

    public async Task<FastImageSource> DecodeArtworkAsync(ArtworkBuffer content, int pixelSize, CancellationToken cancellationToken)
    {
        using BrowserPicture? picture = await DecodeBytesAsync(content.Memory, pixelSize, 0, cancellationToken);
        return picture is null ? throw new InvalidDataException("The artwork contains no image.") : CreateArtwork(picture);
    }

    public async Task<Bitmap?> DecodeBitmapAsync(byte[] content, int? width, BitmapInterpolationMode interpolationMode, CancellationToken cancellationToken)
    {
        using BrowserPicture? picture = await DecodeBytesAsync(content, width ?? BrowserMediaOperations.FullSizeDimension, 0,
            cancellationToken);
        return picture is null ? null : CreateBitmap(picture, AlphaFormat.Unpremul);
    }

    public async Task<Bitmap?> DecodeBackdropAsync(byte[] content, int width, float blurSigma, CancellationToken cancellationToken)
    {
        using BrowserPicture? picture = await DecodeBytesAsync(content, width, blurSigma, cancellationToken);
        return picture is null ? null : CreateBitmap(picture, AlphaFormat.Premul);
    }

    private static Task<BrowserPicture?> DecodeBytesAsync(ReadOnlyMemory<byte> content, int maxDimension, float blurSigma,
        CancellationToken cancellationToken) =>
        BrowserMediaOperations.DecodeImageAsync(new MemoryMediaInput("image", content), maxDimension, embedded: false,
            maxEncodedBytes: 0, cancellationToken, blurSigma);

    private static FastImageSource CreateArtwork(BrowserPicture picture)
    {
        using var pixmap = new SKPixmap(new SKImageInfo(picture.Width, picture.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul),
            picture.Pixels.Address, picture.Width * 4);
        return new FastImageSource(SKImage.FromPixelCopy(pixmap));
    }

    private static Bitmap CreateBitmap(BrowserPicture picture, AlphaFormat alpha) =>
        new(PixelFormat.Bgra8888, alpha, picture.Pixels.Address, new PixelSize(picture.Width, picture.Height),
            new Vector(96, 96), picture.Width * 4);
}
