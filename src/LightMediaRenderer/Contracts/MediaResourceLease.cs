using System;
using System.Threading;

namespace LightMediaRenderer.Contracts;

public readonly record struct MediaResourceHandle(string OwnerId, string ResourceId, int Generation);

public interface IMediaResourceOwner
{
    /// <summary>Returns ownership without waiting for the producer to process the release.</summary>
    void Release(MediaResourceHandle handle);
}

public sealed class MediaResourceLease : IDisposable
{
    private IMediaResourceOwner? owner;
    private readonly ReadOnlyMemory<byte>? memory;

    public MediaResourceLease(
        MediaResourceHandle handle,
        int byteLength,
        IMediaResourceOwner owner,
        ReadOnlyMemory<byte>? memory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(handle.OwnerId);
        ArgumentException.ThrowIfNullOrEmpty(handle.ResourceId);
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        ArgumentNullException.ThrowIfNull(owner);
        if (memory is { } local && local.Length < byteLength)
            throw new ArgumentException("The borrowed memory is smaller than the resource.", nameof(memory));

        Handle = handle;
        ByteLength = byteLength;
        this.owner = owner;
        this.memory = memory;
    }

    public MediaResourceHandle Handle { get; }

    public int ByteLength { get; }

    public bool TryGetMemory(out ReadOnlyMemory<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref owner) is null, this);
        buffer = memory is { } local ? local[..ByteLength] : default;
        return memory.HasValue;
    }

    public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(Handle);
}