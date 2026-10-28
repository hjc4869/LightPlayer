using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using LightStudio.NativeBindings.Types;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services;

internal static unsafe partial class PhotoDecoder
{
    private static readonly PhotoColorProfile GainSampleProfile = new([1, 1, 0, 1, 0, 0, 0],
        [1, 0, 0, 0, 1, 0, 0, 0, 1]);

    internal static PhotoColorProfile? CreateGpuProfile(SKColorSpace space, bool relativeHdr, bool hasAlpha = false) =>
        PhotoColorProfile.Create(space, relativeHdr, hasAlpha);

    private static void ApplyGpuProfile(VideoFrame frame, PhotoColorProfile profile, bool hasGainMap)
    {
        frame.PhotoColor = profile;
        frame.ColorPrimaries = VideoColorPrimaries.Bt2020;
        frame.ColorTransfer = hasGainMap ? VideoColorTransfer.Linear : profile.TransferCode switch
        {
            16 => VideoColorTransfer.Pq,
            18 => VideoColorTransfer.Hlg,
            _ => VideoColorTransfer.Linear,
        };
        frame.FieldOrder = VideoFieldOrder.Progressive;
    }

    private static VideoFrame? DecodeGpu(Stream stream, CancellationToken cancellationToken, uint? imageItem = null,
        bool useTiles = true, AVCodecID codecHint = AVCodecID.AV_CODEC_ID_NONE)
    {
        long origin = stream.Position;
        Span<byte> signature = stackalloc byte[12];
        int read = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        stream.Position = origin;
        if (read < 4 || signature.StartsWith("II"u8) || signature.StartsWith("MM"u8))
            return null;
        try
        {
            if (codecHint == AVCodecID.AV_CODEC_ID_NONE && !(read >= 8 && signature.Slice(4, 4).SequenceEqual("ftyp"u8)))
            {
                using SKData data = SKData.Create(stream);
                using SKCodec? codec = SKCodec.Create(data);
                using SKColorSpace? color = codec?.Info.ColorSpace ?? SKColorSpace.CreateSrgb();
                bool hasAlpha = codec?.Info.AlphaType != SKAlphaType.Opaque;
                PhotoColorProfile? codecProfile = ReadPngColorProfile(data, hasAlpha, out double peakLuminance)
                    ?? CreateGpuProfile(color, relativeHdr: false, hasAlpha);
                if (codec is not null && codecProfile is not null)
                {
                    VideoFrame frame = CreateJpegTiles(codec, data, codecProfile, hasGainMap: false, useTiles, cancellationToken)
                        ?? CreateCodecTiles(codec, data, codecProfile, hasGainMap: false);
                    if (peakLuminance > 0)
                        frame.HdrPeakLuminance = peakLuminance;
                    return frame;
                }
                stream.Position = origin;
            }
            if (useTiles && read >= 8 && (signature.Slice(4, 4).SequenceEqual("ftyp"u8) ||
                signature.Slice(4, 4).SequenceEqual("jP  "u8) || signature.StartsWith((ReadOnlySpan<byte>)[0xff, 0x4f, 0xff, 0x51])) &&
                StillImage.OpenTiles(stream, imageItem, cancellationToken, includeSingleImages: true) is { } tiles)
            {
                using SKColorSpace? color = CreateColorSpace(tiles.IccProfile, tiles.ColorPrimaries, tiles.ColorTransfer);
                if (color is not null && CreateGpuProfile(color, relativeHdr: false) is { } tileProfile)
                    return CreateNativeTiles(tiles, tileProfile, hasGainMap: false);
                tiles.Dispose();
            }
            using StillImageNative? native = StillImage.DecodeNative(stream, imageItem, cancellationToken, codecHint);
            if (native is null)
                return null;
            using SKColorSpace? space = CreateColorSpace(native.IccProfile, native.ColorPrimaries, native.ColorTransfer);
            if (space is null || CreateGpuProfile(space, relativeHdr: false) is not { } profile)
                return null;
            ApplyGpuProfile(native.Frame, profile, hasGainMap: false);
            return native.Frame.AddRef();
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static PhotoColorProfile? ReadPngColorProfile(SKData data, bool hasAlpha, out double peakLuminance)
    {
        peakLuminance = 0;
        ReadOnlySpan<byte> bytes = new((void*)data.Data, (int)Math.Min(data.Size, int.MaxValue));
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (!bytes.StartsWith(signature))
            return null;

        int primaries = 0;
        int transfer = 0;
        bytes = bytes[8..];
        while (bytes.Length >= 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            if (length > bytes.Length - 12)
                break;
            ReadOnlySpan<byte> kind = bytes.Slice(4, 4);
            if (kind.SequenceEqual("IDAT"u8) || kind.SequenceEqual("IEND"u8))
                break;
            ReadOnlySpan<byte> payload = bytes.Slice(8, (int)length);
            if (kind.SequenceEqual("cICP"u8) && payload.Length == 4 && payload[2] == 0 && payload[3] == 1)
            {
                primaries = payload[0];
                transfer = payload[1];
            }
            else if ((kind.SequenceEqual("cLLi"u8) || kind.SequenceEqual("cLLI"u8)) && payload.Length == 8)
            {
                uint maximum = BinaryPrimitives.ReadUInt32BigEndian(payload);
                if (maximum > 0)
                    peakLuminance = maximum / 10000.0;
            }
            bytes = bytes[((int)length + 12)..];
        }

        if (primaries is not (1 or 5 or 6 or 7 or 9 or 11 or 12) ||
            transfer is not (1 or 4 or 5 or 6 or 8 or 13 or 14 or 15 or 16 or 18))
            return null;

        using SKColorSpace? space = CreateColorSpace([], primaries, transfer);
        if (space is null)
            return null;
        if (transfer is not (16 or 18))
            return CreateGpuProfile(space, relativeHdr: false, hasAlpha);
        if (!space.ToColorSpaceXyz(out SKColorSpaceXyz gamut))
            return null;
        SKColorSpaceTransferFn curve = transfer == 16 ? SKColorSpaceTransferFn.Pq : SKColorSpaceTransferFn.Hlg;
        return new PhotoColorProfile([curve.G, curve.A, curve.B, curve.C, curve.D, curve.E, curve.F],
            SKColorSpaceXyz.Concat(SKColorSpaceXyz.Rec2020.Invert(), gamut).Values,
            transfer, 1, hasAlpha);
    }

    private static VideoFrame? ReconstructGpu(SKCodec primary, SKCodec gain, GainMap metadata,
        SKColorSpace baseSpace, SKColorSpaceXyz mathGamut, CancellationToken cancellationToken, SKData? primaryData, SKData? gainData,
        bool useTiles)
    {
        if (CreateGpuProfile(baseSpace, relativeHdr: true, hasAlpha: primary.Info.AlphaType != SKAlphaType.Opaque) is not { } profile || primaryData is null)
            return null;
        VideoFrame frame = CreateJpegTiles(primary, primaryData, profile, hasGainMap: true, useTiles, cancellationToken)
            ?? CreateCodecTiles(primary, primaryData, profile, hasGainMap: true);
        try
        {
            if (gainData is not null)
            {
                VideoFrame samples = CreateJpegTiles(gain, gainData, GainSampleProfile, hasGainMap: false, useTiles, cancellationToken)
                    ?? CreateCodecTiles(gain, gainData, GainSampleProfile, hasGainMap: false);
                try { frame.GainMap = CreateGainMap(samples, mathGamut, metadata); }
                finally { samples.Release(); }
                ApplyGpuProfile(frame, profile, hasGainMap: true);
                return frame;
            }
            int bits = gain.EncodedFormat == SKEncodedImageFormat.Jpeg ? 8 : 16;
            using var pixels = new SKBitmap(new SKImageInfo(gain.Info.Width, gain.Info.Height,
                bits == 8 ? SKColorType.Rgba8888 : SKColorType.Rgba16161616, SKAlphaType.Opaque));
            if (!DecodeGainPixels(gain, pixels))
            {
                frame.Release();
                return null;
            }
            AttachGainMap(frame, pixels, mathGamut, metadata, bits, cancellationToken);
            ApplyGpuProfile(frame, profile, hasGainMap: true);
            return frame;
        }
        catch
        {
            frame.Release();
            throw;
        }
    }

    private static VideoFrame? ReconstructContainerGpu(Stream stream, GainMapSource source, CancellationToken cancellationToken,
        bool useTiles)
    {
        long origin = stream.Position;
        StillImage.TileSource? tiles = null;
        VideoFrame? result = null;
        try
        {
            tiles = useTiles && source.BaseItem is not null ? StillImage.OpenTiles(stream, source.BaseItem, cancellationToken, includeSingleImages: true) : null;
            using StillImageNative? native = tiles is null ? StillImage.DecodeNative(stream, source.BaseItem, cancellationToken) : null;
            if (native is null && tiles is null)
                return null;
            using SKColorSpace? space = CreateColorSpace(source.BaseProfile ?? tiles?.IccProfile ?? native!.IccProfile,
                tiles?.ColorPrimaries ?? native!.ColorPrimaries, tiles?.ColorTransfer ?? native!.ColorTransfer);
            using SKColorSpace? alternate = source.Metadata.UseBaseColorSpace ? null :
                CreateColorSpace(source.MathProfile ?? [], source.MathPrimaries == 0 ? 1 : source.MathPrimaries, 8);
            if (space is null || CreateGpuProfile(space, relativeHdr: true) is not { } profile ||
                !source.Metadata.UseBaseColorSpace && alternate is null ||
                !(alternate ?? space).ToColorSpaceXyz(out SKColorSpaceXyz gamut))
                return null;
            stream.Position = origin;
            using var gainStream = source.EncodedGain is { } encoded ? new MemoryStream(encoded, writable: false) : null;
            if (useTiles && source.GainItem is not null && StillImage.OpenTiles(stream, source.GainItem, cancellationToken, includeSingleImages: true) is { } gainTiles)
            {
                VideoFrame gainFrame = CreateNativeTiles(gainTiles, GainSampleProfile, hasGainMap: false);
                try
                {
                    result = tiles is not null ? CreateNativeTiles(tiles, profile, hasGainMap: true) : native!.Frame.AddRef();
                    tiles = null;
                    result.GainMap = CreateGainMap(gainFrame, gamut, source.Metadata);
                    ApplyGpuProfile(result, profile, hasGainMap: true);
                    VideoFrame tiledResult = result;
                    result = null;
                    return tiledResult;
                }
                finally { gainFrame.Release(); }
            }
            StillImagePixels? gain = StillImage.DecodePixels(gainStream ?? stream, source.GainItem, cancellationToken);
            if (gain is null)
                return null;
            byte[] samples = new byte[checked(gain.Width * gain.Height * 8)];
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(gain.Pixels.AsSpan()).CopyTo(samples);
            result = tiles is not null ? CreateNativeTiles(tiles, profile, hasGainMap: true) : native!.Frame.AddRef();
            tiles = null;
            result.GainMap = CreateGainMap(gain.Width, gain.Height, samples, gamut, source.Metadata, 16);
            ApplyGpuProfile(result, profile, hasGainMap: true);
            VideoFrame completed = result;
            result = null;
            return completed;
        }
        finally
        {
            tiles?.Dispose();
            result?.Release();
            stream.Position = origin;
        }
    }

    private static void AttachGainMap(VideoFrame frame, SKBitmap pixels, SKColorSpaceXyz gamut, GainMap metadata,
        int bits, CancellationToken cancellationToken)
    {
        int rowBytes = checked(pixels.Width * 4 * (bits / 8));
        byte[] samples = new byte[checked(rowBytes * pixels.Height)];
        for (int row = 0; row < pixels.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            new ReadOnlySpan<byte>((byte*)pixels.GetPixels() + (long)row * pixels.RowBytes, rowBytes)
                .CopyTo(samples.AsSpan(row * rowBytes));
        }
        frame.GainMap = CreateGainMap(pixels.Width, pixels.Height, samples, gamut, metadata, bits);
    }

    private static PhotoGainMap CreateGainMap(int width, int height, byte[] samples, SKColorSpaceXyz gamut,
        GainMap metadata, int bits) => new(width, height, samples,
            metadata.Minimum, metadata.Maximum, metadata.Gamma, metadata.OffsetSdr, metadata.OffsetHdr,
            metadata.CapacityMin, metadata.CapacityMax,
            SKColorSpaceXyz.Concat(gamut.Invert(), SKColorSpaceXyz.Rec2020).Values,
            SKColorSpaceXyz.Concat(SKColorSpaceXyz.Rec2020.Invert(), gamut).Values, bits)
        { IsApple = metadata.IsApple, BaseIsHdr = metadata.BaseIsHdr };

    private static PhotoGainMap CreateGainMap(VideoFrame samples, SKColorSpaceXyz gamut, GainMap metadata) => new(samples,
        metadata.Minimum, metadata.Maximum, metadata.Gamma, metadata.OffsetSdr, metadata.OffsetHdr,
        metadata.CapacityMin, metadata.CapacityMax,
        SKColorSpaceXyz.Concat(gamut.Invert(), SKColorSpaceXyz.Rec2020).Values,
        SKColorSpaceXyz.Concat(SKColorSpaceXyz.Rec2020.Invert(), gamut).Values)
        { IsApple = metadata.IsApple, BaseIsHdr = metadata.BaseIsHdr };
}