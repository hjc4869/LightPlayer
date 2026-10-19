using System;
using System.IO;
using System.Threading;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Core;
using SkiaSharp;
using static LightStudio.FfmpegShim.Ffmpeg;

namespace LightStudio.FfmpegShim.Media;

internal sealed partial class SkiaImageAnimation : IDisposable
{
    private readonly SKData data;
    private readonly SKCodec codec;
    private readonly SKCodecFrameInfo[] frames;
    private readonly PhotoColorProfile profile;
    private readonly int rotation;
    private readonly bool mirror;
    private readonly double aspect;
    private SKBitmap canvas;
    private SKBitmap restore;
    private int index;
    private int prior = -1;
    private long plays;

    public static bool Supports(ReadOnlySpan<byte> header) => header.Length >= 12 &&
        (header[..3].SequenceEqual("GIF"u8) || header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8));

    public SkiaImageAnimation(NativeBuffer bytes, int rotation, bool mirror, double aspect)
    {
        this.rotation = rotation;
        this.mirror = mirror;
        this.aspect = aspect;
        data = SKData.Create(bytes.Address, bytes.Length, static (_, _) => { }, null);
        try
        {
            codec = SKCodec.Create(data) ?? throw new InvalidDataException("Could not open the animated image.");
            if (codec.FrameCount > 100000 || (long)codec.Info.Width * codec.Info.Height > 32L * 1024 * 1024)
                throw new InvalidDataException("The animation exceeds the frame count or canvas size limit.");
            frames = codec.FrameInfo;
            using SKColorSpace color = codec.Info.ColorSpace ?? SKColorSpace.CreateSrgb();
            bool hasAlpha = codec.Info.AlphaType != SKAlphaType.Opaque;
            foreach (SKCodecFrameInfo frame in frames)
                hasAlpha |= frame.AlphaType != SKAlphaType.Opaque;
            profile = PhotoColorProfile.Create(color, hasAlpha: hasAlpha)
                ?? throw new NotSupportedException("The animation color profile cannot be rendered.");
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            canvas = new SKBitmap(info);
            restore = new SKBitmap(info);
        }
        catch { codec?.Dispose(); data.Dispose(); canvas?.Dispose(); restore?.Dispose(); throw; }
    }

    public StillImageNative Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (frames.Length == 0 && index != 0)
            return null;
        if (frames.Length > 0 && index == frames.Length)
        {
            plays++;
            if (codec.RepetitionCount >= 0 && plays > codec.RepetitionCount)
                return null;
            index = 0;
            prior = -1;
        }
        SKCodecFrameInfo metadata = frames.Length == 0 ? default : frames[index];
        bool restorePrevious = metadata.DisposalMethod == SKCodecAnimationDisposalMethod.RestorePrevious;
        if (restorePrevious)
        {
            using SKPixmap saved = canvas.PeekPixels();
            if (!saved.ReadPixels(restore.Info, restore.GetPixels(), restore.RowBytes))
                throw new InvalidDataException("Could not preserve the animation canvas.");
        }
        int previous = metadata.RequiredFrame < 0 || prior < metadata.RequiredFrame ? -1 : prior;
        SKCodecResult result = codec.GetPixels(canvas.Info, canvas.GetPixels(), canvas.RowBytes,
            new SKCodecOptions(index, previous));
        if (result != SKCodecResult.Success)
            throw new InvalidDataException($"Could not decode animation frame {index}: {result}.");
        VideoFrame frame = VideoFrame.CreateStandalone(canvas.Width, canvas.Height, VideoPixelFormat.Bgra32);
        try
        {
            using SKPixmap pixels = canvas.PeekPixels();
            if (!pixels.ReadPixels(canvas.Info.WithAlphaType(SKAlphaType.Unpremul), frame.Data, frame.Stride))
                throw new InvalidDataException("Could not copy the animation pixels.");
            frame.PhotoColor = profile;
            frame.ColorPrimaries = VideoColorPrimaries.Bt2020;
            frame.ColorTransfer = VideoColorTransfer.Linear;
            frame.ColorRange = VideoColorRange.Full;
            frame.FieldOrder = VideoFieldOrder.Progressive;
            frame.Rotation = rotation;
            frame.MirrorHorizontally = mirror;
            frame.PixelAspectRatio = aspect;
            frame.Duration = metadata.Duration <= 10 ? 0.1 : metadata.Duration / 1000d;
            cancellationToken.ThrowIfCancellationRequested();
            if (restorePrevious)
                (canvas, restore) = (restore, canvas);
            else
                prior = index;
            index++;
            return new StillImageNative { Frame = frame };
        }
        catch { frame.Release(); throw; }
    }

    public void Dispose()
    {
        canvas.Dispose();
        restore.Dispose();
        codec.Dispose();
        data.Dispose();
    }
}

public static partial class StillImage
{
    public static StillImageNative DecodeRaster(Stream stream, CancellationToken cancellationToken = default)
    {
        if (!stream.CanSeek)
            return null;
        long origin = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[12];
            int count = stream.ReadAtLeast(header, header.Length, false);
            stream.Position = origin;
            if (count != header.Length || !(SkiaImageAnimation.Supports(header) || header[..2].SequenceEqual("BM"u8)))
                return null;
            long length = stream.Length - origin;
            if (length > 256L * 1024 * 1024)
                throw new InvalidDataException("The encoded image exceeds the 256 MiB limit.");
            ExifOrientation.TryRead(stream, out int rotation, out bool mirror);
            double aspect = ReadDensityAspectRatio(stream);
            using var bytes = new NativeBuffer((int)length);
            for (int offset = 0; offset < bytes.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int take = Math.Min(64 * 1024, bytes.Length - offset);
                stream.ReadExactly(bytes.GetSpan().Slice(offset, take));
                offset += take;
            }
            using var decoder = new SkiaImageAnimation(bytes, rotation, mirror, aspect > 0 ? aspect : 1);
            return decoder.Read(cancellationToken);
        }
        finally { stream.Position = origin; }
    }

    public static NativeImageThumbnail DecodeRasterThumbnail(Stream stream, int maxDimension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDimension, 0);
        if (!stream.CanSeek)
            return null;
        long origin = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[12];
            int count = stream.ReadAtLeast(header, header.Length, false);
            stream.Position = origin;
            if (count != header.Length || !SkiaImageAnimation.Supports(header))
                return null;
            cancellationToken.ThrowIfCancellationRequested();
            double aspect = ReadPixelAspectRatio(stream);
            using var managed = new SKManagedStream(stream, false);
            using var codec = SKCodec.Create(managed) ?? throw new InvalidDataException("Could not open the image.");
            if ((long)codec.Info.Width * codec.Info.Height > 32L * 1024 * 1024)
                throw new InvalidDataException("The image exceeds the 32 megapixel limit.");
            using var color = SKColorSpace.CreateSrgb();
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul, color);
            using var bitmap = new SKBitmap(info);
            SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels(), bitmap.RowBytes, new SKCodecOptions(0, -1));
            if (result != SKCodecResult.Success)
                throw new InvalidDataException($"Could not decode the image: {result}.");
            cancellationToken.ThrowIfCancellationRequested();
            ComputeSize(info.Width, info.Height, av_d2q(aspect, int.MaxValue),
                maxDimension, out int width, out int height);
            var pixels = new NativeBuffer(checked(width * height * 4));
            try
            {
                using var source = bitmap.PeekPixels();
                using var target = new SKPixmap(info.WithSize(width, height), pixels.Address, width * 4);
                if (!source.ScalePixels(target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)))
                    throw new InvalidDataException("Could not resize the image.");
                cancellationToken.ThrowIfCancellationRequested();
                ExifOrientation.TryDecode((int)codec.EncodedOrigin, out int rotation, out bool mirror);
                return Orient(pixels, width, height, rotation, mirror);
            }
            catch { pixels.Dispose(); throw; }
        }
        finally { stream.Position = origin; }
    }
}