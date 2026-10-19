using System;
using System.IO;
using System.Numerics;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services;

internal static unsafe partial class PhotoDecoder
{
    private static bool CopyPixels(SKImageInfo sourceInfo, IntPtr sourceAddress, int sourceStride,
        SKBitmap destination, CancellationToken cancellationToken)
    {
        IntPtr targetAddress = destination.GetPixels();
        int targetStride = destination.RowBytes;
        SKImageInfo targetInfo = destination.Info;
        int failed = 0;
        ForRows(sourceInfo.Width, sourceInfo.Height, cancellationToken, (firstRow, lastRow) =>
        {
            int rows = lastRow - firstRow;
            using var pixmap = new SKPixmap(sourceInfo.WithSize(sourceInfo.Width, rows),
                (IntPtr)((byte*)sourceAddress + (long)firstRow * sourceStride), sourceStride);
            if (!pixmap.ReadPixels(targetInfo.WithSize(sourceInfo.Width, rows),
                (IntPtr)((byte*)targetAddress + (long)firstRow * targetStride), targetStride))
            {
                Interlocked.Exchange(ref failed, 1);
            }
        });
        return failed == 0;
    }

    private static bool DecodeGainPixels(SKCodec codec, SKBitmap destination)
    {
        SKCodecResult result = codec.GetPixels(destination.Info, destination.GetPixels(), destination.RowBytes, SKCodecOptions.Default);
        if (result == SKCodecResult.Success)
            return true;
        if (result != SKCodecResult.InvalidConversion)
            return false;
        using var pixels = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        if (codec.GetPixels(pixels.Info, pixels.GetPixels(), pixels.RowBytes, SKCodecOptions.Default) != SKCodecResult.Success)
            return false;
        using SKPixmap source = pixels.PeekPixels();
        return source.ReadPixels(destination.Info, destination.GetPixels(), destination.RowBytes);
    }

    private static VideoFrame? DecodeContainerGainMap(Stream stream, GainMapSource source, CancellationToken cancellationToken,
        bool useTiles = true)
    {
        long origin = stream.Position;
        using var gainStream = source.EncodedGain is { } encoded ? new MemoryStream(encoded, writable: false) : null;
        using SKData? primaryData = source.BaseItem is null ? SKData.Create(stream) : null;
        using SKCodec? primaryCodec = primaryData is null ? null : SKCodec.Create(primaryData);
        using SKData? gainData = source.EncodedGain is { } bytes ? SKData.CreateCopy(bytes) : null;
        using SKCodec? gainCodec = gainData is null ? null : SKCodec.Create(gainData);
        if (primaryCodec is not null && gainCodec is not null)
            return Reconstruct(primaryCodec, gainCodec, source.Metadata, cancellationToken, primaryData, gainData);

        stream.Position = origin;
        if (ReconstructContainerGpu(stream, source, cancellationToken, useTiles) is { } direct)
            return direct;

        StillImagePixels? primary = StillImage.DecodePixels(stream, source.BaseItem, cancellationToken);
        if (primary is null)
            return null;
        stream.Position = origin;
        StillImagePixels? gain = StillImage.DecodePixels(gainStream ?? stream, source.GainItem, cancellationToken);
        if (gain is null)
            return null;

        using SKColorSpace? baseSpace = CreateColorSpace(source.BaseProfile ?? primary.IccProfile,
            primary.ColorPrimaries, primary.ColorTransfer);
        using SKColorSpace? alternateSpace = source.Metadata.UseBaseColorSpace ? null :
            CreateColorSpace(source.MathProfile ?? [], source.MathPrimaries == 0 ? 1 : source.MathPrimaries, 8);
        if (baseSpace is null || (!source.Metadata.UseBaseColorSpace && alternateSpace is null))
            return null;
        using SKColorSpace linearSpace = baseSpace.ToLinearGamma();
        using SKColorSpace mathSpace = (alternateSpace ?? baseSpace).ToLinearGamma();
        if (!mathSpace.ToColorSpaceXyz(out SKColorSpaceXyz sourceXyz))
            return null;
        using var basePixels = new SKBitmap(new SKImageInfo(primary.Width, primary.Height,
            SKColorType.RgbaF16, SKAlphaType.Unpremul, linearSpace));
        using var gainPixels = new SKBitmap(new SKImageInfo(gain.Width, gain.Height,
            SKColorType.Rgba16161616, SKAlphaType.Opaque));
        fixed (ushort* pixels = primary.Pixels)
        {
            if (!CopyPixels(new SKImageInfo(primary.Width, primary.Height,
                SKColorType.Rgba16161616, SKAlphaType.Unpremul, baseSpace),
                (IntPtr)pixels, checked(primary.Width * 8), basePixels, cancellationToken))
                return null;
        }
        for (int row = 0; row < gain.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            gain.Pixels.AsSpan(row * gain.Width * 4, gain.Width * 4)
                .CopyTo(new Span<ushort>((byte*)gainPixels.GetPixels() + (long)row * gainPixels.RowBytes, gain.Width * 4));
        }

        NormalizeBase(basePixels, TransferCode(baseSpace), cancellationToken);
        using SKBitmap? converted = alternateSpace is null ? null : ConvertBase(basePixels, mathSpace);
        if (alternateSpace is not null && converted is null)
            return null;
        return CreateGainMapFrame(converted ?? basePixels, gainPixels, sourceXyz, source.Metadata,
            primary.Rotation, primary.MirrorHorizontally, primary.PixelAspectRatio, cancellationToken);
    }

    private static int TransferCode(SKColorSpace space)
    {
        space.GetNumericalTransferFunction(out SKColorSpaceTransferFn transfer);
        return transfer.G == SKColorSpaceTransferFn.Pq.G ? 16 : transfer.G == SKColorSpaceTransferFn.Hlg.G ? 18 : 0;
    }

    private static void NormalizeBase(SKBitmap pixels, int transfer, CancellationToken cancellationToken)
    {
        if (transfer != 18)
            return;
        for (int row = 0; row < pixels.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = (Half*)((byte*)pixels.GetPixels() + (long)row * pixels.RowBytes);
            for (int column = 0; column < pixels.Width; column++)
            for (int channel = 0; channel < 3; channel++)
            {
                float value = (float)samples[column * 4 + channel];
                samples[column * 4 + channel] = (Half)(MathF.Pow(Math.Max(value / 12, 0), 1.2f) * (1000f / 203));
            }
        }
    }

    private static SKBitmap? ConvertBase(SKBitmap source, SKColorSpace space)
    {
        var result = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.RgbaF16, SKAlphaType.Unpremul, space));
        using SKPixmap pixels = source.PeekPixels();
        if (pixels.ReadPixels(result.Info, result.GetPixels(), result.RowBytes))
            return result;
        result.Dispose();
        return null;
    }

    internal static SKColorSpace? CreateColorSpace(byte[] profile, int primaries, int transfer) =>
        PhotoColorProfile.CreateColorSpace(profile, primaries, transfer);

    private static Vector3 SampleGain(SKBitmap gain, int column, int row, int width, int height)
    {
        float horizontal = Math.Clamp((column + 0.5f) * gain.Width / width - 0.5f, 0, gain.Width - 1);
        float vertical = Math.Clamp((row + 0.5f) * gain.Height / height - 0.5f, 0, gain.Height - 1);
        int left = (int)horizontal, top = (int)vertical;
        int right = Math.Min(left + 1, gain.Width - 1), bottom = Math.Min(top + 1, gain.Height - 1);
        var upper = (ushort*)((byte*)gain.GetPixels() + (long)top * gain.RowBytes);
        var lower = (ushort*)((byte*)gain.GetPixels() + (long)bottom * gain.RowBytes);
        Vector3 upperLeft = new(upper[left * 4], upper[left * 4 + 1], upper[left * 4 + 2]);
        Vector3 upperRight = new(upper[right * 4], upper[right * 4 + 1], upper[right * 4 + 2]);
        Vector3 lowerLeft = new(lower[left * 4], lower[left * 4 + 1], lower[left * 4 + 2]);
        Vector3 lowerRight = new(lower[right * 4], lower[right * 4 + 1], lower[right * 4 + 2]);
        return Vector3.Lerp(Vector3.Lerp(upperLeft, upperRight, horizontal - left),
            Vector3.Lerp(lowerLeft, lowerRight, horizontal - left), vertical - top) / 65535;
    }
}