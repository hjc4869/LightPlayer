#nullable enable
using System;
using System.IO;
using System.Threading;

namespace LightStudio.FfmpegShim.Demuxing;

/// <summary>A byte range requested by an adaptive FFmpeg demuxer.</summary>
/// <param name="Offset">First byte to return.</param>
/// <param name="EndOffset">Exclusive end offset, or null to read through the resource end.</param>
public readonly record struct MediaResourceRange(long Offset, long? EndOffset);

/// <summary>One resource opened for an adaptive manifest, including its final redirected URL.</summary>
public sealed class MediaResource : IDisposable
{
    private Action? release;

    public MediaResource(Stream stream, string resolvedUrl, Action? release = null)
    {
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        ResolvedUrl = string.IsNullOrWhiteSpace(resolvedUrl)
            ? throw new ArgumentException("A resolved URL is required.", nameof(resolvedUrl))
            : resolvedUrl;
        this.release = release ?? stream.Dispose;
    }

    public Stream Stream { get; }

    public string ResolvedUrl { get; }

    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}

/// <summary>Opens manifest children through an application-owned transport.</summary>
public interface IMediaResourceResolver
{
    MediaResource Open(string url, MediaResourceRange? range = null);
}