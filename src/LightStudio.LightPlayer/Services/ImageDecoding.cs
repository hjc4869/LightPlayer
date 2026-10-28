using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LightMediaRenderer.Avalonia.Controls;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using LightStudio.Logging;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.LightPlayer.Services;

public sealed record PhotoDecodeRequest(IMultiMediaPlatformIO Platform, string Path)
{
    public PhotoPreviewRequest? Preview { get; init; }
    public bool? RenderRaw { get; init; }

    /// <summary>
    /// Decodes the whole picture for processing rather than display: container tile grids are assembled
    /// in parallel instead of becoming tile sources, and display-only measurements are skipped.
    /// </summary>
    public bool WholeImage { get; init; }
}

/// <summary>
/// Borrows inputs until each operation completes and transfers ownership of returned images.
/// Completion, including cancellation, acknowledges that input access and decode work have ended.
/// </summary>
public interface IImageDecoder
{
    async Task<FastImageSource?> LoadArtworkAsync(PhotoDecodeRequest request, int pixelSize, CancellationToken cancellationToken)
    {
        using var content = await request.Platform.GetAlbumCoverBufferAsync(request.Path, cancellationToken).ConfigureAwait(false);
        return content is null ? null : await DecodeArtworkAsync(content, pixelSize, cancellationToken).ConfigureAwait(false);
    }

    Task<VideoFrame?> DecodePhotoAsync(PhotoDecodeRequest request, IProgress<Size>? displaySize, CancellationToken cancellationToken);
    Task<StillImageThumbnail?> DecodePhotoThumbnailAsync(PhotoDecodeRequest request, int maxDimension, CancellationToken cancellationToken);

    /// <summary>
    /// Encodes an upright thumbnail that fits <paramref name="maxDimension"/>, or returns null for no picture. Decoders
    /// that support it keep wide-gamut color, gain maps and HDR; others encode sRGB JPEG.
    /// </summary>
    Task<byte[]?> EncodePhotoThumbnailAsync(PhotoDecodeRequest request, int maxDimension, CancellationToken cancellationToken);
    Task<FastImageSource> DecodeArtworkAsync(ArtworkBuffer content, int pixelSize, CancellationToken cancellationToken);
    Task<Bitmap?> DecodeBitmapAsync(byte[] content, int? width, BitmapInterpolationMode interpolationMode, CancellationToken cancellationToken);

    /// <summary>
    /// Decodes an encoded thumbnail at least <paramref name="width"/> pixels across where the codec can reduce it.
    /// Decoders that support it keep wide-gamut color, HDR transfer and gain maps for the renderer.
    /// </summary>
    async Task<IImage?> DecodeThumbnailAsync(byte[] content, int width, CancellationToken cancellationToken) =>
        await DecodeBitmapAsync(content, width, BitmapInterpolationMode.MediumQuality, cancellationToken).ConfigureAwait(false);

    /// <summary>Whether <see cref="DecodeThumbnailAsync"/> decodes JPEG XL.</summary>
    bool CanDecodeJpegXl => false;

    /// <summary>
    /// Decodes a picture <paramref name="width"/> pixels across and applies a Gaussian blur with standard deviation
    /// <paramref name="blurSigma"/> at that width, as a premultiplied bitmap.
    /// </summary>
    Task<Bitmap?> DecodeBackdropAsync(byte[] content, int width, float blurSigma, CancellationToken cancellationToken);
}

public static class ImageDecoding
{
    public static async Task<FastImageSource?> LoadArtworkAsync(
        PhotoDecodeRequest request, int pixelSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelSize);
        cancellationToken.ThrowIfCancellationRequested();
        var image = await Decoder.LoadArtworkAsync(request, pixelSize, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            image?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return image;
    }

    private static IImageDecoder decoder = new InProcessImageDecoder();

    public static IImageDecoder Decoder
    {
        get => decoder;
        set => decoder = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static async Task<VideoFrame?> DecodePhotoAsync(
        PhotoDecodeRequest request, IProgress<Size>? displaySize = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VideoFrame? frame = await Decoder.DecodePhotoAsync(request, displaySize, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            Task? released = frame?.PhotoSurface?.WhenReleased;
            frame?.Release();
            if (released is not null)
                await released.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return frame;
    }

    public static async Task<StillImageThumbnail?> DecodePhotoThumbnailAsync(
        PhotoDecodeRequest request, int maxDimension, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDimension);
        cancellationToken.ThrowIfCancellationRequested();
        StillImageThumbnail? thumbnail = await Decoder.DecodePhotoThumbnailAsync(request, maxDimension, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return thumbnail;
    }

    public static async Task<byte[]?> EncodePhotoThumbnailAsync(
        PhotoDecodeRequest request, int maxDimension, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDimension);
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? encoded = await Decoder.EncodePhotoThumbnailAsync(request, maxDimension, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return encoded;
    }

    public static async Task<FastImageSource> DecodeArtworkAsync(
        ArtworkBuffer content, int pixelSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelSize);
        cancellationToken.ThrowIfCancellationRequested();
        FastImageSource image = await Decoder.DecodeArtworkAsync(content, pixelSize, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            image.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return image;
    }

    public static async Task<Bitmap?> DecodeBitmapAsync(
        byte[]? content, int? width = null, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.MediumQuality,
        CancellationToken cancellationToken = default)
    {
        if (width is { } requestedWidth)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedWidth);
        cancellationToken.ThrowIfCancellationRequested();
        if (content is not { Length: > 0 })
            return null;
        Bitmap? bitmap = await Decoder.DecodeBitmapAsync(content, width, interpolationMode, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            bitmap?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return bitmap;
    }

    public static bool CanDecodeJpegXl => Decoder.CanDecodeJpegXl;

    public static async Task<IImage?> DecodeThumbnailAsync(byte[]? content, int width, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        cancellationToken.ThrowIfCancellationRequested();
        if (content is not { Length: > 0 })
            return null;
        IImage? image = await Decoder.DecodeThumbnailAsync(content, width, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            (image as IDisposable)?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return image;
    }

    public static async Task<Bitmap?> DecodeBackdropAsync(
        byte[]? content, int width, float blurSigma, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegative(blurSigma);
        cancellationToken.ThrowIfCancellationRequested();
        if (content is not { Length: > 0 })
            return null;
        Bitmap? bitmap = await Decoder.DecodeBackdropAsync(content, width, blurSigma, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            bitmap?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return bitmap;
    }
}

internal sealed class InProcessImageDecoder : IImageDecoder
{
    public Task<VideoFrame?> DecodePhotoAsync(
        PhotoDecodeRequest request, IProgress<Size>? displaySize, CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            using Stream stream = await request.Platform.OpenMediaFileAsync(request.Path, cancellationToken).ConfigureAwait(false);
            if (displaySize is not null)
            {
                try
                {
                    Size size = PhotoDecoder.ReadDisplaySize(stream, cancellationToken);
                    if (size.Width > 0 && size.Height > 0)
                        displaySize.Report(size);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    AppLog.Warning("Photo", $"Could not read image dimensions for {request.Path}.", exception);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            VideoFrame? frame = request.Preview is { } preview
                ? PhotoDecoder.DecodePreview(stream, preview, cancellationToken)
                : PhotoDecoder.Decode(stream, cancellationToken, request.RenderRaw, request.Path, useTiles: !request.WholeImage);
            try
            {
                if (!request.WholeImage && frame is { ColorTransfer: VideoColorTransfer.Pq, GainMap: null, PhotoAnimation: null })
                    frame.PhotoPeakLuminance = await MeasurePhotoPeakAsync(frame, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return frame;
            }
            catch
            {
                Task? released = frame?.PhotoSurface?.WhenReleased;
                frame?.Release();
                if (released is not null)
                    await released.ConfigureAwait(false);
                throw;
            }
        },
        cancellationToken);

    private static async ValueTask<double> MeasurePhotoPeakAsync(VideoFrame frame, CancellationToken cancellationToken)
    {
        if (frame.PhotoSurface is not null)
        {
            VideoFrame downloaded = await frame.DownloadPhotoAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Could not download photo luminance samples.");
            try { return await MeasurePhotoPeakAsync(downloaded, cancellationToken).ConfigureAwait(false); }
            finally { downloaded.Release(); }
        }
        if (frame.PhotoTiles is not { } tiles)
            return LightMediaRenderer.Shaders.VideoFramePipeline.MeasurePhotoPeak(frame, cancellationToken);
        double peak = 0;
        for (int index = 0; index < tiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VideoFrame[] decoded = await tiles.DecodeBatchAsync(new[] { new PhotoTileRequest(index, 1) }, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                PhotoTileBounds bounds = tiles.GetBounds(index);
                var region = SkiaSharp.SKRectI.Create(-bounds.X, -bounds.Y, frame.Width, frame.Height);
                foreach (VideoFrame tile in decoded)
                    peak = Math.Max(peak, LightMediaRenderer.Shaders.VideoFramePipeline.MeasurePhotoPeak(tile, cancellationToken, region));
            }
            finally
            {
                foreach (VideoFrame tile in decoded)
                    tile.Release();
            }
        }
        return peak;
    }

    public Task<StillImageThumbnail?> DecodePhotoThumbnailAsync(
        PhotoDecodeRequest request, int maxDimension, CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            using Stream stream = await request.Platform.OpenMediaFileAsync(request.Path, cancellationToken).ConfigureAwait(false);
            if (request.RenderRaw == false && PhotoDecoder.DecodeDngThumbnail(stream, maxDimension, cancellationToken) is { } embedded)
                return embedded;
            return PhotoBitmap.DecodeThumbnail(stream, maxDimension, leaveOpen: true, cancellationToken);
        },
        cancellationToken);

    public Task<byte[]?> EncodePhotoThumbnailAsync(
        PhotoDecodeRequest request, int maxDimension, CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            using Stream stream = await request.Platform.OpenMediaFileAsync(request.Path, cancellationToken).ConfigureAwait(false);
            return await PhotoThumbnailEncoder.EncodePhotoAsync(stream, maxDimension, request.RenderRaw, cancellationToken)
                .ConfigureAwait(false);
        },
        cancellationToken);

    public bool CanDecodeJpegXl => canDecodeJpegXl.Value;

    private static readonly Lazy<bool> canDecodeJpegXl =
        new(() => StillImage.IsDecoderAvailable(LightStudio.NativeBindings.Types.AVCodecID.AV_CODEC_ID_JPEGXL));

    public Task<IImage?> DecodeThumbnailAsync(byte[] content, int width, CancellationToken cancellationToken) => Task.Run<IImage?>(
        () =>
        {
            VideoFrame? frame = PhotoDecoder.DecodeThumbnailFrame(content, width, cancellationToken);
            if (frame is null)
                return BitmapDecoder.DecodeToWidth(content, width, BitmapInterpolationMode.MediumQuality);
            try
            {
                if (frame is { ColorTransfer: VideoColorTransfer.Pq, GainMap: null })
                    frame.PhotoPeakLuminance = LightMediaRenderer.Shaders.VideoFramePipeline.MeasurePhotoPeak(frame, cancellationToken);
                return new PhotoFrameImage(frame);
            }
            finally
            {
                frame.Release();
            }
        },
        cancellationToken);

    public Task<FastImageSource> DecodeArtworkAsync(ArtworkBuffer content, int pixelSize, CancellationToken cancellationToken) =>
        Task.Run(() => ThumbnailDecoder.Decode(content.Span, pixelSize), cancellationToken);

    public Task<Bitmap?> DecodeBitmapAsync(
        byte[] content, int? width, BitmapInterpolationMode interpolationMode, CancellationToken cancellationToken) => Task.Run(
        () =>
        {
            if (width is { } requestedWidth)
                return BitmapDecoder.DecodeToWidth(content, requestedWidth, interpolationMode);
            using var stream = new MemoryStream(content, writable: false);
            return new Bitmap(stream);
        },
        cancellationToken);

    public Task<Bitmap?> DecodeBackdropAsync(byte[] content, int width, float blurSigma, CancellationToken cancellationToken) =>
        Task.Run(() => BitmapDecoder.DecodeBackdrop(content, width, blurSigma), cancellationToken);
}
