using System;
using System.Threading;
using System.Threading.Tasks;
using LightMediaRenderer.Contracts;
using SkiaSharp;

namespace LightMediaRenderer.Avalonia.Shaders;

/// <summary>
/// Regions of a platform photo that is already a texture, handed to the tile cache as frames that
/// read their part of the imported picture through its texture rectangle.
/// </summary>
internal sealed class PhotoSurfaceTileSource(VideoFrameImageCache owner, VideoFrame frame) : IPhotoTileSource
{
    /// <summary>
    /// Small enough that a 1:1 viewport's regions fit the detail budget at full resolution on phone
    /// and tablet displays, large enough that a fitted view draws a moderate number of them.
    /// </summary>
    private const int RegionSize = 1024;
    private readonly int columns = (frame.Width + RegionSize - 1) / RegionSize;

    public int Count => columns * ((frame.Height + RegionSize - 1) / RegionSize);

    public long EncodedBytes => 0;

    public bool HasScaledOverview => false;

    public bool AlwaysPreferMemory => true;

    /// <summary>Four regions per batch; the memory-mode pixel target would otherwise admit one.</summary>
    public int PreferredBatchSize => 64;

    public PhotoTileBounds GetBounds(int index)
    {
        int x = index % columns * RegionSize;
        int y = index / columns * RegionSize;
        return new PhotoTileBounds(x, y, Math.Min(RegionSize, frame.Width - x), Math.Min(RegionSize, frame.Height - y));
    }

    /// <summary>The photo's imported texture, which every region samples.</summary>
    public SKImage? Import(GRContext? context) => owner.GetPackedImage(default, context);

    public ValueTask<VideoFrame[]> DecodeBatchAsync(ReadOnlyMemory<PhotoTileRequest> requests, CancellationToken cancellationToken)
    {
        var regions = new VideoFrame[requests.Length];
        for (int index = 0; index < regions.Length; index++)
        {
            PhotoTileBounds bounds = GetBounds(requests.Span[index].Index);
            VideoFrame region = VideoFrame.CreateHosted(bounds.Width, bounds.Height, VideoPixelFormat.Bgra32);
            region.ExternalTextureRect = new VideoTextureRect(1, 1, (float)bounds.X / frame.Width, (float)bounds.Y / frame.Height);
            region.PhotoColor = frame.PhotoColor;
            region.ColorPrimaries = frame.ColorPrimaries;
            region.ColorTransfer = frame.ColorTransfer;
            region.ColorRange = frame.ColorRange;
            region.FieldOrder = frame.FieldOrder;
            region.HdrPeakLuminance = frame.HdrPeakLuminance;
            region.PhotoPeakLuminance = frame.PhotoPeakLuminance;
            region.HdrMasteringLuminance = frame.HdrMasteringLuminance;
            regions[index] = region;
        }
        return ValueTask.FromResult(regions);
    }

    public void Dispose()
    {
    }
}
