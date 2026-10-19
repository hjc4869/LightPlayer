using System;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services;

internal static unsafe partial class PhotoDecoder
{
    public static Size ReadDisplaySize(Stream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!stream.CanSeek)
            return default;

        long origin = stream.Position;
        try
        {
            Span<byte> signature = stackalloc byte[4];
            int count = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
            stream.Position = origin;
            if (count == signature.Length && JxrDecoder.IsJxr(signature))
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                cancellationToken.ThrowIfCancellationRequested();
                JxrImageInfo info = JxrDecoder.Identify(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
                return info.Orientation >= 4 ? new Size(info.Height, info.Width) : new Size(info.Width, info.Height);
            }

            using (var managed = new SKManagedStream(stream, false))
            using (SKCodec? codec = SKCodec.Create(managed))
            {
                if (codec is not null)
                {
                    ReadCodecOrientation(codec, stream, out int rotation, out _);
                    stream.Position = origin;
                    double displayWidth = codec.Info.Width * StillImage.ReadPixelAspectRatio(stream);
                    return rotation is 90 or 270
                        ? new Size(codec.Info.Height, displayWidth)
                        : new Size(displayWidth, codec.Info.Height);
                }
            }

            stream.Position = origin;
            (double width, double height) = StillImage.ReadDisplaySize(stream);
            return new Size(width, height);
        }
        finally
        {
            stream.Position = origin;
        }
    }

    public static VideoFrame? Decode(Stream stream, CancellationToken cancellationToken = default, bool? renderRaw = null,
        string? fileName = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!stream.CanSeek)
        {
            using var buffered = new MemoryStream();
            stream.CopyTo(buffered);
            buffered.Position = 0;
            return Decode(buffered, cancellationToken, renderRaw, fileName);
        }

        if (DecodeAnimation(stream, cancellationToken) is { } animated)
            return animated;
        if (string.Equals(Path.GetExtension(fileName), ".tga", StringComparison.OrdinalIgnoreCase))
            return DecodeGpu(stream, cancellationToken, codecHint: LightStudio.NativeBindings.Types.AVCodecID.AV_CODEC_ID_TARGA);
        if (renderRaw == true)
            return StillImage.Decode(stream, leaveOpen: true, cancellationToken);
        if (renderRaw == false && DecodeDngPreview(stream, cancellationToken, includeReduced: true) is { } preview)
            return preview;

        long origin = stream.Position;
        try
        {
            if (PhotoDecodeAcceleration.Decoder?.TryDecode(stream, DecodeSoftware, cancellationToken) is { } accelerated)
                return accelerated;
        }
        finally
        {
            stream.Position = origin;
        }

        return DecodeSoftware(stream, cancellationToken);
    }

    public static VideoFrame? DecodePreview(Stream stream, PhotoPreviewRequest request, CancellationToken cancellationToken)
    {
        EmbeddedPhotoPreview? preview = FindEmbeddedPreview(stream, request, cancellationToken);
        if (preview is null)
            return null;
        VideoFrame? frame;
        if (preview.Item is { } item)
        {
            long origin = stream.Position;
            GainMapSource? gain;
            try { gain = ReadContainer(stream, cancellationToken, item); }
            finally { stream.Position = origin; }
            frame = gain is not null ? DecodeContainerGainMap(stream, gain, cancellationToken, useTiles: false)
                : DecodeGpu(stream, cancellationToken, item, useTiles: false);
        }
        else
        {
            using var encoded = new MemoryStream(preview.Encoded!, writable: false);
            frame = preview.Reduction > 1 ? DecodeJxr(encoded, cancellationToken, preview.Reduction)
                : DecodeSoftware(encoded, cancellationToken);
        }
        if (frame is null)
            return null;
        if (frame.Rotation == 0 && !frame.MirrorHorizontally)
        {
            frame.Rotation = preview.Rotation;
            frame.MirrorHorizontally = preview.Mirror;
        }
        double width = frame.Width * (frame.PixelAspectRatio > 0 ? frame.PixelAspectRatio : 1);
        bool better = frame.Rotation % 180 == 0 ? request.IsBetter(width, frame.Height) : request.IsBetter(frame.Height, width);
        if (better)
            return frame;
        frame.Release();
        return null;
    }

    private static VideoFrame? DecodeSoftware(Stream stream, CancellationToken cancellationToken)
    {
        long origin = stream.Position;
        VideoFrame? decoded = null;
        try
        {
            decoded = DecodeJxr(stream, cancellationToken) ??
                DecodeDngPreview(stream, cancellationToken) ?? DecodeGainMap(stream, cancellationToken);
        }
        catch (EndOfStreamException)
        {
        }
        finally
        {
            stream.Position = origin;
        }

        return decoded ?? DecodeGpu(stream, cancellationToken) ?? StillImage.Decode(stream, leaveOpen: true, cancellationToken);
    }

    private static VideoFrame? DecodeGainMap(Stream stream, CancellationToken cancellationToken)
    {
        long origin = stream.Position;
        GainMapSource? source = ReadContainer(stream, cancellationToken);
        stream.Position = origin;
        if (source is not null)
            return DecodeContainerGainMap(stream, source, cancellationToken);

        if (!TryFindJpegGainMap(stream, out ImageRange primaryImage, out ImageRange gainImage, out GainMap gainMap))
            return null;

        using SKData primaryData = ReadData(stream, primaryImage);
        using SKData gainData = ReadData(stream, gainImage);
        using SKCodec? primary = SKCodec.Create(primaryData);
        using SKCodec? gain = SKCodec.Create(gainData);
        if (primary is null || gain is null)
            return null;

        return Reconstruct(primary, gain, gainMap, cancellationToken, primaryData, gainData);
    }

    private static SKData ReadData(Stream stream, ImageRange image)
    {
        image = TrimJpegRange(stream, image);
        SKData data = SKData.Create(image.Length);
        try
        {
            stream.Position = image.Offset;
            stream.ReadExactly(new Span<byte>((void*)data.Data, image.Length));
            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    private static VideoFrame? Reconstruct(SKCodec primary, SKCodec gain, GainMap metadata,
        CancellationToken cancellationToken, SKData? primaryData = null, SKData? gainData = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SKColorSpace sourceColorSpace = primary.Info.ColorSpace ?? SKColorSpace.CreateSrgb();
        using SKColorSpace? alternate = metadata.UseBaseColorSpace ? null : gain.Info.ColorSpace ?? SKColorSpace.CreateSrgb();
        using SKColorSpace linearColorSpace = sourceColorSpace.ToLinearGamma();
        using SKColorSpace mathColorSpace = (alternate ?? sourceColorSpace).ToLinearGamma();
        if (!mathColorSpace.ToColorSpaceXyz(out SKColorSpaceXyz sourceXyz))
            return null;

        if (ReconstructGpu(primary, gain, metadata, sourceColorSpace, sourceXyz, cancellationToken, primaryData, gainData) is { } direct)
            return direct;

        using var basePixels = new SKBitmap(new SKImageInfo(
            primary.Info.Width, primary.Info.Height, SKColorType.RgbaF16, SKAlphaType.Unpremul, linearColorSpace));
        using var gainPixels = new SKBitmap(new SKImageInfo(
            gain.Info.Width, gain.Info.Height, SKColorType.Rgba16161616, SKAlphaType.Opaque));
        if (primary.GetPixels(basePixels.Info, basePixels.GetPixels(), basePixels.RowBytes, SKCodecOptions.Default) != SKCodecResult.Success ||
            !DecodeGainPixels(gain, gainPixels))
        {
            return null;
        }

        using Stream geometryStream = primaryData?.AsStream() ?? Stream.Null;
        ReadCodecOrientation(primary, geometryStream, out int rotation, out bool mirror);
        double pixelAspectRatio = primaryData is null ? 1 : StillImage.ReadPixelAspectRatio(geometryStream);
        NormalizeBase(basePixels, TransferCode(sourceColorSpace), cancellationToken);
        using SKBitmap? converted = alternate is null ? null : ConvertBase(basePixels, mathColorSpace);
        if (alternate is not null && converted is null)
            return null;
        return CreateGainMapFrame(converted ?? basePixels, gainPixels, sourceXyz, metadata, rotation, mirror, pixelAspectRatio, cancellationToken);
    }

    private static VideoFrame CreateGainMapFrame(SKBitmap basePixels, SKBitmap gainPixels, SKColorSpaceXyz sourceXyz,
        GainMap metadata, int rotation, bool mirror, double pixelAspectRatio, CancellationToken cancellationToken)
    {
        float[] matrix = SKColorSpaceXyz.Concat(SKColorSpaceXyz.Rec2020.Invert(), sourceXyz).Values;
        VideoFrame frame = VideoFrame.CreateStandalone(basePixels.Width, basePixels.Height, VideoPixelFormat.Rgba64);
        frame.HasAlpha = true;
        frame.ColorPrimaries = VideoColorPrimaries.Bt2020;
        frame.ColorSpace = VideoColorSpace.Bt2020;
        frame.ColorTransfer = VideoColorTransfer.Linear;
        frame.ColorRange = VideoColorRange.Full;
        frame.ChromaLocation = VideoChromaLocation.TopLeft;
        frame.FieldOrder = VideoFieldOrder.Progressive;
        frame.Rotation = rotation;
        frame.MirrorHorizontally = mirror;
        frame.PixelAspectRatio = pixelAspectRatio;

        try
        {
            byte[] gainSamples = new byte[checked(gainPixels.Width * gainPixels.Height * 8)];
            for (int row = 0; row < gainPixels.Height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                new ReadOnlySpan<byte>((byte*)gainPixels.GetPixels() + (long)row * gainPixels.RowBytes, gainPixels.Width * 8)
                    .CopyTo(gainSamples.AsSpan(row * gainPixels.Width * 8));
            }
            frame.GainMap = new PhotoGainMap(gainPixels.Width, gainPixels.Height, gainSamples,
                metadata.Minimum, metadata.Maximum, metadata.Gamma, metadata.OffsetSdr, metadata.OffsetHdr,
                metadata.CapacityMin, metadata.CapacityMax,
                SKColorSpaceXyz.Concat(sourceXyz.Invert(), SKColorSpaceXyz.Rec2020).Values, matrix, bitsPerComponent: 16)
                { IsApple = metadata.IsApple };

            ForRows(frame.Width, frame.Height, cancellationToken, (firstRow, lastRow) =>
            {
                for (int row = firstRow; row < lastRow; row++)
                    ConvertRow(row);
            });
            return frame;

            void ConvertRow(int row)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = (Half*)((byte*)basePixels.GetPixels() + ((long)row * basePixels.RowBytes));
                var target = (ushort*)((byte*)frame.Data + ((long)row * frame.Stride));
                for (int column = 0; column < frame.Width; column++)
                {
                    Vector3 linear = new((float)source[column * 4], (float)source[(column * 4) + 1], (float)source[(column * 4) + 2]);
                    if (metadata.BaseIsHdr)
                    {
                        Vector3 encoded = SampleGain(gainPixels, column, row, frame.Width, frame.Height);
                        for (int channel = 0; channel < 3; channel++)
                        {
                            float logarithm = metadata.Minimum[channel] + (metadata.Maximum[channel] - metadata.Minimum[channel]) *
                                MathF.Pow(encoded[channel], 1 / metadata.Gamma[channel]);
                            linear[channel] = (linear[channel] + metadata.OffsetHdr[channel]) * MathF.Pow(2, -logarithm) -
                                metadata.OffsetSdr[channel];
                        }
                    }
                    Vector3 converted = Vector3.Clamp(new Vector3(
                        (matrix[0] * linear.X) + (matrix[1] * linear.Y) + (matrix[2] * linear.Z),
                        (matrix[3] * linear.X) + (matrix[4] * linear.Y) + (matrix[5] * linear.Z),
                        (matrix[6] * linear.X) + (matrix[7] * linear.Y) + (matrix[8] * linear.Z)), Vector3.Zero, Vector3.One);
                    target[column * 4] = Quantize(converted.X);
                    target[column * 4 + 1] = Quantize(converted.Y);
                    target[column * 4 + 2] = Quantize(converted.Z);
                    target[column * 4 + 3] = Quantize((float)source[column * 4 + 3]);
                }
            }
        }
        catch
        {
            frame.Release();
            throw;
        }
    }

    private static void ForRows(int width, int height, CancellationToken cancellationToken, Action<int, int> convert)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int workers = OperatingSystem.IsBrowser() ? 1 : Math.Min(Environment.ProcessorCount, Math.Min(height, 16));
        if (workers <= 1 || (long)width * height < 1_000_000)
        {
            convert(0, height);
            return;
        }

        Parallel.For(0, workers, new ParallelOptions
        {
            MaxDegreeOfParallelism = workers,
            CancellationToken = cancellationToken,
        }, worker => convert((int)((long)height * worker / workers), (int)((long)height * (worker + 1) / workers)));
    }
}
