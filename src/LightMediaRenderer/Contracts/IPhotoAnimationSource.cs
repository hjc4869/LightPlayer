using System;
using System.Threading;
using System.Threading.Tasks;

namespace LightMediaRenderer.Contracts;

/// <summary>Owns encoded animation data and creates independent, lazily decoded playback sessions.</summary>
public interface IPhotoAnimationSource : IDisposable
{
    long EncodedBytes { get; }
    IPhotoAnimation Open();
}

/// <summary>Transfers one composited frame per read, starting at the first frame; null ends playback.</summary>
public interface IPhotoAnimation : IAsyncDisposable
{
    ValueTask<VideoFrame?> ReadNextAsync(CancellationToken cancellationToken);
}