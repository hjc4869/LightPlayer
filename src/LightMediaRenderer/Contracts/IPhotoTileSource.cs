using System;
using System.Threading;
using System.Threading.Tasks;

namespace LightMediaRenderer.Contracts;

public readonly record struct PhotoTileBounds(int X, int Y, int Width, int Height);

public readonly record struct PhotoTileRequest(int Index, int Reduction);

/// <summary>Compressed photo regions decoded on demand; each returned frame belongs to its caller.</summary>
public interface IPhotoTileSource : IDisposable
{
    int Count { get; }
    long EncodedBytes { get; }
    bool HasScaledOverview { get; }

    /// <summary>Container-tiled formats such as HEIF and AVIF ignore the throughput decode preference.</summary>
    bool AlwaysPreferMemory => false;

    /// <summary>Tiles worth decoding in one batch when more than the platform default; 0 keeps the default.</summary>
    int PreferredBatchSize => 0;

    PhotoTileBounds GetBounds(int index);

    /// <summary>Returns one owned frame per request without blocking the caller.</summary>
    ValueTask<VideoFrame[]> DecodeBatchAsync(ReadOnlyMemory<PhotoTileRequest> requests, CancellationToken cancellationToken);
}