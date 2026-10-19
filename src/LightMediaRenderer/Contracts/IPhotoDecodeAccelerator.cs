using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace LightMediaRenderer.Contracts;

public interface IPhotoDecodeAccelerator
{
    /// <summary>Decodes a full image on a worker thread, returning null when acceleration is unavailable.</summary>
    VideoFrame? TryDecode(Stream stream, Func<Stream, CancellationToken, VideoFrame?> softwareFallback,
        CancellationToken cancellationToken);
}

public interface IPhotoSurface : IDisposable
{
    long SizeInBytes { get; }

    /// <summary>Completes after native storage and all imported images have released their ownership.</summary>
    Task WhenReleased { get; }

    /// <summary>Imports during a render pass; the returned image retains its native resources until disposed.</summary>
    SKImage? TryImport(GRContext context, Func<IDisposable>? makeCurrent);
}

public static class PhotoDecodeAcceleration
{
    public static IPhotoDecodeAccelerator? Decoder { get; set; }
}
