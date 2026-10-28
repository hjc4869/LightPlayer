using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;

namespace LightStudio.LightPlayer.Services.PhotoExport;

/// <summary>The gain map math of a source photo, as <see cref="PhotoGainMap"/> describes it.</summary>
internal sealed class PhotoExportGainParameters
{
    public PhotoExportGainParameters(PhotoGainMap gainMap)
    {
        Minimum = gainMap.Minimum;
        Maximum = gainMap.Maximum;
        Gamma = gainMap.Gamma;
        OffsetSdr = gainMap.OffsetSdr;
        OffsetHdr = gainMap.OffsetHdr;
        CapacityMin = gainMap.CapacityMin;
        CapacityMax = gainMap.CapacityMax;
        AppleHeadroom = gainMap.IsApple ? MathF.Pow(2, gainMap.CapacityMax) : 0;
        BaseIsHdr = gainMap.BaseIsHdr;
        ToBase = gainMap.ToBase.ToArray();
        ToBt2020 = gainMap.ToBt2020.ToArray();
    }

    public Vector3 Minimum { get; }
    public Vector3 Maximum { get; }
    public Vector3 Gamma { get; }
    public Vector3 OffsetSdr { get; }
    public Vector3 OffsetHdr { get; }
    public float CapacityMin { get; }
    public float CapacityMax { get; }
    public float AppleHeadroom { get; }
    public bool BaseIsHdr { get; }
    public float[] ToBase { get; }
    public float[] ToBt2020 { get; }

    /// <summary>
    /// Adapts a BT.2020 base color with an encoded gain sample; weight 0 gives the SDR rendition and
    /// 1 the HDR rendition. Keep in sync with Shaders/VideoPhotoGainMap.cs in the renderer.
    /// </summary>
    public Vector3 Apply(Vector3 standard, Vector3 encoded, float coverage, float weight)
    {
        float[] m = ToBase;
        var value = new Vector3(
            m[0] * standard.X + m[1] * standard.Y + m[2] * standard.Z,
            m[3] * standard.X + m[4] * standard.Y + m[5] * standard.Z,
            m[6] * standard.X + m[7] * standard.Y + m[8] * standard.Z);
        encoded = Vector3.Clamp(encoded, Vector3.Zero, Vector3.One);
        Vector3 logarithm;
        if (AppleHeadroom > 0)
        {
            logarithm = new Vector3(
                MathF.Log2(1 + (AppleHeadroom - 1) * MathF.Pow(encoded.X, 1.961f)),
                MathF.Log2(1 + (AppleHeadroom - 1) * MathF.Pow(encoded.Y, 1.961f)),
                MathF.Log2(1 + (AppleHeadroom - 1) * MathF.Pow(encoded.Z, 1.961f)));
        }
        else
        {
            logarithm = new Vector3(
                Minimum.X + (Maximum.X - Minimum.X) * MathF.Pow(encoded.X, 1 / Gamma.X),
                Minimum.Y + (Maximum.Y - Minimum.Y) * MathF.Pow(encoded.Y, 1 / Gamma.Y),
                Minimum.Z + (Maximum.Z - Minimum.Z) * MathF.Pow(encoded.Z, 1 / Gamma.Z));
        }

        logarithm *= coverage;
        if (BaseIsHdr)
            value = (value + OffsetHdr) * Exp2(-logarithm) - OffsetSdr;
        if (weight > 0)
            value = (Vector3.Max(value, Vector3.Zero) + OffsetSdr) * Exp2(logarithm * weight) - OffsetHdr;
        m = ToBt2020;
        return new Vector3(
            m[0] * value.X + m[1] * value.Y + m[2] * value.Z,
            m[3] * value.X + m[4] * value.Y + m[5] * value.Z,
            m[6] * value.X + m[7] * value.Y + m[8] * value.Z);

        static Vector3 Exp2(Vector3 v) => new(MathF.Pow(2, v.X), MathF.Pow(2, v.Y), MathF.Pow(2, v.Z));
    }
}

/// <summary>Rows of a source image as linear BT.2020 RGBA light with straight alpha, rendered on request.</summary>
internal interface IPhotoExportRows : IDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>Writes <paramref name="count"/> rows from <paramref name="top"/> as single floats.</summary>
    /// <param name="stride">Distance between target rows, in floats.</param>
    void Render(int top, int count, IntPtr target, int stride, CancellationToken cancellationToken);
}

/// <summary>Converts CPU pixels of a decoded frame with the renderer's scene-light pipeline, owning the frame.</summary>
internal sealed class PhotoExportFrameRows(VideoFrame frame) : IPhotoExportRows
{
    public int Width => frame.Width;

    public int Height => frame.Height;

    public void Render(int top, int count, IntPtr target, int stride, CancellationToken cancellationToken) =>
        StillImage.RenderScene(frame, 0, top, frame.Width, count, target, stride, cancellationToken);

    public void Dispose() => frame.Release();
}

/// <summary>Rows of an image already in linear light, owning the image.</summary>
internal sealed unsafe class PhotoExportImageRows(LinearImage image) : IPhotoExportRows
{
    public int Width => image.Width;

    public int Height => image.Height;

    public void Render(int top, int count, IntPtr target, int stride, CancellationToken cancellationToken)
    {
        for (int row = 0; row < count; row++)
            Buffer.MemoryCopy(image.Row(top + row), (float*)target + (long)row * stride, (long)image.Width * 16, (long)image.Width * 16);
    }

    public void Dispose() => image.Dispose();
}

/// <summary>Encoded 8- or 16-bit gain samples, as values from 0 to 1 with opaque alpha.</summary>
internal sealed unsafe class PhotoExportGainSampleRows : IPhotoExportRows
{
    private readonly PhotoGainMap gainMap;

    public PhotoExportGainSampleRows(PhotoGainMap gainMap) => this.gainMap = gainMap.AddRef();

    public int Width => gainMap.Width;

    public int Height => gainMap.Height;

    public void Render(int top, int count, IntPtr target, int stride, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> pixels = gainMap.Pixels.Span;
        ReadOnlySpan<ushort> wide = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(pixels);
        bool sixteen = gainMap.BitsPerComponent == 16;
        int components = gainMap.Width * 4;
        for (int row = 0; row < count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float* output = (float*)target + (long)row * stride;
            int first = (top + row) * components;
            for (int index = 0; index < components; index++)
                output[index] = index % 4 == 3 ? 1f : sixteen ? wide[first + index] / 65535f : pixels[first + index] / 255f;
        }
    }

    public void Dispose() => gainMap.Release();
}

/// <summary>A decoded source photo as rows of linear light, before orientation and resizing.</summary>
internal sealed class PhotoExportInput : IDisposable
{
    public required IPhotoExportRows Base { get; init; }

    /// <summary>Encoded gain samples of a gain-map photo.</summary>
    public IPhotoExportRows? Gain { get; init; }

    public PhotoExportGainParameters? GainParameters { get; init; }

    public required PhotoExportSourceKind Kind { get; init; }

    public required int Rotation { get; init; }

    public required bool Mirror { get; init; }

    public required double PixelAspectRatio { get; init; }

    /// <summary>Peak luminance the source declares, in nits, or zero when it declares none.</summary>
    public required double DeclaredPeakNits { get; init; }

    /// <inheritdoc cref="PhotoExportColor.SourceGamuts"/>
    public required float[][] SourceGamuts { get; init; }

    /// <summary>Stored size of the base image.</summary>
    public int Width => Base.Width;

    public int Height => Base.Height;

    public void Dispose()
    {
        Base.Dispose();
        Gain?.Dispose();
    }
}

/// <summary>A source photo placed on the output canvas, ready for rendering.</summary>
internal sealed class PhotoExportScene : IDisposable
{
    /// <summary>BT.2020 linear light: nits for absolute sources, relative otherwise.</summary>
    public required LinearImage Canvas { get; init; }

    /// <summary>Encoded gain samples placed like <see cref="Canvas"/>, for gain-map sources.</summary>
    public LinearImage? Gain { get; init; }

    public PhotoExportGainParameters? GainParameters { get; init; }

    public required PhotoExportSourceKind Kind { get; init; }

    public required double DeclaredPeakNits { get; init; }

    /// <inheritdoc cref="PhotoExportColor.SourceGamuts"/>
    public required float[][] SourceGamuts { get; init; }

    /// <summary>Colors of the source pixels the output shows.</summary>
    public required PhotoExportGamutStatistics Statistics { get; init; }

    /// <summary>How many canvas pixels each pixel of a created gain map covers along each axis.</summary>
    public required int GainScale { get; init; }

    public void Dispose()
    {
        Canvas.Dispose();
        Gain?.Dispose();
    }
}

/// <summary>
/// Opens a decoded photo, including tiled, GPU-backed and gain-map frames, as full-resolution rows of
/// linear light. Absolute-luminance photos are kept in nits; others put SDR white at 1.
/// </summary>
internal static class PhotoExportSource
{
    private const int TileBatch = 8;

    public static PhotoExportSourceKind Classify(VideoFrame frame) =>
        frame.GainMap is not null ? PhotoExportSourceKind.GainMap
        : frame.DolbyVision.IsPresent || frame.ColorTransfer is VideoColorTransfer.Pq or VideoColorTransfer.Hlg
            ? PhotoExportSourceKind.Absolute
            : PhotoExportSourceKind.Sdr;

    /// <param name="overviewDimension">
    /// Longest edge the rows need; tile sources with a scaled overview decode it reduced to no less than this.
    /// Zero keeps the full resolution.
    /// </param>
    public static async Task<PhotoExportInput> OpenAsync(VideoFrame frame, CancellationToken cancellationToken,
        int overviewDimension = 0)
    {
        if (frame.PhotoAnimation is not null)
            throw new PhotoExportException(PhotoExportFailure.Unsupported);
        IPhotoExportRows image = await OpenRowsAsync(frame, cancellationToken, overviewDimension).ConfigureAwait(false);
        IPhotoExportRows? gain = null;
        try
        {
            if (frame.GainMap is { } gainMap)
                gain = gainMap.Samples is { } samples
                    ? await OpenRowsAsync(samples, cancellationToken, overviewDimension).ConfigureAwait(false)
                    : new PhotoExportGainSampleRows(gainMap);
            return CreateInput(frame, image, gain);
        }
        catch
        {
            image.Dispose();
            gain?.Dispose();
            throw;
        }
    }

    /// <summary>Opens a frame whose pixels and gain samples are already in memory, without tiles.</summary>
    public static PhotoExportInput Open(VideoFrame frame)
    {
        if (frame.PhotoAnimation is not null || frame.PhotoTiles is not null || NeedsDownload(frame) ||
            frame.GainMap?.Samples is not null)
            throw new ArgumentException("The frame's pixels are not in memory.", nameof(frame));
        var image = new PhotoExportFrameRows(frame.AddRef());
        PhotoExportGainSampleRows? gain = null;
        try
        {
            if (frame.GainMap is { } gainMap)
                gain = new PhotoExportGainSampleRows(gainMap);
            return CreateInput(frame, image, gain);
        }
        catch
        {
            image.Dispose();
            gain?.Dispose();
            throw;
        }
    }

    private static PhotoExportInput CreateInput(VideoFrame frame, IPhotoExportRows image, IPhotoExportRows? gain) => new()
    {
        Base = image,
        Gain = gain,
        GainParameters = frame.GainMap is { } parameters ? new PhotoExportGainParameters(parameters) : null,
        Kind = Classify(frame),
        Rotation = ((frame.Rotation % 360) + 360) % 360,
        Mirror = frame.MirrorHorizontally,
        PixelAspectRatio = frame.PixelAspectRatio > 0 && double.IsFinite(frame.PixelAspectRatio) ? frame.PixelAspectRatio : 1,
        DeclaredPeakNits = double.IsFinite(frame.HdrPeakLuminance) ? Math.Max(frame.HdrPeakLuminance, 0) : 0,
        SourceGamuts = PhotoExportColor.SourceGamuts(frame),
    };

    /// <summary>
    /// Rows of a frame. Tile sources that can decode the whole picture at once do so, which avoids
    /// decoding the data in front of every region again; others are assembled tile by tile.
    /// </summary>
    private static async Task<IPhotoExportRows> OpenRowsAsync(VideoFrame frame, CancellationToken cancellationToken,
        int overviewDimension)
    {
        if (frame.PhotoTiles is not { } tiles)
            return await OwnRowsAsync(frame.AddRef(), cancellationToken).ConfigureAwait(false);

        var whole = new PhotoTileBounds(0, 0, frame.Width, frame.Height);
        int? index = tiles.HasScaledOverview && tiles.GetBounds(-1) == whole ? -1
            : tiles.Count == 1 && tiles.GetBounds(0) == whole ? 0
            : null;
        if (index is { } wholeIndex)
        {
            int reduction = 1;
            while (wholeIndex < 0 && overviewDimension > 0 && reduction < 8 &&
                Math.Max(frame.Width, frame.Height) / (reduction * 2) >= overviewDimension)
                reduction *= 2;
            VideoFrame[] decoded = await tiles.DecodeBatchAsync(new[] { new PhotoTileRequest(wholeIndex, reduction) }, cancellationToken)
                .ConfigureAwait(false);
            if (decoded is [{ } pixels] && (reduction > 1 || pixels.Width == frame.Width && pixels.Height == frame.Height))
                return await OwnRowsAsync(pixels, cancellationToken).ConfigureAwait(false);
            foreach (VideoFrame tile in decoded)
                tile?.Release();
        }

        return new PhotoExportImageRows(await RasterizeTilesAsync(frame, tiles, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Takes ownership of <paramref name="pixels"/>, reading GPU-resident pixels back into memory.</summary>
    private static async Task<IPhotoExportRows> OwnRowsAsync(VideoFrame pixels, CancellationToken cancellationToken)
    {
        try
        {
            if (!NeedsDownload(pixels))
            {
                var rows = new PhotoExportFrameRows(pixels);
                pixels = null!;
                return rows;
            }

            return new PhotoExportFrameRows(await DownloadAsync(pixels, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            pixels?.Release();
        }
    }

    private static async Task<LinearImage> RasterizeTilesAsync(VideoFrame frame, IPhotoTileSource tiles,
        CancellationToken cancellationToken)
    {
        var image = new LinearImage(frame.Width, frame.Height);
        try
        {
            int batch = Math.Max(TileBatch, tiles.PreferredBatchSize);
            for (int first = 0; first < tiles.Count; first += batch)
            {
                var requests = new PhotoTileRequest[Math.Min(batch, tiles.Count - first)];
                for (int index = 0; index < requests.Length; index++)
                    requests[index] = new PhotoTileRequest(first + index, 1);
                VideoFrame[] decoded = await tiles.DecodeBatchAsync(requests, cancellationToken).ConfigureAwait(false);
                var downloaded = new VideoFrame?[decoded.Length];
                try
                {
                    for (int index = 0; index < decoded.Length; index++)
                    {
                        if (NeedsDownload(decoded[index]))
                            downloaded[index] = await DownloadAsync(decoded[index], cancellationToken).ConfigureAwait(false);
                    }

                    int start = first;
                    await Task.Run(() => Parallel.For(0, decoded.Length,
                        new ParallelOptions { CancellationToken = cancellationToken },
                        index => RasterizeTile(downloaded[index] ?? decoded[index], tiles.GetBounds(start + index), image,
                            cancellationToken)), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    foreach (VideoFrame tile in decoded)
                        tile?.Release();
                    foreach (VideoFrame? tile in downloaded)
                        tile?.Release();
                }
            }

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static unsafe void RasterizeTile(VideoFrame pixels, PhotoTileBounds bounds, LinearImage image,
        CancellationToken cancellationToken)
    {
        int left = Math.Max(bounds.X, 0), top = Math.Max(bounds.Y, 0);
        int right = Math.Min(bounds.X + bounds.Width, image.Width), bottom = Math.Min(bounds.Y + bounds.Height, image.Height);
        if (right <= left || bottom <= top)
            return;
        if (pixels.Width != bounds.Width || pixels.Height != bounds.Height)
            throw new InvalidDataException($"A {bounds.Width}x{bounds.Height} photo tile decoded as {pixels.Width}x{pixels.Height}.");
        StillImage.RenderScene(pixels, left - bounds.X, top - bounds.Y, right - left, bottom - top,
            (IntPtr)(image.Row(top) + (long)left * 4), image.Stride, cancellationToken);
    }

    private static bool NeedsDownload(VideoFrame frame) =>
        frame.PhotoSurface is not null || frame.HasSurface || frame.Format == VideoPixelFormat.ExternalTexture;

    private static async Task<VideoFrame> DownloadAsync(VideoFrame frame, CancellationToken cancellationToken)
    {
        VideoFrame pixels = await frame.DownloadPhotoAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The photo's GPU pixels could not be read back.");
        if (NeedsDownload(pixels) || pixels.PhotoTiles is not null)
        {
            pixels.Release();
            throw new InvalidDataException("The photo's GPU pixels were not read back into memory.");
        }

        pixels.PhotoColor ??= frame.PhotoColor;
        if (pixels.PhotoColor == frame.PhotoColor)
        {
            pixels.ColorPrimaries = frame.ColorPrimaries;
            pixels.ColorTransfer = frame.ColorTransfer;
        }

        return pixels;
    }
}
