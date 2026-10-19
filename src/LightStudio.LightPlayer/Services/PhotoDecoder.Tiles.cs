using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using LightStudio.FfmpegShim.Media;
using SkiaSharp;

namespace LightStudio.LightPlayer.Services;

internal static partial class PhotoDecoder
{
    private static VideoFrame? CreateJpegTiles(SKCodec codec, SKData data, PhotoColorProfile profile, bool hasGainMap,
        CancellationToken cancellationToken)
    {
        if (codec.EncodedFormat != SKEncodedImageFormat.Jpeg)
            return null;
        if (!ReferenceEquals(profile, GainSampleProfile) && codec.Info.Width <= 16384 && codec.Info.Height <= 16384 &&
            PhotoDecodeResources.TryReserve((long)codec.Info.Width * codec.Info.Height * 4, out IDisposable reservation))
        {
            try
            {
                using Stream stream = data.AsStream();
                using StillImageNative? native = StillImage.DecodeNative(stream, cancellationToken: cancellationToken);
                if (native is not null)
                {
                    ApplyGpuProfile(native.Frame, profile, hasGainMap);
                    Action<VideoFrame>? release = native.Frame.Recycled;
                    native.Frame.Recycled = released =>
                    {
                        try { release?.Invoke(released); }
                        finally { reservation.Dispose(); }
                    };
                    return native.Frame.AddRef();
                }
            }
            catch
            {
                reservation.Dispose();
                throw;
            }
            reservation.Dispose();
        }
        var source = new JpegTileSource(data, codec.Info.Width, codec.Info.Height, profile);
        VideoFrame frame = VideoFrame.CreateHosted(codec.Info.Width, codec.Info.Height, VideoPixelFormat.Bgra32);
        frame.PhotoTiles = source;
        using Stream orientationStream = data.AsStream();
        ReadCodecOrientation(codec, orientationStream, out int rotation, out bool mirror);
        frame.Rotation = rotation;
        frame.MirrorHorizontally = mirror;
        frame.PixelAspectRatio = StillImage.ReadPixelAspectRatio(orientationStream);
        ApplyGpuProfile(frame, profile, hasGainMap);
        return frame;
    }

    private static VideoFrame CreateCodecTiles(SKCodec codec, SKData data, PhotoColorProfile profile, bool hasGainMap)
    {
        VideoFrame frame = VideoFrame.CreateHosted(codec.Info.Width, codec.Info.Height, VideoPixelFormat.Bgra32);
        SKSizeI reduced = codec.GetScaledDimensions(0.5f);
        frame.PhotoTiles = new CodecTileSource(data, codec.Info.Width, codec.Info.Height, profile,
            reduced.Width < codec.Info.Width || reduced.Height < codec.Info.Height);
        using Stream stream = data.AsStream();
        ReadCodecOrientation(codec, stream, out int rotation, out bool mirror);
        frame.Rotation = rotation;
        frame.MirrorHorizontally = mirror;
        frame.PixelAspectRatio = StillImage.ReadPixelAspectRatio(stream);
        ApplyGpuProfile(frame, profile, hasGainMap);
        return frame;
    }

    private static void ReadCodecOrientation(SKCodec codec, Stream stream, out int rotation, out bool mirror)
    {
        rotation = codec.EncodedOrigin switch
        {
            SKEncodedOrigin.BottomRight or SKEncodedOrigin.BottomLeft => 180,
            SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom => 90,
            SKEncodedOrigin.LeftTop or SKEncodedOrigin.LeftBottom => 270,
            _ => 0,
        };
        mirror = codec.EncodedOrigin is SKEncodedOrigin.TopRight or
            SKEncodedOrigin.BottomLeft or SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightBottom;
        if (codec.EncodedOrigin == SKEncodedOrigin.TopLeft &&
            codec.EncodedFormat is SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp &&
            ExifOrientation.TryRead(stream, out int exifRotation, out bool exifMirror))
        {
            rotation = exifRotation;
            mirror = exifMirror;
        }
    }

    private static ValueTask<VideoFrame[]> DecodeTileBatchAsync(ReadOnlyMemory<PhotoTileRequest> requests,
        CancellationToken cancellationToken, Func<int, int, CancellationToken, VideoFrame> decodeTile) =>
        new(Task.Run(() =>
        {
            var frames = new VideoFrame[requests.Length];
            try
            {
                for (int index = 0; index < frames.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PhotoTileRequest request = requests.Span[index];
                    frames[index] = decodeTile(request.Index, request.Reduction, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return frames;
            }
            catch
            {
                foreach (VideoFrame frame in frames)
                    frame?.Release();
                throw;
            }
        }, cancellationToken));

    private sealed class CodecTileSource : IPhotoTileSource
    {
        private readonly SKData data;
        private readonly int width;
        private readonly int height;
        private readonly PhotoColorProfile profile;

        public CodecTileSource(SKData source, int width, int height, PhotoColorProfile profile, bool hasScaledOverview)
        {
            data = SKData.CreateCopy(source.Data, source.Size);
            this.width = width;
            this.height = height;
            this.profile = profile;
            HasScaledOverview = hasScaledOverview;
        }

        public int Count => 1;
        public long EncodedBytes => data.Size;
        public bool HasScaledOverview { get; }
        public PhotoTileBounds GetBounds(int index) => new(0, 0, width, height);

        public ValueTask<VideoFrame[]> DecodeBatchAsync(ReadOnlyMemory<PhotoTileRequest> requests,
            CancellationToken cancellationToken) => DecodeTileBatchAsync(requests, cancellationToken, DecodeTile);

        private unsafe VideoFrame DecodeTile(int index, int reduction, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SKCodec codec = SKCodec.Create(data) ?? throw new InvalidDataException("Could not open image pixels.");
            ReadOnlySpan<byte> header = new((void*)data.Data, (int)Math.Min(data.Size, 25));
            if (codec.EncodedFormat == SKEncodedImageFormat.Png && header.Length >= 25 && header[24] == 16)
            {
                using Stream stream = data.AsStream();
                using StillImageNative? native = StillImage.DecodeNative(stream, cancellationToken: cancellationToken);
                if (native is not null)
                {
                    ApplyGpuProfile(native.Frame, profile, hasGainMap: true);
                    return native.Frame.AddRef();
                }
            }
            SKColorType type = codec.Info.ColorType is SKColorType.RgbaF16 or SKColorType.RgbaF32
                ? SKColorType.RgbaF16 : codec.Info.BytesPerPixel > 4
                    ? SKColorType.Rgba16161616 : SKColorType.Bgra8888;
            SKSizeI size = HasScaledOverview ? codec.GetScaledDimensions(1f / Math.Max(1, reduction)) : codec.Info.Size;
            VideoFrame frame = VideoFrame.CreateStandalone(size.Width, size.Height,
                type == SKColorType.RgbaF16 ? VideoPixelFormat.RgbaF16 :
                    type == SKColorType.Rgba16161616 ? VideoPixelFormat.Rgba64 : VideoPixelFormat.Bgra32);
            try
            {
                var info = new SKImageInfo(size.Width, size.Height, type, SKAlphaType.Unpremul);
                SKCodecResult result = codec.GetPixels(info, frame.Data, frame.Stride, SKCodecOptions.Default);
                if (result != SKCodecResult.Success)
                    throw new InvalidDataException($"Could not decode image pixels: {result}.");
                cancellationToken.ThrowIfCancellationRequested();
                ApplyGpuProfile(frame, profile, hasGainMap: true);
                return frame;
            }
            catch
            {
                frame.Release();
                throw;
            }
        }

        public void Dispose() => data.Dispose();
    }

    private static VideoFrame CreateNativeTiles(StillImage.TileSource source, PhotoColorProfile profile, bool hasGainMap)
    {
        VideoFrame frame = VideoFrame.CreateHosted(source.Width, source.Height, VideoPixelFormat.Bgra32);
        frame.PhotoTiles = new NativeTileSource(source.CreateDecoder(), profile);
        frame.Rotation = source.Rotation;
        frame.MirrorHorizontally = source.MirrorHorizontally;
        frame.PixelAspectRatio = source.PixelAspectRatio;
        ApplyGpuProfile(frame, profile, hasGainMap);
        return frame;
    }

    private sealed class NativeTileSource(IPhotoTileSource source, PhotoColorProfile profile) : IPhotoTileSource
    {
        public int Count => source.Count;
        public long EncodedBytes => source.EncodedBytes;
        public bool HasScaledOverview => source.HasScaledOverview;
        public int PreferredBatchSize => source.PreferredBatchSize;
        public PhotoTileBounds GetBounds(int index) => source.GetBounds(index);

        public async ValueTask<VideoFrame[]> DecodeBatchAsync(ReadOnlyMemory<PhotoTileRequest> requests,
            CancellationToken cancellationToken)
        {
            VideoFrame[] frames = await source.DecodeBatchAsync(requests, cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (VideoFrame frame in frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ApplyGpuProfile(frame, profile, hasGainMap: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return frames;
            }
            catch
            {
                foreach (VideoFrame frame in frames)
                    frame?.Release();
                throw;
            }
        }

        public void Dispose() => source.Dispose();
    }

    private sealed class JpegTileSource : IPhotoTileSource
    {
        private const int TileSize = 1024;
        private readonly SKData data;
        private readonly int width;
        private readonly int height;
        private readonly int columns;
        private readonly PhotoColorProfile profile;

        public JpegTileSource(SKData source, int width, int height, PhotoColorProfile profile)
        {
            data = SKData.CreateCopy(source.Data, source.Size);
            this.width = width;
            this.height = height;
            this.profile = profile;
            columns = (width + TileSize - 1) / TileSize;
            Count = columns * ((height + TileSize - 1) / TileSize);
        }

        public int Count { get; }
        public long EncodedBytes => data.Size;
        public bool HasScaledOverview => true;

        public PhotoTileBounds GetBounds(int index)
        {
            if (index < 0)
                return new PhotoTileBounds(0, 0, width, height);
            int left = Math.Max(0, index % columns * TileSize - 8);
            int top = Math.Max(0, index / columns * TileSize - 8);
            int right = Math.Min(width, (index % columns + 1) * TileSize + 8);
            int bottom = Math.Min(height, (index / columns + 1) * TileSize + 8);
            return new PhotoTileBounds(left, top, right - left, bottom - top);
        }

        public ValueTask<VideoFrame[]> DecodeBatchAsync(ReadOnlyMemory<PhotoTileRequest> requests,
            CancellationToken cancellationToken) => DecodeTileBatchAsync(requests, cancellationToken, DecodeTile);

        private unsafe VideoFrame DecodeTile(int index, int reduction, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index < 0 && reduction == 1 && !ReferenceEquals(profile, GainSampleProfile))
            {
                using Stream stream = data.AsStream();
                using StillImageNative? native = StillImage.DecodeNative(stream, cancellationToken: cancellationToken);
                if (native is not null)
                {
                    ApplyGpuProfile(native.Frame, profile, hasGainMap: true);
                    return native.Frame.AddRef();
                }
            }
            using SKCodec codec = SKCodec.Create(data) ?? throw new InvalidDataException("Could not open JPEG pixels.");
            SKSizeI scaled = codec.GetScaledDimensions(1f / Math.Max(1, reduction));
            PhotoTileBounds bounds = GetBounds(index);
            int left = (int)((long)bounds.X * scaled.Width / width);
            int top = (int)((long)bounds.Y * scaled.Height / height);
            int right = (int)(((long)(bounds.X + bounds.Width) * scaled.Width + width - 1) / width);
            int bottom = (int)(((long)(bounds.Y + bounds.Height) * scaled.Height + height - 1) / height);
            VideoFrame frame = VideoFrame.CreateStandalone(right - left, bottom - top, VideoPixelFormat.Bgra32);
            try
            {
                var info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                if (index < 0)
                {
                    SKCodecResult result = codec.GetPixels(info, frame.Data, frame.Stride, SKCodecOptions.Default);
                    if (result != SKCodecResult.Success)
                        throw new InvalidDataException($"Could not decode JPEG overview: {result}.");
                }
                else
                {
                    SKCodecResult result = codec.StartScanlineDecode(info);
                    if (result != SKCodecResult.Success || !codec.SkipScanlines(top))
                        throw new InvalidDataException($"Could not decode JPEG region: {result}.");
                    IntPtr row = Marshal.AllocHGlobal(info.RowBytes);
                    try
                    {
                        for (int line = 0; line < frame.Height; line++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (codec.GetScanlines(row, 1, info.RowBytes) != 1)
                                throw new InvalidDataException("Incomplete JPEG region.");
                            Buffer.MemoryCopy((byte*)row + left * 4, (byte*)frame.Data + (long)line * frame.Stride,
                                frame.Stride, frame.Width * 4);
                        }
                    }
                    finally { Marshal.FreeHGlobal(row); }
                }
                cancellationToken.ThrowIfCancellationRequested();
                ApplyGpuProfile(frame, profile, hasGainMap: true);
                return frame;
            }
            catch
            {
                frame.Release();
                throw;
            }
        }

        public void Dispose() => data.Dispose();
    }
}